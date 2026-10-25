-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedColumnStore', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedColumnStore
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedColumnStore
AS
BEGIN
  -- Unsupported-feature policy for what the target cannot do with an index in #Indexes: columnstore indexes, and
  -- rowstore index compression where the edition lacks it. Operates on the caller's #Indexes (deferred name resolution
  -- at CREATE time); called from MissingTableAndColumnQuench (the TableQuench path) and IndexOnly[Xml]Quench (the
  -- --IndexOnly path), so both paths degrade the same way. 'fail' aborts naming every offending index; 'warn'
  -- (default) records one 'downgraded' manifest row per index and either drops it from #Indexes (columnstore) or
  -- clears its compression, so every downstream create/modify pass simply never sees it.
  --
  -- A columnstore index is skipped for one of four reasons, first match wins:
  --   * below its introduction: nonclustered 2012 (major 11), clustered 2014 (12);
  --   * an edition without columnstore below 2016 SP1 (fn_EnterpriseFeaturesUnavailable);
  --   * nonclustered on 2012/2014, where it makes the table read-only: creating it would change what the
  --     application can do, so it waits for 2016;
  --   * clustered beside rowstore indexes on 2014, which refuses the combination (35304); the rowstore indexes carry
  --     keys and uniqueness, so the columnstore is the one skipped.
  --   * clustered over a (max) or a non-persisted computed column below 2017 (35343, 35307 on 2016), new indexes only.
  -- The last three only stop SchemaSmith CREATING the index. One that already exists on the table was made on purpose
  -- on this server, and leaving it out of the working set would let a later pass drop it.
  DECLARE @v_Major INT = SchemaSmith.fn_ServerMajorVersion()
  DECLARE @v_NoEnterprise BIT = SchemaSmith.fn_EnterpriseFeaturesUnavailable()

  CREATE TABLE #IndexDegrades ([Schema] NVARCHAR(500) COLLATE DATABASE_DEFAULT, [TableName] NVARCHAR(500) COLLATE DATABASE_DEFAULT,
                               [IndexName] NVARCHAR(500) COLLATE DATABASE_DEFAULT, [ObjectType] NVARCHAR(200) COLLATE DATABASE_DEFAULT,
                               [Reason] NVARCHAR(200) COLLATE DATABASE_DEFAULT, [DropIndex] BIT)

  -- Tables whose columns a clustered columnstore cannot hold below 2017: the declared columns where the TableQuench
  -- path parsed them, otherwise (--IndexOnly, where the table exists) the live ones.
  CREATE TABLE #CciBlockers ([Schema] NVARCHAR(500) COLLATE DATABASE_DEFAULT, [TableName] NVARCHAR(500) COLLATE DATABASE_DEFAULT)
  IF @v_Major < 14
  BEGIN
    IF OBJECT_ID('tempdb..#Columns') IS NOT NULL
      INSERT INTO #CciBlockers ([Schema], [TableName])
        SELECT DISTINCT c.[Schema], c.[TableName]
          FROM #Columns c WITH (NOLOCK)
          WHERE c.[DataType] LIKE '%(MAX)%' OR (RTRIM(ISNULL(c.[ComputedExpression], '')) <> '' AND ISNULL(c.[Persisted], 0) = 0)
    INSERT INTO #CciBlockers ([Schema], [TableName])
      SELECT DISTINCT i.[Schema], i.[TableName]
        FROM #Indexes i WITH (NOLOCK)
        JOIN sys.columns sc ON sc.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
        LEFT JOIN sys.computed_columns cc ON cc.[object_id] = sc.[object_id] AND cc.column_id = sc.column_id
        WHERE sc.max_length = -1 OR (cc.column_id IS NOT NULL AND cc.is_persisted = 0)
  END

  INSERT INTO #IndexDegrades ([Schema], [TableName], [IndexName], [ObjectType], [Reason], [DropIndex])
    SELECT i.[Schema], i.[TableName], i.[IndexName], d.[ObjectType], d.[Reason], 1
      FROM #Indexes i WITH (NOLOCK)
      CROSS APPLY (SELECT CASE
                     WHEN @v_Major < CASE WHEN i.[Clustered] = 1 THEN 12 ELSE 11 END
                       THEN 'columnstore index (SQL Server 2012/2014)'
                     WHEN @v_NoEnterprise = 1
                       THEN 'columnstore index (Enterprise edition below SQL Server 2016 SP1)'
                     WHEN @v_Major < 13 AND i.[Clustered] = 0 AND INDEXPROPERTY(OBJECT_ID(i.[Schema] + '.' + i.[TableName]), SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'IndexID') IS NULL
                       THEN 'nonclustered columnstore index (writable from SQL Server 2016)'
                     WHEN @v_Major < 13 AND i.[Clustered] = 1
                          AND INDEXPROPERTY(OBJECT_ID(i.[Schema] + '.' + i.[TableName]), SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'IndexID') IS NULL
                          AND EXISTS (SELECT 1 FROM #Indexes o WITH (NOLOCK)
                                       WHERE o.[Schema] = i.[Schema] AND o.[TableName] = i.[TableName] AND o.[ColumnStore] = 0)
                       THEN 'clustered columnstore beside rowstore indexes (SQL Server 2016)'
                     WHEN @v_Major < 14 AND i.[Clustered] = 1
                          AND INDEXPROPERTY(OBJECT_ID(i.[Schema] + '.' + i.[TableName]), SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), 'IndexID') IS NULL
                          AND EXISTS (SELECT 1 FROM #CciBlockers b WHERE b.[Schema] = i.[Schema] AND b.[TableName] = i.[TableName])
                       THEN 'columnstore over a (max) or computed column (SQL Server 2017)'
                   END AS [ObjectType]) t
      CROSS APPLY (SELECT t.[ObjectType], CASE t.[ObjectType]
                     WHEN 'columnstore index (SQL Server 2012/2014)'
                       THEN 'Columnstore indexes require SQL Server 2012 (nonclustered) / 2014 (clustered)'
                     WHEN 'columnstore index (Enterprise edition below SQL Server 2016 SP1)'
                       THEN 'Columnstore indexes require Enterprise or Developer edition below SQL Server 2016 SP1'
                     WHEN 'nonclustered columnstore index (writable from SQL Server 2016)'
                       THEN 'A nonclustered columnstore index makes its table read-only below SQL Server 2016'
                     WHEN 'clustered columnstore beside rowstore indexes (SQL Server 2016)'
                       THEN 'A clustered columnstore index cannot sit beside rowstore indexes below SQL Server 2016'
                     ELSE 'A clustered columnstore index cannot include a (max) or non-persisted computed column below SQL Server 2017'
                   END AS [Reason]) d
      WHERE i.[ColumnStore] = 1 AND t.[ObjectType] IS NOT NULL

  -- Rowstore index compression where the edition lacks it: the index is kept and created uncompressed.
  INSERT INTO #IndexDegrades ([Schema], [TableName], [IndexName], [ObjectType], [Reason], [DropIndex])
    SELECT i.[Schema], i.[TableName], i.[IndexName], 'data compression (Enterprise edition below SQL Server 2016 SP1)',
           'Data compression requires Enterprise or Developer edition below SQL Server 2016 SP1', 0
      FROM #Indexes i WITH (NOLOCK)
      WHERE @v_NoEnterprise = 1 AND i.[ColumnStore] = 0 AND RTRIM(ISNULL(i.[CompressionType], 'NONE')) IN ('ROW', 'PAGE')

  -- An ordered columnstore index (ORDER): clustered from 2022, nonclustered from 2025. Kept, and created unordered.
  INSERT INTO #IndexDegrades ([Schema], [TableName], [IndexName], [ObjectType], [Reason], [DropIndex])
    SELECT i.[Schema], i.[TableName], i.[IndexName], 'columnstore index without its ORDER',
           'An ordered columnstore index requires SQL Server 2022 (clustered) / 2025 (nonclustered)', 0
      FROM #Indexes i WITH (NOLOCK)
      WHERE i.[ColumnStore] = 1 AND RTRIM(ISNULL(i.[Order], '')) <> ''
        AND (@v_Major < CASE WHEN i.[Clustered] = 1 THEN 16 ELSE 17 END OR {{ColumnStoreOrderCanRead}} = 0)
        AND NOT EXISTS (SELECT 1 FROM #IndexDegrades x WHERE x.[Schema] = i.[Schema] AND x.[TableName] = i.[TableName] AND x.[IndexName] = i.[IndexName])

  IF NOT EXISTS (SELECT 1 FROM #IndexDegrades) RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_List NVARCHAR(MAX) = STUFF((SELECT '; ' + d.[Schema] + '.' + d.[TableName] + '.' + d.[IndexName] + ': ' + d.[Reason]
                                             FROM #IndexDegrades d
                                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    DECLARE @v_FailMsg NVARCHAR(2048) = LEFT(@v_List, 1900) + ' (detected major ' + CONVERT(NVARCHAR(10), @v_Major) + ').'
    RAISERROR(@v_FailMsg, 16, 1)
    RETURN
  END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, d.[ObjectType], d.[Schema] + '.' + d.[TableName] + '.' + d.[IndexName], 'downgraded'
      FROM #IndexDegrades d

  DECLARE @v_Named NVARCHAR(MAX)
  IF EXISTS (SELECT 1 FROM #IndexDegrades WHERE [DropIndex] = 1)
  BEGIN
    SET @v_Named = STUFF((SELECT ', ' + d.[Schema] + '.' + d.[TableName] + '.' + d.[IndexName] FROM #IndexDegrades d WITH (NOLOCK) WHERE d.[DropIndex] = 1
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    EXEC SchemaSmith.PrintDowngrade N'  Columnstore index skipped (not supported on this server version or edition - downgraded)', @v_Named
  END
  IF EXISTS (SELECT 1 FROM #IndexDegrades WHERE [DropIndex] = 0 AND [ObjectType] LIKE 'data compression%')
  BEGIN
    SET @v_Named = STUFF((SELECT ', ' + d.[Schema] + '.' + d.[TableName] + '.' + d.[IndexName] FROM #IndexDegrades d WITH (NOLOCK) WHERE d.[DropIndex] = 0 AND d.[ObjectType] LIKE 'data compression%'
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    EXEC SchemaSmith.PrintDowngrade N'  Index compression skipped (Enterprise edition below SQL Server 2016 SP1 - downgraded)', @v_Named
  END
  IF EXISTS (SELECT 1 FROM #IndexDegrades WHERE [ObjectType] = 'columnstore index without its ORDER')
  BEGIN
    SET @v_Named = STUFF((SELECT ', ' + d.[Schema] + '.' + d.[TableName] + '.' + d.[IndexName] FROM #IndexDegrades d WITH (NOLOCK) WHERE d.[ObjectType] = 'columnstore index without its ORDER'
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    EXEC SchemaSmith.PrintDowngrade N'  Columnstore index created without its ORDER (SQL Server 2022 clustered / 2025 nonclustered - downgraded)', @v_Named
  END

  DELETE i
    FROM #Indexes i
    JOIN #IndexDegrades d ON d.[Schema] = i.[Schema] AND d.[TableName] = i.[TableName] AND d.[IndexName] = i.[IndexName]
    WHERE d.[DropIndex] = 1

  UPDATE i SET [CompressionType] = 'NONE'
    FROM #Indexes i
    JOIN #IndexDegrades d ON d.[Schema] = i.[Schema] AND d.[TableName] = i.[TableName] AND d.[IndexName] = i.[IndexName]
    WHERE d.[DropIndex] = 0 AND d.[ObjectType] LIKE 'data compression%'

  UPDATE i SET [Order] = NULL
    FROM #Indexes i
    JOIN #IndexDegrades d ON d.[Schema] = i.[Schema] AND d.[TableName] = i.[TableName] AND d.[IndexName] = i.[IndexName]
    WHERE d.[ObjectType] = 'columnstore index without its ORDER'
END
