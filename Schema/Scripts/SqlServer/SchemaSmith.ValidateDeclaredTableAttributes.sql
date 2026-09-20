-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ValidateDeclaredTableAttributes', 'P') IS NOT NULL
  DROP PROCEDURE SchemaSmith.ValidateDeclaredTableAttributes
GO

-- Refuses a declaration that disagrees with a deployed table on an attribute SQL Server cannot ALTER:
-- filegroup / LOB / FILESTREAM placement, partition scheme and column, GraphType, MemoryOptimized and
-- Durability, and Ledger. Every check here names the table and the disagreement rather than attempting a
-- statement that would rewrite every row, or one SQL Server has no ALTER for at all.
--
-- WHY THIS IS ITS OWN PROCEDURE. It was 328 lines inside ModifiedTableQuench, and SQL Server compiles the
-- statements of a procedure whether or not they run: measured on this server, 40 catalog statements behind
-- a guard that is always false still cost 14x the plan size (272 KB -> 3800 KB) and 17x the first-call time
-- (83ms -> 1419ms). ModifiedTableQuench's plan was ~22 MB and cost ~2.6s to compile for ~117ms of actual
-- work -- paid per DATABASE, because SQL Server caches plans per object per database, so a fresh database
-- never reuses one. Most deploys declare none of these attributes and touch no table that has them, so
-- moving the checks behind a call means they are not compiled at all on the common path.
--
-- The caller decides whether to call this (see the probe in ModifiedTableQuench). That probe considers BOTH
-- what the package declares AND what the catalog holds: a package that is SILENT about an attribute the
-- live table has is exactly the case that must be refused, so a declaration-only guard would skip the
-- refusal and let the run attempt ALTERs on, say, a memory-optimized table.
--
-- Reads #Tables and #Indexes from the caller (SQL Server scopes caller temp tables into called procedures)
-- and owns every #Deployed* table it creates. Takes no parameters: the CdcFilegroup template default is
-- RESOLVED into #Tables by the caller before this runs, because that resolution must happen on every
-- deploy, not only the ones that reach validation.
CREATE PROCEDURE SchemaSmith.ValidateDeclaredTableAttributes
AS
BEGIN
  SET NOCOUNT ON

  RAISERROR('Validate declared table filegroup matches deployed', 10, 100) WITH NOWAIT
  -- A partitioned table's heap/clustered data_space_id names a partition SCHEME, not a filegroup, so it has
  -- no single filegroup to compare against and ISNULL(...,'') would read as "on no filegroup" and mismatch
  -- every partitioned table. Resolve the data space once and branch on its type instead.
  -- An UNSET FileGroup means "SchemaSmith does not manage placement here" -- it is NOT a declaration of
  -- the default filegroup. Comparing ISNULL(declared, <db default>) made every undeclared object read as
  -- declaring PRIMARY, so anything already living elsewhere failed its SECOND deploy: a table whose own
  -- filegroup is declared but whose indexes are not (an index created with no ON clause follows its
  -- table, not the database default), and any pre-existing DBA placement in a package that never
  -- mentions filegroups -- which deployed fine before this feature existed. The first deploy always
  -- succeeded, so a single-deploy test cannot see it.
  -- Trade-off: clearing a declared FileGroup to move an object back to the default is now a silent
  -- no-op rather than an error. That is the correct side to err on -- SchemaSmith never moves objects
  -- between filegroups anyway, so the alternative is failing a package for a move it would refuse.
  IF OBJECT_ID('tempdb..#DeployedTablePlacement') IS NOT NULL DROP TABLE #DeployedTablePlacement
  SELECT t.[Schema] + '.' + t.[Name] AS FullName,
         SchemaSmith.fn_StripBracketWrapping(t.[FileGroup]) AS Declared,
         t.[FileGroup] AS DeclaredRaw,
         ds.[name] AS DeployedSpace,
         ds.[type] AS DeployedSpaceType,
         -- Partition placement (#partitioning, K1): carried on the SAME row so the checks below compare
         -- both halves of the declaration against one resolved data space, rather than re-reading the
         -- catalog per check and risking two answers.
         SchemaSmith.fn_StripBracketWrapping(t.[PartitionScheme]) AS DeclaredScheme,
         SchemaSmith.fn_StripBracketWrapping(t.[PartitionColumn]) AS DeclaredPartitionColumn,
         (SELECT pc.[name]
            FROM sys.index_columns pic WITH (NOLOCK)
            JOIN sys.columns pc WITH (NOLOCK) ON pc.[object_id] = pic.[object_id] AND pc.column_id = pic.column_id
           WHERE pic.[object_id] = si.[object_id] AND pic.index_id = si.index_id
             AND pic.partition_ordinal = 1) AS DeployedPartitionColumn
    INTO #DeployedTablePlacement
    FROM #Tables t WITH (NOLOCK)
    LEFT JOIN sys.indexes si WITH (NOLOCK)
      ON si.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name]) AND si.index_id IN (0, 1)
    LEFT JOIN sys.data_spaces ds WITH (NOLOCK) ON ds.data_space_id = si.data_space_id
   WHERE t.NewTable = 0

  IF EXISTS (SELECT 1 FROM #DeployedTablePlacement WHERE DeclaredRaw IS NOT NULL AND DeployedSpaceType = 'FG' AND Declared <> DeployedSpace)
  BEGIN
    DECLARE @v_MoveTable NVARCHAR(1010), @v_MoveDeclared NVARCHAR(500), @v_MoveLive NVARCHAR(500)
    SELECT TOP 1 @v_MoveTable = FullName, @v_MoveDeclared = Declared, @v_MoveLive = DeployedSpace
      FROM #DeployedTablePlacement
     WHERE DeclaredRaw IS NOT NULL AND DeployedSpaceType = 'FG' AND Declared <> DeployedSpace
    RAISERROR('Table %s declares filegroup %s, but is currently deployed on filegroup %s. SchemaSmith does not move an existing table to a different filegroup (that is a rebuild) -- migrate it manually, or correct the declared filegroup to match.', 16, 1, @v_MoveTable, @v_MoveDeclared, @v_MoveLive)
  END

  -- The other two placement clauses. Same posture as FileGroup above: neither TEXTIMAGE_ON nor
  -- FILESTREAM_ON has an ALTER, so a declared name that differs from the live one is refused rather
  -- than silently ignored. Read from the table's own lob/filestream data spaces, which is why this
  -- cannot reuse the index-based lookup the FileGroup check uses.
  IF OBJECT_ID('tempdb..#DeployedLobPlacement') IS NOT NULL DROP TABLE #DeployedLobPlacement
  SELECT t.[Schema] + '.' + t.[Name] AS FullName,
         [Clause] = CONVERT(NVARCHAR(20), 'TextImageFileGroup'),
         SchemaSmith.fn_StripBracketWrapping(t.[TextImageFileGroup]) AS Declared,
         lds.[name] AS DeployedSpace
    INTO #DeployedLobPlacement
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    LEFT JOIN sys.data_spaces lds WITH (NOLOCK) ON lds.data_space_id = st.lob_data_space_id
   WHERE t.NewTable = 0 AND t.[TextImageFileGroup] IS NOT NULL
  UNION ALL
  SELECT t.[Schema] + '.' + t.[Name],
         'FileStreamFileGroup',
         SchemaSmith.fn_StripBracketWrapping(t.[FileStreamFileGroup]),
         fds.[name]
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    LEFT JOIN sys.data_spaces fds WITH (NOLOCK) ON fds.data_space_id = st.filestream_data_space_id
   WHERE t.NewTable = 0 AND t.[FileStreamFileGroup] IS NOT NULL

  IF EXISTS (SELECT 1 FROM #DeployedLobPlacement WHERE DeployedSpace IS NOT NULL AND Declared <> DeployedSpace)
  BEGIN
    DECLARE @v_LobTable NVARCHAR(1010), @v_LobClause NVARCHAR(20), @v_LobDeclared NVARCHAR(500), @v_LobLive NVARCHAR(500)
    SELECT TOP 1 @v_LobTable = FullName, @v_LobClause = Clause, @v_LobDeclared = Declared, @v_LobLive = DeployedSpace
      FROM #DeployedLobPlacement
     WHERE DeployedSpace IS NOT NULL AND Declared <> DeployedSpace
    RAISERROR('Table %s declares %s %s, but its data is currently on filegroup %s. SchemaSmith does not move an existing table''s large-object or FILESTREAM data to a different filegroup -- there is no ALTER for it. Migrate it manually, or correct the declared filegroup to match.', 16, 1, @v_LobTable, @v_LobClause, @v_LobDeclared, @v_LobLive)
  END

  -- An explicit FileGroup on a table living on a partition scheme is a placement we cannot honour, so it is
  -- refused rather than silently ignored. Leaving FileGroup unset on such a table stays supported untouched.
  IF EXISTS (SELECT 1 FROM #DeployedTablePlacement
              WHERE DeclaredRaw IS NOT NULL AND DeployedSpaceType IS NOT NULL AND DeployedSpaceType <> 'FG')
  BEGIN
    DECLARE @v_PsTable NVARCHAR(1010), @v_PsDeclared NVARCHAR(500), @v_PsScheme NVARCHAR(500)
    SELECT TOP 1 @v_PsTable = FullName, @v_PsDeclared = Declared, @v_PsScheme = DeployedSpace
      FROM #DeployedTablePlacement
     WHERE DeclaredRaw IS NOT NULL AND DeployedSpaceType IS NOT NULL AND DeployedSpaceType <> 'FG'
    RAISERROR('Table %s declares filegroup %s, but is currently deployed on partition scheme %s. SchemaSmith cannot place a partitioned table on a single filegroup -- remove the declared FileGroup, or migrate the table manually.', 16, 1, @v_PsTable, @v_PsDeclared, @v_PsScheme)
  END


  -- A filegroup that does not exist would otherwise surface as sp_cdc_enable_table's own error from the middle
  -- of the run, after the column work -- so refuse it up front, naming the table and the setting.
  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE t.EnableCDC = 1 AND t.CdcFilegroup IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM sys.filegroups fg WITH (NOLOCK) WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup)))
  BEGIN
    DECLARE @v_CdcFgTable NVARCHAR(1010), @v_CdcFgName NVARCHAR(500)
    SELECT TOP 1 @v_CdcFgTable = t.[Schema] + '.' + t.[Name], @v_CdcFgName = SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup)
      FROM #Tables t WITH (NOLOCK)
     WHERE t.EnableCDC = 1 AND t.CdcFilegroup IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.filegroups fg WITH (NOLOCK) WHERE fg.[name] = SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup))
    RAISERROR('Table %s declares CdcFilegroup %s (on the table or as the template default), but this database has no filegroup by that name. Create it (ALTER DATABASE ... ADD FILEGROUP, then ADD FILE ... TO FILEGROUP), or correct CdcFilegroup.', 16, 1, @v_CdcFgTable, @v_CdcFgName)
  END

  -- Partition placement (#partitioning, K1) -- ADOPT AND VERIFY, the other half of the create-side apply.
  -- Every disagreement below describes a statement that REWRITES EVERY ROW of the table, and a state-based
  -- diff cannot derive the SPLIT/MERGE intent behind a boundary change from two layouts -- it can only see
  -- that they differ. So each one is refused by name rather than attempted.
  --
  -- An UNSET PartitionScheme means "SchemaSmith does not manage placement here", exactly as an unset
  -- FileGroup does: a package that never mentions partitioning must keep deploying against a partitioned
  -- table it inherited, which is how the pre-existing DBA-partitioned table stays supported. Only a
  -- DECLARED scheme is compared.
  RAISERROR('Validate declared partition scheme matches deployed', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1 FROM #DeployedTablePlacement
              WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType = 'PS' AND DeclaredScheme <> DeployedSpace)
  BEGIN
    DECLARE @v_PsMoveTable NVARCHAR(1010), @v_PsMoveDeclared NVARCHAR(500), @v_PsMoveLive NVARCHAR(500)
    SELECT TOP 1 @v_PsMoveTable = FullName, @v_PsMoveDeclared = DeclaredScheme, @v_PsMoveLive = DeployedSpace
      FROM #DeployedTablePlacement
     WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType = 'PS' AND DeclaredScheme <> DeployedSpace
    RAISERROR('Table %s declares partition scheme %s, but is currently deployed on partition scheme %s. SchemaSmith does not move an existing table between partition schemes -- that rewrites every row. Migrate it manually, or correct the declared scheme to match.', 16, 1, @v_PsMoveTable, @v_PsMoveDeclared, @v_PsMoveLive)
  END

  -- Same scheme, different column: the function is applied to a different column, which is a different
  -- physical layout even though the scheme name matches. Comparing only the name would let this through.
  IF EXISTS (SELECT 1 FROM #DeployedTablePlacement
              WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType = 'PS'
                AND DeclaredPartitionColumn IS NOT NULL AND DeployedPartitionColumn IS NOT NULL
                AND DeclaredPartitionColumn <> DeployedPartitionColumn)
  BEGIN
    DECLARE @v_PsColTable NVARCHAR(1010), @v_PsColDeclared NVARCHAR(500), @v_PsColLive NVARCHAR(500)
    SELECT TOP 1 @v_PsColTable = FullName, @v_PsColDeclared = DeclaredPartitionColumn, @v_PsColLive = DeployedPartitionColumn
      FROM #DeployedTablePlacement
     WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType = 'PS'
       AND DeclaredPartitionColumn IS NOT NULL AND DeployedPartitionColumn IS NOT NULL
       AND DeclaredPartitionColumn <> DeployedPartitionColumn
    RAISERROR('Table %s declares partition column %s, but is currently partitioned on %s. Repartitioning on a different column rewrites every row -- migrate it manually, or correct the declared column to match.', 16, 1, @v_PsColTable, @v_PsColDeclared, @v_PsColLive)
  END

  -- Declaring a scheme on a table that is NOT partitioned: adopting an existing table into partitioning is
  -- the same whole-table rewrite as moving between schemes, so it is refused the same way. DeployedSpace is
  -- named so the message says which way round the disagreement runs.
  IF EXISTS (SELECT 1 FROM #DeployedTablePlacement
              WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType IS NOT NULL AND DeployedSpaceType <> 'PS')
  BEGIN
    DECLARE @v_PsAdoptTable NVARCHAR(1010), @v_PsAdoptDeclared NVARCHAR(500), @v_PsAdoptLive NVARCHAR(500)
    SELECT TOP 1 @v_PsAdoptTable = FullName, @v_PsAdoptDeclared = DeclaredScheme, @v_PsAdoptLive = DeployedSpace
      FROM #DeployedTablePlacement
     WHERE DeclaredScheme IS NOT NULL AND DeployedSpaceType IS NOT NULL AND DeployedSpaceType <> 'PS'
    RAISERROR('Table %s declares partition scheme %s, but is currently deployed unpartitioned on filegroup %s. SchemaSmith does not partition an existing table -- that rewrites every row. Migrate it manually, or remove the declared PartitionScheme.', 16, 1, @v_PsAdoptTable, @v_PsAdoptDeclared, @v_PsAdoptLive)
  END

  RAISERROR('Validate declared GraphType matches deployed', 10, 100) WITH NOWAIT
  -- Graph tables are create-time only: SQL Server has no ALTER for them at all -- ALTER TABLE ... SET
  -- (AS NODE) is error 156, not even syntax. So a declaration that disagrees with the deployed table
  -- cannot be applied, and the choice is refuse or silently ignore. Refusing names the table and the
  -- property; ignoring would leave a package permanently claiming something untrue about its target.
  --
  -- sys.tables.is_node / is_edge are 2017+ and this is the JSON tier (2017 floor), but the same proc
  -- body is kindled for the XML tier, which reaches older servers -- hence the version guard around the
  -- read rather than a static reference.
  IF SchemaSmith.fn_ServerMajorVersion() >= 14
  BEGIN
    IF OBJECT_ID('tempdb..#DeployedGraphType') IS NOT NULL DROP TABLE #DeployedGraphType
    CREATE TABLE #DeployedGraphType (FullName NVARCHAR(1010), Declared NVARCHAR(10), Deployed NVARCHAR(10))
    EXEC sp_executesql N'
      INSERT INTO #DeployedGraphType (FullName, Declared, Deployed)
        SELECT t.[Schema] + ''.'' + t.[Name],
               ISNULL(NULLIF(t.[GraphType], ''''), ''None''),
               CASE WHEN st.is_node = 1 THEN ''Node'' WHEN st.is_edge = 1 THEN ''Edge'' ELSE ''None'' END
          FROM #Tables t WITH (NOLOCK)
          JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name])
         WHERE t.NewTable = 0'

    IF EXISTS (SELECT 1 FROM #DeployedGraphType WHERE Declared <> Deployed)
    BEGIN
      DECLARE @v_GraphTable NVARCHAR(1010), @v_GraphDeclared NVARCHAR(10), @v_GraphLive NVARCHAR(10)
      SELECT TOP 1 @v_GraphTable = FullName, @v_GraphDeclared = Declared, @v_GraphLive = Deployed
        FROM #DeployedGraphType WHERE Declared <> Deployed
      RAISERROR('Table %s declares GraphType %s, but is currently deployed as %s. SQL Server has no ALTER that converts a table to or from a graph node/edge table, so SchemaSmith will not attempt it -- recreate the table, or correct the declared GraphType to match.', 16, 1, @v_GraphTable, @v_GraphDeclared, @v_GraphLive)
    END
  END

  RAISERROR('Validate declared MemoryOptimized/Durability matches deployed', 10, 100) WITH NOWAIT
  -- Memory-optimized tables (#J1) are create-time only in the hardest sense: there is no
  -- ALTER TABLE ... SET (MEMORY_OPTIMIZED = ON/OFF) and no ALTER for DURABILITY -- both are error 102,
  -- not even syntax -- so a table cannot be converted in either direction. A declaration that disagrees
  -- with the deployed table is refused by name, exactly like GraphType above.
  --
  -- sys.tables.is_memory_optimized / durability_desc are SQL Server 2014, and this proc body is kindled
  -- for the XML tier too, so the read is version-gated and dynamic rather than a static reference that
  -- would fail to CREATE on a 2008/2012 binary.
  IF SchemaSmith.fn_ServerMajorVersion() >= 12
  BEGIN
    IF OBJECT_ID('tempdb..#DeployedMemOpt') IS NOT NULL DROP TABLE #DeployedMemOpt
    CREATE TABLE #DeployedMemOpt (FullName NVARCHAR(1010), DeclaredMO BIT, DeployedMO BIT, DeclaredDur NVARCHAR(20), DeployedDur NVARCHAR(20))
    EXEC sp_executesql N'
      INSERT INTO #DeployedMemOpt (FullName, DeclaredMO, DeployedMO, DeclaredDur, DeployedDur)
        SELECT t.[Schema] + ''.'' + t.[Name],
               ISNULL(t.[MemoryOptimized], 0),
               CONVERT(BIT, st.is_memory_optimized),
               ISNULL(NULLIF(t.[Durability], ''''), ''SCHEMA_AND_DATA''),
               st.durability_desc
          FROM #Tables t WITH (NOLOCK)
          JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name])
         WHERE t.NewTable = 0'

    IF EXISTS (SELECT 1 FROM #DeployedMemOpt WHERE DeclaredMO <> DeployedMO)
    BEGIN
      DECLARE @v_MoTable NVARCHAR(1010), @v_MoDeclared INT, @v_MoLive INT
      SELECT TOP 1 @v_MoTable = FullName, @v_MoDeclared = CONVERT(INT, DeclaredMO), @v_MoLive = CONVERT(INT, DeployedMO)
        FROM #DeployedMemOpt WHERE DeclaredMO <> DeployedMO
      RAISERROR('Table %s declares MemoryOptimized = %d, but is currently deployed with is_memory_optimized = %d. SQL Server has no ALTER that converts a table to or from the memory-optimized (Hekaton) engine -- migrate it manually, or correct the declaration to match.', 16, 1, @v_MoTable, @v_MoDeclared, @v_MoLive)
    END

    -- DURABILITY change on a table that IS and STAYS memory-optimized: also un-ALTERable, refused the same way.
    IF EXISTS (SELECT 1 FROM #DeployedMemOpt WHERE DeployedMO = 1 AND DeclaredMO = 1 AND DeclaredDur <> DeployedDur)
    BEGIN
      DECLARE @v_DurTable NVARCHAR(1010), @v_DurDeclared NVARCHAR(20), @v_DurLive NVARCHAR(20)
      SELECT TOP 1 @v_DurTable = FullName, @v_DurDeclared = DeclaredDur, @v_DurLive = DeployedDur
        FROM #DeployedMemOpt WHERE DeployedMO = 1 AND DeclaredMO = 1 AND DeclaredDur <> DeployedDur
      RAISERROR('Table %s declares memory-optimized DURABILITY %s, but is currently %s. There is no ALTER for a memory-optimized table''s durability -- migrate it manually, or correct the declared Durability to match.', 16, 1, @v_DurTable, @v_DurDeclared, @v_DurLive)
    END

    -- Inline-index changes on an existing memory-optimized table. Its indexes are declared inline in the
    -- CREATE and are immutable through SchemaSmith's ordinary CREATE/DROP INDEX convergence -- the engine
    -- rejects both -- so an added or removed inline index, a changed key-column set, a uniqueness flip, or
    -- a changed hash bucket count cannot be applied. Refuse by name (like the MemoryOptimized/Durability
    -- refusals above) rather than silently ignoring the declared change; recreate via a migration script.
    -- Bucket count uses a power-of-two RANGE test (deployed/2 < declared <= deployed) because the engine
    -- rounds a declared bucket count UP to the next power of two, so an unchanged redeploy of, say, 1000
    -- buckets must compare equal to the deployed 1024. This proc is SHARED with the compatibility-level-100
    -- XML kindle path and must bind on a genuine pre-2016 binary, so: the 2014 catalog references
    -- (is_memory_optimized, sys.hash_indexes) go in dynamic SQL, and STRING_AGG (2017) / STRING_SPLIT
    -- (needs compat 130) are avoided entirely -- fn_SplitList and FOR XML PATH are the all-version idioms.
    IF OBJECT_ID('tempdb..#MODeplIx') IS NOT NULL DROP TABLE #MODeplIx
    CREATE TABLE #MODeplIx (sch SYSNAME, tbl SYSNAME, ixname SYSNAME, obj_id INT, idx_id INT, is_unique BIT, is_hash BIT, buckets BIGINT, colset NVARCHAR(MAX) NULL)
    EXEC sp_executesql N'
      INSERT INTO #MODeplIx (sch, tbl, ixname, obj_id, idx_id, is_unique, is_hash, buckets)
        SELECT SCHEMA_NAME(o.[schema_id]), o.[name], i.[name], i.[object_id], i.index_id, i.is_unique,
               CASE WHEN i.[type] = 7 THEN 1 ELSE 0 END,
               ISNULL(hi.bucket_count, 0)
          FROM sys.indexes i
          JOIN sys.tables o ON o.[object_id] = i.[object_id]
          LEFT JOIN sys.hash_indexes hi ON hi.[object_id] = i.[object_id] AND hi.index_id = i.index_id
         WHERE o.is_memory_optimized = 1 AND i.index_id >= 1 AND i.[type] <> 0'

    -- Deployed key-column set, sorted and lowercased. sys.index_columns / sys.columns exist on every
    -- version, so this is a static all-version aggregation (FOR XML PATH, not STRING_AGG).
    UPDATE p
      SET colset = STUFF((SELECT ',' + LOWER(c.[name])
                            FROM sys.index_columns ic WITH (NOLOCK)
                            JOIN sys.columns c WITH (NOLOCK) ON c.[object_id] = ic.[object_id] AND c.column_id = ic.column_id
                            WHERE ic.[object_id] = p.obj_id AND ic.index_id = p.idx_id AND ic.is_included_column = 0
                            ORDER BY LOWER(c.[name])
                            FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '')
      FROM #MODeplIx p

    IF OBJECT_ID('tempdb..#MODeclIx') IS NOT NULL DROP TABLE #MODeclIx
    SELECT sch = SchemaSmith.fn_StripBracketWrapping(i.[Schema]),
           tbl = SchemaSmith.fn_StripBracketWrapping(i.[TableName]),
           ixname = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]),
           is_unique = CONVERT(BIT, i.[Unique]),
           is_hash = CONVERT(BIT, CASE WHEN i.[BucketCount] IS NOT NULL THEN 1 ELSE 0 END),
           decl_buckets = CONVERT(BIGINT, ISNULL(i.[BucketCount], 0)),
           colset = STUFF((SELECT ',' + LOWER(SchemaSmith.fn_StripBracketWrapping(RTRIM(REPLACE(sl.[value], ' DESC', ''))))
                             FROM SchemaSmith.fn_SplitList(i.[IndexColumns], ',') sl
                             ORDER BY LOWER(SchemaSmith.fn_StripBracketWrapping(RTRIM(REPLACE(sl.[value], ' DESC', ''))))
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '')
      INTO #MODeclIx
      FROM #Indexes i WITH (NOLOCK)
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = i.[Schema] AND t.[Name] = i.[TableName]
      WHERE t.[MemoryOptimized] = 1 AND t.NewTable = 0

    IF OBJECT_ID('tempdb..#MOIndexDrift') IS NOT NULL DROP TABLE #MOIndexDrift
    SELECT DISTINCT FullName = '[' + COALESCE(d.sch, p.sch) + '].[' + COALESCE(d.tbl, p.tbl) + ']'
      INTO #MOIndexDrift
      FROM #MODeclIx d
      FULL OUTER JOIN #MODeplIx p ON p.sch = d.sch AND p.tbl = d.tbl AND p.ixname = d.ixname
      -- only tables that are memory-optimized on BOTH sides (a memory-optimized/disk flip is refused above)
      WHERE EXISTS (SELECT 1 FROM #MODeclIx x WHERE x.sch = COALESCE(d.sch, p.sch) AND x.tbl = COALESCE(d.tbl, p.tbl))
        AND EXISTS (SELECT 1 FROM #MODeplIx y WHERE y.sch = COALESCE(d.sch, p.sch) AND y.tbl = COALESCE(d.tbl, p.tbl))
        AND (d.ixname IS NULL
          OR p.ixname IS NULL
          OR d.is_unique <> p.is_unique
          OR d.is_hash <> p.is_hash
          OR ISNULL(d.colset, '') <> ISNULL(p.colset, '')
          OR (d.is_hash = 1 AND NOT (p.buckets / 2 < d.decl_buckets AND d.decl_buckets <= p.buckets)))

    IF EXISTS (SELECT 1 FROM #MOIndexDrift)
    BEGIN
      DECLARE @v_MoIxTable NVARCHAR(1010)
      SELECT TOP 1 @v_MoIxTable = FullName FROM #MOIndexDrift
      RAISERROR('Table %s is memory-optimized and its declared inline index set differs from what is deployed (an added or removed index, changed key columns, a uniqueness change, or a changed hash bucket count). SQL Server memory-optimized indexes are immutable through ordinary CREATE/DROP INDEX, so SchemaSmith will not attempt it -- recreate the table via a migration script, or correct the declaration to match.', 16, 1, @v_MoIxTable)
    END
  END

  RAISERROR('Validate declared Ledger matches deployed', 10, 100) WITH NOWAIT
  -- Ledger tables are create-time only: ALTER TABLE ... SET (LEDGER = ON) is error 102, not syntax. And
  -- unlike most refusals this one cannot be worked around by recreating the table, because DROP on a
  -- ledger table is not a drop -- SQL Server retains it as MSSQL_DroppedLedgerTable_<name>_<guid>. So a
  -- mismatch is reported rather than acted on, and the message says which side to change.
  IF SchemaSmith.fn_ServerMajorVersion() >= 16
  BEGIN
    IF OBJECT_ID('tempdb..#DeployedLedger') IS NOT NULL DROP TABLE #DeployedLedger
    CREATE TABLE #DeployedLedger (FullName NVARCHAR(1010), Declared NVARCHAR(12), Deployed NVARCHAR(12))
    EXEC sp_executesql N'
      INSERT INTO #DeployedLedger (FullName, Declared, Deployed)
        SELECT t.[Schema] + ''.'' + t.[Name],
               ISNULL(NULLIF(t.[Ledger], ''''), ''Off''),
               CASE st.ledger_type_desc WHEN ''APPEND_ONLY_LEDGER_TABLE'' THEN ''AppendOnly''
                                        WHEN ''UPDATABLE_LEDGER_TABLE'' THEN ''Updatable''
                                        ELSE ''Off'' END
          FROM #Tables t WITH (NOLOCK)
          JOIN sys.tables st WITH (NOLOCK) ON st.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name])
         WHERE t.NewTable = 0'

    IF EXISTS (SELECT 1 FROM #DeployedLedger WHERE Declared <> Deployed)
    BEGIN
      DECLARE @v_LedgerTable NVARCHAR(1010), @v_LedgerDeclared NVARCHAR(12), @v_LedgerLive NVARCHAR(12)
      SELECT TOP 1 @v_LedgerTable = FullName, @v_LedgerDeclared = Declared, @v_LedgerLive = Deployed
        FROM #DeployedLedger WHERE Declared <> Deployed
      RAISERROR('Table %s declares Ledger %s, but is currently deployed as %s. SQL Server has no ALTER that converts a table to or from a ledger table, and DROP does not remove one (it is retained as MSSQL_DroppedLedgerTable_<name>_<guid>), so SchemaSmith will not attempt it -- correct the declared Ledger to match, or migrate the data to a new table.', 16, 1, @v_LedgerTable, @v_LedgerDeclared, @v_LedgerLive)
    END
  END
END
GO
