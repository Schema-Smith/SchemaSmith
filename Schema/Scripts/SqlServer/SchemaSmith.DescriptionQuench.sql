-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.DescriptionQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DescriptionQuench
GO
-- The declared Description of every table, column and index, converged onto the MS_Description extended
-- property once the objects exist. A declared value is added or updated, '' drops the property, and an
-- omitted value (NULL in the temp tables) leaves whatever is there alone: a package that never mentions
-- descriptions never touches the ones a DBA wrote, the same rule the permission carry-across keeps.
-- Memory-optimized tables reject extended properties and are skipped. Under @IndexesOnly only #Indexes is
-- read, because IndexOnlyQuench builds no #Columns.
CREATE PROCEDURE SchemaSmith.DescriptionQuench
  @WhatIf BIT = 0,
  @IndexesOnly BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX)

  IF OBJECT_ID('tempdb..#Descriptions') IS NOT NULL DROP TABLE #Descriptions
  CREATE TABLE #Descriptions
  (
    [ObjectType] NVARCHAR(10) COLLATE DATABASE_DEFAULT NOT NULL,
    [Schema] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
    [Table] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
    [Level2Type] NVARCHAR(20) COLLATE DATABASE_DEFAULT NULL,
    [Level2Name] NVARCHAR(200) COLLATE DATABASE_DEFAULT NULL,
    [Declared] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NOT NULL,
    [Existing] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL,
    [ExistingFound] BIT NOT NULL DEFAULT 0
  )

  IF @IndexesOnly = 0
  BEGIN
    INSERT INTO #Descriptions ([ObjectType], [Schema], [Table], [Declared])
      SELECT 'table', SchemaSmith.fn_StripBracketWrapping(t.[Schema]), SchemaSmith.fn_StripBracketWrapping(t.[Name]), t.[Description]
        FROM #Tables t WITH (NOLOCK)
       WHERE t.[Description] IS NOT NULL
         AND OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U') IS NOT NULL
         AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U'), 'TableIsMemoryOptimized'), 0) = 0

    INSERT INTO #Descriptions ([ObjectType], [Schema], [Table], [Level2Type], [Level2Name], [Declared])
      SELECT 'column', SchemaSmith.fn_StripBracketWrapping(c.[Schema]), SchemaSmith.fn_StripBracketWrapping(c.[TableName]),
             'COLUMN', SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]), c.[Description]
        FROM #Columns c WITH (NOLOCK)
       WHERE c.[Description] IS NOT NULL
         AND COL_LENGTH(c.[Schema] + '.' + c.[TableName], SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])) IS NOT NULL
         AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(c.[Schema] + '.' + c.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0
  END

  INSERT INTO #Descriptions ([ObjectType], [Schema], [Table], [Level2Name], [Declared])
    SELECT 'index', SchemaSmith.fn_StripBracketWrapping(i.[Schema]), SchemaSmith.fn_StripBracketWrapping(i.[TableName]),
           SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), i.[Description]
      FROM #Indexes i WITH (NOLOCK)
     WHERE i.[Description] IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.indexes si
                    WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName], 'U')
                      AND si.[name] COLLATE DATABASE_DEFAULT = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
       AND ISNULL(OBJECTPROPERTYEX(OBJECT_ID(i.[Schema] + '.' + i.[TableName], 'U'), 'TableIsMemoryOptimized'), 0) = 0

  -- What the database already says, read once. An index's property may sit on the index itself (class 7)
  -- or, for a key constraint, on the constraint (class 1); whichever holds it is the one updated or
  -- dropped, and a new one goes on the constraint when the index backs one.
  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1
    FROM #Descriptions d
    JOIN sys.extended_properties ep
      ON ep.class = 1 AND ep.[name] = 'MS_Description'
     AND ep.major_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
     AND ep.minor_id = CASE WHEN d.[ObjectType] = 'table' THEN 0 ELSE COLUMNPROPERTY(ep.major_id, d.[Level2Name], 'ColumnId') END
   WHERE d.[ObjectType] IN ('table', 'column')

  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1, [Level2Type] = 'INDEX'
    FROM #Descriptions d
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                       AND si.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name]
    JOIN sys.extended_properties ep ON ep.class = 7 AND ep.major_id = si.[object_id] AND ep.minor_id = si.index_id AND ep.[name] = 'MS_Description'
   WHERE d.[ObjectType] = 'index'

  UPDATE d
     SET [Existing] = CONVERT(NVARCHAR(MAX), ep.[value]), [ExistingFound] = 1, [Level2Type] = 'CONSTRAINT'
    FROM #Descriptions d
    JOIN sys.key_constraints kc ON kc.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                               AND kc.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name]
    JOIN sys.extended_properties ep ON ep.class = 1 AND ep.major_id = kc.[object_id] AND ep.minor_id = 0 AND ep.[name] = 'MS_Description'
   WHERE d.[ObjectType] = 'index' AND d.[ExistingFound] = 0

  UPDATE d
     SET [Level2Type] = CASE WHEN EXISTS (SELECT 1 FROM sys.key_constraints kc
                                           WHERE kc.parent_object_id = OBJECT_ID(QUOTENAME(d.[Schema]) + '.' + QUOTENAME(d.[Table]), 'U')
                                             AND kc.[name] COLLATE DATABASE_DEFAULT = d.[Level2Name])
                             THEN 'CONSTRAINT' ELSE 'INDEX' END
    FROM #Descriptions d
   WHERE d.[ObjectType] = 'index' AND d.[Level2Type] IS NULL

  RAISERROR('Converge descriptions', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  ' + CASE WHEN d.[Declared] = '' THEN 'Removing' ELSE 'Setting' END + ' the description of ' + d.[ObjectType] + ' ' +
          REPLACE(REPLACE(x.[QualifiedName], '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT' + CHAR(13) + CHAR(10) +
      CASE WHEN d.[Declared] = '' THEN 'EXEC sp_dropextendedproperty @name = N''MS_Description'', '
           WHEN d.[ExistingFound] = 1 THEN 'EXEC sp_updateextendedproperty @name = N''MS_Description'', @value = N''' + REPLACE(d.[Declared], '''', '''''') + ''', '
           ELSE 'EXEC sp_addextendedproperty @name = N''MS_Description'', @value = N''' + REPLACE(d.[Declared], '''', '''''') + ''', ' END +
      '@level0type = N''SCHEMA'', @level0name = N''' + REPLACE(d.[Schema], '''', '''''') + ''', @level1type = N''TABLE'', @level1name = N''' + REPLACE(d.[Table], '''', '''''') + '''' +
      CASE WHEN d.[Level2Type] IS NULL THEN '' ELSE ', @level2type = N''' + d.[Level2Type] + ''', @level2name = N''' + REPLACE(d.[Level2Name], '''', '''''') + '''' END + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + d.[ObjectType] + ''', ''' + REPLACE(x.[QualifiedName], '''', '''''') + ''', ''modified'')'
      AS NVARCHAR(MAX))
    FROM #Descriptions d
    CROSS APPLY (SELECT d.[Schema] + '.' + d.[Table] + ISNULL('.' + d.[Level2Name], '') AS [QualifiedName]) x
    WHERE (d.[Declared] = '' AND d.[ExistingFound] = 1)
       OR (d.[Declared] <> '' AND (d.[ExistingFound] = 0 OR d.[Existing] <> d.[Declared] COLLATE Latin1_General_BIN2))
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 BEGIN EXEC SchemaSmith.PrintWithNoWait @v_SQL; EXEC SchemaSmith.RecordWouldChanges @v_SQL END ELSE EXEC(@v_SQL)

  IF OBJECT_ID('tempdb..#Descriptions') IS NOT NULL DROP TABLE #Descriptions
  SET NOCOUNT OFF
END
