-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ExpressionMapRecord') IS NOT NULL
  DROP PROCEDURE SchemaSmith.ExpressionMapRecord
GO

-- #242. Records what was applied, for every expression-bearing object this run declared: the authored text as
-- the package wrote it, the canonical text the engine reports NOW (immediately after the apply passes), and the
-- context that decides canonicalisation. SchemaSmith.fn_ExpressionMapUnchanged reads it on the next deploy.
--
-- Runs LAST, after the modify passes and after MissingIndexesAndConstraintsQuench has created anything missing,
-- so a constraint created moments ago is recorded on the same run rather than churning once more on the next.
--
-- Re-baselining is the same statement: a row whose engine version or compat level no longer matches is
-- overwritten here with the current reading, without touching the object. That is the whole of the
-- context-change story -- the decision function already declined to re-apply.
--
-- Reads the caller's #Tables / #Columns / #CheckConstraints, the same contract every other quench proc has.
-- Never runs under WhatIf: a preview must not write.
CREATE PROCEDURE SchemaSmith.ExpressionMapRecord
  @WhatIf BIT = 0
AS
BEGIN TRY
  SET NOCOUNT ON
  IF @WhatIf = 1 RETURN

  -- The parse-produced temp tables are not always in scope: an index-only quench and a template with no tables
  -- never build them. Each read below is guarded, because a statement SQL Server never executes is never bound
  -- to a missing temp table -- and an unguarded read fails the whole deploy with "Invalid object name".
  IF OBJECT_ID('tempdb..#Columns') IS NULL AND OBJECT_ID('tempdb..#CheckConstraints') IS NULL
     AND OBJECT_ID('tempdb..#Indexes') IS NULL AND OBJECT_ID('tempdb..#Statistics') IS NULL RETURN

  DECLARE @v_Version VARCHAR(50) = CONVERT(VARCHAR(50), SERVERPROPERTY('ProductVersion'))
  -- sys.databases, not DATABASEPROPERTYEX(..., 'CompatibilityLevel'): that property returns NULL before SQL Server
  -- 2017, so on 2008 R2 through 2016 no compatibility level was ever recorded and a compatibility change was
  -- never seen as a context change -- measured on genuine 2012, 2014 and 2016 instances.
  DECLARE @v_Compat INT = (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID())

  IF OBJECT_ID('tempdb..#ExpressionMapDeclared') IS NOT NULL DROP TABLE #ExpressionMapDeclared
  CREATE TABLE #ExpressionMapDeclared (
    ObjectSchema NVARCHAR(256) NOT NULL, ObjectTable NVARCHAR(256) NOT NULL, ObjectKind VARCHAR(32) NOT NULL,
    ObjectName NVARCHAR(256) NOT NULL, Slot VARCHAR(32) NOT NULL,
    AuthoredText NVARCHAR(MAX) NOT NULL, CanonicalText NVARCHAR(MAX) NOT NULL)

  -- Table-level check constraints.
  IF OBJECT_ID('tempdb..#CheckConstraints') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(cc.[Schema]), SchemaSmith.fn_StripBracketWrapping(cc.[TableName]),
           'CHECK', SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]), 'expression',
           cc.[Expression], ck.[definition]
      FROM #CheckConstraints cc WITH (NOLOCK)
      JOIN sys.check_constraints ck
        ON ck.parent_object_id = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
       AND ck.[name] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName])
     WHERE RTRIM(ISNULL(cc.[Expression], '')) <> ''

  -- Column-level check constraints. SQL Server attributes these to their column (parent_column_id), which is
  -- why they round-trip here and not on MySQL.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'CHECK', ck.[name], 'expression',
           c.[CheckExpression], ck.[definition]
      FROM #Columns c WITH (NOLOCK)
      JOIN sys.check_constraints ck
        ON ck.parent_object_id = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
       AND ck.parent_column_id <> 0
       AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[CheckExpression], '')) <> ''
       AND NOT EXISTS (SELECT 1 FROM #ExpressionMapDeclared d
                        WHERE d.ObjectName = ck.[name]
                          AND d.ObjectTable = SchemaSmith.fn_StripBracketWrapping(c.[TableName]))

  -- Computed columns.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'COLUMN', SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'computed',
           c.[ComputedExpression], comp.[definition]
      FROM #Columns c WITH (NOLOCK)
      JOIN sys.computed_columns comp
        ON comp.[object_id] = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
       AND comp.[name] = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[ComputedExpression], '')) <> ''

  -- Index filter expressions.
  IF OBJECT_ID('tempdb..#Indexes') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(i.[Schema]), SchemaSmith.fn_StripBracketWrapping(i.[TableName]),
           'INDEX', SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'filter',
           i.[FilterExpression], ISNULL(SchemaSmith.fn_StripParenWrapping(si.filter_definition), '')
      FROM #Indexes i WITH (NOLOCK)
      JOIN sys.indexes si
        ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
       AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
     WHERE RTRIM(ISNULL(i.[FilterExpression], '')) <> ''

  -- Column defaults. The catalog reports the stored (reframed) form; that is what the next deploy compares.
  IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
           'COLUMN', SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), 'default',
           c.[Default], ISNULL(ic.COLUMN_DEFAULT, '')
      FROM #Columns c WITH (NOLOCK)
      JOIN INFORMATION_SCHEMA.COLUMNS ic
        ON ic.TABLE_SCHEMA = SchemaSmith.fn_StripBracketWrapping(c.[Schema])
       AND ic.TABLE_NAME = SchemaSmith.fn_StripBracketWrapping(c.[TableName])
       AND ic.COLUMN_NAME = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
     WHERE RTRIM(ISNULL(c.[Default], '')) <> ''

  -- Filtered statistics.
  IF OBJECT_ID('tempdb..#Statistics') IS NOT NULL
  INSERT #ExpressionMapDeclared (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText)
    SELECT SchemaSmith.fn_StripBracketWrapping(s.[Schema]), SchemaSmith.fn_StripBracketWrapping(s.[TableName]),
           'STATISTIC', SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]), 'filter',
           s.[FilterExpression], ISNULL(SchemaSmith.fn_StripParenWrapping(st.filter_definition), '')
      FROM #Statistics s WITH (NOLOCK)
      JOIN sys.stats st
        ON st.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName])
       AND st.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName])
     WHERE RTRIM(ISNULL(s.[FilterExpression], '')) <> ''

  -- Say so when a re-baseline happens (Paul, 2026-09-08: "re-baseline, and say so in the log -- log the count so
  -- it is visible rather than silent"). A re-baseline is a row whose DECLARATION is unchanged but whose engine
  -- context moved -- an engine upgrade or a compatibility-level change. A row whose declaration also changed was
  -- APPLIED, not re-baselined, and is not counted.
  DECLARE @v_Rebaselined INT =
    (SELECT COUNT(*)
       FROM #ExpressionMapDeclared d
       JOIN SchemaSmith.ExpressionMap m WITH (NOLOCK)
         ON m.[ObjectSchema] = d.ObjectSchema AND m.[ObjectTable] = d.ObjectTable AND m.[ObjectKind] = d.ObjectKind
        AND m.[ObjectName] = d.ObjectName AND m.[Slot] = d.Slot
      WHERE m.[AuthoredText] = d.AuthoredText
        AND (m.[EngineVersion] <> @v_Version OR ISNULL(m.[CompatLevel], -1) <> ISNULL(@v_Compat, -1)))
  IF @v_Rebaselined > 0
    RAISERROR('  Re-baselined %d recorded expression(s): they were recorded under a different SQL Server version or compatibility level, and their stored canonical text is now refreshed for version %s at compatibility level %d. No object was changed.', 10, 100, @v_Rebaselined, @v_Version, @v_Compat) WITH NOWAIT

  MERGE SchemaSmith.ExpressionMap AS target
  USING #ExpressionMapDeclared AS source
     ON target.[ObjectSchema] = source.ObjectSchema
    AND target.[ObjectTable] = source.ObjectTable
    AND target.[ObjectKind] = source.ObjectKind
    AND target.[ObjectName] = source.ObjectName
    AND target.[Slot] = source.Slot
  WHEN MATCHED AND (target.[AuthoredText] <> source.AuthoredText
                 OR target.[CanonicalText] <> source.CanonicalText
                 OR target.[EngineVersion] <> @v_Version
                 OR ISNULL(target.[CompatLevel], -1) <> ISNULL(@v_Compat, -1))
    THEN UPDATE SET [AuthoredText] = source.AuthoredText, [CanonicalText] = source.CanonicalText,
                    [PlatformName] = 'SqlServer', [EngineVersion] = @v_Version, [CompatLevel] = @v_Compat,
                    [UpdatedUtc] = SYSUTCDATETIME()
  WHEN NOT MATCHED BY TARGET
    THEN INSERT ([ObjectSchema], [ObjectTable], [ObjectKind], [ObjectName], [Slot], [AuthoredText],
                 [CanonicalText], [PlatformName], [EngineVersion], [CompatLevel], [UpdatedUtc])
         VALUES (source.ObjectSchema, source.ObjectTable, source.ObjectKind, source.ObjectName, source.Slot,
                 source.AuthoredText, source.CanonicalText, 'SqlServer', @v_Version, @v_Compat, SYSUTCDATETIME());

  DROP TABLE #ExpressionMapDeclared
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
GO
