-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.CollectTableScopedExtendedProperties', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.CollectTableScopedExtendedProperties
GO
-- The rows ExtendedPropertyQuench converges on a table, its columns, foreign keys and check constraints, read into the
-- caller's #DeclaredProperties. A procedure of its own so that an index-only caller, whose working set has no #Columns,
-- #ForeignKeys or #CheckConstraints and a #Tables without [Description], never binds these statements: SQL Server
-- resolves a temp table that exists at compile time, and a branch that is not taken still has to compile.
CREATE PROCEDURE SchemaSmith.CollectTableScopedExtendedProperties
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @InternalNames TABLE ([Name] NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL)
  INSERT @InternalNames VALUES (N'ProductName'), (N'PreventDrop'), (N'SchemaSmith_SuspendedHistory')

  INSERT INTO #DeclaredProperties ([ObjectType], [Schema], [Table], [Name], [Declared])
    SELECT 'table', x.[Schema], x.[Table], p.[Name], p.[Declared]
      FROM #Tables t WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(t.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(t.[Name]) AS [Table]) x
      CROSS APPLY (SELECT N'MS_Description' AS [Name], NULLIF(t.[Description], '') AS [Declared] WHERE t.[Description] IS NOT NULL
                   UNION ALL
                   SELECT ep.[Name], NULLIF(ep.[Value], '')
                     FROM #ExtendedProperties ep WITH (NOLOCK)
                    WHERE ep.[ObjectType] = 'table'
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[Schema]) = x.[Schema]
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[TableName]) = x.[Table]
                      AND ep.[Name] <> N'MS_Description'
                      AND ep.[Name] NOT IN (SELECT [Name] FROM @InternalNames)) p
     WHERE OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U') IS NOT NULL
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), 'TableIsMemoryOptimized'), 0) = 0

  INSERT INTO #DeclaredProperties ([ObjectType], [Schema], [Table], [Level2Type], [Level2Name], [Name], [Declared])
    SELECT 'column', x.[Schema], x.[Table], 'COLUMN', x.[Column], p.[Name], p.[Declared]
      FROM #Columns c WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(c.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(c.[TableName]) AS [Table],
                          SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]) AS [Column]) x
      CROSS APPLY (SELECT N'MS_Description' AS [Name], NULLIF(c.[Description], '') AS [Declared] WHERE c.[Description] IS NOT NULL
                   UNION ALL
                   SELECT ep.[Name], NULLIF(ep.[Value], '')
                     FROM #ExtendedProperties ep WITH (NOLOCK)
                    WHERE ep.[ObjectType] = 'column'
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[Schema]) = x.[Schema]
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[TableName]) = x.[Table]
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[ObjectName]) = x.[Column]
                      AND ep.[Name] <> N'MS_Description'
                      AND ep.[Name] NOT IN (SELECT [Name] FROM @InternalNames)) p
     WHERE COL_LENGTH(c.[Schema] + '.' + c.[TableName], x.[Column]) IS NOT NULL
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(c.[Schema] + '.' + c.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0

  -- A foreign key's or check constraint's property sits on the constraint object (class 1, minor 0). Neither
  -- has a Description key of its own, so MS_Description comes from the bag like any other name.
  INSERT INTO #DeclaredProperties ([ObjectType], [Schema], [Table], [Level2Type], [Level2Name], [Name], [Declared])
    SELECT 'foreignKey', x.[Schema], x.[Table], 'CONSTRAINT', x.[Key], ep.[Name], NULLIF(ep.[Value], '')
      FROM #ForeignKeys k WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(k.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(k.[TableName]) AS [Table],
                          SchemaSmith.fn_StripBracketWrapping(k.[KeyName]) AS [Key]) x
      JOIN #ExtendedProperties ep WITH (NOLOCK)
        ON ep.[ObjectType] = 'foreignKey'
       AND SchemaSmith.fn_StripBracketWrapping(ep.[Schema]) = x.[Schema]
       AND SchemaSmith.fn_StripBracketWrapping(ep.[TableName]) = x.[Table]
       AND SchemaSmith.fn_StripBracketWrapping(ep.[ObjectName]) = x.[Key]
     WHERE ep.[Name] NOT IN (SELECT [Name] FROM @InternalNames)
       AND EXISTS (SELECT 1 FROM sys.foreign_keys fk
                    WHERE fk.parent_object_id = OBJECT_ID(k.[Schema] + '.' + k.[TableName], 'U')
                      AND fk.[name] COLLATE DATABASE_DEFAULT = x.[Key])
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(k.[Schema] + '.' + k.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0

  INSERT INTO #DeclaredProperties ([ObjectType], [Schema], [Table], [Level2Type], [Level2Name], [Name], [Declared])
    SELECT 'constraint', x.[Schema], x.[Table], 'CONSTRAINT', x.[Constraint], ep.[Name], NULLIF(ep.[Value], '')
      FROM #CheckConstraints cc WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(cc.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(cc.[TableName]) AS [Table],
                          SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]) AS [Constraint]) x
      JOIN #ExtendedProperties ep WITH (NOLOCK)
        ON ep.[ObjectType] = 'constraint'
       AND SchemaSmith.fn_StripBracketWrapping(ep.[Schema]) = x.[Schema]
       AND SchemaSmith.fn_StripBracketWrapping(ep.[TableName]) = x.[Table]
       AND SchemaSmith.fn_StripBracketWrapping(ep.[ObjectName]) = x.[Constraint]
     WHERE ep.[Name] NOT IN (SELECT [Name] FROM @InternalNames)
       AND EXISTS (SELECT 1 FROM sys.check_constraints ck
                    WHERE ck.parent_object_id = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName], 'U')
                      AND ck.[name] COLLATE DATABASE_DEFAULT = x.[Constraint])
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(cc.[Schema] + '.' + cc.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0
  SET NOCOUNT OFF
END
GO

IF OBJECT_ID('SchemaSmith.ExtendedPropertyQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ExtendedPropertyQuench
GO
-- The declared extended properties of every table, column, index, foreign key and check constraint, converged
-- once the objects exist. A table's, column's or index's Description is its MS_Description; the rest of each
-- component's Extensions.ExtendedProperties bag is applied name by name. A declared value is added or updated,
-- an empty one ('' or null) drops the property, and a name the package does not mention is left alone: a
-- package that never mentions a property never touches the ones a DBA wrote, the same rule the permission
-- carry-across keeps. The names SchemaSmith stamps for itself (ProductName, PreventDrop,
-- SchemaSmith_SuspendedHistory) are never taken from the bag, and MS_Description on a table, column or index
-- comes only from Description, into which the parse folds a bag entry left from before 2.8. Memory-optimized
-- tables reject extended properties and are skipped. Statistics cannot carry one on any version. Under
-- @IndexesOnly only #Indexes is read, because IndexOnlyQuench builds no #Columns, #ForeignKeys or #CheckConstraints.
CREATE PROCEDURE SchemaSmith.ExtendedPropertyQuench
  @WhatIf BIT = 0,
  @IndexesOnly BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX)

  DECLARE @InternalNames TABLE ([Name] NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL)
  INSERT @InternalNames VALUES (N'ProductName'), (N'PreventDrop'), (N'SchemaSmith_SuspendedHistory')

  IF OBJECT_ID('tempdb..#DeclaredProperties') IS NOT NULL DROP TABLE #DeclaredProperties
  CREATE TABLE #DeclaredProperties
  (
    [ObjectType] NVARCHAR(20) COLLATE DATABASE_DEFAULT NOT NULL,
    [Schema] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
    [Table] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
    [Level2Type] NVARCHAR(20) COLLATE DATABASE_DEFAULT NULL,
    [Level2Name] NVARCHAR(500) COLLATE DATABASE_DEFAULT NULL,
    [Name] NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL,
    -- NULL removes the property.
    [Declared] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
    [Existing] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
    [ExistingFound] BIT NOT NULL DEFAULT 0
  )

  IF @IndexesOnly = 0 EXEC SchemaSmith.CollectTableScopedExtendedProperties

  INSERT INTO #DeclaredProperties ([ObjectType], [Schema], [Table], [Level2Name], [Name], [Declared])
    SELECT 'index', x.[Schema], x.[Table], x.[Index], p.[Name], p.[Declared]
      FROM #Indexes i WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(i.[Schema]) AS [Schema], SchemaSmith.fn_StripBracketWrapping(i.[TableName]) AS [Table],
                          SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) AS [Index]) x
      CROSS APPLY (SELECT N'MS_Description' AS [Name], NULLIF(i.[Description], '') AS [Declared] WHERE i.[Description] IS NOT NULL
                   UNION ALL
                   SELECT ep.[Name], NULLIF(ep.[Value], '')
                     FROM #ExtendedProperties ep WITH (NOLOCK)
                    WHERE ep.[ObjectType] = 'index'
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[Schema]) = x.[Schema]
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[TableName]) = x.[Table]
                      AND SchemaSmith.fn_StripBracketWrapping(ep.[ObjectName]) = x.[Index]
                      AND ep.[Name] <> N'MS_Description'
                      AND ep.[Name] NOT IN (SELECT [Name] FROM @InternalNames)) p
     WHERE EXISTS (SELECT 1 FROM sys.indexes si
                    WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName], 'U')
                      AND si.[name] COLLATE DATABASE_DEFAULT = x.[Index])
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(i.[Schema] + '.' + i.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0

  -- What the database already says, read once. An index's property may sit on the index itself (class 7)
  -- or, for a key constraint, on the constraint (class 1); whichever holds it is the one updated or
  -- dropped, and a new one goes on the constraint when the index backs one.
  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1
    FROM #DeclaredProperties d
    JOIN sys.extended_properties ep
      ON ep.class = 1 AND ep.[name] COLLATE DATABASE_DEFAULT = d.[Name]
     AND ep.major_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
     AND ep.minor_id = CASE WHEN d.[ObjectType] = 'table' THEN 0 ELSE COLUMNPROPERTY(ep.major_id, d.[Level2Name], 'ColumnId') END
   WHERE d.[ObjectType] IN ('table', 'column')

  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1, [Level2Type] = 'INDEX'
    FROM #DeclaredProperties d
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                       AND si.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name]
    JOIN sys.extended_properties ep ON ep.class = 7 AND ep.major_id = si.[object_id] AND ep.minor_id = si.index_id AND ep.[name] COLLATE DATABASE_DEFAULT = d.[Name]
   WHERE d.[ObjectType] = 'index'

  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1, [Level2Type] = 'CONSTRAINT'
    FROM #DeclaredProperties d
    JOIN sys.key_constraints kc ON kc.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                               AND kc.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name]
    JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = kc.[object_id] AND ep.minor_id = 0 AND ep.[name] COLLATE DATABASE_DEFAULT = d.[Name]
   WHERE d.[ObjectType] = 'index' AND d.[ExistingFound] = 0

  UPDATE d
     SET [Level2Type] = CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints kc
                                           WHERE kc.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                                             AND kc.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name])
                             THEN 'CONSTRAINT' ELSE 'INDEX' END
    FROM #DeclaredProperties d
   WHERE d.[ObjectType] = 'index' AND d.[Level2Type] IS NULL

  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1
    FROM #DeclaredProperties d
    JOIN sys.objects o ON o.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                      AND o.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name]
                      AND o.[type] IN ('F', 'C')
    JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = o.[object_id] AND ep.minor_id = 0 AND ep.[name] COLLATE DATABASE_DEFAULT = d.[Name]
   WHERE d.[ObjectType] IN ('foreignKey', 'constraint')

  RAISERROR('Converge extended properties', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  ' + CASE WHEN d.[Declared] IS NULL THEN 'Removing' ELSE 'Setting' END +
          CASE WHEN d.[Name] = N'MS_Description' THEN ' the description of '
               ELSE ' extended property ' + REPLACE(REPLACE(d.[Name], '''', ''''''), '%', '%%') + CASE WHEN d.[Declared] IS NULL THEN ' from ' ELSE ' on ' END END +
          x.[Kind] + ' ' + REPLACE(REPLACE(x.[QualifiedName], '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT' + CHAR(13) + CHAR(10) +
      CASE WHEN d.[Declared] IS NULL THEN 'EXEC sp_dropextendedproperty @name = N''' + REPLACE(d.[Name], '''', '''''') + ''', '
           WHEN d.[ExistingFound] = 1 THEN 'EXEC sp_updateextendedproperty @name = N''' + REPLACE(d.[Name], '''', '''''') + ''', @value = N''' + REPLACE(d.[Declared], '''', '''''') + ''', '
           ELSE 'EXEC sp_addextendedproperty @name = N''' + REPLACE(d.[Name], '''', '''''') + ''', @value = N''' + REPLACE(d.[Declared], '''', '''''') + ''', ' END +
      '@level0type = N''SCHEMA'', @level0name = N''' + REPLACE(d.[Schema], '''', '''''') + ''', @level1type = N''TABLE'', @level1name = N''' + REPLACE(d.[Table], '''', '''''') + '''' +
      CASE WHEN d.[Level2Type] IS NULL THEN '' ELSE ', @level2type = N''' + d.[Level2Type] + ''', @level2name = N''' + REPLACE(d.[Level2Name], '''', '''''') + '''' END + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + d.[ObjectType] + ''', ''' + REPLACE(x.[QualifiedName], '''', '''''') + ''', ''modified'')'
      AS NVARCHAR(MAX))
    FROM #DeclaredProperties d
    CROSS APPLY (SELECT d.[Schema] + '.' + d.[Table] + ISNULL('.' + d.[Level2Name], '') AS [QualifiedName],
                        CASE d.[ObjectType] WHEN 'foreignKey' THEN 'foreign key' WHEN 'constraint' THEN 'check constraint' ELSE d.[ObjectType] END AS [Kind]) x
    WHERE (d.[Declared] IS NULL AND d.[ExistingFound] = 1)
       OR (d.[Declared] IS NOT NULL AND (d.[ExistingFound] = 0 OR d.[Existing] <> d.[Declared] COLLATE Latin1_General_BIN2))
    ORDER BY d.[ObjectType], d.[Schema], d.[Table], d.[Level2Name], d.[Name]
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 BEGIN EXEC SchemaSmith.PrintWithNoWait @v_SQL; EXEC SchemaSmith.RecordWouldChanges @v_SQL END ELSE EXEC(@v_SQL)

  IF OBJECT_ID('tempdb..#DeclaredProperties') IS NOT NULL DROP TABLE #DeclaredProperties
  SET NOCOUNT OFF
END
