-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ExpressionMapAdopt') IS NOT NULL
  DROP PROCEDURE SchemaSmith.ExpressionMapAdopt
GO

-- #508. The expression map only knows what SchemaSmith applied, so a database another tool built -- or a declaration
-- reworded without changing its meaning -- has every expression whose stored text differs from the declared text
-- re-applied once: a CHECK re-validated under a schema lock, a filtered unique index dropped and rebuilt, a computed
-- column dropped and re-added. Before that happens, this asks SQL Server itself: the DECLARED expression is applied to
-- an empty copy of the table, and if the engine stores it as exactly the live object's text, the live object IS that
-- expression. The map row the recorder would write is written now, and the compare passes that follow leave it alone.
--
-- Anything uncertain falls back to applying, as before: a different rendering (real drift stays drift), a probe error.
-- It runs before any table or column is created, so only objects that already exist are candidates. It can only ever
-- apply less.
--
-- The copy is in the table's own database and schema, not tempdb: tempdb renders at its own compatibility level and
-- resolves names there, so a database below the server's level (CONVERT renders differently at 100) or an expression
-- calling the database's own function renders wrongly or not at all. Each probe is inside a transaction that is always
-- rolled back, so nothing is left behind -- which is also why it runs under WhatIf. Under WhatIf the rows it writes
-- are put back by ExpressionMapRecord at the end of the run (#ExpressionMapAdoptUndo, created by the caller), so a
-- preview shows what the real deploy would do and leaves the map as it found it.
CREATE PROCEDURE SchemaSmith.ExpressionMapAdopt
  @WhatIf BIT = 0
AS
BEGIN TRY
  SET NOCOUNT ON
  -- A caller's open transaction cannot be rolled back for a probe without rolling back the caller's work.
  IF @@TRANCOUNT > 0 RETURN
  IF OBJECT_ID('tempdb..#Columns') IS NULL AND OBJECT_ID('tempdb..#CheckConstraints') IS NULL
     AND OBJECT_ID('tempdb..#Indexes') IS NULL AND OBJECT_ID('tempdb..#Statistics') IS NULL RETURN

  DECLARE @v_Version VARCHAR(50) = CONVERT(VARCHAR(50), SERVERPROPERTY('ProductVersion'))
  DECLARE @v_Compat INT = (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID())

  IF OBJECT_ID('tempdb..#ExpressionMapCandidates') IS NOT NULL DROP TABLE #ExpressionMapCandidates
  CREATE TABLE #ExpressionMapCandidates (
    [Id] INT IDENTITY(1, 1) PRIMARY KEY,
    ObjectSchema NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL, ObjectTable NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL,
    ObjectKind VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL, ObjectName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL,
    Slot VARCHAR(32) COLLATE DATABASE_DEFAULT NOT NULL,
    AuthoredText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NOT NULL, LiveText NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NOT NULL,
    -- The DDL the engine would run, against the copy: {probe} is replaced by the copy's name.
    Render NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NOT NULL)

  -- Each block takes the live text exactly as ExpressionMapRecord reads it, and only where the compare that follows
  -- would apply: no row vouches for it, and the caller's own text test calls it different.

  -- Table-level check constraints.
  IF OBJECT_ID('tempdb..#CheckConstraints') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(cc.[Schema]), SchemaSmith.fn_StripBracketWrapping(cc.[TableName]),
           'CHECK', ck.[name], 'expression', cc.[Expression], ck.[definition],
           'ALTER TABLE {probe} ADD CHECK (' + cc.[Expression] + ')'
      FROM #CheckConstraints cc WITH (NOLOCK)
      JOIN sys.check_constraints ck
        ON ck.parent_object_id = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
       AND ck.[name] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName])
     WHERE RTRIM(ISNULL(cc.[Expression], '')) <> ''
       AND SchemaSmith.fn_NormalizeCheckExpression(ck.[definition]) <> SchemaSmith.fn_NormalizeCheckExpression(cc.[Expression])
       AND SchemaSmith.fn_ExpressionMapUnchanged(cc.[Schema], cc.[TableName], 'CHECK', ck.[name], 'expression', cc.[Expression], ck.[definition]) = 0

  -- Column-level check constraints, attributed to their column as the recorder does.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'CHECK', ck.[name], 'expression', c.[CheckExpression], ck.[definition],
           'ALTER TABLE {probe} ADD CHECK (' + c.[CheckExpression] + ')'
      FROM #Columns c WITH (NOLOCK)
      JOIN sys.check_constraints ck
        ON ck.parent_object_id = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
       AND ck.parent_column_id <> 0
       AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[CheckExpression], '')) <> ''
       AND SchemaSmith.fn_NormalizeCheckExpression(ck.[definition]) <> SchemaSmith.fn_NormalizeCheckExpression(c.[CheckExpression])
       AND SchemaSmith.fn_ExpressionMapUnchanged(c.[Schema], c.[TableName], 'CHECK', ck.[name], 'expression', c.[CheckExpression], ck.[definition]) = 0
       AND NOT EXISTS (SELECT 1 FROM #ExpressionMapCandidates d
                        WHERE d.ObjectName = ck.[name] COLLATE DATABASE_DEFAULT AND d.ObjectTable = SchemaSmith.fn_StripBracketWrapping(c.[TableName]))

  -- Computed columns. Added under another name: the copy already has the column, as a plain one.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'COLUMN', SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'computed', c.[ComputedExpression], comp.[definition],
           'ALTER TABLE {probe} ADD [SchemaSmith_Probe_Column] AS (' + c.[ComputedExpression] + ')'
      FROM #Columns c WITH (NOLOCK)
      JOIN sys.computed_columns comp
        ON comp.[object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
       AND comp.[name] = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[ComputedExpression], '')) <> ''
       AND ISNULL(SchemaSmith.fn_StripParenWrapping(comp.[definition]), '') <> c.[ComputedExpression]
       AND SchemaSmith.fn_ExpressionMapUnchanged(c.[Schema], c.[TableName], 'COLUMN', c.[ColumnName], 'computed', c.[ComputedExpression], comp.[definition]) = 0

  -- Column defaults.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'COLUMN', SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'default', c.[Default], ic.COLUMN_DEFAULT,
           'ALTER TABLE {probe} ADD DEFAULT ' + c.[Default] + ' FOR ' + c.[ColumnName]
      FROM #Columns c WITH (NOLOCK)
      JOIN INFORMATION_SCHEMA.COLUMNS ic
        ON ic.TABLE_SCHEMA = SchemaSmith.fn_StripBracketWrapping(c.[Schema])
       AND ic.TABLE_NAME = SchemaSmith.fn_StripBracketWrapping(c.[TableName])
       AND ic.COLUMN_NAME = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[Default], '')) <> '' AND ic.COLUMN_DEFAULT IS NOT NULL
       AND SchemaSmith.fn_StripParenWrapping(ic.COLUMN_DEFAULT) <> c.[Default]
       AND SchemaSmith.fn_ExpressionMapUnchanged(c.[Schema], c.[TableName], 'COLUMN', c.[ColumnName], 'default', c.[Default], ic.COLUMN_DEFAULT) = 0

  -- Index filters. The key is the declared one; a filtered index is always nonclustered.
  IF OBJECT_ID('tempdb..#Indexes') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(i.[Schema]), SchemaSmith.fn_StripBracketWrapping(i.[TableName]),
           'INDEX', si.[name], 'filter', i.[FilterExpression], SchemaSmith.fn_StripParenWrapping(si.filter_definition),
           'CREATE NONCLUSTERED ' + CASE WHEN ISNULL(i.[ColumnStore], 0) = 1 THEN 'COLUMNSTORE ' ELSE '' END +
           'INDEX [SchemaSmith_Probe_Index] ON {probe} (' + i.[IndexColumns] + ') WHERE ' + i.[FilterExpression]
      FROM #Indexes i WITH (NOLOCK)
      JOIN sys.indexes si
        ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
       AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
     WHERE RTRIM(ISNULL(i.[FilterExpression], '')) <> '' AND si.has_filter = 1
       AND SchemaSmith.fn_StripParenWrapping(si.filter_definition) <> i.[FilterExpression]
       AND SchemaSmith.fn_ExpressionMapUnchanged(i.[Schema], i.[TableName], 'INDEX', i.[IndexName], 'filter', i.[FilterExpression], SchemaSmith.fn_StripParenWrapping(si.filter_definition)) = 0

  -- Filtered statistics.
  IF OBJECT_ID('tempdb..#Statistics') IS NOT NULL
  INSERT #ExpressionMapCandidates (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render)
    SELECT SchemaSmith.fn_StripBracketWrapping(s.[Schema]), SchemaSmith.fn_StripBracketWrapping(s.[TableName]),
           'STATISTIC', st.[name], 'filter', s.[FilterExpression], SchemaSmith.fn_StripParenWrapping(st.filter_definition),
           'CREATE STATISTICS [SchemaSmith_Probe_Statistic] ON {probe} (' + s.[Columns] + ') WHERE ' + s.[FilterExpression]
      FROM #Statistics s WITH (NOLOCK)
      JOIN sys.stats st
        ON st.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName])
       AND st.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName])
     WHERE RTRIM(ISNULL(s.[FilterExpression], '')) <> '' AND st.has_filter = 1
       AND SchemaSmith.fn_StripParenWrapping(st.filter_definition) <> s.[FilterExpression]
       AND SchemaSmith.fn_ExpressionMapUnchanged(s.[Schema], s.[TableName], 'STATISTIC', s.[StatisticName], 'filter', s.[FilterExpression], SchemaSmith.fn_StripParenWrapping(st.filter_definition)) = 0

  IF NOT EXISTS (SELECT 1 FROM #ExpressionMapCandidates) RETURN

  DECLARE @v_Id INT = 0, @v_Schema NVARCHAR(256), @v_Table NVARCHAR(256), @v_Kind VARCHAR(32), @v_Name NVARCHAR(256),
          @v_Slot VARCHAR(32), @v_Authored NVARCHAR(MAX), @v_Live NVARCHAR(MAX), @v_Render NVARCHAR(MAX),
          @v_Probe NVARCHAR(600), @v_Copy NVARCHAR(MAX), @v_Rendered NVARCHAR(MAX), @v_Adopted INT = 0
  WHILE 1 = 1
  BEGIN
    SELECT TOP (1) @v_Id = [Id], @v_Schema = ObjectSchema, @v_Table = ObjectTable, @v_Kind = ObjectKind, @v_Name = ObjectName,
                   @v_Slot = Slot, @v_Authored = AuthoredText, @v_Live = LiveText, @v_Render = Render
      FROM #ExpressionMapCandidates WHERE [Id] > @v_Id ORDER BY [Id]
    IF @@ROWCOUNT = 0 BREAK

    SET @v_Probe = QUOTENAME(@v_Schema) + '.' + QUOTENAME('SchemaSmith_Probe_' + CAST(@@SPID AS VARCHAR(10)))
    -- Named columns rather than *: a column set would stand in for its sparse columns, and an expression naming one
    -- of them would not bind on the copy.
    SET @v_Copy = 'SELECT TOP (0) ' +
                  STUFF((SELECT ', ' + QUOTENAME(sc.[name])
                           FROM sys.columns sc
                          WHERE sc.[object_id] = OBJECT_ID(QUOTENAME(@v_Schema) + '.' + QUOTENAME(@v_Table)) AND sc.is_column_set = 0
                          ORDER BY sc.column_id
                            FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') +
                  ' INTO ' + @v_Probe + ' FROM ' + QUOTENAME(@v_Schema) + '.' + QUOTENAME(@v_Table)
    SET @v_Rendered = NULL
    BEGIN TRANSACTION
    BEGIN TRY
      EXEC(@v_Copy)
      SET @v_Render = REPLACE(@v_Render, '{probe}', @v_Probe)
      EXEC(@v_Render)
      IF @v_Kind = 'CHECK'
        SELECT @v_Rendered = [definition] FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID(@v_Probe)
      ELSE IF @v_Slot = 'computed'
        SELECT @v_Rendered = [definition] FROM sys.computed_columns WHERE [object_id] = OBJECT_ID(@v_Probe)
      ELSE IF @v_Slot = 'default'
        SELECT @v_Rendered = [definition] FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID(@v_Probe)
      ELSE IF @v_Kind = 'INDEX'
        SELECT @v_Rendered = SchemaSmith.fn_StripParenWrapping(filter_definition) FROM sys.indexes WHERE [object_id] = OBJECT_ID(@v_Probe) AND has_filter = 1
      ELSE IF @v_Kind = 'STATISTIC'
        SELECT @v_Rendered = SchemaSmith.fn_StripParenWrapping(filter_definition) FROM sys.stats WHERE [object_id] = OBJECT_ID(@v_Probe) AND has_filter = 1
    END TRY
    BEGIN CATCH
      SET @v_Rendered = NULL
    END CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION

    -- Byte for byte: a binary collation, and the length too, because = ignores trailing spaces.
    IF @v_Rendered COLLATE Latin1_General_BIN2 = @v_Live COLLATE Latin1_General_BIN2 AND DATALENGTH(@v_Rendered) = DATALENGTH(@v_Live)
    BEGIN
      -- No way to put it back means a preview does not write. Nested rather than ANDed: SQL Server binds the whole
      -- condition, and the undo table exists only under a caller that made one.
      IF @WhatIf = 1
      BEGIN
        IF OBJECT_ID('tempdb..#ExpressionMapAdoptUndo') IS NULL CONTINUE
        IF NOT EXISTS (SELECT 1 FROM #ExpressionMapAdoptUndo u WHERE u.ObjectSchema = @v_Schema AND u.ObjectTable = @v_Table
                          AND u.ObjectKind = @v_Kind AND u.ObjectName = @v_Name AND u.Slot = @v_Slot)
          INSERT #ExpressionMapAdoptUndo (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, WasPresent,
                                          AuthoredText, CanonicalText, PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
            SELECT @v_Schema, @v_Table, @v_Kind, @v_Name, @v_Slot, CASE WHEN m.[Slot] IS NULL THEN 0 ELSE 1 END,
                   m.[AuthoredText], m.[CanonicalText], m.[PlatformName], m.[EngineVersion], m.[CompatLevel], m.[UpdatedUtc]
              FROM (SELECT 1 AS x) one
              LEFT JOIN SchemaSmith.ExpressionMap m
                ON m.[ObjectSchema] = @v_Schema AND m.[ObjectTable] = @v_Table AND m.[ObjectKind] = @v_Kind
               AND m.[ObjectName] = @v_Name AND m.[Slot] = @v_Slot
      END

      UPDATE SchemaSmith.ExpressionMap
         SET [AuthoredText] = @v_Authored, [CanonicalText] = @v_Live, [PlatformName] = 'SqlServer',
             [EngineVersion] = @v_Version, [CompatLevel] = @v_Compat, [UpdatedUtc] = SYSUTCDATETIME()
       WHERE [ObjectSchema] = @v_Schema AND [ObjectTable] = @v_Table AND [ObjectKind] = @v_Kind
         AND [ObjectName] = @v_Name AND [Slot] = @v_Slot
      IF @@ROWCOUNT = 0
        INSERT SchemaSmith.ExpressionMap ([ObjectSchema], [ObjectTable], [ObjectKind], [ObjectName], [Slot], [AuthoredText],
                                          [CanonicalText], [PlatformName], [EngineVersion], [CompatLevel], [UpdatedUtc])
          VALUES (@v_Schema, @v_Table, @v_Kind, @v_Name, @v_Slot, @v_Authored, @v_Live, 'SqlServer', @v_Version, @v_Compat, SYSUTCDATETIME())
      SET @v_Adopted = @v_Adopted + 1
    END
  END

  IF @v_Adopted > 0
    RAISERROR('  Verified %d expression(s) on a scratch copy of their table: the declared text renders to exactly what the database holds, so they are recorded rather than re-applied.', 10, 100, @v_Adopted) WITH NOWAIT

  DROP TABLE #ExpressionMapCandidates
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  IF XACT_STATE() <> 0 ROLLBACK TRANSACTION
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
GO
