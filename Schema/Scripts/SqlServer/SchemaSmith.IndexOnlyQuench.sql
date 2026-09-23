-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.IndexOnlyQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.IndexOnlyQuench
GO
CREATE PROCEDURE SchemaSmith.IndexOnlyQuench
  @ProductName NVARCHAR(50),
  @TableDefinitions NVARCHAR(MAX),
  @WhatIf BIT = 0,
  @DropUnknownIndexes BIT = 0,
  @DropIndexesRemovedFromProduct BIT = 1,
  @UpdateFillFactor BIT = 1,
  @CaptureWouldDrop BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON
  RAISERROR('Parse Tables from Json', 10, 100) WITH NOWAIT

  -- I5: missing/blank [Schema] is a programmer error after slice-1's SchemaDefaultResolver.
  -- Silent 'dbo' fallback here is data-loss-equivalent — fail loud with a clear pointer at
  -- the Template.Load contract.
  IF EXISTS (SELECT 1 FROM OPENJSON(@TableDefinitions) WITH ([Schema] NVARCHAR(500) '$.Schema', [Name] NVARCHAR(500) '$.Name')
                 WHERE NULLIF(RTRIM(ISNULL([Schema], '')), '') IS NULL)
  BEGIN
    DECLARE @v_BadTable NVARCHAR(500) =
      (SELECT TOP 1 ISNULL([Name], '<unnamed>') FROM OPENJSON(@TableDefinitions) WITH ([Schema] NVARCHAR(500) '$.Schema', [Name] NVARCHAR(500) '$.Name')
         WHERE NULLIF(RTRIM(ISNULL([Schema], '')), '') IS NULL);
    DECLARE @v_Msg NVARCHAR(2000) = 'Table JSON is missing Schema for table ''' + @v_BadTable + '''. ' +
      'Schema must be populated before reaching IndexOnlyQuench — this is a programmer error. ' +
      'In production the SchemaDefaultResolver fills Schema with the platform default or the {{SchemaName}} token; ' +
      'a blank value here means a caller bypassed Template.Load or substituted the token away.';
    THROW 51000, @v_Msg, 1;
  END

  DROP TABLE IF EXISTS #TableDefinitions
  SELECT [Schema] = SchemaSmith.fn_SafeBracketWrap([Schema]), [Name] = SchemaSmith.fn_SafeBracketWrap([Name]), [CompressionType] = ISNULL([CompressionType], 'NONE'),
         [UpdateFillFactor] = ISNULL([UpdateFillFactor], 0), [Indexes], [XmlIndexes], [Statistics], [FullTextIndex], [DropIndexesRemovedFromProduct]
    INTO #TableDefinitions
    FROM OPENJSON(@TableDefinitions) WITH (
      [Schema] NVARCHAR(500) '$.Schema',
      [Name] NVARCHAR(500) '$.Name',
      [CompressionType] NVARCHAR(100) '$.CompressionType',
      [UpdateFillFactor] BIT '$.UpdateFillFactor',
	  [Indexes] NVARCHAR(MAX) '$.Indexes' AS JSON,
	  [XmlIndexes] NVARCHAR(MAX) '$.XmlIndexes' AS JSON,
	  [Statistics] NVARCHAR(MAX) '$.Statistics' AS JSON,
	  [FullTextIndex] NVARCHAR(MAX) '$.FullTextIndex' AS JSON,
	  [DropIndexesRemovedFromProduct] BIT '$.DropIndexesRemovedFromProduct'
      ) t;
  
  DROP TABLE IF EXISTS #Tables
  SELECT [Schema], [Name], [CompressionType], [UpdateFillFactor], [DropIndexesRemovedFromProduct],
         CONVERT(BIT, CASE WHEN OBJECT_ID([Schema] + '.' + [Name], 'U') IS NULL THEN 1 ELSE 0 END) AS MissingTable
    INTO #Tables
    FROM #TableDefinitions WITH (NOLOCK)

  RAISERROR('Parse Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #Indexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), [CompressionType] = ISNULL(i.[CompressionType], 'NONE'), [XmlCompression] = ISNULL(i.[XmlCompression], 0), [PrimaryKey] = ISNULL(i.[PrimaryKey], 0),
         [Unique] = COALESCE(NULLIF(i.[Unique], 0), NULLIF(i.[PrimaryKey], 0), i.[UniqueConstraint], 0),
         [UniqueConstraint] = ISNULL(i.[UniqueConstraint], 0), [Clustered] = ISNULL(i.[Clustered], 0), [ColumnStore] = ISNULL(i.[ColumnStore], 0), [FillFactor] = ISNULL(NULLIF(i.[FillFactor], 0), 100),
         i.[FilterExpression], [FileGroup] = SchemaSmith.fn_SafeBracketWrap(i.[FileGroup]),
         [PartitionScheme] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionScheme]), [PartitionColumn] = SchemaSmith.fn_SafeBracketWrap(i.[PartitionColumn]), [UpdateFillFactor] = CONVERT(BIT, CASE WHEN @UpdateFillFactor = 1 OR t.[UpdateFillFactor] = 1 OR i.[UpdateFillFactor] = 1 THEN 1 ELSE 0 END),
         [IndexColumns] = (SELECT STRING_AGG(CAST(CASE WHEN RTRIM([value]) LIKE '% DESC'
                                                       THEN SchemaSmith.fn_SafeBracketWrap(SUBSTRING(RTRIM([value]), 1, LEN(RTRIM([value])) - 5)) + ' DESC'
                                                       ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                                       END AS NVARCHAR(MAX)), ',') 
                             FROM STRING_SPLIT(i.[IndexColumns], ',') 
                             WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [IncludeColumns] = (SELECT STRING_AGG(CAST(SchemaSmith.fn_SafeBracketWrap([value]) AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY SchemaSmith.fn_SafeBracketWrap([value]))
                               FROM STRING_SPLIT(i.[IncludeColumns], ',') 
                               WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         [IgnoreDuplicateKey] = ISNULL(i.[IgnoreDuplicateKey], 0), [PadIndex] = ISNULL(i.[PadIndex], 0),
         i.[ShouldApplyExpression], i.[VariantName]
    INTO #Indexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(Indexes) WITH (
      [IndexName] NVARCHAR(500) '$.Name',
      [CompressionType] NVARCHAR(100) '$.CompressionType',
      [XmlCompression] BIT '$.XmlCompression',
      [PrimaryKey] BIT '$.PrimaryKey',
      [Unique] BIT '$.Unique',
	  [UniqueConstraint] BIT '$.UniqueConstraint',
      [Clustered] BIT '$.Clustered',
      [ColumnStore] BIT '$.ColumnStore',
      [IgnoreDuplicateKey] BIT '$.IgnoreDuplicateKey',
      [PadIndex] BIT '$.PadIndex',
      [FillFactor] TINYINT '$.FillFactor',
      [FilterExpression] NVARCHAR(MAX) '$.FilterExpression',
      [IndexColumns] NVARCHAR(MAX) '$.IndexColumns',
      [IncludeColumns] NVARCHAR(MAX) '$.IncludeColumns',
      [FileGroup] NVARCHAR(500) '$.FileGroup',
      [PartitionScheme] NVARCHAR(500) '$.PartitionScheme',
      [PartitionColumn] NVARCHAR(500) '$.PartitionColumn',
      [UpdateFillFactor] BIT '$.UpdateFillFactor',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) i;

  -- Identify Indexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #Indexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Indexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  -- Unsupported-feature policy: drop below-2012/2014 columnstore indexes from #Indexes so the --IndexOnly
  -- create/modify passes never emit COLUMNSTORE on an older target. See SchemaSmith.DegradeUnsupportedColumnStore.
  EXEC SchemaSmith.DegradeUnsupportedColumnStore

  RAISERROR('Parse XML Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #XmlIndexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [IndexName] = SchemaSmith.fn_SafeBracketWrap(i.[IndexName]), i.[IsPrimary],
         [Column] = SchemaSmith.fn_SafeBracketWrap(i.[Column]), [PrimaryIndex] = SchemaSmith.fn_SafeBracketWrap(i.[PrimaryIndex]),
         i.[SecondaryIndexType], i.[ShouldApplyExpression], i.[VariantName]
    INTO #XmlIndexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON(XmlIndexes) WITH (
      [IndexName] NVARCHAR(500) '$.Name',
      [IsPrimary] BIT '$.IsPrimary',
      [Column] NVARCHAR(500) '$.Column',
      [PrimaryIndex] NVARCHAR(500) '$.PrimaryIndex',
	  [SecondaryIndexType] NVARCHAR(500) '$.SecondaryIndexType',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) i;

  -- Identify XmlIndexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #XmlIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #XmlIndexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  RAISERROR('Parse Statistics from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #Statistics
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [StatisticName] = SchemaSmith.fn_SafeBracketWrap(s.[StatisticName]), [SampleSize] = ISNULL(s.[SampleSize], 0), s.[FilterExpression],
         [Columns] = (SELECT STRING_AGG(CAST(SchemaSmith.fn_SafeBracketWrap([value]) AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(s.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         s.[ShouldApplyExpression], s.[VariantName]
    INTO #Statistics
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON([Statistics]) WITH (
      [StatisticName] NVARCHAR(500) '$.Name',
      [SampleSize] TINYINT '$.SampleSize',
      [FilterExpression] NVARCHAR(MAX) '$.FilterExpression',
      [Columns] NVARCHAR(MAX) '$.Columns',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) s;

  -- Identify Statistics to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #Statistics WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Statistics WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  RAISERROR('Parse Full Text Indexes from Json', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #FullTextIndexes
  SELECT [_RowId] = ROW_NUMBER() OVER (ORDER BY (SELECT NULL)),
         t.[Schema], t.[Name] AS [TableName], [FullTextCatalog] = SchemaSmith.fn_SafeBracketWrap(f.[FullTextCatalog]), [KeyIndex] = SchemaSmith.fn_SafeBracketWrap(f.[KeyIndex]),
         -- Guarded like StopList beside it. Unguarded, this concatenates into the CREATE FULLTEXT INDEX
         -- statement, and T-SQL concatenation with NULL yields NULL -- the whole statement becomes NULL and
         -- NO index is created, with no error and no log line. Reachable from an ordinary package: the C#
         -- default is AUTO, but an explicit "ChangeTracking": null in a table file overwrites it. 'AUTO'
         -- here matches that C# default, so an omitted and an explicitly-null value behave the same.
         [ChangeTracking] = COALESCE(NULLIF(RTRIM(f.[ChangeTracking]), ''), 'AUTO'),
         [StopList] = SchemaSmith.fn_SafeBracketWrap(COALESCE(NULLIF(RTRIM(f.[StopList]), ''), 'SYSTEM')),
         -- Full-text LANGUAGE churn: a per-column "LANGUAGE nnnn" suffix must round-trip byte-identical
         -- against the live-side build below (drift compares these as strings). Peel it off before
         -- bracket-wrapping the column (+ optional TYPE COLUMN) part -- same shape as the " DESC" handling
         -- for IndexColumns above -- then reattach it; the LCID is variable-length so it's located and
         -- sliced rather than trimmed by a fixed count.
         [Columns] = (SELECT STRING_AGG(CAST(CASE WHEN RTRIM([value]) LIKE '% LANGUAGE [0-9]%'
                                                   THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) - 1)) +
                                                        ' LANGUAGE ' + SUBSTRING(RTRIM([value]), CHARINDEX(' LANGUAGE ', RTRIM([value])) + 10, 4000)
                                                   -- Mirrors ParseTableJsonIntoTempTables: STATISTICAL_SEMANTICS can appear with no LANGUAGE, and
                                                   -- must be peeled rather than bracket-wrapped into the column name.
                                                   WHEN RTRIM([value]) LIKE '% STATISTICAL[_]SEMANTICS'
                                                        THEN SchemaSmith.fn_SafeBracketWrap(LEFT(RTRIM([value]), CHARINDEX(' STATISTICAL_SEMANTICS', RTRIM([value])) - 1)) +
                                                             ' STATISTICAL_SEMANTICS'
                                                   ELSE SchemaSmith.fn_SafeBracketWrap([value])
                                                   END AS NVARCHAR(MAX)), ',') FROM STRING_SPLIT(f.[Columns], ',') WHERE SchemaSmith.fn_StripBracketWrapping(RTRIM(LTRIM([Value]))) <> ''),
         f.[ShouldApplyExpression], f.[VariantName]
    INTO #FullTextIndexes
    FROM #TableDefinitions t WITH (NOLOCK)
    CROSS APPLY OPENJSON([FullTextIndex]) WITH (
      [Columns] NVARCHAR(MAX) '$.Columns',
      [FullTextCatalog] NVARCHAR(500) '$.FullTextCatalog',
      [KeyIndex] NVARCHAR(500) '$.KeyIndex',
      [ChangeTracking] NVARCHAR(500) '$.ChangeTracking',
      [StopList] NVARCHAR(500) '$.StopList',
      [ShouldApplyExpression] NVARCHAR(MAX) '$.ShouldApplyExpression',
      [VariantName] NVARCHAR(128) '$.VariantName'
      ) f;

  -- Identify FullTextIndexes to skip based on ShouldApply expression (scoped by [_RowId])
  SELECT @v_SQL = STRING_AGG(CAST('DELETE FROM #FullTextIndexes WHERE [_RowId] = ' + CAST([_RowId] AS NVARCHAR(20)) + ' AND NOT (' + SchemaSmith.fn_StripLeadingSelect([ShouldApplyExpression]) + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #FullTextIndexes WITH (NOLOCK)
    WHERE RTRIM(ISNULL([ShouldApplyExpression], '')) <> ''
  EXEC(@v_SQL)

  -- A table with 2+ surviving variants cannot be honored: SQL Server allows ONE full-text index per table
  IF EXISTS (SELECT 1 FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1)
  BEGIN
    DECLARE @v_FTDupTable NVARCHAR(1010) =
      (SELECT TOP 1 [Schema] + '.' + [TableName] FROM #FullTextIndexes WITH (NOLOCK) GROUP BY [Schema], [TableName] HAVING COUNT(*) > 1 ORDER BY [Schema], [TableName]);
    DECLARE @v_FTDupMsg NVARCHAR(2000) = 'Multiple full-text index variants matched on this target for table ' + @v_FTDupTable +
      '. SQL Server allows one full-text index per table — ShouldApplyExpressions must be mutually exclusive.';
    THROW 51000, @v_FTDupMsg, 1;
  END

  -- Handle index compression changes
  RAISERROR('Fixup Index Compression', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Altering index compression for ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ' TO ' + i.[CompressionType] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' REBUILD PARTITION=ALL WITH (DATA_COMPRESSION=' + i.[CompressionType] + ');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Indexes i WITH (NOLOCK) 
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                                     AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    LEFT JOIN sys.partitions p ON p.[object_id] = si.[object_id]
                                            AND p.index_id = si.index_id
    WHERE COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') <> i.[CompressionType]
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Collect Existing Index Definitions', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #ExistingIndexes
  SELECT xSchema = t.[Schema], [xTableName] = t.[Name], [xIndexName] = CAST(si.[Name] AS NVARCHAR(500)),
         IsConstraint = CAST(CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1 THEN 1 ELSE 0 END AS BIT),
         IsUnique = si.is_unique, IsClustered = CAST(CASE WHEN si.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END AS BIT), [FillFactor] = ISNULL(NULLIF(si.fill_factor, 0), 100),
         -- Filegroup placement (#filegroups): the index's LIVE filegroup name, for the declared-vs-deployed
         -- move check below. Deliberately NOT folded into [IndexScript] -- a placement difference must
         -- ERROR, never trigger a silent rebuild via the ordinary "index definition changed" path. Mirrors
         -- ModifiedTableQuench.sql's #ExistingIndexes build.
         [xFileGroup] = fg.[name],
         -- Partition placement (#partitioning, K1): mutually exclusive with the filegroup above, since an
         -- index lives on ONE data space. Read with its column, because the same scheme on a different
         -- column is a different physical layout.
         [xPartitionScheme] = pds.[name],
         [xPartitionColumn] = (SELECT pc.[name]
                                 FROM sys.index_columns pic
                                 JOIN sys.columns pc ON pc.[object_id] = pic.[object_id] AND pc.column_id = pic.column_id
                                WHERE pic.[object_id] = si.[object_id] AND pic.index_id = si.index_id
                                  AND pic.partition_ordinal = 1),
         IndexScript = 'CREATE ' +
                       CASE WHEN si.is_unique = 1 THEN 'UNIQUE ' ELSE '' END + 
                       CASE WHEN si.[type] IN (1, 5) THEN '' ELSE 'NON' END + 'CLUSTERED ' +
                       CASE WHEN si.[type] IN (5, 6) THEN 'COLUMNSTORE ' ELSE '' END +
                       'INDEX [' + si.[Name] + '] ON ' + t.[Schema] + '.' + t.[Name] + 
                       CASE WHEN si.[type] NOT IN (5, 6) 
                            THEN ' (' + (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY key_ordinal)
                                           FROM sys.index_columns ic
                                           WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 0) + ')' +
                                 CASE WHEN EXISTS (SELECT * FROM sys.index_columns ic WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1)
                                      THEN ' INCLUDE (' +
                                           (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY COL_NAME(ic.[object_id], ic.column_id))
                                              FROM sys.index_columns ic
                                              WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1) + ')'
                                      ELSE '' END
                            WHEN si.[type] IN (6) 
                            THEN ' (' + (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY COL_NAME(ic.[object_id], ic.column_id))
                                           FROM sys.index_columns ic
                                           WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1) + ')'
                            ELSE '' END +
                       CASE WHEN si.has_filter = 1 THEN ' WHERE ' + SchemaSmith.fn_StripParenWrapping(si.filter_definition) ELSE '' END +
                       CASE WHEN o.[WithOptions] <> '' THEN ' WITH (' + STUFF(o.[WithOptions], 1, 2, '') + ')' ELSE '' END
    INTO #ExistingIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                     AND si.index_id > 0
                                     AND is_hypothetical = 0
                                     AND is_disabled = 0
    LEFT JOIN sys.partitions p  ON p.[object_id] = si.[object_id]
                                             AND p.index_id = si.index_id
    CROSS APPLY (SELECT [WithOptions] =
                   CASE WHEN (si.[type] NOT IN (5, 6) AND ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT IN ('NONE', 'ROW', 'PAGE'))
                             OR (si.[type] IN (5, 6) AND ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                        THEN ', DATA_COMPRESSION=' + ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT ELSE '' END +
                   CASE WHEN si.ignore_dup_key = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                   CASE WHEN si.is_padded = 1 THEN ', PAD_INDEX=ON' ELSE '' END) o
    LEFT JOIN sys.filegroups fg ON fg.data_space_id = si.data_space_id
    LEFT JOIN sys.data_spaces pds ON pds.data_space_id = si.data_space_id AND pds.[type] = 'PS'
    WHERE t.MissingTable = 0
      AND NOT EXISTS (SELECT * FROM sys.xml_indexes xi WHERE xi.[object_id] = si.[object_id] AND xi.index_id = si.index_id)

  -- Filegroup placement (#filegroups): an EXISTING index (found live, still declared by name) whose declared
  -- filegroup differs from where it is currently deployed is a MOVE -- SQL Server rebuild territory, so this
  -- errors naming both rather than silently rebuilding it. Same "error, don't rebuild" contract as
  -- ModifiedTableQuench.sql's index-level check. Declared NULL means "the database's own default filegroup",
  -- so an ordinary index with FileGroup unset never trips this against a live index already on the default.
  RAISERROR('Validate declared index filegroup matches deployed', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #ExistingIndexes ei WITH (NOLOCK)
               JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                            AND ei.[xTableName] = i.[TableName]
                                            AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
               WHERE ei.[xFileGroup] IS NOT NULL AND i.[FileGroup] IS NOT NULL
                 AND SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]) <> ei.[xFileGroup])
  BEGIN
    DECLARE @v_IdxMoveIndex NVARCHAR(1510), @v_IdxMoveDeclared NVARCHAR(500), @v_IdxMoveLive NVARCHAR(500)
    SELECT TOP 1 @v_IdxMoveIndex = ei.[xSchema] + '.' + ei.[xTableName] + '.' + ei.[xIndexName],
                 @v_IdxMoveDeclared = SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]),
                 @v_IdxMoveLive = ei.[xFileGroup]
      FROM #ExistingIndexes ei WITH (NOLOCK)
      JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                   AND ei.[xTableName] = i.[TableName]
                                   AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
      WHERE ei.[xFileGroup] IS NOT NULL AND i.[FileGroup] IS NOT NULL
        AND SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]) <> ei.[xFileGroup]
    DECLARE @v_IdxMoveMsg NVARCHAR(2000) = 'Index ' + @v_IdxMoveIndex + ' declares filegroup ' + @v_IdxMoveDeclared +
      ', but is currently deployed on filegroup ' + @v_IdxMoveLive + '. SchemaSmith does not move an existing index to a different filegroup (that is a rebuild) -- migrate it manually, or correct the declared filegroup to match.';
    THROW 51000, @v_IdxMoveMsg, 1;
  END

  -- Partition placement (#partitioning, K1): the index-level twin of the table check in
  -- ModifiedTableQuench, refused for the same reason -- rebuilding an index onto a different scheme
  -- rewrites the whole index. Placement is deliberately absent from [IndexScript], so without this an index
  -- whose declared scheme changed would be SILENTLY left where it is: the ordinary drop-and-recreate path
  -- cannot see a difference it never compares.
  RAISERROR('Validate declared index partition scheme matches deployed', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #ExistingIndexes ei WITH (NOLOCK)
               JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                            AND ei.[xTableName] = i.[TableName]
                                            AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
               WHERE i.[PartitionScheme] IS NOT NULL
                 AND (ei.[xPartitionScheme] IS NULL
                      OR SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]) <> ei.[xPartitionScheme]
                      OR (ei.[xPartitionColumn] IS NOT NULL
                          AND SchemaSmith.fn_StripBracketWrapping(i.[PartitionColumn]) <> ei.[xPartitionColumn])))
  BEGIN
    DECLARE @v_IdxPsIndex NVARCHAR(1510), @v_IdxPsDeclared NVARCHAR(1010), @v_IdxPsLive NVARCHAR(1010)
    SELECT TOP 1 @v_IdxPsIndex = ei.[xSchema] + '.' + ei.[xTableName] + '.' + ei.[xIndexName],
                 @v_IdxPsDeclared = SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]) + '(' + SchemaSmith.fn_StripBracketWrapping(i.[PartitionColumn]) + ')',
                 @v_IdxPsLive = ISNULL(ei.[xPartitionScheme] + '(' + ISNULL(ei.[xPartitionColumn], '?') + ')', 'filegroup ' + ISNULL(ei.[xFileGroup], 'the database default'))
      FROM #ExistingIndexes ei WITH (NOLOCK)
      JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                   AND ei.[xTableName] = i.[TableName]
                                   AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
      WHERE i.[PartitionScheme] IS NOT NULL
        AND (ei.[xPartitionScheme] IS NULL
             OR SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]) <> ei.[xPartitionScheme]
             OR (ei.[xPartitionColumn] IS NOT NULL
                 AND SchemaSmith.fn_StripBracketWrapping(i.[PartitionColumn]) <> ei.[xPartitionColumn]))
    DECLARE @v_IdxPsMsg NVARCHAR(2000) = 'Index ' + @v_IdxPsIndex + ' declares partition placement ' + @v_IdxPsDeclared +
      ', but is currently deployed on ' + @v_IdxPsLive + '. SchemaSmith does not move an existing index onto or between partition schemes -- that rebuilds the whole index. Migrate it manually, or correct the declaration to match.';
    THROW 51000, @v_IdxPsMsg, 1;
  END

  RAISERROR('Collect Existing XML Index Definitions', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #ExistingXmlIndexes
  SELECT xSchema = t.[Schema], [xTableName] = t.[Name], [xIndexName] = CAST(i.[Name] COLLATE DATABASE_DEFAULT AS NVARCHAR(500)),
         IndexScript = 'CREATE ' + CASE WHEN i.xml_index_type = 0 THEN 'PRIMARY ' ELSE '' END + 
                       'XML INDEX [' + i.[name] COLLATE DATABASE_DEFAULT + '] ON [' + OBJECT_SCHEMA_NAME(i.[object_id]) + '].[' + OBJECT_NAME(i.[object_id]) + '] ' + 
                       '([' + COL_NAME(i.[Object_id], ic.column_id) + '])' + 
                       CASE WHEN i.xml_index_type = 1 
                            THEN ' USING XML INDEX [' + (SELECT [Name] FROM sys.xml_indexes i2 WHERE i2.[object_id] = i.[object_id] AND i2.index_id = i.using_xml_index_id) COLLATE DATABASE_DEFAULT + '] ' + 
                                 'FOR ' + i.secondary_type_desc COLLATE DATABASE_DEFAULT 
                            ELSE '' END
    INTO #ExistingXmlIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.xml_indexes i ON i.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    JOIN sys.index_columns ic ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id
    WHERE t.MissingTable = 0

  RAISERROR('Detect Xml Index Changes', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #XmlIndexChanges
  SELECT i.[Schema], i.[TableName], i.[IndexName]
    INTO #XmlIndexChanges
    FROM #ExistingXmlIndexes ei WITH (NOLOCK)
    JOIN #XmlIndexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                    AND ei.[xTableName] = i.[TableName]
                                    AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    WHERE EXISTS (SELECT * 
                    FROM sys.xml_indexes si
                    WHERE si.[object_id] = OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]) 
                      AND si.[name] = ei.[xIndexName])
      AND ei.IndexScript <> 'CREATE ' + CASE WHEN i.IsPrimary = 1 THEN 'PRIMARY ' ELSE '' END + 
                            'XML INDEX ' + i.[IndexName] COLLATE DATABASE_DEFAULT + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' (' + i.[Column] + ')' + 
                            CASE WHEN i.IsPrimary = 0
                                 THEN ' USING XML INDEX ' + i.PrimaryIndex + ' FOR ' + i.SecondaryIndexType
                                 ELSE '' END
  
  RAISERROR('Detect Xml Index Renames', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #XmlIndexRenames
  SELECT i.[Schema], i.[TableName], [NewName] = i.[IndexName], [OldName] = ei.[xIndexName]
    INTO #XmlIndexRenames
    FROM #ExistingXmlIndexes ei WITH (NOLOCK)
    JOIN #XmlIndexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                    AND ei.[xTableName] = i.[TableName]
                                    AND ei.[xIndexName] <> SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    WHERE NOT EXISTS (SELECT * FROM #XmlIndexes i2 WITH (NOLOCK) WHERE i2.[Schema] = ei.[xSchema] AND i2.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i2.[IndexName]) = ei.[xIndexName])
      AND INDEXPROPERTY(OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]), SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'IndexID') IS NULL
      AND EXISTS (SELECT * 
                    FROM sys.xml_indexes si
                    WHERE si.[object_id] = OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]) 
                      AND si.[name] = ei.[xIndexName])
      AND REPLACE(ei.IndexScript, ei.[xIndexName], 'IndexName') = 'CREATE ' + CASE WHEN i.IsPrimary = 1 THEN 'PRIMARY ' ELSE '' END + 
                                                                  'XML INDEX ' + i.[IndexName] COLLATE DATABASE_DEFAULT + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' (' + i.[Column] + ')' + 
                                                                  CASE WHEN i.IsPrimary = 0
                                                                       THEN ' USING XML INDEX ' + i.PrimaryIndex + ' FOR ' + i.SecondaryIndexType
                                                                       ELSE '' END

  RAISERROR('Handle Renamed Xml Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Renaming ' + [OldName] + ' to ' + [NewName] + ' ON ' + ir.[Schema] + '.' + ir.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN INDEXPROPERTY(OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]), SchemaSmith.fn_StripBracketWrapping(ir.[NewName]), 'IndexID') IS NULL
                                       THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(ir.[TableName]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''INDEX'';'
                                       ELSE 'DROP INDEX IF EXISTS [' + ir.[OldName] + '] ON ' + ir.[Schema] + '.' + ir.[TableName] + ';'
                                       END AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #XmlIndexRenames ir WITH (NOLOCK)
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    
  RAISERROR('Collect Existing FullText Indexes', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #ExistingFullTextIndexes
  SELECT t.[Schema], [TableName] = t.[Name],
         (SELECT STRING_AGG(CAST('[' + COL_NAME(fc.[object_id], fc.column_id) + ']' +
                            CASE WHEN fc.type_column_id IS NOT NULL
                                 THEN ' TYPE COLUMN [' + COL_NAME(fc.[object_id], fc.type_column_id) + ']'
                                 ELSE '' END +
                            -- Full-text LANGUAGE churn: LANGUAGE only when it deviates from the column's own
                            -- collation-implied default -- stamping every column would churn every existing
                            -- full-text index once. Must render byte-identical to GenerateTableJson.sql's
                            -- extraction and the declared-side parse above; drift compares these as strings.
                            -- c.collation_name comes from a JOIN, not a correlated subquery -- STRING_AGG
                            -- rejects an aggregate expression containing a subquery. A NULL collation (a
                            -- non-character column) has no default to compare against, so LANGUAGE is always
                            -- emitted for it -- see GenerateTableJson.sql for the full rationale.
                            CASE WHEN c.collation_name IS NULL OR fc.language_id <> COLLATIONPROPERTY(c.collation_name, 'LCID')
                                 THEN ' LANGUAGE ' + CAST(fc.language_id AS NVARCHAR(10))
                                 ELSE '' END +
                            CASE WHEN fc.statistical_semantics = 1
                                 THEN ' STATISTICAL_SEMANTICS' ELSE '' END AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY COL_NAME(fc.[object_id], fc.column_id))
            FROM sys.fulltext_index_columns fc
            JOIN sys.columns c ON c.[object_id] = fc.[object_id] AND c.column_id = fc.column_id
            WHERE fi.[object_id] = fc.[object_id]) AS [Columns],
         FullTextCatalog = '[' + (SELECT c.[name] COLLATE DATABASE_DEFAULT FROM sys.fulltext_catalogs c WHERE c.fulltext_catalog_id = fi.fulltext_catalog_id) + ']',
         KeyIndex = '[' + (SELECT i.[Name] COLLATE DATABASE_DEFAULT FROM sys.indexes i WHERE i.[object_id] = fi.[object_id] AND i.[index_id] = fi.[unique_index_id]) + ']',
         ChangeTracking = change_tracking_state_desc COLLATE DATABASE_DEFAULT,
         [StopList] = '[' + COALESCE((SELECT fs.[name] COLLATE DATABASE_DEFAULT FROM sys.fulltext_stoplists fs WHERE fs.stoplist_id = fi.stoplist_id), 'SYSTEM') + ']'
    INTO #ExistingFullTextIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    WHERE t.MissingTable = 0
  
  RAISERROR('Detect Index Changes', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #IndexChanges
  SELECT i.[Schema], i.[TableName], i.[IndexName], ei.[IsConstraint], IsUnique = i.[Unique], IsClustered = i.[Clustered]
    INTO #IndexChanges
    FROM #ExistingIndexes ei WITH (NOLOCK)
    JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                 AND ei.[xTableName] = i.[TableName]
                                 AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    CROSS APPLY (SELECT [WithOptions] =
                   CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                             OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                        THEN ', DATA_COMPRESSION=' + RTRIM(ISNULL(i.[CompressionType], '')) ELSE '' END +
                   CASE WHEN i.[IgnoreDuplicateKey] = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                   CASE WHEN i.[PadIndex] = 1 THEN ', PAD_INDEX=ON' ELSE '' END) o
    WHERE EXISTS (SELECT * 
                    FROM sys.indexes si
                    WHERE si.[object_id] = OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]) 
                      AND si.[name] = ei.[xIndexName])
      AND ei.IndexScript <> 'CREATE ' + 
                            CASE WHEN i.[Unique] = 1 THEN 'UNIQUE ' ELSE '' END + 
                            CASE WHEN i.[Clustered] = 1 THEN '' ELSE 'NON' END + 'CLUSTERED ' +
                            CASE WHEN i.[ColumnStore] = 1 THEN 'COLUMNSTORE ' ELSE '' END + 
	                        'INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + 
                            CASE WHEN i.[ColumnStore] = 0 THEN ' (' + i.[IndexColumns] + ')' + CASE WHEN RTRIM(ISNULL(i.[IncludeColumns], '')) <> '' THEN ' INCLUDE (' + i.[IncludeColumns] + ')' ELSE '' END
                                 WHEN i.[ColumnStore] = 1 AND i.[Clustered] = 0 THEN ' (' + i.[IncludeColumns] + ')'
                                 ELSE '' END +
                            -- #242: the engine's own rendering of the filter when the mapping vouches for it (fn_ExpressionMapEffective).
                            CASE WHEN RTRIM(ISNULL(i.[FilterExpression], '')) <> '' THEN ' WHERE ' + SchemaSmith.fn_ExpressionMapEffective(i.[Schema], i.[TableName], 'INDEX', i.[IndexName], 'filter', i.[FilterExpression], SchemaSmith.fn_StripParenWrapping((SELECT si2.filter_definition FROM sys.indexes si2 WHERE si2.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si2.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])))) ELSE '' END +
                            CASE WHEN o.[WithOptions] <> '' THEN ' WITH (' + STUFF(o.[WithOptions], 1, 2, '') + ')' ELSE '' END
  
  RAISERROR('Detect Index Renames', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #IndexRenames
  SELECT i.[Schema], i.[TableName], [NewName] = i.[IndexName], ei.[IsConstraint], IsUnique = i.[Unique], [OldName] = ei.[xIndexName]
    INTO #IndexRenames
    FROM #ExistingIndexes ei WITH (NOLOCK)
    JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                 AND ei.[xTableName] = i.[TableName]
                                 AND ei.[xIndexName] <> SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    WHERE NOT EXISTS (SELECT * FROM #Indexes i2 WITH (NOLOCK) WHERE i2.[Schema] = ei.[xSchema] AND i2.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i2.[IndexName]) = ei.[xIndexName])
      AND INDEXPROPERTY(OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]), SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'IndexID') IS NULL
      -- A PK / unique constraint and a plain index are NOT rename-equivalent even when structurally
      -- identical: renaming a plain unique index into a PK name leaves an index where the constraint
      -- should be, and the PK is then never created (#304). Only rename when constraint-ness matches.
      AND ei.[IsConstraint] = (CASE WHEN i.[PrimaryKey] = 1 OR i.[UniqueConstraint] = 1 THEN 1 ELSE 0 END)
      AND EXISTS (SELECT *
                    FROM sys.indexes si
                    WHERE si.[object_id] = OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName])
                      AND si.[name] = ei.[xIndexName])
      AND REPLACE(ei.IndexScript, ei.[xIndexName], 'IndexName') = 'CREATE ' + 
                                                                  CASE WHEN i.[Unique] = 1 OR i.[PrimaryKey] = 1 THEN 'UNIQUE ' ELSE '' END + 
                                                                  CASE WHEN i.[Clustered] = 1 THEN '' ELSE 'NON' END + 'CLUSTERED ' +
                                                                  CASE WHEN i.[ColumnStore] = 1 THEN 'COLUMNSTORE ' ELSE '' END + 
	                                                              'INDEX [IndexName] ON ' + i.[Schema] + '.' + i.[TableName] + 
                                                                  CASE WHEN i.[ColumnStore] = 0 THEN ' (' + i.[IndexColumns] + ')' + CASE WHEN RTRIM(ISNULL(i.[IncludeColumns], '')) <> '' THEN ' INCLUDE (' + i.[IncludeColumns] + ')' ELSE '' END
                                                                       WHEN i.[ColumnStore] = 1 AND i.[Clustered] = 0 THEN ' (' + i.[IncludeColumns] + ')'
                                                                       ELSE '' END +
                                                                  -- #242: keyed on the OLD name -- the mapping row was written under the name the index has now.
                                                                  CASE WHEN RTRIM(ISNULL(i.[FilterExpression], '')) <> '' THEN ' WHERE ' + SchemaSmith.fn_ExpressionMapEffective(i.[Schema], i.[TableName], 'INDEX', ei.[xIndexName], 'filter', i.[FilterExpression], SchemaSmith.fn_StripParenWrapping((SELECT si3.filter_definition FROM sys.indexes si3 WHERE si3.[object_id] = OBJECT_ID(ei.[xSchema] + '.' + ei.[xTableName]) AND si3.[name] = ei.[xIndexName]))) ELSE '' END +
                                                                  CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                                         OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                                       THEN ' WITH (DATA_COMPRESSION=' + RTRIM(ISNULL(i.[CompressionType], '')) + ')'
                                                                       ELSE '' END

  -- Remove duplicates from the rename list
  SELECT MAX([NewName]) AS ValidNewName, [OldName] AS [OriginalName]
    INTO #IndexRenameDedupe
    FROM #IndexRenames ir WITH (NOLOCK)
    GROUP BY [OldName]  
  DELETE FROM #IndexRenames WHERE EXISTS (SELECT * FROM #IndexRenameDedupe dd WITH (NOLOCK) WHERE [OriginalName] = [OldName] AND [ValidNewName] <> [NewName])
  
  RAISERROR('Handle Renamed Indexes And Unique Constraints', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Renaming ' + [OldName] + ' to ' + [NewName] + ' ON ' + ir.[Schema] + '.' + ir.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN IsConstraint = 1
                                       THEN CASE WHEN OBJECT_ID(ir.[Schema] + '.' + ir.[NewName]) IS NULL
                                                 THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''OBJECT'';'
                                                 ELSE 'ALTER TABLE ' + ir.[Schema] + '.' + ir.[TableName] + ' DROP CONSTRAINT IF EXISTS [' + ir.[OldName] + '];'
                                                 END
                                       ELSE CASE WHEN INDEXPROPERTY(OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]), SchemaSmith.fn_StripBracketWrapping(ir.[NewName]), 'IndexID') IS NULL
                                                 THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(ir.[TableName]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''INDEX'';'
                                                 ELSE 'DROP INDEX IF EXISTS [' + ir.[OldName] + '] ON ' + ir.[Schema] + '.' + ir.[TableName] + ';'
                                                 END
                                       END AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #IndexRenames ir WITH (NOLOCK)
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Collect index level extended properties', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #IndexProperties
  SELECT t.[Schema], t.[Name] AS TableName, objname COLLATE DATABASE_DEFAULT AS IndexName, x.[Name] COLLATE DATABASE_DEFAULT AS PropertyName, CONVERT(NVARCHAR(50), x.[value]) COLLATE DATABASE_DEFAULT AS [value]
    INTO #IndexProperties
    FROM #Tables t WITH (NOLOCK)
    CROSS APPLY fn_listextendedproperty(default, 'Schema', SchemaSmith.fn_StripBracketWrapping(t.[Schema]), 'Table', SchemaSmith.fn_StripBracketWrapping(t.[Name]), 'Index', default) x
  WHERE x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'

  RAISERROR('Identify indexes removed from the product', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #IndexesRemovedFromProduct
  SELECT xp.[Schema], xp.TableName, xp.IndexName, IsConstraint = CAST(CASE WHEN OBJECT_ID(xp.[Schema] + '.' + xp.IndexName) IS NOT NULL THEN 1 ELSE 0 END AS BIT)
    INTO #IndexesRemovedFromProduct
    FROM #IndexProperties xp
    WHERE xp.[value] = @ProductName
      AND NOT EXISTS (SELECT *
                        FROM #Indexes i WITH (NOLOCK)
                        WHERE i.[Schema] = xp.[Schema]
                          AND i.TableName = xp.TableName
                          AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = xp.IndexName)
      AND NOT EXISTS (SELECT *
                        FROM #XmlIndexes i WITH (NOLOCK)
                        WHERE i.[Schema] = xp.[Schema]
                          AND i.TableName = xp.TableName
                          AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = xp.IndexName)
                      
  RAISERROR('Identify unknown and modified indexes to drop', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #IndexesToDrop
  -- Indexes removed from the prouduct
  SELECT [Schema] = CAST([Schema] AS NVARCHAR(500)), [TableName] = CAST([TableName] AS NVARCHAR(500)),
         [IndexName] = CAST(SchemaSmith.fn_StripBracketWrapping([IndexName]) AS NVARCHAR(500)), [IsConstraint], [IsUnique] = i.[is_unique],
         [IsClustered] = CAST(CASE WHEN i.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END AS BIT)
    INTO #IndexesToDrop
    FROM #IndexesRemovedFromProduct ir WITH (NOLOCK)
    JOIN sys.indexes i ON i.[object_id] = OBJECT_ID([Schema] + '.' + [TableName]) AND i.[Name] = SchemaSmith.fn_StripBracketWrapping([IndexName])
    -- Removed-from-product (ownership-stamped) drop is gated by the cascade flag + per-table
    -- tightening; the unknown (@DropUnknownIndexes) and modified branches below are unaffected.
    WHERE @DropIndexesRemovedFromProduct = 1
      AND ISNULL((SELECT t.[DropIndexesRemovedFromProduct] FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = ir.[Schema] AND t.[Name] = ir.[TableName]), 1) = 1
  UNION
  -- Unknown indexes if we're dropping unknown
  SELECT [Schema] = CAST([xSchema] AS NVARCHAR(500)), [TableName] = CAST([xTableName] AS NVARCHAR(500)), [IndexName] = CAST([xIndexName] AS NVARCHAR(500)), IsConstraint, IsUnique, IsClustered
    FROM #ExistingIndexes di WITH (NOLOCK)
    WHERE @DropUnknownIndexes = 1
      AND NOT EXISTS (SELECT * FROM #Indexes i WITH (NOLOCK) WHERE i.[Schema] = di.[xSchema] AND i.[TableName] = di.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = di.[xIndexName])
      -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
      -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
      AND NOT EXISTS (SELECT * FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = di.[xSchema] AND rn.[TableName] = di.[xTableName] AND rn.[OldName] = di.[xIndexName])
  UNION 
  -- Indexes where the index definition was modified
  SELECT i.[Schema], i.[TableName], SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), i.[IsConstraint], i.[IsUnique], i.[IsClustered]
    FROM #IndexChanges i WITH (NOLOCK)
  UNION
  -- Unknown xml indexes if we're dropping unknown
  SELECT [xSchema], [xTableName], [xIndexName], [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
    FROM #ExistingXmlIndexes ei WITH (NOLOCK)
    WHERE @DropUnknownIndexes = 1
      AND NOT EXISTS (SELECT * FROM #XmlIndexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
      -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
      -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
      AND NOT EXISTS (SELECT * FROM #XmlIndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
  UNION
  -- Xml Indexes where the index definition was modified
  SELECT [Schema], [TableName], SchemaSmith.fn_StripBracketWrapping([IndexName]), [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
    FROM #XmlIndexChanges WITH (NOLOCK)

  -- Need to drop all the XML indexes if we're removing the clustered PK
  INSERT #IndexesToDrop ([Schema], [TableName], [IndexName], [IsConstraint], [IsUnique], [IsClustered])
    SELECT [xSchema], [xTableName], [xIndexName], [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
      FROM #ExistingXmlIndexes ei WITH (NOLOCK)
      WHERE EXISTS (SELECT * FROM #IndexesToDrop id WITH (NOLOCK) WHERE [xSchema] = [Schema] AND [xTableName] = [TableName] AND id.[IsClustered] = 1)
        AND NOT EXISTS (SELECT * FROM #IndexesToDrop id WITH (NOLOCK) WHERE [xSchema] = [Schema] AND [xTableName] = [TableName] AND [xIndexName] = [IndexName])

  RAISERROR('Drop Referencing Foreign Keys When Dropping Unique Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping foreign Key ' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id) + '.' + fk.[name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT IF EXISTS [' + fk.[name] + '];' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #IndexesToDrop di WITH (NOLOCK)
    JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(di.[Schema] + '.' + di.[TableName])
    WHERE IsConstraint = 1 OR IsUnique = 1
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Drop FullText Indexes Referencing Unique Indexes That Will Be Dropped', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping fulltext index on ' + ef.[Schema] + '.' + ef.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP FULLTEXT INDEX ON ' + ef.[Schema] + '.' + ef.[TableName] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''fullTextIndex'', ''' + ef.[Schema] + '.' + ef.[TableName] + ''', ''dropped'');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #IndexesToDrop id WITH (NOLOCK)
    JOIN #ExistingFullTextIndexes ef WITH (NOLOCK) ON id.[Schema] = ef.[Schema]
                                                  AND id.[TableName] = ef.[TableName]
                                                  AND id.[IndexName] = SchemaSmith.fn_StripBracketWrapping(ef.[KeyIndex])
    JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(ef.[Schema] + '.' + ef.[TableName])
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'fullTextIndex'/'dropped' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'fullTextIndex', ef.[Schema] + '.' + ef.[TableName], 'wouldDrop'
        FROM #IndexesToDrop id WITH (NOLOCK)
        JOIN #ExistingFullTextIndexes ef WITH (NOLOCK) ON id.[Schema] = ef.[Schema]
                                                      AND id.[TableName] = ef.[TableName]
                                                      AND id.[IndexName] = SchemaSmith.fn_StripBracketWrapping(ef.[KeyIndex])
        JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(ef.[Schema] + '.' + ef.[TableName])

  -- No-drop protection tier (#270): when protected mode is active the caller forces @DropUnknownIndexes
  -- and @DropIndexesRemovedFromProduct to 0, so the unknown and removed-from-product branches of
  -- #IndexesToDrop above contribute nothing and the drop pass below skips them. Record the indexes that
  -- WOULD have been dropped by absence -- unknown/out-of-band, and product-owned indexes removed from the
  -- definition with the per-table cascade tightening not opting out -- to the ChangeAudit seam as
  -- 'dropSuppressed' so the run can surface a manifest. Same by-absence predicates as those branches minus the
  -- env gates; the modified branches are not by-absence and are never suppressed, so they are excluded.
  -- Audit rows only -- no DDL -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture indexes suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STRING_AGG(CAST(
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + w.[ObjectType] + ''', ''' + w.[Schema] + '.' + w.[TableName] + '.' + w.[IndexName] + ''', ''dropSuppressed'');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
      FROM (
        -- Indexes removed from the product (minus the @DropIndexesRemovedFromProduct gate; per-table opt-out kept)
        SELECT [Schema] = CAST(ir.[Schema] AS NVARCHAR(500)), [TableName] = CAST(ir.[TableName] AS NVARCHAR(500)),
               [IndexName] = CAST(SchemaSmith.fn_StripBracketWrapping(ir.[IndexName]) AS NVARCHAR(500)),
               [ObjectType] = CASE WHEN ir.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END
          FROM #IndexesRemovedFromProduct ir WITH (NOLOCK)
          JOIN sys.indexes i ON i.[object_id] = OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]) AND i.[Name] = SchemaSmith.fn_StripBracketWrapping(ir.[IndexName])
          WHERE ISNULL((SELECT t.[DropIndexesRemovedFromProduct] FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = ir.[Schema] AND t.[Name] = ir.[TableName]), 1) = 1
        UNION
        -- Unknown indexes (minus the @DropUnknownIndexes gate)
        SELECT CAST(di.[xSchema] AS NVARCHAR(500)), CAST(di.[xTableName] AS NVARCHAR(500)), CAST(di.[xIndexName] AS NVARCHAR(500)),
               CASE WHEN di.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END
          FROM #ExistingIndexes di WITH (NOLOCK)
          WHERE NOT EXISTS (SELECT * FROM #Indexes i WITH (NOLOCK) WHERE i.[Schema] = di.[xSchema] AND i.[TableName] = di.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = di.[xIndexName])
            -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
            -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
            AND NOT EXISTS (SELECT * FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = di.[xSchema] AND rn.[TableName] = di.[xTableName] AND rn.[OldName] = di.[xIndexName])
        UNION
        -- Unknown xml indexes (minus the @DropUnknownIndexes gate)
        SELECT CAST(ei.[xSchema] AS NVARCHAR(500)), CAST(ei.[xTableName] AS NVARCHAR(500)), CAST(ei.[xIndexName] AS NVARCHAR(500)), 'index'
          FROM #ExistingXmlIndexes ei WITH (NOLOCK)
          WHERE NOT EXISTS (SELECT * FROM #XmlIndexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
            -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
            -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
            AND NOT EXISTS (SELECT * FROM #XmlIndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
      ) w
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  RAISERROR('Drop Unknown and Modified Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping ' + CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + di.[Schema] + '.' + di.[TableName] + '.' + di.[IndexName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN IsConstraint = 1
                                       THEN 'ALTER TABLE ' + di.[Schema] + '.' + di.[TableName] + ' DROP CONSTRAINT IF EXISTS [' + di.[IndexName] + '];'
                                       ELSE 'DROP INDEX IF EXISTS [' + di.[IndexName] + '] ON ' + di.[Schema] + '.' + di.[TableName] + ';'
                                       END AS NVARCHAR(MAX)), CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY CASE WHEN [IsClustered] = 0 THEN 0 ELSE 1 END)
    FROM #IndexesToDrop di WITH (NOLOCK)
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Fixup Modified Fillfactors', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Fixup ' + CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END + ' fillfactor in ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ''', 10, 100) WITH NOWAIT; ' + 
                                  'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' REBUILD WITH (FILLFACTOR = ' + CONVERT(NVARCHAR(5), i.[FillFactor]) + ', SORT_IN_TEMPDB = ON);' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #ExistingIndexes ei WITH (NOLOCK)
    JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                 AND ei.[xTableName] = i.[TableName]
                                 AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
    WHERE i.[UpdateFillFactor] = 1
      AND ei.[FillFactor] <> i.[FillFactor]
      AND INDEXPROPERTY(OBJECT_ID(i.[Schema] + '.' + i.[TableName]), ei.[xIndexName], 'IndexID') IS NOT NULL
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Collect Existing Statistics Definitions', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #ExistingStats
  SELECT t.[Schema], [TableName] = t.[Name], [StatsName] = si.[Name],
         StatisticScript = 'CREATE STATISTICS ' +
                           '[' + si.[Name] + '] ON ' + t.[Schema] + '.' + t.[Name] + ' (' +
                           (SELECT STRING_AGG(CAST('[' + COL_NAME(ic.[object_id], ic.column_id) + ']' AS NVARCHAR(MAX)), ',') WITHIN GROUP (ORDER BY ic.stats_column_id)
                              FROM sys.stats_columns ic
                              WHERE si.[object_id] = ic.[object_id] AND si.stats_id = ic.stats_id) + ')' +
                           CASE WHEN si.has_filter = 1 THEN ' WHERE ' + SchemaSmith.fn_StripParenWrapping(si.filter_definition) ELSE '' END 
    INTO #ExistingStats 
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.stats si ON si.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                   AND auto_created = 0
                                   AND user_created = 1
                                   AND is_temporary = 0
                                   AND si.[Name] NOT LIKE 'stat[_]%'
                                   AND si.[Name] NOT LIKE 'hind[_]%'
    WHERE t.MissingTable = 0
  
  RAISERROR('Detect Statistics Changes', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #StatsChanges
  SELECT s.[Schema], s.[TableName], s.[StatisticName]
    INTO #StatsChanges
    FROM #Statistics s WITH (NOLOCK)
    JOIN #ExistingStats es WITH (NOLOCK) ON s.[Schema] = es.[Schema]
                                        AND s.[TableName] = es.[TableName]
                                        AND SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]) = es.[StatsName]
    -- StatisticName is bracket-wrapped at parse; wrapping it again read [[name]] against a live [name], so
    -- every statistic was re-created on every index-only deploy. Same form as ModifiedTableQuench now.
    WHERE es.StatisticScript <> 'CREATE STATISTICS ' + s.[StatisticName] + ' ON ' + s.[Schema] + '.' + s.[TableName] + ' (' + s.[Columns] + ')' +
                                CASE WHEN RTRIM(ISNULL(s.[FilterExpression], '')) <> '' THEN ' WHERE ' + SchemaSmith.fn_ExpressionMapEffective(s.[Schema], s.[TableName], 'STATISTIC', s.[StatisticName], 'filter', s.[FilterExpression], SchemaSmith.fn_StripParenWrapping((SELECT st2.filter_definition FROM sys.stats st2 WHERE st2.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName]) AND st2.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName])))) ELSE '' END
  
  RAISERROR('Drop Modified Statistics', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping statistics ' + sc.[Schema] + '.' + sc.[TableName] + '.' + sc.[StatisticName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP STATISTICS ' + sc.[Schema] + '.' + sc.[TableName] + '.' + sc.[StatisticName] + ';' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #StatsChanges sc WITH (NOLOCK)
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  -- A table can hold only one clustered index. When the product declares a clustered index that
  -- doesn't yet exist by name and a DIFFERENT clustered index already occupies the slot, drop the
  -- existing one first so the new clustered index/PK can be created (#302). The full table quench
  -- already does this; the index-only path did not, so replacing a clustered index in index-only
  -- mode failed with "Cannot create more than one clustered index" (1913).
  RAISERROR('Identify Existing Clustered Index Conflicts', 10, 100) WITH NOWAIT
  DROP TABLE IF EXISTS #MissingClusteredIndexTables
  SELECT DISTINCT i.[Schema], i.[TableName]
    INTO #MissingClusteredIndexTables
    FROM #Indexes i WITH (NOLOCK)
    WHERE i.[Clustered] = 1
      AND NOT EXISTS (SELECT *
                        FROM sys.indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))

  RAISERROR('Drop Conflicting Clustered Index', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping ' + CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + mct.[Schema] + '.' + mct.[TableName] + '.' + si.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1
                                       THEN 'ALTER TABLE ' + mct.[Schema] + '.' + mct.[TableName] + ' DROP CONSTRAINT IF EXISTS [' + si.[Name] + '];'
                                       ELSE 'DROP INDEX IF EXISTS [' + si.[Name] + '] ON ' + mct.[Schema] + '.' + mct.[TableName] + ';'
                                       END AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #MissingClusteredIndexTables mct WITH (NOLOCK)
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(mct.[Schema] + '.' + mct.[TableName])
                                     AND si.[type] IN (1, 5)
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Filegroup placement (#filegroups): a NEW index declaring a filegroup name that does not exist on this
  -- target must fail loudly BEFORE any DDL runs, naming both the index and the filegroup -- same contract as
  -- MissingIndexesAndConstraintsQuench.sql's check. Only indexes not yet present are checked; an
  -- already-existing index's declared vs. deployed filegroup is the "move" question, handled above.
  RAISERROR('Validate declared index filegroups exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Indexes i WITH (NOLOCK)
               WHERE i.[FileGroup] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.filegroups fg WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]))
                 AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])))
  BEGIN
    DECLARE @v_IdxFGIndex NVARCHAR(1510), @v_IdxFGName NVARCHAR(500)
    SELECT TOP 1 @v_IdxFGIndex = i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], @v_IdxFGName = i.[FileGroup]
      FROM #Indexes i WITH (NOLOCK)
      WHERE i.[FileGroup] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.filegroups fg WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(i.[FileGroup]))
        AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
    DECLARE @v_IdxFGMsg NVARCHAR(2000) = 'Index ' + @v_IdxFGIndex + ' declares filegroup ' + @v_IdxFGName +
      ', which does not exist on this database. SchemaSmith does not create filegroups -- create it on the target first, or correct the declared name.';
    THROW 51000, @v_IdxFGMsg, 1;
  END

  -- Partition placement (#partitioning, K1): the scheme must exist, same contract as the filegroup check
  -- above -- SchemaSmith creates neither.
  RAISERROR('Validate declared index partition schemes exist', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1
               FROM #Indexes i WITH (NOLOCK)
               WHERE i.[PartitionScheme] IS NOT NULL
                 AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]))
                 AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])))
  BEGIN
    DECLARE @v_IdxPsMissing NVARCHAR(1510), @v_IdxPsMissingName NVARCHAR(500)
    SELECT TOP 1 @v_IdxPsMissing = i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName], @v_IdxPsMissingName = i.[PartitionScheme]
      FROM #Indexes i WITH (NOLOCK)
      WHERE i.[PartitionScheme] IS NOT NULL
        AND NOT EXISTS (SELECT * FROM sys.partition_schemes ps WHERE ps.[name] = SchemaSmith.fn_StripBracketWrapping(i.[PartitionScheme]))
        AND NOT EXISTS (SELECT * FROM sys.indexes si WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
    DECLARE @v_IdxPsMissingMsg NVARCHAR(2000) = 'Index ' + @v_IdxPsMissing + ' declares partition scheme ' + @v_IdxPsMissingName +
      ', which does not exist on this database. SchemaSmith does not create partition functions or schemes -- create them on the target first, or correct the declared name.';
    THROW 51000, @v_IdxPsMissingMsg, 1;
  END

  RAISERROR('Add Missing Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Creating ' + CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + CASE WHEN RTRIM(ISNULL(i.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(i.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN i.PrimaryKey = 1 OR i.UniqueConstraint = 1
                                       THEN 'ALTER TABLE ' + i.[Schema] + '.' + i.[TableName] + ' ADD CONSTRAINT ' + i.[IndexName] +
                                            CASE WHEN i.PrimaryKey = 1 THEN ' PRIMARY KEY ' WHEN i.UniqueConstraint = 1 THEN ' UNIQUE ' END +
                                            CASE WHEN i.[Clustered] =  1 THEN '' ELSE 'NON' END + 'CLUSTERED (' + i.IndexColumns + ')' +
					                        CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE')
                                                   OR ISNULL(i.[FillFactor], 100) NOT IN (0, 100)
                                                 THEN ' WITH (' +
                                                      CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE') THEN 'DATA_COMPRESSION=' + i.[CompressionType] ELSE '' END +
                                                      CASE WHEN ISNULL(i.[FillFactor], 100) NOT IN (0, 100) 
                                                           THEN CASE WHEN RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE') THEN ', ' ELSE '' END +
                                                                'FILLFACTOR = ' + CAST(i.[FillFactor] AS NVARCHAR(20))
                                                           ELSE '' END +
                                                      CASE WHEN i.[IgnoreDuplicateKey] = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                                                      CASE WHEN i.[PadIndex] = 1 THEN ', PAD_INDEX=ON' ELSE '' END +

                                                      -- XML_COMPRESSION rides the same WITH list. Leading comma is safe for the
                                                      -- same reason PAD_INDEX's is: CompressionType is ISNULL'd to 'NONE' in the
                                                      -- parse, so DATA_COMPRESSION always leads. Gated on 2022 by VALUE; only the
                                                      -- catalog READ needs kindle-time composition, because that names a column.
                                                      CASE WHEN i.[XmlCompression] = 1 AND SchemaSmith.fn_ServerMajorVersion() >= 16 THEN ', XML_COMPRESSION=ON' ELSE '' END +
							                          ')'
                                                 ELSE '' END +
                                            -- Filegroup placement (#filegroups): ON comes AFTER the WITH
                                            -- clause for ADD CONSTRAINT, per its own grammar (unlike CREATE
                                            -- TABLE, where ON precedes WITH). Existence validated above.
                                            CASE WHEN i.[PartitionScheme] IS NOT NULL THEN ' ON ' + i.[PartitionScheme] + '(' + i.[PartitionColumn] + ')'
                                                 WHEN i.[FileGroup] IS NOT NULL THEN ' ON ' + i.[FileGroup] ELSE '' END
                                       ELSE 'CREATE ' +
                                            CASE WHEN i.[Unique] = 1 THEN 'UNIQUE ' ELSE '' END +
                                            CASE WHEN i.[Clustered] =  1 THEN '' ELSE 'NON' END + 'CLUSTERED ' +
                                            CASE WHEN i.[ColumnStore] = 1 THEN 'COLUMNSTORE ' ELSE '' END +
                                            'INDEX ' + i.[IndexName] +
                                            ' ON ' + i.[Schema] + '.' + i.[TableName] + 
                                            CASE WHEN i.[ColumnStore] = 0 THEN ' (' + i.[IndexColumns] + ')' + CASE WHEN RTRIM(ISNULL(i.[IncludeColumns], '')) <> '' THEN ' INCLUDE (' + i.[IncludeColumns] + ')' ELSE '' END
                                                 WHEN i.[ColumnStore] = 1 AND i.[Clustered] = 0 THEN ' (' + i.[IncludeColumns] + ')'
                                                 ELSE '' END +
                                            CASE WHEN RTRIM(ISNULL(i.[FilterExpression], '')) <> '' THEN ' WHERE ' + i.[FilterExpression] ELSE '' END +
					                        CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                   OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                   OR ISNULL(i.[FillFactor], 100) NOT IN (0, 100)
                                                 THEN ' WITH (' +
                                                      CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                             OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                           THEN 'DATA_COMPRESSION=' + i.[CompressionType] ELSE '' END +
                                                      CASE WHEN ISNULL(i.[FillFactor], 100) NOT IN (0, 100) 
                                                           THEN CASE WHEN (i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('NONE', 'ROW', 'PAGE'))
                                                                       OR (i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[CompressionType], '')) IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                                                                     THEN ', ' ELSE '' END +
                                                                'FILLFACTOR = ' + CAST(i.[FillFactor] AS NVARCHAR(20))
                                                           ELSE '' END +
                                                      CASE WHEN i.[IgnoreDuplicateKey] = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                                                      CASE WHEN i.[PadIndex] = 1 THEN ', PAD_INDEX=ON' ELSE '' END +

                                                      -- XML_COMPRESSION rides the same WITH list. Leading comma is safe for the
                                                      -- same reason PAD_INDEX's is: CompressionType is ISNULL'd to 'NONE' in the
                                                      -- parse, so DATA_COMPRESSION always leads. Gated on 2022 by VALUE; only the
                                                      -- catalog READ needs kindle-time composition, because that names a column.
                                                      CASE WHEN i.[XmlCompression] = 1 AND SchemaSmith.fn_ServerMajorVersion() >= 16 THEN ', XML_COMPRESSION=ON' ELSE '' END +
							                          ')'
                                                 ELSE '' END +
                                            -- Filegroup placement (#filegroups): ON comes AFTER the WITH
                                            -- clause for CREATE INDEX too. Existence validated above.
                                            CASE WHEN i.[PartitionScheme] IS NOT NULL THEN ' ON ' + i.[PartitionScheme] + '(' + i.[PartitionColumn] + ')'
                                                 WHEN i.[FileGroup] IS NOT NULL THEN ' ON ' + i.[FileGroup] ELSE '' END
                                       END + ';' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY i.[Schema], i.[TableName], CASE WHEN i.[Clustered] =  1 THEN 0 ELSE 1 END, i.[IndexName])
    FROM #Indexes i WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT * 
                        FROM sys.indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) 
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))    
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Add missing ProductName extended property to indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                  '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                  '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', ' +
                                  '@level2type = N''Index'', @level2name = ''' + SchemaSmith.fn_StripBracketWrapping(i.IndexName) + ''';' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Indexes i WITH (NOLOCK)
    JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = i.[Schema] AND t.[Name] = i.[TableName]
    WHERE INDEXPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name]), SchemaSmith.fn_StripBracketWrapping(i.IndexName), 'IndexID') IS NOT NULL
      AND NOT EXISTS (SELECT * FROM #IndexProperties ip WITH (NOLOCK) WHERE i.[Schema] = ip.[Schema] AND i.TableName = ip.TableName AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = ip.IndexName AND ip.PropertyName = 'ProductName')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Add Missing Xml Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Creating index ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + CASE WHEN RTRIM(ISNULL(i.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(i.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE ' + CASE WHEN i.IsPrimary = 1 THEN 'PRIMARY ' ELSE '' END + 
                                  'XML INDEX ' + i.[IndexName] COLLATE DATABASE_DEFAULT + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' (' + i.[Column] + ')' +
                                  CASE WHEN i.IsPrimary = 0
                                       THEN ' USING XML INDEX ' + i.PrimaryIndex + ' FOR ' + i.SecondaryIndexType
                                       ELSE '' END + ';' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10)) WITHIN GROUP (ORDER BY i.[Schema], i.[TableName], CASE WHEN i.IsPrimary =  1 THEN 0 ELSE 1 END, i.[IndexName])
    FROM #XmlIndexes i WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT * 
                        FROM sys.xml_indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) 
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))    
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Add missing ProductName extended property to xml indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                  '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                  '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', ' +
                                  '@level2type = N''Index'', @level2name = ''' + SchemaSmith.fn_StripBracketWrapping(i.IndexName) + ''';' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #XmlIndexes i WITH (NOLOCK)
    JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = i.[Schema] AND t.[Name] = i.[TableName]
    WHERE INDEXPROPERTY(OBJECT_ID(t.[Schema] + '.' + t.[Name]), SchemaSmith.fn_StripBracketWrapping(i.IndexName), 'IndexID') IS NOT NULL
      AND NOT EXISTS (SELECT * FROM #IndexProperties ip WITH (NOLOCK) WHERE i.[Schema] = ip.[Schema] AND i.TableName = ip.TableName AND SchemaSmith.fn_StripBracketWrapping(i.IndexName) = ip.IndexName AND ip.PropertyName = 'ProductName')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Add Missing Statistics', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Creating statistics ' + s.[Schema] + '.' + s.[TableName] + '.' + s.[StatisticName] + CASE WHEN RTRIM(ISNULL(s.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(s.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE STATISTICS ' + s.[StatisticName] + ' ON ' + s.[Schema] + '.' + s.[TableName] + ' (' + s.[Columns] + ')' +
                                  CASE WHEN RTRIM(ISNULL(s.[FilterExpression], '')) <> '' THEN ' WHERE ' + s.[FilterExpression] ELSE '' END +
                                  ' WITH SAMPLE ' + CAST(ISNULL(s.[SampleSize], 100) AS NVARCHAR(20)) + ' PERCENT;' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #Statistics s WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT * 
                        FROM sys.stats ss
                        WHERE ss.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName]) 
                          AND ss.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]))
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Drop Modified or Removed FullText Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Dropping fulltext index on ' + ei.[Schema] + '.' + ei.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP FULLTEXT INDEX ON ' + ei.[Schema] + '.' + ei.[TableName] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''fullTextIndex'', ''' + ei.[Schema] + '.' + ei.[TableName] + ''', ''dropped'');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #ExistingFullTextIndexes ei WITH (NOLOCK)
    LEFT JOIN #FullTextIndexes fi WITH (NOLOCK) ON fi.[Schema] = ei.[Schema]
                                               AND fi.[TableName] = ei.[TableName]
    JOIN sys.fulltext_indexes ft ON ft.[object_id] = OBJECT_ID(ei.[Schema] + '.' + ei.[TableName])
    WHERE RTRIM(ISNULL(fi.[Columns], '')) <> RTRIM(ISNULL(ei.[Columns], ''))
       OR SchemaSmith.fn_StripBracketWrapping(fi.[FullTextCatalog]) <> SchemaSmith.fn_StripBracketWrapping(ei.[FullTextCatalog])
       OR SchemaSmith.fn_StripBracketWrapping(fi.[KeyIndex]) <> SchemaSmith.fn_StripBracketWrapping(ei.[KeyIndex])
       OR fi.[ChangeTracking] <> ei.[ChangeTracking]
       OR RTRIM(ISNULL(fi.[StopList], '')) <> RTRIM(ISNULL(ei.[StopList], ''))
       OR fi.[TableName] IS NULL
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'fullTextIndex'/'dropped' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'fullTextIndex', ei.[Schema] + '.' + ei.[TableName], 'wouldDrop'
        FROM #ExistingFullTextIndexes ei WITH (NOLOCK)
        LEFT JOIN #FullTextIndexes fi WITH (NOLOCK) ON fi.[Schema] = ei.[Schema]
                                                   AND fi.[TableName] = ei.[TableName]
        JOIN sys.fulltext_indexes ft ON ft.[object_id] = OBJECT_ID(ei.[Schema] + '.' + ei.[TableName])
        WHERE RTRIM(ISNULL(fi.[Columns], '')) <> RTRIM(ISNULL(ei.[Columns], ''))
           OR SchemaSmith.fn_StripBracketWrapping(fi.[FullTextCatalog]) <> SchemaSmith.fn_StripBracketWrapping(ei.[FullTextCatalog])
           OR SchemaSmith.fn_StripBracketWrapping(fi.[KeyIndex]) <> SchemaSmith.fn_StripBracketWrapping(ei.[KeyIndex])
           OR fi.[ChangeTracking] <> ei.[ChangeTracking]
           OR RTRIM(ISNULL(fi.[StopList], '')) <> RTRIM(ISNULL(ei.[StopList], ''))
           OR fi.[TableName] IS NULL

  RAISERROR('Add Missing FullText Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Adding fulltext index on ' + fi.[Schema] + '.' + fi.[TableName] + CASE WHEN RTRIM(ISNULL(fi.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(fi.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'CREATE FULLTEXT INDEX ON ' + fi.[Schema] + '.' + fi.[TableName] + ' (' + [Columns] + ') KEY INDEX ' + [KeyIndex] + ' ON ' + [FullTextCatalog] + 
                                  ' WITH CHANGE_TRACKING = ' + [ChangeTracking] +
                                  CASE WHEN RTRIM(ISNULL(fi.[StopList], '')) <> '' THEN ', STOPLIST = ' + [StopList] ELSE '' END + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''fullTextIndex'', ''' + fi.[Schema] + '.' + fi.[TableName] + ''', ''created'');' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
    FROM #FullTextIndexes fi WITH (NOLOCK)
    WHERE NOT EXISTS (SELECT * FROM sys.fulltext_indexes ft WHERE ft.[object_id] = OBJECT_ID(fi.[Schema] + '.' + fi.[TableName]))
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'fullTextIndex'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'fullTextIndex', fi.[Schema] + '.' + fi.[TableName], 'wouldCreate'
        FROM #FullTextIndexes fi WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT * FROM sys.fulltext_indexes ft WHERE ft.[object_id] = OBJECT_ID(fi.[Schema] + '.' + fi.[TableName]))

  -- #242: record here, not in the caller -- this proc's temp tables are gone the moment it returns.
  EXEC SchemaSmith.ExpressionMapRecord @WhatIf
  
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  THROW
END CATCH