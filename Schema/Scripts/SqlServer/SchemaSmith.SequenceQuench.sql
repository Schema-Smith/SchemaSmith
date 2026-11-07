-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Converges declared sequences (SQL Server 2012+) in place, before tables, so a column DEFAULT (NEXT VALUE FOR ...)
-- finds its sequence. One procedure, always fed XML: sequences are flat scalars and XML ingest works on every version,
-- including 2012-2016, where JSON does not.
--
-- What SQL Server's ALTER SEQUENCE cannot do decides the shape. It has no AS, so a changed data type is refused, by
-- name, before anything is altered. It has no START WITH, so Start applies only when the sequence is created; a
-- different Start on an existing sequence is reported, not changed. And it keeps the start value as a bound for life:
-- a minimum above the start value (11703), or bounds that leave out the current value (11704), cannot land either, so
-- those are refused by name too. The current value is data: no RESTART, ever.
--
-- An omitted MinValue or MaxValue means the data type's bound, and an omitted Cache the engine's default cache; both
-- are converged back to that, as PostgreSQL's procedure does. A declared alias type compares as its base type, which is
-- what SQL Server records for the sequence.
--
-- CREATEs on SQL Server 2008 R2 (no THROW, CONCAT or IIF; sys.sequences is resolved when first reached), where it
-- records each declared sequence as unsupported and returns.
IF OBJECT_ID('[SchemaSmith].[SequenceQuench]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[SequenceQuench]
GO
CREATE PROCEDURE [SchemaSmith].[SequenceQuench]
    @ProductName NVARCHAR(200),
    @Sequences XML,
    @WhatIf BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF OBJECT_ID('tempdb..#Sequences') IS NOT NULL DROP TABLE #Sequences;
    CREATE TABLE #Sequences (
        [_RowId] INT NOT NULL PRIMARY KEY,
        SeqSchema NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL,
        SeqName NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL,
        DataType NVARCHAR(400) COLLATE DATABASE_DEFAULT NOT NULL,
        StartValue DECIMAL(38, 0) NULL,
        Increment DECIMAL(38, 0) NOT NULL,
        MinValue DECIMAL(38, 0) NULL,
        MaxValue DECIMAL(38, 0) NULL,
        Cache BIGINT NULL,
        Cycle BIT NOT NULL,
        ShouldApplyExpression NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        VariantName NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL,
        Description NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
        -- The declared type as compared: base type (decimal for numeric too) and, for decimal, its precision.
        BaseType NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL,
        TypePrecision INT NULL,
        TypeMin DECIMAL(38, 0) NULL,
        TypeMax DECIMAL(38, 0) NULL,
        -- The live sequence, when there is one.
        Exists_ BIT NOT NULL DEFAULT 0,
        LiveBaseType NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL,
        LivePrecision INT NULL,
        LiveStart DECIMAL(38, 0) NULL,
        LiveIncrement DECIMAL(38, 0) NULL,
        LiveMin DECIMAL(38, 0) NULL,
        LiveMax DECIMAL(38, 0) NULL,
        LiveCurrent DECIMAL(38, 0) NULL,
        LiveCycle BIT NULL,
        LiveCached BIT NULL,
        LiveCacheSize BIGINT NULL
    );

    INSERT INTO #Sequences ([_RowId], SeqSchema, SeqName, DataType, StartValue, Increment, MinValue, MaxValue, Cache, Cycle,
                            ShouldApplyExpression, VariantName, Description)
    SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
           ISNULL([SchemaSmith].[fn_StripBracketWrapping](s.value('(Schema/text())[1]', 'NVARCHAR(128)')), 'dbo'),
           [SchemaSmith].[fn_StripBracketWrapping](s.value('(Name/text())[1]', 'NVARCHAR(128)')),
           ISNULL(NULLIF(LTRIM(RTRIM(s.value('(DataType/text())[1]', 'NVARCHAR(400)'))), ''), 'bigint'),
           s.value('(Start/text())[1]', 'DECIMAL(38, 0)'),
           ISNULL(s.value('(Increment/text())[1]', 'DECIMAL(38, 0)'), 1),
           s.value('(MinValue/text())[1]', 'DECIMAL(38, 0)'),
           s.value('(MaxValue/text())[1]', 'DECIMAL(38, 0)'),
           s.value('(Cache/text())[1]', 'BIGINT'),
           CASE WHEN s.value('(Cycle/text())[1]', 'NVARCHAR(10)') = 'true' THEN 1 ELSE 0 END,
           s.value('(ShouldApplyExpression/text())[1]', 'NVARCHAR(MAX)'),
           s.value('(VariantName/text())[1]', 'NVARCHAR(128)'),
           -- An empty element is a declared "" (remove the description); an absent one leaves it alone.
           CASE WHEN s.exist('Description') = 1 THEN ISNULL(s.value('(Description/text())[1]', 'NVARCHAR(MAX)'), '') END
      FROM @Sequences.nodes('/Sequences/Sequence') AS X(s);

    -- A ShouldApplyExpression that is false on this target removes its sequence, even under WhatIf.
    DECLARE @gateSql NVARCHAR(MAX);
    SELECT @gateSql = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('DELETE FROM #Sequences WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect(ShouldApplyExpression) + ');' AS NVARCHAR(MAX))
                             FROM #Sequences
                             WHERE RTRIM(ISNULL(ShouldApplyExpression, '')) <> ''
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
    IF @gateSql IS NOT NULL EXEC(@gateSql);

    DECLARE @msg NVARCHAR(2047);
    IF EXISTS (SELECT 1 FROM #Sequences GROUP BY SeqSchema, SeqName HAVING COUNT(*) > 1)
    BEGIN
        SET @msg = 'Multiple sequence variants matched on this target for sequence '
                   + (SELECT TOP 1 SeqSchema + '.' + SeqName FROM #Sequences GROUP BY SeqSchema, SeqName HAVING COUNT(*) > 1 ORDER BY SeqSchema, SeqName)
                   + '. Make their ShouldApplyExpressions mutually exclusive.';
        RAISERROR('%s', 16, 1, @msg);
        RETURN;
    END

    IF NOT EXISTS (SELECT 1 FROM #Sequences) RETURN;

    -- Below SQL Server 2012 there are no sequences: the unsupported-feature policy decides.
    IF SchemaSmith.fn_ServerMajorVersion() < 11
    BEGIN
        DECLARE @list NVARCHAR(MAX) = STUFF((SELECT ', ' + SeqSchema + '.' + SeqName FROM #Sequences ORDER BY SeqSchema, SeqName FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
        IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
        BEGIN
            SET @msg = 'Sequences require SQL Server 2012 (UnsupportedFeaturePolicy=fail): ' + @list;
            RAISERROR('%s', 16, 1, @msg);
            RETURN;
        END
        EXEC SchemaSmith.PrintDowngrade N'  Sequence skipped (requires SQL Server 2012 - downgraded)', @list;
        INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'sequence (SQL Server 2012)', SeqSchema + '.' + SeqName, 'downgraded' FROM #Sequences;
        RETURN;
    END

    -- The declared type as SQL Server records it. An alias resolves to its base type; decimal and numeric are one type;
    -- a bare decimal is decimal(18); the scale is always 0 for a sequence and is not compared.
    UPDATE #Sequences
       SET BaseType = CASE WHEN t.user_type_id IS NOT NULL AND t.is_user_defined = 1 THEN TYPE_NAME(t.system_type_id)
                           ELSE LOWER(LTRIM(RTRIM(REPLACE(REPLACE(CASE WHEN CHARINDEX('(', DataType) > 0 THEN LEFT(DataType, CHARINDEX('(', DataType) - 1) ELSE DataType END, '[', ''), ']', '')))) END,
           TypePrecision = CASE WHEN t.user_type_id IS NOT NULL AND t.is_user_defined = 1 THEN t.[precision]
                                WHEN CHARINDEX('(', DataType) > 0
                                THEN CAST(LTRIM(RTRIM(SUBSTRING(DataType, CHARINDEX('(', DataType) + 1,
                                          CASE WHEN CHARINDEX(',', DataType) > 0 THEN CHARINDEX(',', DataType) ELSE CHARINDEX(')', DataType) END - CHARINDEX('(', DataType) - 1))) AS INT)
                                ELSE NULL END
      FROM #Sequences s
      LEFT JOIN sys.types t ON t.user_type_id = TYPE_ID(s.DataType);
    UPDATE #Sequences SET BaseType = 'int' WHERE BaseType = 'integer';
    UPDATE #Sequences SET BaseType = 'decimal' WHERE BaseType IN ('numeric', 'dec');
    UPDATE #Sequences SET TypePrecision = 18 WHERE BaseType = 'decimal' AND TypePrecision IS NULL;
    UPDATE #Sequences SET TypePrecision = NULL WHERE BaseType <> 'decimal';
    UPDATE #Sequences
       SET TypeMin = CASE BaseType WHEN 'tinyint' THEN 0 WHEN 'smallint' THEN -32768 WHEN 'int' THEN -2147483648
                                   WHEN 'bigint' THEN -9223372036854775808
                                   WHEN 'decimal' THEN -CAST(REPLICATE('9', TypePrecision) AS DECIMAL(38, 0)) END,
           TypeMax = CASE BaseType WHEN 'tinyint' THEN 255 WHEN 'smallint' THEN 32767 WHEN 'int' THEN 2147483647
                                   WHEN 'bigint' THEN 9223372036854775807
                                   WHEN 'decimal' THEN CAST(REPLICATE('9', TypePrecision) AS DECIMAL(38, 0)) END;

    UPDATE s
       SET Exists_ = 1,
           LiveBaseType = CASE WHEN TYPE_NAME(q.system_type_id) = 'numeric' THEN 'decimal' ELSE TYPE_NAME(q.system_type_id) END,
           LivePrecision = CASE WHEN TYPE_NAME(q.system_type_id) IN ('decimal', 'numeric') THEN q.[precision] END,
           LiveStart = CAST(q.start_value AS DECIMAL(38, 0)),
           LiveIncrement = CAST(q.increment AS DECIMAL(38, 0)),
           LiveMin = CAST(q.minimum_value AS DECIMAL(38, 0)),
           LiveMax = CAST(q.maximum_value AS DECIMAL(38, 0)),
           LiveCurrent = CAST(q.current_value AS DECIMAL(38, 0)),
           LiveCycle = q.is_cycling,
           LiveCached = q.is_cached,
           LiveCacheSize = q.cache_size
      FROM #Sequences s
      JOIN sys.sequences q ON q.[object_id] = OBJECT_ID(QUOTENAME(s.SeqSchema) + '.' + QUOTENAME(s.SeqName)) AND q.[name] = s.SeqName;

    -- REFUSALS, all before any DDL.
    DECLARE @bad NVARCHAR(MAX);
    SELECT TOP 1 @bad = 'Sequence ' + SeqSchema + '.' + SeqName + ' declares data type ' + DataType + ', but is deployed as '
                        + LiveBaseType + ISNULL('(' + CAST(LivePrecision AS NVARCHAR(10)) + ')', '')
                        + '. SQL Server cannot change a sequence''s data type (ALTER SEQUENCE has no AS); changing it means dropping the sequence and every default that uses it. Migrate it with a script, or correct the declared type to match.'
      FROM #Sequences
     WHERE Exists_ = 1 AND (BaseType <> LiveBaseType OR ISNULL(TypePrecision, -1) <> ISNULL(LivePrecision, -1))
     ORDER BY SeqSchema, SeqName;
    IF @bad IS NOT NULL BEGIN RAISERROR('%s', 16, 1, @bad); RETURN; END

    SELECT TOP 1 @bad = 'Sequence ' + SeqSchema + '.' + SeqName + ' declares a range of '
                        + CAST(ISNULL(MinValue, TypeMin) AS NVARCHAR(45)) + ' to ' + CAST(ISNULL(MaxValue, TypeMax) AS NVARCHAR(45))
                        + ', which leaves out its start value ' + CAST(LiveStart AS NVARCHAR(45)) + ' or its current value ' + CAST(LiveCurrent AS NVARCHAR(45))
                        + '. SQL Server keeps both inside the range and cannot change the start value, and SchemaSmith never resets the current value. Migrate it with a script, or widen the declared range.'
      FROM #Sequences
     WHERE Exists_ = 1
       AND (ISNULL(MinValue, TypeMin) > LiveStart OR ISNULL(MaxValue, TypeMax) < LiveStart
            OR ISNULL(MinValue, TypeMin) > LiveCurrent OR ISNULL(MaxValue, TypeMax) < LiveCurrent)
     ORDER BY SeqSchema, SeqName;
    IF @bad IS NOT NULL BEGIN RAISERROR('%s', 16, 1, @bad); RETURN; END

    DECLARE @id INT, @sql NVARCHAR(MAX), @label NVARCHAR(400), @description NVARCHAR(MAX), @descriptionFound BIT;
    DECLARE seq_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT [_RowId] FROM #Sequences ORDER BY SeqSchema, SeqName;
    OPEN seq_cursor;
    FETCH NEXT FROM seq_cursor INTO @id;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = NULL;
        SELECT @label = SeqSchema + '.' + SeqName,
               @sql = CASE WHEN Exists_ = 0
                           THEN 'CREATE SEQUENCE ' + QUOTENAME(SeqSchema) + '.' + QUOTENAME(SeqName) + ' AS ' + DataType
                                + ISNULL(' START WITH ' + CAST(StartValue AS NVARCHAR(45)), '')
                                + ' INCREMENT BY ' + CAST(Increment AS NVARCHAR(45))
                                + ISNULL(' MINVALUE ' + CAST(MinValue AS NVARCHAR(45)), ' NO MINVALUE')
                                + ISNULL(' MAXVALUE ' + CAST(MaxValue AS NVARCHAR(45)), ' NO MAXVALUE')
                                + CASE WHEN Cycle = 1 THEN ' CYCLE' ELSE ' NO CYCLE' END
                                + CASE WHEN Cache = 0 THEN ' NO CACHE' WHEN Cache IS NOT NULL THEN ' CACHE ' + CAST(Cache AS NVARCHAR(20)) ELSE '' END
                           ELSE NULLIF(
                                  CASE WHEN Increment <> LiveIncrement THEN ' INCREMENT BY ' + CAST(Increment AS NVARCHAR(45)) ELSE '' END
                                + CASE WHEN ISNULL(MinValue, TypeMin) <> LiveMin THEN ISNULL(' MINVALUE ' + CAST(MinValue AS NVARCHAR(45)), ' NO MINVALUE') ELSE '' END
                                + CASE WHEN ISNULL(MaxValue, TypeMax) <> LiveMax THEN ISNULL(' MAXVALUE ' + CAST(MaxValue AS NVARCHAR(45)), ' NO MAXVALUE') ELSE '' END
                                + CASE WHEN Cycle <> LiveCycle THEN CASE WHEN Cycle = 1 THEN ' CYCLE' ELSE ' NO CYCLE' END ELSE '' END
                                + CASE WHEN Cache IS NULL AND (LiveCached = 0 OR LiveCacheSize IS NOT NULL) THEN ' CACHE'
                                       WHEN Cache = 0 AND LiveCached = 1 THEN ' NO CACHE'
                                       WHEN Cache > 0 AND (LiveCached = 0 OR ISNULL(LiveCacheSize, -1) <> Cache) THEN ' CACHE ' + CAST(Cache AS NVARCHAR(20))
                                       ELSE '' END, '') END
          FROM #Sequences WHERE [_RowId] = @id;

        IF EXISTS (SELECT 1 FROM #Sequences WHERE [_RowId] = @id AND Exists_ = 0)
        BEGIN
            RAISERROR('  Create sequence %s', 10, 1, @label) WITH NOWAIT;
            RAISERROR('%s', 10, 1, @sql) WITH NOWAIT;
            IF @WhatIf = 0 EXEC(@sql);
            INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            VALUES (@@SPID, 'sequence', @label, CASE WHEN @WhatIf = 1 THEN 'wouldCreate' ELSE 'created' END);
        END
        ELSE IF @sql IS NOT NULL
        BEGIN
            SELECT @sql = 'ALTER SEQUENCE ' + QUOTENAME(SeqSchema) + '.' + QUOTENAME(SeqName) + @sql FROM #Sequences WHERE [_RowId] = @id;
            RAISERROR('  Altering sequence %s', 10, 1, @label) WITH NOWAIT;
            RAISERROR('%s', 10, 1, @sql) WITH NOWAIT;
            IF @WhatIf = 0 EXEC(@sql);
            INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            VALUES (@@SPID, 'sequence', @label, CASE WHEN @WhatIf = 1 THEN 'wouldModify' ELSE 'modified' END);
        END

        -- Start applies at creation only: SQL Server has no START WITH on ALTER. Said, not changed, and not audited.
        IF EXISTS (SELECT 1 FROM #Sequences WHERE [_RowId] = @id AND Exists_ = 1 AND StartValue IS NOT NULL AND StartValue <> LiveStart)
            RAISERROR('  Sequence %s declares a different Start; Start applies only when the sequence is created, so it is left as it is.', 10, 1, @label) WITH NOWAIT;

        -- The description: an omitted one leaves the live one alone, '' removes it.
        SELECT @description = Description FROM #Sequences WHERE [_RowId] = @id;
        IF @description IS NOT NULL AND (@WhatIf = 0 OR EXISTS (SELECT 1 FROM #Sequences WHERE [_RowId] = @id AND Exists_ = 1))
        BEGIN
            SET @descriptionFound = 0;
            DECLARE @liveDescription NVARCHAR(MAX) = NULL;
            SELECT @descriptionFound = 1, @liveDescription = CAST(ep.[value] AS NVARCHAR(MAX))
              FROM #Sequences s
              JOIN sys.extended_properties ep
                ON ep.class = 1 AND ep.minor_id = 0 AND ep.[name] = 'MS_Description'
               AND ep.major_id = OBJECT_ID(QUOTENAME(s.SeqSchema) + '.' + QUOTENAME(s.SeqName))
             WHERE s.[_RowId] = @id;
            IF (@description = '' AND @descriptionFound = 1) OR (@description <> '' AND ISNULL(@liveDescription, NCHAR(0)) <> @description)
            BEGIN
                SELECT @sql = CASE WHEN @description = '' THEN 'EXEC sp_dropextendedproperty @name = N''MS_Description'', '
                                   WHEN @descriptionFound = 1 THEN 'EXEC sp_updateextendedproperty @name = N''MS_Description'', @value = N''' + REPLACE(@description, '''', '''''') + ''', '
                                   ELSE 'EXEC sp_addextendedproperty @name = N''MS_Description'', @value = N''' + REPLACE(@description, '''', '''''') + ''', ' END
                              + '@level0type = N''SCHEMA'', @level0name = N''' + REPLACE(SeqSchema, '''', '''''') + ''', @level1type = N''SEQUENCE'', @level1name = N''' + REPLACE(SeqName, '''', '''''') + ''';'
                  FROM #Sequences WHERE [_RowId] = @id;
                RAISERROR('  Setting the description of sequence %s', 10, 1, @label) WITH NOWAIT;
                IF @WhatIf = 0 EXEC(@sql);
                INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                VALUES (@@SPID, 'sequence', @label, CASE WHEN @WhatIf = 1 THEN 'wouldModify' ELSE 'modified' END);
            END
        END

        FETCH NEXT FROM seq_cursor INTO @id;
    END
    CLOSE seq_cursor;
    DEALLOCATE seq_cursor;
END
GO
