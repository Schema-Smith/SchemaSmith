-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Callers pass the active template name + iteration schema so the existing-indexed-views
-- lookup that drives the drop cursor is scoped to that schema. A regular template passes
-- @SchemaName = '' and the lookup covers all schemas. Without this scoping, parallel
-- schema-template iterations treat sibling iterations' indexed views as "removed from
-- product" and race to drop each other's objects.
IF OBJECT_ID('[SchemaSmith].[IndexedViewQuench]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[IndexedViewQuench]
GO
CREATE PROCEDURE [SchemaSmith].[IndexedViewQuench]
    @ProductName NVARCHAR(200),
    @IndexedViewSchema NVARCHAR(MAX),
    @WhatIf BIT = 0,
    @UpdateFillFactor BIT = 0,
    @TemplateName NVARCHAR(256),
    @SchemaName NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;

    -- Parse indexed views from JSON using OPENJSON WITH for NVARCHAR(MAX) support
    -- [_RowId] gives each parsed view a unique identifier so the per-target ShouldApply
    -- DELETE below targets exactly the source row whose expression evaluated false. Without
    -- it, the DELETE matched on (Schema, Name) and would silently wipe both rows when two
    -- same-named variants had mutually exclusive ShouldApply expressions.
    DECLARE @Views TABLE (
        [_RowId] INT,
        ViewSchema NVARCHAR(200),
        ViewName NVARCHAR(200),
        Definition NVARCHAR(MAX),
        IndexJson NVARCHAR(MAX),
        VariantName NVARCHAR(128),
        ShouldApplyExpression NVARCHAR(MAX)
    );

    INSERT INTO @Views ([_RowId], ViewSchema, ViewName, Definition, IndexJson, VariantName, ShouldApplyExpression)
    SELECT
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
        [SchemaSmith].[fn_StripBracketWrapping](v.[Schema]),
        [SchemaSmith].[fn_StripBracketWrapping](v.[Name]),
        v.[Definition],
        v.[Indexes],
        v.[VariantName],
        v.[ShouldApplyExpression]
    FROM OPENJSON(@IndexedViewSchema)
    WITH (
        [Schema] NVARCHAR(200) '$.Schema',
        [Name] NVARCHAR(200) '$.Name',
        [Definition] NVARCHAR(MAX) '$.Definition',
        [Indexes] NVARCHAR(MAX) '$.Indexes' AS JSON,
        [VariantName] NVARCHAR(128) '$.VariantName',
        [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression'
    ) v;

    -- Evaluate ShouldApplyExpression per-target: remove views whose expression is false.
    -- Scoped by [_RowId] so each generated DELETE targets exactly the source row whose
    -- expression evaluated false (no collateral damage to siblings with the same Name).
    -- The expression is embedded as live SQL (not a string literal), mirroring
    -- ParseTableJsonIntoTempTables.
    -- @Views is a table variable, not visible to the child batch EXEC() spawns, so the
    -- gate DELETEs are built and run against a temp table populated from @Views, then
    -- survivors are reconciled back. Building the aggregate gate SQL directly against
    -- #ViewGate avoids a fragile string REPLACE that a ShouldApplyExpression could corrupt.
    DROP TABLE IF EXISTS #ViewGate;
    SELECT * INTO #ViewGate FROM @Views;
    DECLARE @gateSql NVARCHAR(MAX);
    SELECT @gateSql = STRING_AGG(CAST('DELETE FROM #ViewGate WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect(ShouldApplyExpression) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #ViewGate
    WHERE RTRIM(ISNULL(ShouldApplyExpression, '')) <> '';
    IF @gateSql IS NOT NULL
    BEGIN
        EXEC(@gateSql);
        DELETE FROM @Views WHERE [_RowId] NOT IN (SELECT [_RowId] FROM #ViewGate);
    END;
    DROP TABLE IF EXISTS #ViewGate;

    -- A view name with 2+ surviving variants cannot be honored: SQL Server allows ONE view
    -- per (schema, name). ShouldApplyExpressions for same-named variants must be mutually
    -- exclusive on any given target. (Mirrors the FullTextIndex mutual-exclusivity guard.)
    IF EXISTS (SELECT 1 FROM @Views GROUP BY ViewSchema, ViewName HAVING COUNT(*) > 1)
    BEGIN
        DECLARE @v_IvDupView NVARCHAR(1010) =
            (SELECT TOP 1 ViewSchema + '.' + ViewName FROM @Views GROUP BY ViewSchema, ViewName HAVING COUNT(*) > 1 ORDER BY ViewSchema, ViewName);
        DECLARE @v_IvDupMsg NVARCHAR(2000) = 'Multiple indexed view variants matched on this target for view ' + @v_IvDupView +
            '. SQL Server allows one view per name — ShouldApplyExpressions must be mutually exclusive.';
        THROW 51000, @v_IvDupMsg, 1;
    END;

    -- Validate ownership: fail if any requested views are owned by a different product
    -- (mirrors PostgreSQL ValidateMaterializedViewOwnership pattern)
    DECLARE @conflictMsg NVARCHAR(MAX);
    SELECT @conflictMsg = STRING_AGG(
        N'Indexed view ' + s.name + N'.' + v.name + N' owned by different product. ['
        + CAST(ep.value AS NVARCHAR(200)) + N'] <> [' + @ProductName + N']', CHAR(10))
    FROM @Views rv
    INNER JOIN sys.schemas s ON s.name = rv.ViewSchema
    INNER JOIN sys.views v ON v.name = rv.ViewName AND v.schema_id = s.schema_id
    INNER JOIN sys.extended_properties ep ON ep.major_id = v.object_id AND ep.minor_id = 0
        AND ep.name = 'SchemaSmith_Product'
    WHERE CAST(ep.value AS NVARCHAR(200)) <> @ProductName;

    IF @conflictMsg IS NOT NULL
    BEGIN
        EXEC [SchemaSmith].[PrintWithNoWait] @conflictMsg;
        THROW 50001, 'One or more indexed views in this quench are already owned by another product', 1;
    END;

    -- Identify existing indexed views owned by this product
    DECLARE @ExistingViews TABLE (
        SchemaName NVARCHAR(200),
        ViewName NVARCHAR(200),
        ObjectId INT,
        CurrentDefinition NVARCHAR(MAX)
    );

    -- @SchemaName = '': regular template, scan-all-schemas (drop-candidate set spans the DB).
    -- @SchemaName <> '': schema-template iteration, scope to the iteration's schema. The
    -- IF/ELSE split (not a single filter predicate) is plan-shape critical — a single query
    -- with `(@SchemaName = N'' OR s.name = @SchemaName)` scans sys.views broadly and S-locks
    -- sysschobjs rows for sibling-schema views, deadlocking against a sibling iteration's
    -- CREATE INDEX (Sch-M on the view, U->X on the same sysschobjs row).
    IF @SchemaName <> N''
    BEGIN
        DECLARE @schemaId INT = SCHEMA_ID(@SchemaName);
        INSERT INTO @ExistingViews
        SELECT @SchemaName, v.name, v.object_id, m.definition
        FROM sys.views v
        INNER JOIN sys.sql_modules m ON v.object_id = m.object_id
        WHERE v.schema_id = @schemaId
        AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1
        AND EXISTS (
            SELECT 1 FROM sys.extended_properties ep
            WHERE ep.major_id = v.object_id AND ep.minor_id = 0
            AND ep.name = 'SchemaSmith_Product' AND CAST(ep.value AS NVARCHAR(200)) = @ProductName
        );
    END
    ELSE
    BEGIN
        INSERT INTO @ExistingViews
        SELECT s.name, v.name, v.object_id, m.definition
        FROM sys.views v
        INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
        INNER JOIN sys.sql_modules m ON v.object_id = m.object_id
        WHERE OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1
        AND EXISTS (
            SELECT 1 FROM sys.extended_properties ep
            WHERE ep.major_id = v.object_id AND ep.minor_id = 0
            AND ep.name = 'SchemaSmith_Product' AND CAST(ep.value AS NVARCHAR(200)) = @ProductName
        );
    END;

    -- Process removals: views owned by product but not in new schema
    -- (Local cursor variables prefixed with @iv* to avoid colliding with the @SchemaName
    -- proc parameter — SQL Server identifiers are case-insensitive.)
    DECLARE @objectId INT, @ivSchema NVARCHAR(200), @ivName NVARCHAR(200);
    DECLARE @sql NVARCHAR(MAX), @msg NVARCHAR(MAX);

    DECLARE remove_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT e.ObjectId, e.SchemaName, e.ViewName
        FROM @ExistingViews e
        WHERE NOT EXISTS (SELECT 1 FROM @Views v WHERE v.ViewSchema = e.SchemaName AND v.ViewName = e.ViewName);

    OPEN remove_cursor;
    FETCH NEXT FROM remove_cursor INTO @objectId, @ivSchema, @ivName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @msg = N'Dropping removed indexed view ' + @ivSchema + N'.' + @ivName;
        EXEC [SchemaSmith].[PrintWithNoWait] @msg;
        -- Drop nonclustered indexes first
        DECLARE @ncDropSql NVARCHAR(MAX);
        SELECT @ncDropSql = STRING_AGG('DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName), '; ')
        FROM sys.indexes i WHERE i.object_id = @objectId AND i.type > 1;
        IF @ncDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @ncDropSql;

        -- Drop clustered index
        DECLARE @clDropSql NVARCHAR(MAX);
        SELECT @clDropSql = 'DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName)
        FROM sys.indexes i WHERE i.object_id = @objectId AND i.type = 1;
        IF @clDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @clDropSql;

        -- Drop view
        SET @sql = 'DROP VIEW ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName);
        IF @WhatIf = 0 EXEC sp_executesql @sql;

        FETCH NEXT FROM remove_cursor INTO @objectId, @ivSchema, @ivName;
    END;
    CLOSE remove_cursor;
    DEALLOCATE remove_cursor;

    -- Process new and changed views
    DECLARE @defn NVARCHAR(MAX), @indexJson NVARCHAR(MAX), @variantName NVARCHAR(128);
    DECLARE @existingDef NVARCHAR(MAX), @existingObjectId INT;
    DECLARE @needsRecreate BIT, @needsIndexUpdate BIT;
    -- What DROP VIEW takes with it and the package does not carry: permissions and extended properties, put back
    -- once the view is created again.
    DECLARE @preserveSql NVARCHAR(MAX), @ivQualified NVARCHAR(300);

    DECLARE view_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT v.ViewSchema, v.ViewName, v.Definition, v.IndexJson, v.VariantName FROM @Views v;

    OPEN view_cursor;
    FETCH NEXT FROM view_cursor INTO @ivSchema, @ivName, @defn, @indexJson, @variantName;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @needsRecreate = 0;
        SET @needsIndexUpdate = 0;
        SET @preserveSql = NULL;
        SET @ivQualified = QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName);
        SET @existingObjectId = NULL;

        -- Check if view exists
        SELECT @existingObjectId = e.ObjectId, @existingDef = e.CurrentDefinition
        FROM @ExistingViews e
        WHERE e.SchemaName = @ivSchema AND e.ViewName = @ivName;

        IF @existingObjectId IS NOT NULL
        BEGIN
            -- Compare definitions (normalize whitespace, case-insensitive)
            DECLARE @normNew NVARCHAR(MAX) = REPLACE(REPLACE(REPLACE(LOWER(LTRIM(RTRIM(@defn))), CHAR(13), ''), CHAR(10), ' '), '  ', ' ');
            -- Extract SELECT from existing full definition
            DECLARE @existingSelect NVARCHAR(MAX);
            DECLARE @bindPos INT = CHARINDEX('SCHEMABINDING', UPPER(@existingDef));
            IF @bindPos > 0
            BEGIN
                DECLARE @afterBind NVARCHAR(MAX) = SUBSTRING(@existingDef, @bindPos + 13, LEN(@existingDef));
                DECLARE @asP INT = PATINDEX('%[^A-Za-z_]AS[^A-Za-z_]%', @afterBind);
                IF @asP > 0
                    SET @existingSelect = LTRIM(RTRIM(SUBSTRING(@afterBind, @asP + 3, LEN(@afterBind))));
                ELSE
                    SET @existingSelect = @existingDef;
            END
            ELSE
                SET @existingSelect = @existingDef;

            DECLARE @normExisting NVARCHAR(MAX) = REPLACE(REPLACE(REPLACE(LOWER(LTRIM(RTRIM(@existingSelect))), CHAR(13), ''), CHAR(10), ' '), '  ', ' ');

            IF @normNew != @normExisting
            BEGIN
                SET @needsRecreate = 1;
                -- Drop existing: nonclustered → clustered → view
                SET @msg = N'Definition changed for indexed view ' + @ivSchema + N'.' + @ivName + N' — dropping for recreation';
                EXEC [SchemaSmith].[PrintWithNoWait] @msg;

                SELECT @ncDropSql = STRING_AGG('DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName), '; ')
                FROM sys.indexes i WHERE i.object_id = @existingObjectId AND i.type > 1;
                IF @ncDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @ncDropSql;

                SELECT @clDropSql = 'DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName)
                FROM sys.indexes i WHERE i.object_id = @existingObjectId AND i.type = 1;
                IF @clDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @clDropSql;

                EXEC SchemaSmith.CapturePermissions @Schema = @ivSchema, @Name = @ivName, @Script = @preserveSql OUTPUT;
                SET @sql = 'DROP VIEW ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName);
                IF @WhatIf = 0 EXEC sp_executesql @sql;
            END
            ELSE
                SET @needsIndexUpdate = 1; -- Definition unchanged, check indexes
        END
        ELSE
            SET @needsRecreate = 1; -- New view

        -- Create view if needed
        IF @needsRecreate = 1
        BEGIN
            SET @sql = 'CREATE VIEW ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName) + ' WITH SCHEMABINDING AS ' + @defn;
            SET @msg = N'Creating indexed view ' + @ivSchema + N'.' + @ivName + CASE WHEN ISNULL(@variantName, N'') <> N'' THEN N' (variant: ' + @variantName + N')' ELSE N'' END;
            EXEC [SchemaSmith].[PrintWithNoWait] @msg;
            IF @WhatIf = 0 EXEC sp_executesql @sql;

            -- Tag ownership
            IF @WhatIf = 0
            BEGIN
                DECLARE @newObjectId INT = OBJECT_ID(QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName));
                IF @newObjectId IS NOT NULL AND NOT EXISTS (
                    SELECT 1 FROM sys.extended_properties WHERE major_id = @newObjectId AND minor_id = 0 AND name = 'SchemaSmith_Product')
                    EXEC sp_addextendedproperty 'SchemaSmith_Product', @ProductName, 'SCHEMA', @ivSchema, 'VIEW', @ivName;
            END;
            EXEC SchemaSmith.ReapplyPermissions @Script = @preserveSql, @ObjectType = 'view', @ObjectName = @ivQualified, @WhatIf = @WhatIf;
        END;

        -- Create/update indexes
        IF @needsRecreate = 1 OR @needsIndexUpdate = 1
        BEGIN
            -- For index updates on existing views, diff individual indexes
            IF @needsIndexUpdate = 1 AND @existingObjectId IS NOT NULL
            BEGIN
                -- Build existing index metadata from sys catalog
                DECLARE @ExistingIdx TABLE (
                    Name NVARCHAR(200),
                    IsUnique BIT,
                    IsClustered BIT,
                    IndexColumns NVARCHAR(MAX),
                    IncludeColumns NVARCHAR(MAX),
                    CompressionType NVARCHAR(200),
                    [FillFactor] INT,
                    PadIndex BIT
                );

                DELETE FROM @ExistingIdx;
                INSERT INTO @ExistingIdx (Name, IsUnique, IsClustered, IndexColumns, IncludeColumns, CompressionType, [FillFactor], PadIndex)
                SELECT
                    i.name,
                    i.is_unique,
                    CAST(CASE WHEN i.type = 1 THEN 1 ELSE 0 END AS BIT),
                    STRING_AGG(
                        CASE WHEN ic.is_included_column = 0
                            THEN QUOTENAME(c.name) + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                            ELSE NULL END, ','
                    ) WITHIN GROUP (ORDER BY ic.key_ordinal),
                    (SELECT STRING_AGG(QUOTENAME(c2.name), ',') WITHIN GROUP (ORDER BY ic2.index_column_id)
                       FROM sys.index_columns ic2
                      INNER JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
                      WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 1),
                    ISNULL(p.data_compression_desc, 'NONE'),
                    i.fill_factor,
                    i.is_padded
                FROM sys.indexes i
                INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                LEFT JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id AND p.partition_number = 1
                WHERE i.object_id = @existingObjectId AND i.type > 0
                GROUP BY i.name, i.index_id, i.is_unique, i.type, i.fill_factor, i.is_padded, p.data_compression_desc, i.object_id;

                -- Parse desired indexes from JSON
                DECLARE @DesiredIdx TABLE (
                    Name NVARCHAR(200),
                    IsUnique BIT,
                    IsClustered BIT,
                    IndexColumns NVARCHAR(MAX),
                    IncludeColumns NVARCHAR(MAX),
                    CompressionType NVARCHAR(200),
                    [FillFactor] INT,
                    PadIndex BIT
                );

                DELETE FROM @DesiredIdx;
                INSERT INTO @DesiredIdx (Name, IsUnique, IsClustered, IndexColumns, IncludeColumns, CompressionType, [FillFactor], PadIndex)
                SELECT
                    [SchemaSmith].[fn_StripBracketWrapping](JSON_VALUE(idx.value, '$.Name')),
                    CAST(ISNULL(JSON_VALUE(idx.value, '$.Unique'), 'false') AS BIT),
                    CAST(ISNULL(JSON_VALUE(idx.value, '$.Clustered'), 'false') AS BIT),
                    REPLACE(JSON_VALUE(idx.value, '$.IndexColumns'), ', ', ','),
                    REPLACE(ISNULL(JSON_VALUE(idx.value, '$.IncludeColumns'), ''), ', ', ','),
                    ISNULL(JSON_VALUE(idx.value, '$.CompressionType'), 'NONE'),
                    CAST(ISNULL(JSON_VALUE(idx.value, '$.FillFactor'), '0') AS INT),
                    CAST(ISNULL(JSON_VALUE(idx.value, '$.PadIndex'), 'false') AS BIT)
                FROM OPENJSON(@indexJson) idx;

                -- Determine if the clustered index needs changing
                -- If so, ALL nonclustered indexes must be dropped first (SQL Server requirement)
                DECLARE @clusteredNeedsChange BIT = 0;

                -- Clustered changed or removed
                IF EXISTS (
                    SELECT 1 FROM @ExistingIdx e
                    WHERE e.IsClustered = 1
                    AND (NOT EXISTS (SELECT 1 FROM @DesiredIdx d WHERE d.Name = e.Name AND d.IsClustered = 1)
                         OR EXISTS (SELECT 1 FROM @DesiredIdx d WHERE d.Name = e.Name AND d.IsClustered = 1
                                    AND (d.IsUnique != e.IsUnique OR d.IndexColumns != e.IndexColumns
                                         OR ISNULL(d.IncludeColumns, '') != ISNULL(e.IncludeColumns, '')
                                         OR d.CompressionType != e.CompressionType
                                         OR (@UpdateFillFactor = 1 AND d.[FillFactor] != e.[FillFactor])
                                         OR ISNULL(d.PadIndex, 0) != ISNULL(e.PadIndex, 0))))
                )
                    SET @clusteredNeedsChange = 1;

                -- New clustered index where none existed
                IF NOT EXISTS (SELECT 1 FROM @ExistingIdx WHERE IsClustered = 1)
                   AND EXISTS (SELECT 1 FROM @DesiredIdx WHERE IsClustered = 1)
                    SET @clusteredNeedsChange = 1;

                IF @clusteredNeedsChange = 1
                BEGIN
                    -- Clustered index changing — must drop all indexes (nonclustered → clustered)
                    SET @msg = N'Clustered index changed on ' + @ivSchema + N'.' + @ivName + N' — dropping all indexes for recreation';
                    EXEC [SchemaSmith].[PrintWithNoWait] @msg;

                    SET @ncDropSql = NULL;
                    SELECT @ncDropSql = STRING_AGG('DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName), '; ')
                    FROM sys.indexes i WHERE i.object_id = @existingObjectId AND i.type > 1;
                    IF @ncDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @ncDropSql;

                    SET @clDropSql = NULL;
                    SELECT @clDropSql = 'DROP INDEX ' + QUOTENAME(i.name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName)
                    FROM sys.indexes i WHERE i.object_id = @existingObjectId AND i.type = 1;
                    IF @clDropSql IS NOT NULL AND @WhatIf = 0 EXEC sp_executesql @clDropSql;
                END
                ELSE
                BEGIN
                    -- Clustered index unchanged — drop only changed and removed nonclustered indexes
                    DECLARE @dropSql NVARCHAR(MAX);

                    -- Drop changed nonclustered indexes (properties differ from spec)
                    SET @dropSql = NULL;
                    SELECT @dropSql = STRING_AGG(
                        'DROP INDEX ' + QUOTENAME(e.Name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName), '; ')
                    FROM @ExistingIdx e
                    INNER JOIN @DesiredIdx d ON d.Name = e.Name
                    WHERE e.IsClustered = 0
                    AND (d.IsUnique != e.IsUnique OR d.IsClustered != e.IsClustered
                         OR d.IndexColumns != e.IndexColumns
                         OR ISNULL(d.IncludeColumns, '') != ISNULL(e.IncludeColumns, '')
                         OR d.CompressionType != e.CompressionType
                         OR (@UpdateFillFactor = 1 AND d.[FillFactor] != e.[FillFactor])
                                         OR ISNULL(d.PadIndex, 0) != ISNULL(e.PadIndex, 0));
                    IF @dropSql IS NOT NULL
                    BEGIN
                        SET @msg = N'Dropping changed nonclustered indexes on ' + @ivSchema + N'.' + @ivName;
                        EXEC [SchemaSmith].[PrintWithNoWait] @msg;
                        IF @WhatIf = 0 EXEC sp_executesql @dropSql;
                    END;

                    -- Drop removed nonclustered indexes (in DB but not in spec)
                    SET @dropSql = NULL;
                    SELECT @dropSql = STRING_AGG(
                        'DROP INDEX ' + QUOTENAME(e.Name) + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName), '; ')
                    FROM @ExistingIdx e
                    WHERE e.IsClustered = 0
                    AND NOT EXISTS (SELECT 1 FROM @DesiredIdx d WHERE d.Name = e.Name);
                    IF @dropSql IS NOT NULL
                    BEGIN
                        SET @msg = N'Dropping removed nonclustered indexes on ' + @ivSchema + N'.' + @ivName;
                        EXEC [SchemaSmith].[PrintWithNoWait] @msg;
                        IF @WhatIf = 0 EXEC sp_executesql @dropSql;
                    END;
                END;
            END;

            -- Create missing indexes (clustered first, then nonclustered)
            -- For new views: all indexes are missing
            -- For index updates: only dropped/new indexes are missing (unchanged indexes still exist and are skipped)
            -- Create missing indexes (clustered first, then nonclustered) in one batch.
            -- Already-existing indexes are skipped via the NOT EXISTS filter (unchanged during an
            -- index-only update). FillFactor is emitted whenever > 0, matching the per-index form.
            DECLARE @createIdxSql NVARCHAR(MAX);
            SELECT @createIdxSql = STRING_AGG(CAST(
                    'CREATE '
                    + CASE WHEN v.IsUnique = 1 THEN 'UNIQUE ' ELSE '' END
                    + CASE WHEN v.IsClustered = 1 THEN 'CLUSTERED ' ELSE '' END
                    + 'INDEX ' + QUOTENAME(v.IdxName)
                    + ' ON ' + QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName)
                    + ' (' + v.IdxColumns + ')'
                    + CASE WHEN v.IdxInclude IS NOT NULL THEN ' INCLUDE (' + v.IdxInclude + ')' ELSE '' END
                    + CASE WHEN w.WithOptions <> '' THEN ' WITH (' + STUFF(w.WithOptions, 1, 2, '') + ')' ELSE '' END
                  AS NVARCHAR(MAX)), '; ' + CHAR(13) + CHAR(10))
                  WITHIN GROUP (ORDER BY CASE WHEN v.IsClustered = 1 THEN 0 ELSE 1 END)
            FROM OPENJSON(@indexJson) idx
            CROSS APPLY (VALUES (
                [SchemaSmith].[fn_StripBracketWrapping](JSON_VALUE(idx.value, '$.Name')),
                CAST(ISNULL(JSON_VALUE(idx.value, '$.Unique'), 'false') AS BIT),
                CAST(ISNULL(JSON_VALUE(idx.value, '$.Clustered'), 'false') AS BIT),
                JSON_VALUE(idx.value, '$.IndexColumns'),
                JSON_VALUE(idx.value, '$.IncludeColumns'),
                ISNULL(JSON_VALUE(idx.value, '$.CompressionType'), 'NONE'),
                CAST(ISNULL(JSON_VALUE(idx.value, '$.FillFactor'), '0') AS INT),
                    CAST(ISNULL(JSON_VALUE(idx.value, '$.PadIndex'), 'false') AS BIT)
            )) AS v(IdxName, IsUnique, IsClustered, IdxColumns, IdxInclude, Compression, FillFactorV, PadIndexV)
            CROSS APPLY (VALUES (
                -- One WITH clause from an option list rather than a branch per combination:
                -- three options would need eight branches, and the next one sixteen.
                CASE WHEN v.Compression <> 'NONE' THEN ', DATA_COMPRESSION = ' + v.Compression ELSE '' END
              + CASE WHEN v.FillFactorV > 0 THEN ', FILLFACTOR = ' + CAST(v.FillFactorV AS NVARCHAR(10)) ELSE '' END
              + CASE WHEN v.PadIndexV = 1 THEN ', PAD_INDEX = ON' ELSE '' END
            )) AS w(WithOptions)
            WHERE NOT EXISTS (
                SELECT 1 FROM sys.indexes si
                WHERE si.object_id = OBJECT_ID(QUOTENAME(@ivSchema) + '.' + QUOTENAME(@ivName))
                AND si.name = v.IdxName
            );

            IF @createIdxSql IS NOT NULL
            BEGIN
                EXEC [SchemaSmith].[PrintWithNoWait] @createIdxSql;
                IF @WhatIf = 0 EXEC sp_executesql @createIdxSql;
            END;
        END;

        FETCH NEXT FROM view_cursor INTO @ivSchema, @ivName, @defn, @indexJson, @variantName;
    END;
    CLOSE view_cursor;
    DEALLOCATE view_cursor;
END;
