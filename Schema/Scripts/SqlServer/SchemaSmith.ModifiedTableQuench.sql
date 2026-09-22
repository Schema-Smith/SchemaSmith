-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- INDEX REMOVAL LIVES HERE on SQL Server (@DropIndexesRemovedFromProduct). Placement differs by engine:
-- PostgreSQL also removes here, but MySQL/MariaDB remove in MissingIndexesAndConstraintsQuench instead,
-- whose name reads as add-only. Do not infer an engine's capability from one procedure's signature --
-- that inference produced six wrong conclusions in a single audit. All three engines honour the flag.
IF OBJECT_ID('SchemaSmith.ModifiedTableQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ModifiedTableQuench
GO
CREATE PROCEDURE SchemaSmith.ModifiedTableQuench
  @ProductName NVARCHAR(50),
  @WhatIf BIT = 0,
  @DropUnknownIndexes BIT = 0,
  @DropTablesRemovedFromProduct BIT = 1,
  @DropSchemaBoundDependents BIT = 0,
  @DropColumnsRemovedFromProduct BIT = 1,
  @DropForeignKeysRemovedFromProduct BIT = 1,
  @DropCheckConstraintsRemovedFromProduct BIT = 1,
  @DropExcludeConstraintsRemovedFromProduct BIT = 1,
  @DropStatisticsRemovedFromProduct BIT = 1,
  @DropIndexesRemovedFromProduct BIT = 1,
  @CaptureWouldDrop BIT = 0,
  -- The RESOLVED upper-tier RebuildPolicy (environment -> product -> template), already collapsed to a
  -- single whole policy by ProductQuench.ResolveCascadedPolicy. It applies to a table ONLY when that table
  -- declared no policy of its own; a table that declared one takes ITS policy entire (see the decision
  -- point below). Defaults are the NEVER default of the domain object, so a caller that passes nothing --
  -- every pre-existing caller, and every package with no RebuildPolicy anywhere -- can never elect a rebuild.
  @RebuildPolicyMode NVARCHAR(20) = 'NEVER',
  @RebuildPolicyThreshold INT = NULL,
  @RebuildPolicyOnOrderMismatch BIT = 0,
  -- Template-level CdcFilegroup (#417): where change tables go for a CDC table that declares none of its
  -- own. NULL at both tiers means unmanaged -- an existing placement is never touched.
  @CdcFilegroup NVARCHAR(128) = NULL
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = '',
          @v_DatabaseCollation NVARCHAR(200) = CAST(DATABASEPROPERTYEX(DB_NAME(), 'COLLATION') AS NVARCHAR(200))
  SET NOCOUNT ON
  RAISERROR('Override table compression to match clustered index', 10, 100) WITH NOWAIT
  UPDATE t
    SET [CompressionType] = CASE WHEN [ColumnStore] = 1 THEN 'COLUMNSTORE' ELSE i.[CompressionType] END
    FROM #Tables t
    JOIN #Indexes i WITH (NOLOCK) ON i.[Schema] = t.[Schema]
                                 AND i.[TableName] = t.[Name]
                                 AND i.[Clustered] = 1
 
  RAISERROR('Get Schema List', 10, 100) WITH NOWAIT
  SELECT DISTINCT t.[Schema]
    INTO #SchemaList
    FROM #Tables t WITH (NOLOCK)

  RAISERROR('Turn off Temporal Tracking for tables no longer defined temporal', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Turn OFF Temporal Tracking for ' + T.[Schema] + '.' + T.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' SET (SYSTEM_VERSIONING = OFF);' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' DROP PERIOD FOR SYSTEM_TIME;' AS NVARCHAR(MAX))
                           FROM #Tables T WITH (NOLOCK)
                           WHERE t.IsTemporal = 0
                             AND OBJECTPROPERTY(OBJECT_ID([Schema] + '.' + [Name]), 'TableTemporalType') = 2
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Collect table level extended properties', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#TableProperties') IS NOT NULL DROP TABLE #TableProperties
  SELECT [Schema], objname COLLATE DATABASE_DEFAULT AS TableName, x.[Name] COLLATE DATABASE_DEFAULT AS PropertyName, CONVERT(NVARCHAR(50), x.[value]) COLLATE DATABASE_DEFAULT AS [value]
    INTO #TableProperties
    FROM #SchemaList WITH (NOLOCK)
    CROSS APPLY fn_listextendedproperty(default, 'Schema', SchemaSmith.fn_StripBracketWrapping([Schema]), 'Table', default, default, default) x
    WHERE x.[Name] COLLATE DATABASE_DEFAULT IN ('ProductName', 'PreventDrop')

  -- Memory-optimized tables cannot carry extended properties (sp_addextendedproperty raises a
  -- schema-version error on them), so their ownership lives in the SchemaSmith.ProductOwnership tracking
  -- table -- the same table-based ownership fallback PostgreSQL and MySQL use for every table. Fold those
  -- rows into #TableProperties in the SAME shape the extended-property scan produces, so every downstream
  -- ownership check (cross-product validation, PreventDrop protection, drop-by-absence) treats a
  -- memory-optimized table identically to an extended-property-tracked one. Scoped to #SchemaList for
  -- parity with the scan above; PreventDrop is emitted as the 'true'/'false' text the marker uses so the
  -- existing px.[value] = 'true' comparisons match unchanged. IndexName = '' is the table-level marker.
  INSERT INTO #TableProperties ([Schema], TableName, PropertyName, [value])
    SELECT po.[Schema] COLLATE DATABASE_DEFAULT, po.[TableName] COLLATE DATABASE_DEFAULT, 'ProductName', po.[ProductName] COLLATE DATABASE_DEFAULT
      FROM SchemaSmith.ProductOwnership po WITH (NOLOCK)
      JOIN #SchemaList sl WITH (NOLOCK) ON sl.[Schema] = po.[Schema] COLLATE DATABASE_DEFAULT
      WHERE po.[IndexName] = ''
    UNION ALL
    SELECT po.[Schema] COLLATE DATABASE_DEFAULT, po.[TableName] COLLATE DATABASE_DEFAULT, 'PreventDrop', CASE WHEN po.[PreventDrop] = 1 THEN 'true' ELSE 'false' END
      FROM SchemaSmith.ProductOwnership po WITH (NOLOCK)
      JOIN #SchemaList sl WITH (NOLOCK) ON sl.[Schema] = po.[Schema] COLLATE DATABASE_DEFAULT
      WHERE po.[IndexName] = ''

  RAISERROR('Validate Table Ownership', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Table ' + tp.[Schema] + '.' + tp.[TableName] + ' owned by different product. [' + tp.[Value] + ']'', 10, 100) WITH NOWAIT;' AS NVARCHAR(MAX))
                           FROM #Tables t WITH (NOLOCK)
                           JOIN #TableProperties tp WITH (NOLOCK) ON t.[Schema] = tp.[Schema]
                                                                 AND t.[Name] = '[' + tp.TableName + ']'
                           WHERE tp.PropertyName = 'ProductName'
                             AND tp.[value] <> @ProductName
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  IF EXISTS (SELECT *
               FROM #Tables t WITH (NOLOCK)
               JOIN #TableProperties tp WITH (NOLOCK) ON t.[Schema] = tp.[Schema]
                                                     AND t.[Name] = '[' + tp.TableName + ']'
               WHERE tp.PropertyName = 'ProductName'
                 AND tp.[value] <> @ProductName)
  BEGIN
    RAISERROR('One or more tables in this quench are already owned by another product', 16, 1) WITH NOWAIT
  END

  -- Filegroup placement (#filegroups): an EXISTING table (NewTable = 0) whose declared filegroup differs
  -- from where it is currently deployed is a MOVE -- SQL Server rebuild territory (Table Rebuild Triggers,
  -- deferred to RC2), so this errors naming both rather than silently rebuilding it. Declared NULL means
  -- "the database's own default filegroup" (matches the extraction/create-side contract), so an ordinary
  -- table with FileGroup unset -- every existing package -- compares its live default-filegroup placement
  -- against itself and never trips this check.
  -- CDC change-table placement (#417). The template default fills in only where a CDC table declared none;
  -- NULL at both tiers stays NULL, which means unmanaged. Resolved here, once, so every later pass reads one
  -- effective value per table.
  --
  -- This is RESOLUTION, not validation, so it stays in this procedure rather than moving into the guarded
  -- call below: it must run on every deploy that sets a template default, including the ones that skip
  -- attribute validation entirely.
  IF @CdcFilegroup IS NOT NULL
    UPDATE #Tables SET CdcFilegroup = SchemaSmith.fn_SafeBracketWrap(@CdcFilegroup)
     WHERE EnableCDC = 1 AND CdcFilegroup IS NULL

  -- Declared-vs-deployed refusals for the attributes SQL Server cannot ALTER (filegroup / LOB / FILESTREAM
  -- placement, partition scheme and column, GraphType, MemoryOptimized, Durability, Ledger, CdcFilegroup)
  -- live in SchemaSmith.ValidateDeclaredTableAttributes and are CALLED rather than inlined.
  --
  -- Why: SQL Server compiles a procedure's statements whether or not they execute, and caches the plan per
  -- object PER DATABASE. Those 328 lines were ~35 catalog statements compiled on every first deploy to
  -- every database, for a feature set most packages never use. Measured here: 40 such statements behind an
  -- always-false guard still cost 14x plan size and 17x first-call time.
  --
  -- The probe below decides whether the call is needed, and deliberately asks TWO questions. A package that
  -- DECLARES one of these attributes obviously needs checking. But so does a package that is SILENT about
  -- an attribute the LIVE table already has -- that is precisely the mismatch that must be refused, and a
  -- declaration-only guard would skip the refusal and let the run attempt ALTERs SQL Server will reject
  -- (or worse, that silently do the wrong thing). Skipping work is safe; skipping a refusal is not.
  DECLARE @v_NeedsAttributeValidation BIT = 0

  IF EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK)
              WHERE NULLIF(RTRIM(ISNULL(t.[FileGroup], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[FileStreamFileGroup], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[TextImageFileGroup], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[PartitionScheme], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[PartitionColumn], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[GraphType], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[Ledger], '')), '') IS NOT NULL
                 OR NULLIF(RTRIM(ISNULL(t.[Durability], '')), '') IS NOT NULL
                 OR ISNULL(t.[MemoryOptimized], 0) = 1
                 OR t.[CdcFilegroup] IS NOT NULL)
    SET @v_NeedsAttributeValidation = 1

  -- Deployed side. Version-composed because the catalog columns arrive in different releases and naming one
  -- on an older binary fails at CREATE PROCEDURE time, not at run time -- this body is kindled for the XML
  -- tier too, so a static reference would make the whole procedure uncreatable there.
  IF @v_NeedsAttributeValidation = 0
  BEGIN
    DECLARE @v_AttrProbe NVARCHAR(MAX) = N'
      SELECT @found = 1
        FROM #Tables t WITH (NOLOCK)
        JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + ''.'' + t.[Name])
        LEFT JOIN sys.indexes si ON si.[object_id] = st.[object_id] AND si.index_id IN (0, 1)
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = si.data_space_id
       WHERE ds.[type] = ''PS''
          OR ISNULL(st.lob_data_space_id, 0) <> 0
          OR st.filestream_data_space_id IS NOT NULL'
    IF SchemaSmith.fn_ServerMajorVersion() >= 12 SET @v_AttrProbe = @v_AttrProbe + N' OR st.is_memory_optimized = 1'
    IF SchemaSmith.fn_ServerMajorVersion() >= 14 SET @v_AttrProbe = @v_AttrProbe + N' OR st.is_node = 1 OR st.is_edge = 1'
    IF SchemaSmith.fn_ServerMajorVersion() >= 16 SET @v_AttrProbe = @v_AttrProbe + N' OR st.ledger_type <> 0'
    EXEC sp_executesql @v_AttrProbe, N'@found BIT OUTPUT', @found = @v_NeedsAttributeValidation OUTPUT
  END

  IF @v_NeedsAttributeValidation = 1
    EXEC SchemaSmith.ValidateDeclaredTableAttributes



  -- No-drop protection tier (#270): when protected mode is active the caller forces
  -- @DropTablesRemovedFromProduct to 0 so the drop block below never runs. Record the tables that
  -- WOULD have been dropped by absence (owned by this product, absent from the package, not already
  -- sticky-PreventDrop) to the ChangeAudit seam as 'dropSuppressed' so the run can surface a manifest.
  -- Audit rows only -- no DDL -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture tables suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Table ' + tp.[Schema] + '.' + tp.TableName + ' removed from product but PreventDrop is active -- skipping drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + tp.[Schema] + '.[' + tp.TableName + ']'', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM #TableProperties tp
      WHERE tp.PropertyName = 'ProductName'
        AND tp.[value] = @ProductName
        AND NOT EXISTS (SELECT 1
                          FROM #TableProperties px WITH (NOLOCK)
                          WHERE px.[Schema] = tp.[Schema]
                            AND px.TableName = tp.TableName
                            AND px.PropertyName = 'PreventDrop'
                            AND px.[value] = 'true')
        AND NOT EXISTS (SELECT *
                          FROM #Tables t WITH (NOLOCK)
                          WHERE t.[Schema] = tp.[Schema]
                            AND t.[Name] = '[' + tp.TableName + ']')
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  IF @DropTablesRemovedFromProduct = 1
  BEGIN
    RAISERROR('Identify tables removed from the product', 10, 100) WITH NOWAIT
    IF OBJECT_ID('tempdb..#TablesRemovedFromProduct') IS NOT NULL DROP TABLE #TablesRemovedFromProduct
    SELECT tp.[Schema], tp.TableName
      INTO #TablesRemovedFromProduct
      FROM #TableProperties tp
      WHERE tp.PropertyName = 'ProductName'
        AND tp.[value] = @ProductName
        AND NOT EXISTS (SELECT 1
                          FROM #TableProperties px WITH (NOLOCK)
                          WHERE px.[Schema] = tp.[Schema]
                            AND px.TableName = tp.TableName
                            AND px.PropertyName = 'PreventDrop'
                            AND px.[value] = 'true')
        AND NOT EXISTS (SELECT *
                          FROM #Tables t WITH (NOLOCK)
                          WHERE t.[Schema] = tp.[Schema]
                            AND t.[Name] = '[' + tp.TableName + ']')
        -- A dropped ledger table is RETAINED as MSSQL_DroppedLedgerTable_<name>_<guid>, inheriting the
        -- extended properties of the table it came from -- including the ProductName stamp this pass
        -- selects on. Without this it reads as "a table removed from the product" on every later
        -- deploy, and SQL Server refuses to drop it ("because it is a ledger dropped object"), so the
        -- deploy fails permanently on an object the user cannot remove either.
        AND tp.TableName NOT LIKE 'MSSQL[_]DroppedLedger%'

    IF EXISTS (SELECT * FROM #TablesRemovedFromProduct WITH (NOLOCK))
    BEGIN
      -- Data-loss guard: a partitioned table spreads data across partitions/filegroups that
      -- DROP TABLE destroys outright. SchemaSmith has no partitioning support -- partitioning
      -- only happens by hand, typically once a table has grown -- so an ordinary product-owned
      -- table can be partitioned after deployment and later look like an ordinary
      -- drop-by-absence candidate. Fail closed before any DDL below, in both live and WhatIf
      -- mode, mirroring the Always Encrypted swap guard further down this proc. index_id < 2
      -- (heap/clustered) + COUNT(*) > 1, not a bare scalar subquery: sys.partitions has one row
      -- per index per partition, and a naive scalar subquery here is exactly what produced
      -- Msg 512 "Subquery returned more than 1 value" during development.
      IF EXISTS (SELECT 1
                   FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                   WHERE (SELECT COUNT(*) FROM sys.partitions p
                            WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.[' + t.TableName + ']')
                              AND p.index_id < 2) > 1)
      BEGIN
        SELECT @v_SQL = STUFF((SELECT ', ' + t.[Schema] + '.[' + t.TableName + ']'
                                 FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                                 WHERE (SELECT COUNT(*) FROM sys.partitions p
                                          WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.[' + t.TableName + ']')
                                            AND p.index_id < 2) > 1
                                 FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
        RAISERROR('Partitioned table(s) removed from the product but NOT dropped: %s. SchemaSmith cannot verify that data spread across partitions can be safely destroyed by DROP TABLE. Drop the table manually after confirming the data is no longer needed, or mark it PreventDrop to keep it in the product permanently.', 16, 1, @v_SQL)
      END

      -- A system-versioned temporal table can't be dropped while versioning is on (error 13552).
      -- Capture each removed temporal table's history table BEFORE turning versioning off
      -- (history_table_id is only valid while versioning is on), then turn versioning off so the
      -- table can be dropped; the now-orphaned history table is dropped after the main drop below.
      RAISERROR('Turn off system versioning for temporal tables removed from the product', 10, 100) WITH NOWAIT
      IF OBJECT_ID('tempdb..#RemovedTemporalHistory') IS NOT NULL DROP TABLE #RemovedTemporalHistory
      CREATE TABLE #RemovedTemporalHistory (HistSchema NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL, HistName NVARCHAR(128) COLLATE DATABASE_DEFAULT NULL)
      -- sys.tables.temporal_type / history_table_id are 2016+, so a STATIC reference would fail to CREATE this
      -- shared proc on a genuine pre-2016 binary. Populate via a fn_ServerMajorVersion()>=13 guarded dynamic
      -- INSERT (identifiers live only in the string); empty below 2016, where no temporal table can exist.
      IF SchemaSmith.fn_ServerMajorVersion() >= 13
        EXEC sp_executesql N'
          INSERT INTO #RemovedTemporalHistory (HistSchema, HistName)
          SELECT hs.[name] COLLATE DATABASE_DEFAULT, h.[name] COLLATE DATABASE_DEFAULT
            FROM #TablesRemovedFromProduct t WITH (NOLOCK)
            JOIN sys.tables mt ON mt.[object_id] = OBJECT_ID(t.[Schema] + ''.['' + t.[TableName] + '']'') AND mt.temporal_type = 2
            JOIN sys.tables h ON h.[object_id] = mt.history_table_id
            JOIN sys.schemas hs ON hs.[schema_id] = h.[schema_id]'
      SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Turn OFF system versioning for ' + t.[Schema] + '.' + t.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                      'ALTER TABLE ' + t.[Schema] + '.[' + t.[TableName] + '] SET (SYSTEM_VERSIONING = OFF);' AS NVARCHAR(MAX))
                               FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                               WHERE OBJECTPROPERTY(OBJECT_ID(t.[Schema] + '.[' + t.[TableName] + ']'), 'TableTemporalType') = 2
                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

      RAISERROR('Drop inbound foreign keys referencing tables removed from the product', 10, 100) WITH NOWAIT
      SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping inbound foreign Key ' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id) + '.' + fk.[name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                      'IF OBJECT_ID(''[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']'') IS NOT NULL ALTER TABLE [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT [' + fk.[name] + '];' + CHAR(13) + CHAR(10) +
                                      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']'', ''dropped'');' AS NVARCHAR(MAX))
                               FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                               JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(t.[Schema] + '.[' + t.[TableName] + ']')
                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

      -- #363: WhatIf twin of the embedded 'foreignKey'/'dropped' audit above (which rides the DROP
      -- CONSTRAINT DDL, executed only on a real run). Same source, same ObjectName shape.
      IF @WhatIf = 1
        INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
          SELECT @@SPID, 'foreignKey', '[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']', 'wouldDrop'
            FROM #TablesRemovedFromProduct t WITH (NOLOCK)
            JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(t.[Schema] + '.[' + t.[TableName] + ']')

      RAISERROR('Drop tables removed from the product', 10, 100) WITH NOWAIT
      SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping table ' + t.[Schema] + '.' + t.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                      CASE WHEN OBJECT_ID('SchemaSmith.CustomTableDrop') IS NOT NULL
                                           THEN 'EXEC SchemaSmith.CustomTableDrop ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ''' + t.[TableName] + ''';'
                                           ELSE 'IF OBJECT_ID(''' + t.[Schema] + '.[' + t.[TableName] + ']'') IS NOT NULL DROP TABLE ' + t.[Schema] + '.[' + t.[TableName] + '];'
                                           END + CHAR(13) + CHAR(10) +
                                      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + t.[Schema] + '.[' + t.[TableName] + ']'', ''dropped'');' AS NVARCHAR(MAX))
                               FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

      -- #363: WhatIf twin of the embedded 'table'/'dropped' audit above.
      IF @WhatIf = 1
        INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
          SELECT @@SPID, 'table', t.[Schema] + '.[' + t.[TableName] + ']', 'wouldDrop'
            FROM #TablesRemovedFromProduct t WITH (NOLOCK)

      -- Drop the now-orphaned history tables of removed temporal tables (versioning was turned off
      -- above). Skipped when a CustomTableDrop hook is installed -- that hook owns table removal,
      -- including any history handling for the recycle path.
      IF OBJECT_ID('SchemaSmith.CustomTableDrop') IS NULL
      BEGIN
        RAISERROR('Drop history tables of temporal tables removed from the product', 10, 100) WITH NOWAIT
        SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping history table ' + h.HistSchema + '.' + h.HistName + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                        'IF OBJECT_ID(''[' + h.HistSchema + '].[' + h.HistName + ']'') IS NOT NULL DROP TABLE [' + h.HistSchema + '].[' + h.HistName + '];' AS NVARCHAR(MAX))
                                 FROM #RemovedTemporalHistory h WITH (NOLOCK)
                                 FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
        IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
      END
    END
  END

  RAISERROR('Report tables removed from the product but retained by PreventDrop', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Retaining table ' + tp.[Schema] + '.' + tp.TableName + ' - removed from product but protected by PreventDrop'', 10, 100) WITH NOWAIT;' AS NVARCHAR(MAX))
                           FROM #TableProperties tp WITH (NOLOCK)
                           WHERE tp.PropertyName = 'ProductName'
                             AND tp.[value] = @ProductName
                             AND EXISTS (SELECT 1
                                           FROM #TableProperties px WITH (NOLOCK)
                                           WHERE px.[Schema] = tp.[Schema]
                                             AND px.TableName = tp.TableName
                                             AND px.PropertyName = 'PreventDrop'
                                             AND px.[value] = 'true')
                             AND NOT EXISTS (SELECT *
                                               FROM #Tables t WITH (NOLOCK)
                                               WHERE t.[Schema] = tp.[Schema]
                                                 AND t.[Name] = '[' + tp.TableName + ']')
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Collect index level extended properties', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexProperties') IS NOT NULL DROP TABLE #IndexProperties
  SELECT t.[Schema], t.[Name] AS TableName, objname COLLATE DATABASE_DEFAULT AS IndexName, x.[Name] COLLATE DATABASE_DEFAULT AS PropertyName, CONVERT(NVARCHAR(50), x.[value]) COLLATE DATABASE_DEFAULT AS [value]
    INTO #IndexProperties
    FROM #Tables t WITH (NOLOCK)
    CROSS APPLY fn_listextendedproperty(default, 'Schema', SchemaSmith.fn_StripBracketWrapping(t.[Schema]), 'Table', SchemaSmith.fn_StripBracketWrapping(t.[Name]), 'Index', default) x
    WHERE x.[Name] COLLATE DATABASE_DEFAULT = 'ProductName'

  RAISERROR('Identify indexes removed from the product', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexesRemovedFromProduct') IS NOT NULL DROP TABLE #IndexesRemovedFromProduct
  SELECT xp.[Schema], xp.TableName, xp.IndexName, IsConstraint = CAST(CASE WHEN OBJECT_ID(xp.[Schema] + '.' + xp.IndexName) IS NOT NULL THEN 1 ELSE 0 END AS BIT)
    INTO #IndexesRemovedFromProduct
    FROM #IndexProperties xp
    WHERE xp.[value] = @ProductName
      AND NOT EXISTS (SELECT * 
                        FROM #Indexes i WITH (NOLOCK) 
                        WHERE i.[Schema] = xp.[Schema] 
                          AND i.TableName = xp.TableName
                          AND i.IndexName = '[' + xp.IndexName + ']')
      AND NOT EXISTS (SELECT * 
                        FROM #XmlIndexes i WITH (NOLOCK) 
                        WHERE i.[Schema] = xp.[Schema] 
                          AND i.TableName = xp.TableName
                          AND i.IndexName = '[' + xp.IndexName + ']')

  -- 2016-era per-column catalog metadata (dynamic data masking + Always Encrypted) is version-gated so this
  -- shared apply proc CREATEs on a genuine pre-2016 binary: a STATIC sys.masked_columns / encryption_* column
  -- reference is a CREATE-time binding error below 2016. Stage it via a fn_ServerMajorVersion()>=13 guarded
  -- dynamic INSERT (the 2016 identifiers live only in the string); #ColumnChanges below LEFT JOINs #ColMeta
  -- instead of the 2016 columns. Empty below 2016 (no masked/encrypted columns exist there) so the ISNULL
  -- defaults preserve behavior.
  RAISERROR('Stage version-gated column metadata (masking, Always Encrypted)', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ColMeta') IS NOT NULL DROP TABLE #ColMeta
  -- Column names carry an Existing* prefix so they do NOT collide with the model's unqualified [EncryptionType]
  -- / [EncryptionAlgorithm] references in #ColumnChanges (which would make those ambiguous).
  CREATE TABLE #ColMeta
  (
    [object_id] INT NOT NULL,
    column_id INT NOT NULL,
    ExistingMaskFn NVARCHAR(4000) NULL,
    ExistingEncType NVARCHAR(64) NULL,
    ExistingEncAlgo NVARCHAR(128) NULL,
    ExistingEncKeyDb NVARCHAR(128) NULL,
    PRIMARY KEY ([object_id], column_id)
  )
  IF SchemaSmith.fn_ServerMajorVersion() >= 13
    EXEC sp_executesql N'
      INSERT INTO #ColMeta ([object_id], column_id, ExistingMaskFn, ExistingEncType, ExistingEncAlgo, ExistingEncKeyDb)
      -- NO NOLOCK ON THESE CATALOG READS. A NOLOCK scan can return the same row twice when pages move
      -- underneath it, and a deploy is exactly when the catalog is being rewritten: with several schemas
      -- of one database quenched at once, another session is creating and altering tables while this
      -- reads. The duplicates land in a table keyed on (object_id, column_id) and the deploy dies with a
      -- primary key violation naming a temp table, which says nothing about the real cause. Observed
      -- intermittently on the multi-schema lifecycle test. These are small catalog reads; the dirty read
      -- bought nothing and cost correctness.
      SELECT sc.[object_id], sc.column_id, mc.masking_function, sc.encryption_type_desc, sc.encryption_algorithm_name, sc.column_encryption_key_database_name
        FROM sys.columns sc
        JOIN sys.tables st ON st.[object_id] = sc.[object_id] AND st.is_ms_shipped = 0
        LEFT JOIN sys.masked_columns mc ON mc.[object_id] = sc.[object_id] AND mc.column_id = sc.column_id'

  RAISERROR('Detect Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ColumnChanges') IS NOT NULL DROP TABLE #ColumnChanges
  SELECT c.[Schema], c.[TableName], c.[ColumnName],
         -- For computed columns, only the expression is needed
         CASE WHEN RTRIM(ISNULL([ComputedExpression], '')) <> ''
              THEN 'AS (' + ComputedExpression + ')' + CASE WHEN c.[Persisted] = 1 THEN ' PERSISTED' ELSE '' END
                                                    + CASE WHEN c.[Persisted] = 1 AND c.[NullableDeclared] = 0 THEN ' NOT NULL' ELSE '' END
              -- Otherwise we need to build the column definition
              ELSE REPLACE(REPLACE(UPPER(LEFT([DataType], COALESCE(NULLIF(CHARINDEX('IDENTITY', [DataType]), 0), LEN([DataType]) + 1) - 1)), 'ROWGUIDCOL', ''), 'NOT FOR REPLICATION', '') +
                   CASE WHEN [Collation] <> 'IGNORE' AND ISNULL(NULLIF(ic.COLLATION_NAME, @v_DatabaseCollation), '') <> [Collation] THEN ' COLLATE ' + ISNULL(NULLIF(RTRIM([Collation]), ''), @v_DatabaseCollation) ELSE '' END +
                   CASE WHEN [Sparse] = 1 THEN ' SPARSE' ELSE '' END +
                   CASE WHEN Nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END +
                   CASE WHEN RTRIM(ISNULL([EncryptionType], 'NONE')) <> 'NONE'
                        THEN ' ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = ' + [EncryptionKey] + ', ENCRYPTION_TYPE = ' + [EncryptionType] + ', ALGORITHM = ''' + [EncryptionAlgorithm] + ''')'
                        ELSE '' END
              END AS [ColumnScript],
         CASE WHEN RTRIM(ISNULL([ComputedExpression], '')) = '' 
              THEN CASE WHEN [DataType] LIKE '%ROWGUIDCOL%' AND sc.is_rowguidcol = 0 THEN ' ADD ROWGUIDCOL' ELSE '' END +
                   CASE WHEN [DataType] NOT LIKE '%ROWGUIDCOL%' AND sc.is_rowguidcol = 1 THEN ' DROP ROWGUIDCOL' ELSE '' END +
                   CASE WHEN [DataType] LIKE '%NOT FOR REPLICATION%' AND ident.is_not_for_replication = 0 THEN ' ADD NOT FOR REPLICATION' ELSE '' END +
                   CASE WHEN [DataType] NOT LIKE '%NOT FOR REPLICATION%' AND ident.is_not_for_replication = 1 THEN ' DROP NOT FOR REPLICATION' ELSE '' END +
                   CASE WHEN cm.ExistingMaskFn IS NOT NULL AND ([DataMaskFunction] = '' OR cm.ExistingMaskFn COLLATE DATABASE_DEFAULT <> [DataMaskFunction]) THEN ' DROP MASKED' ELSE '' END +
                   CASE WHEN [DataMaskFunction] <> '' AND cm.ExistingMaskFn IS NULL THEN ' ADD MASKED WITH (FUNCTION = ''' + [DataMaskFunction] + ''')' ELSE '' END +
                   CASE WHEN [DataMaskFunction] <> '' AND cm.ExistingMaskFn COLLATE DATABASE_DEFAULT <> [DataMaskFunction]
                        THEN '; ALTER TABLE ' + c.[Schema] + '.' + c.[TableName] + ' ALTER COLUMN ' + c.[ColumnName] + ' ADD MASKED WITH (FUNCTION = ''' + [DataMaskFunction] + ''')'
                        ELSE '' END
              ELSE ''
              END AS [SpecialColumnScript],
         CAST(CASE WHEN cc.[definition] IS NOT NULL OR RTRIM(ISNULL([ComputedExpression], '')) <> ''
                     OR (ident.column_id IS NULL AND [DataType] LIKE '%IDENTITY%') -- switching to identity... requires drop and recreate column
                     -- A column set cannot be altered in place (Microsoft docs: "The column set column cannot
                     -- be changed or renamed" -- ALTER COLUMN does not even accept the COLUMN_SET clause), so
                     -- toggling a column into/out of being one goes through drop+recreate like the identity
                     -- switch above. The recreate re-adds it via THIS proc's own "Add Missing Physical Columns"
                     -- step (below), reusing the same #Columns.[ColumnScript] expression the earlier, separate
                     -- MissingTableAndColumnQuench phase builds new columns from -- but the two phases run as
                     -- two SEPARATE statements in two SEPARATE proc calls (SchemaSmith.TableQuench.sql:24-25),
                     -- never batched together. Confirmed (not assumed): a genuinely-new sparse column declared
                     -- in the SAME quench as a conversion is already physically committed by
                     -- MissingTableAndColumnQuench.sql's "Add New Physical Columns" step BEFORE this proc even
                     -- starts, so by the time this drop+recreate's ADD runs, the table already has a sparse
                     -- column -- SQL Server rejects it ("... because the table already contains one or more
                     -- sparse columns"), the same restriction a table with sparse columns from a prior deploy
                     -- hits. This is a real limitation of the two-phase quench design, not pre-validated or
                     -- special-cased here -- see TableQuench_ColumnSetTests.cs for the covered/uncovered shapes.
                     OR sc.is_column_set <> [IsColumnSet]
                   THEN 1 ELSE 0 END AS BIT) AS MustDropAndRecreate,
         CAST(CASE WHEN (ident.column_id IS NOT NULL AND [DataType] NOT LIKE '%IDENTITY%'
                        AND RTRIM(ISNULL([ComputedExpression], '')) = '') -- identity removal (data-preserving swap)
                    OR (ISNULL(cm.ExistingEncType, 'NONE') COLLATE DATABASE_DEFAULT <> [EncryptionType]) -- encryption change (data-preserving swap)
                   THEN 1 ELSE 0 END AS BIT) AS MustSwapColumn,
         CAST(0 AS BIT) AS DropOnly
    INTO #ColumnChanges
    FROM #Tables T WITH (NOLOCK)
    JOIN #Columns c WITH (NOLOCK) ON C.[Schema] = T.[Schema] 
                                 AND C.[TableName] = T.[Name]
                                 AND C.[NewColumn] = 0
    -- The declared side is compared in the bracket-wrapped form the working set already stores, by
    -- wrapping the catalog's name instead of stripping ours. Calling a scalar function on the join
    -- predicate evaluates it per row and keeps the statement on a serial plan; wrapping the other side
    -- is the same comparison with none of that. Measured on a 1,783-table model: this procedure went
    -- from 35,540 ms to 20,413 ms on a no-op re-deploy, medians of three, ranges not overlapping.
    --
    -- COLLATE DATABASE_DEFAULT because the two sides come from different places: a temp table's columns
    -- take TEMPDB's collation while a catalog name carries the DATABASE's. The old form compared two
    -- database-collated values (the function returns one), so without this the change would work
    -- wherever those two collations happen to agree and fail where they do not.
    JOIN INFORMATION_SCHEMA.COLUMNS ic ON C.[Schema] COLLATE DATABASE_DEFAULT = '[' + ic.TABLE_SCHEMA + ']'
                                                     AND C.[TableName] COLLATE DATABASE_DEFAULT = '[' + ic.TABLE_NAME + ']'
                                                     AND C.[ColumnName] COLLATE DATABASE_DEFAULT = '[' + ic.COLUMN_NAME + ']'
    JOIN sys.columns sc ON sc.[object_id] = OBJECT_ID(ic.TABLE_SCHEMA + '.' + ic.TABLE_NAME) AND sc.[name] = ic.COLUMN_NAME
    JOIN (SELECT CASE WHEN SCHEMA_NAME(st.[schema_id]) IN ('sys', 'dbo')
                      THEN '' ELSE SCHEMA_NAME(st.[schema_id]) + '.' END + st.[name] AS USER_TYPE, st.user_type_id
            FROM sys.types st) st ON st.user_type_id = sc.user_type_id
    LEFT JOIN sys.identity_columns ident ON ident.[Name] = COLUMN_NAME
                                                      AND ident.[object_id] = OBJECT_ID(TABLE_SCHEMA + '.' + TABLE_NAME)
    LEFT JOIN sys.computed_columns cc ON c.ColumnName COLLATE DATABASE_DEFAULT = '[' + cc.[name] + ']'
                                                   AND cc.[object_id] = OBJECT_ID(C.[Schema] + '.' + C.[TableName])
    LEFT JOIN #ColMeta cm ON cm.[object_id] = sc.[object_id] AND cm.column_id = sc.column_id
    WHERE t.NewTable = 0
      AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(UPPER(USER_TYPE) + SchemaSmith.fn_ColumnTypeArguments(USER_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION,
                                           CASE WHEN sc.xml_collection_id <> 0
                                                THEN (SELECT '[' + SCHEMA_NAME(xc.[schema_id]) + '].[' + xc.[name] + ']' FROM sys.xml_schema_collections xc WHERE xc.xml_collection_id = sc.xml_collection_id)
                                                END,
                                           sc.is_rowguidcol) +
                                      CASE WHEN ident.column_id IS NOT NULL
                                           THEN ' IDENTITY(' + CONVERT(NVARCHAR(20), ident.seed_value) + ', ' + CONVERT(NVARCHAR(20), ident.increment_value) + ')' +
                                                CASE WHEN ident.is_not_for_replication = 1 THEN ' NOT FOR REPLICATION' ELSE '' END
                                           ELSE '' END), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')  <> REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
        -- A computed column's nullability belongs to the engine unless the package states one: it is derivable from
        -- the expression, and only a PERSISTED column can be declared NOT NULL at all. Comparing an OMITTED value
        -- here re-added every such column on every deploy, and (once the emit side agreed with it) dropped an
        -- existing nullable column and failed to put it back on a table whose rows made the expression NULL.
        OR (CASE WHEN c.Nullable = 1 THEN 'YES' ELSE 'NO' END <> ic.IS_NULLABLE
            AND (RTRIM(ISNULL(c.[ComputedExpression], '')) = ''
                 OR (c.[Persisted] = 1 AND c.[NullableDeclared] IS NOT NULL)))
        OR (ISNULL(SchemaSmith.fn_StripParenWrapping(cc.[definition]), '') <> ISNULL(c.ComputedExpression, '')
            -- #242: same question as the check constraints. A computed column has no normalisation at all, so
            -- every non-trivial expression was dropped and re-added on every deploy -- a table rewrite when
            -- the column is PERSISTED.
            AND SchemaSmith.fn_ExpressionMapUnchanged(c.[Schema], c.[TableName], 'COLUMN', c.[ColumnName],
                  'computed', c.ComputedExpression, cc.[definition]) = 0)
        OR ISNULL(cc.is_persisted, 0) <> ISNULL(c.[Persisted], 0))
        OR sc.is_sparse <> [Sparse]
        OR sc.is_column_set <> [IsColumnSet]
        OR ISNULL(cm.ExistingMaskFn, '') COLLATE DATABASE_DEFAULT <> [DataMaskFunction]
        OR ([Collation] <> 'IGNORE' AND ISNULL(NULLIF(ic.COLLATION_NAME, @v_DatabaseCollation), '') <> [Collation])
        OR ISNULL(cm.ExistingEncType, 'NONE') COLLATE DATABASE_DEFAULT <> [EncryptionType]
        OR (ISNULL(cm.ExistingEncType, 'NONE') COLLATE DATABASE_DEFAULT <> 'NONE' AND (ISNULL(cm.ExistingEncAlgo, '') COLLATE DATABASE_DEFAULT <> [EncryptionAlgorithm] OR ISNULL(cm.ExistingEncKeyDb, '') COLLATE DATABASE_DEFAULT <> [EncryptionKey]))
  
  RAISERROR('Detect Computed Columns Impacted by Other Column Changes', 10, 100) WITH NOWAIT
  INSERT #ColumnChanges ([Schema], [TableName], [ColumnName], [ColumnScript], [SpecialColumnScript], MustDropAndRecreate, MustSwapColumn, [DropOnly])
    SELECT C.[Schema], C.[TableName], c.[ColumnName],
           [ColumnScript] = 'AS (' + ComputedExpression + ')' + CASE WHEN c.[Persisted] = 1 THEN ' PERSISTED' ELSE '' END
                                                              + CASE WHEN c.[Persisted] = 1 AND c.[NullableDeclared] = 0 THEN ' NOT NULL' ELSE '' END,
           [SpecialColumnScript] = '',
           MustDropAndRecreate = CAST(1 AS BIT), MustSwapColumn = CAST(0 AS BIT), [DropOnly] = CAST(0 AS BIT)
      FROM #ColumnChanges cc WITH (NOLOCK)
      JOIN sys.computed_columns sc ON sc.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
                                                AND sc.[definition] LIKE '%' + SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) + '%'
      JOIN #Columns c WITH (NOLOCK) ON C.[Schema] = cc.[Schema] 
                                   AND C.[TableName] = cc.[TableName]
                                   AND c.[ColumnName] = cc.[ColumnName]
      WHERE NOT EXISTS (SELECT * FROM #ColumnChanges cc2 WITH (NOLOCK) WHERE cc2.[Schema] = cc.[Schema] AND cc2.[TableName] = cc.[TableName] AND cc2.[ColumnName] = cc.[ColumnName])
  
  -- Engine-owned columns must never be considered for a drop. They exist because the table is a node or
  -- edge table, not because anything declared them, so the drop-by-absence pass would otherwise try to
  -- remove every one on the SECOND deploy -- and SQL Server refuses with "Internal graph columns cannot
  -- be altered", which turns an unchanged package into a failing one.
  --
  -- sys.columns.graph_type is 2017+, so it is staged behind a version guard rather than referenced
  -- statically: this proc body is kindled for the XML tier too, which reaches older servers. Empty below
  -- 2017, where graph tables cannot exist.
  IF OBJECT_ID('tempdb..#EngineOwnedColumns') IS NOT NULL DROP TABLE #EngineOwnedColumns
  CREATE TABLE #EngineOwnedColumns (TableSchema NVARCHAR(256), TableName NVARCHAR(256), ColumnName NVARCHAR(256))
  IF SchemaSmith.fn_ServerMajorVersion() >= 14
    EXEC sp_executesql N'
      INSERT INTO #EngineOwnedColumns (TableSchema, TableName, ColumnName)
        SELECT SCHEMA_NAME(st.[schema_id]), st.[name], c.[name]
          FROM sys.columns c
          JOIN sys.tables st ON st.[object_id] = c.[object_id]
         WHERE c.graph_type IS NOT NULL
            -- Ledger''s generated columns (AS_TRANSACTION_ID_START/END, AS_SEQUENCE_NUMBER_START/END)
            -- are engine-owned in the same way and hit the same failure: SQL Server refuses to drop
            -- them, so an unchanged package fails on its second deploy. Identified by
            -- generated_always_type rather than by name -- the type codes are what actually mean it.
            OR c.generated_always_type IN (7, 8, 9, 10)'

  RAISERROR('Detect Column Drops', 10, 100) WITH NOWAIT
  INSERT #ColumnChanges ([Schema], [TableName], [ColumnName], [ColumnScript], [SpecialColumnScript], MustDropAndRecreate, MustSwapColumn, [DropOnly])
    SELECT t.[Schema], [TableName] = t.[Name], [ColumnName] = '[' + COLUMN_NAME + ']', '', '', 0, 0, 1
      FROM #Tables t WITH (NOLOCK)
      JOIN INFORMATION_SCHEMA.COLUMNS ON t.[Schema] COLLATE DATABASE_DEFAULT = '[' + TABLE_SCHEMA + ']'
                                                   AND t.[Name] COLLATE DATABASE_DEFAULT = '[' + TABLE_NAME + ']' 
      WHERE NOT EXISTS (SELECT * 
                          FROM #Columns c WITH (NOLOCK)
                          WHERE c.[Schema] = t.[Schema]
                            AND c.[TableName] = t.[Name]
                            AND c.[ColumnName] COLLATE DATABASE_DEFAULT = '[' + COLUMN_NAME + ']')
        AND NOT (t.IsTemporal = 1 AND COLUMN_NAME IN ('ValidFrom', 'ValidTo'))
        AND NOT EXISTS (SELECT 1 FROM #EngineOwnedColumns g WITH (NOLOCK)
                         WHERE g.TableSchema = TABLE_SCHEMA AND g.TableName = TABLE_NAME
                           AND g.ColumnName = COLUMN_NAME)

  -- #358: A DropOnly column whose drop is SUPPRESSED (env PreventDrop forces
  -- @DropColumnsRemovedFromProduct = 0, or the per-table cascade opts out) survives -- so its
  -- dependent objects (index / statistics / FK / default / check) must survive with it. Collect those
  -- columns and exclude them from the dependent-cleanup passes below, which otherwise clear the way for
  -- a column drop that never happens. Genuinely-modified columns (DropOnly = 0) are never listed here,
  -- so their transient drop-and-recreate cleanup is untouched.
  RAISERROR('Identify suppressed column drops whose dependents must be retained (#358)', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#SuppressedColumnDrops') IS NOT NULL DROP TABLE #SuppressedColumnDrops
  SELECT cc.[Schema], cc.[TableName], cc.[ColumnName]
    INTO #SuppressedColumnDrops
    FROM #ColumnChanges cc WITH (NOLOCK)
    JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = cc.[Schema] AND t.[Name] = cc.[TableName]
    WHERE cc.DropOnly = 1
      AND (@DropColumnsRemovedFromProduct = 0 OR ISNULL(t.[DropColumnsRemovedFromProduct], 1) = 0)

  ----------------------------------------------------------------------------------------------------
  -- THE REBUILD DECISION POINT.
  --
  -- WHY HERE. Column-change detection finished one statement above: #ColumnChanges holds every
  -- modification and every by-absence drop, and #SuppressedColumnDrops names the drops this run will NOT
  -- perform. And nothing has yet touched a table that is STILL DECLARED: everything executed above this
  -- line either turns off temporal tracking for a table no longer declared temporal or drops a table that
  -- left the product entirely. The very next EXEC -- the foreign-key capture and drop immediately below --
  -- is where this procedure starts dismantling the dependent objects OF a declared table, and a rebuild
  -- drops all of those wholesale anyway. A rebuild inheriting a half-dismantled table would have
  -- RebuildTable's pre-rebuild refusals reasoning about a live state that no longer matches the declared
  -- file, so "before the ALTER phases" is the wrong boundary and this one is the right one. Renames land
  -- earlier still, in MissingTableAndColumnQuench, which matters: RebuildTable refuses outright while a
  -- table or column rename is pending, because the copy matches columns by their CURRENT name.
  --
  -- OPT-IN BY CONSTRUCTION. #RebuildElection is built by a WHERE that only an explicit ALWAYS/THRESHOLD,
  -- or an explicit OnOrderMismatch, can satisfy. A package with no RebuildPolicy anywhere resolves to the
  -- domain object's NEVER default with OnOrderMismatch false
  -- at every level, elects nothing, and this whole block is a no-op: no rebuild, no statement, nothing
  -- added to the run. A rebuild moves user data, so a table that did not ask for one must never get one,
  -- and that has to be structurally true rather than true because the conditions happen not to match.
  --
  -- WHOLE-OBJECT RESOLUTION. [RebuildPolicySpecified] picks WHICH policy applies -- the table's own, or
  -- the resolved upper-tier one passed in -- and then every field comes from that ONE policy. Never a
  -- per-field COALESCE: a table declaring only { "Mode": "ALWAYS" } must not inherit a product's
  -- Threshold. Mirrors ProductQuench.ResolveCascadedPolicy, which collapses the three upper tiers the
  -- same way before this procedure is called.
  ----------------------------------------------------------------------------------------------------

  ----------------------------------------------------------------------------------------------------
  -- COLUMN-ORDER DRIFT, the input to the OnOrderMismatch trigger.
  --
  -- Reordering existing columns is impossible in place on every engine SchemaSmith supports, so a rebuild
  -- is the only mechanism that can converge a table whose columns are in the wrong order. This pass names
  -- the tables where that is true.
  --
  -- DECLARED ORDER IS [_RowId], because that is what RebuildTable orders the shadow's CREATE by. Detection
  -- and repair MUST read the declared order off the same column: if this pass elected on one definition of
  -- "declared order" and the rebuild produced another, the table would be re-elected on every subsequent
  -- deploy and rebuilt forever -- which on this feature means copying every row of the table, every deploy.
  --
  -- THE COMPARISON IS RELATIVE, NEVER ABSOLUTE. #DeclaredColumnOrder pairs each column’s declared position
  -- with its live ORDINAL_POSITION, and the mismatch test below looks only for an INVERSION -- two columns
  -- whose declared order disagrees with their live order. Comparing the two positions for EQUALITY would be
  -- wrong, and the reason is engine-specific rather than universal (measured 2026-08-27):
  --   * PostgreSQL’s information_schema.ordinal_position is attnum and KEEPS the gap left by every column
  --     ever dropped. A correctly-ordered table that has ever lost a column would show declared 2 against
  --     live 3 and be rebuilt on every single deploy -- the infinite-rebuild trap, and on this feature that
  --     means moving every row of the table every time.
  --   * SQL Server’s INFORMATION_SCHEMA.ORDINAL_POSITION RENUMBERS, so the gap is invisible here. It is
  --     sys.columns.column_id that retains it -- which this query deliberately does not read.
  --   * MySQL renumbers contiguously and never exposes a gap either.
  -- An inversion test never reads a position’s value, only two positions’ order, so a uniform shift by any
  -- number of gaps is invisible to it on every engine and only genuine drift is detected. Writing it this
  -- way means the three engines need no per-engine branch.
  --
  -- THE COMPARED SET IS THE INTERSECTION, which the join produces by construction.
  --   * Declared AND live -- compared. This is the real question.
  --   * Live but NOT declared -- excluded, because it has no declared position to compare. These are the
  --     columns this run drops by absence (a table RETAINING one cannot reach the election at all: the
  --     #SuppressedColumnDrops guard below excludes it), so they will not exist after the run and must not
  --     drag a correctly-ordered table into a rebuild. Including them would re-elect any table with a
  --     retained column forever -- the same infinite loop from the other direction.
  --   * Declared but NOT live -- cannot occur here: MissingTableAndColumnQuench added it earlier in the
  --     same run, so by this line it is live and the join finds it. It needs no special case, and the
  --     comparison is simply over the live set. Note this makes a NEW column in a mid-file position a
  --     genuine mismatch, correctly: the ADD appended it to the end, and only a rebuild can move it.
  ----------------------------------------------------------------------------------------------------
  RAISERROR('Detect declared-vs-deployed column order drift', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#DeclaredColumnOrder') IS NOT NULL DROP TABLE #DeclaredColumnOrder
  SELECT c.[Schema], c.[TableName], [DeclaredPos] = c.[_RowId], [LivePos] = ic.ORDINAL_POSITION
    INTO #DeclaredColumnOrder
    FROM #Columns c WITH (NOLOCK)
    JOIN INFORMATION_SCHEMA.COLUMNS ic
      ON c.[Schema] COLLATE DATABASE_DEFAULT = '[' + ic.TABLE_SCHEMA + ']'
     AND c.[TableName] COLLATE DATABASE_DEFAULT = '[' + ic.TABLE_NAME + ']'
     AND c.[ColumnName] COLLATE DATABASE_DEFAULT = '[' + ic.COLUMN_NAME + ']'

  IF OBJECT_ID('tempdb..#RebuildOrderMismatch') IS NOT NULL DROP TABLE #RebuildOrderMismatch
  SELECT DISTINCT a.[Schema], a.[TableName]
    INTO #RebuildOrderMismatch
    FROM #DeclaredColumnOrder a WITH (NOLOCK)
    JOIN #DeclaredColumnOrder b WITH (NOLOCK) ON b.[Schema] = a.[Schema]
                                             AND b.[TableName] = a.[TableName]
    WHERE a.[DeclaredPos] < b.[DeclaredPos]
      AND a.[LivePos] > b.[LivePos]

  RAISERROR('Check for SCHEMABINDING dependents blocking column changes', 10, 100) WITH NOWAIT
  -- SQL Server refuses ALTER COLUMN while a SCHEMABINDING module references the column, with error 4922
  -- ("one or more objects access this column") -- which names neither the module nor what to do about it.
  -- The catalog knows both before the attempt, so say so instead of letting the engine's message through.
  --
  -- SchemaSmith does not drop these on its own initiative: a schemabound view or function is a SCRIPTED
  -- object it does not own, so dropping one it cannot recreate would destroy something the package never
  -- described. The supported answer is to move the module into a schema-bound object folder, which the
  -- deploy drops before the table work and the after-tables object pass puts back in the same run.
  --
  -- sys.sql_modules.is_schema_bound and sys.sql_expression_dependencies both predate the 2008 floor, so
  -- these are safe to reference statically.
  IF EXISTS (SELECT 1 FROM #ColumnChanges WITH (NOLOCK))
  BEGIN
    -- The chain is TRANSITIVE, and the catalog does not hand it over. sys.sql_expression_dependencies
    -- reports only the module bound DIRECTLY to the table, so a flat list holds the inner view but not an
    -- outer one bound to it -- and dropping the inner one then fails with "because it is being referenced
    -- by object <outer>". Walk it, record how deep each module sits, and drop deepest-first. This is the
    -- ordering #323 asked for.
    IF OBJECT_ID('tempdb..#SbChain') IS NOT NULL DROP TABLE #SbChain
    ;WITH SbWalk AS (
      SELECT d.referencing_id AS ModuleId, 1 AS Depth,
             CONVERT(NVARCHAR(600), cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName]) AS Blocks
        FROM #ColumnChanges cc WITH (NOLOCK)
        JOIN sys.sql_expression_dependencies d
          ON d.referenced_id = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
        JOIN sys.sql_modules m
          ON m.[object_id] = d.referencing_id AND m.is_schema_bound = 1
       WHERE d.referenced_minor_id = 0
          OR d.referenced_minor_id = COLUMNPROPERTY(d.referenced_id,
                 SchemaSmith.fn_StripBracketWrapping(cc.[ColumnName]), 'ColumnId')
      UNION ALL
      -- A module bound to a module already in the chain must come out BEFORE it. The depth cap is a
      -- cycle guard: SQL Server does not permit a schemabinding cycle, but a recursive CTE with no
      -- ceiling turns any catalog oddity into a runaway query rather than an error.
      SELECT d.referencing_id, w.Depth + 1, w.Blocks
        FROM SbWalk w
        JOIN sys.sql_expression_dependencies d ON d.referenced_id = w.ModuleId
        JOIN sys.sql_modules m
          ON m.[object_id] = d.referencing_id AND m.is_schema_bound = 1
       WHERE w.Depth < 32
    )
    SELECT ModuleId, MAX(Depth) AS Depth, MIN(Blocks) AS Blocks
      INTO #SbChain
      FROM SbWalk
     GROUP BY ModuleId
    OPTION (MAXRECURSION 32)

    -- An indexed view is dropped and recreated by IndexedViewQuench already, so it is not ours to name
    -- or to drop. Removing it here keeps both the blocked list and the drop chain honest.
    DELETE #SbChain
      FROM #SbChain c
     WHERE EXISTS (SELECT 1 FROM sys.indexes i
                    WHERE i.[object_id] = c.ModuleId AND i.index_id > 0)
    DECLARE @v_SbBlocked NVARCHAR(MAX) =
      STUFF((SELECT ', ' + OBJECT_SCHEMA_NAME(c.ModuleId) + '.' + OBJECT_NAME(c.ModuleId) + ' (blocks ' + c.Blocks + ')'
               FROM #SbChain c
              ORDER BY c.Depth, OBJECT_NAME(c.ModuleId)
                FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

    -- WITH ENCRYPTION returns NULL from OBJECT_DEFINITION, so NOTHING can put such a module back --
    -- not SchemaSmith, and not the package's own script, which could not have been extracted from the
    -- server either. Dropping one would be unrecoverable, so it is refused even when the option is on,
    -- and refused BEFORE anything is dropped so a partial run cannot leave it destroyed.
    DECLARE @v_SbEncrypted NVARCHAR(MAX) =
      STUFF((SELECT ', ' + OBJECT_SCHEMA_NAME(c.ModuleId) + '.' + OBJECT_NAME(c.ModuleId)
               FROM #SbChain c
              WHERE OBJECT_DEFINITION(c.ModuleId) IS NULL
              ORDER BY OBJECT_NAME(c.ModuleId)
                FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')

    IF @v_SbEncrypted IS NOT NULL
      RAISERROR('Column change blocked by an ENCRYPTED schema-bound module: %s. Its definition cannot be read from the server, so nothing can recreate it once dropped -- DropSchemaBoundDependents deliberately does not extend to it. Remove SCHEMABINDING or the encryption from the listed module(s).', 16, 1, @v_SbEncrypted)

    IF @v_SbBlocked IS NOT NULL AND @DropSchemaBoundDependents = 0
      RAISERROR('Column change blocked by SCHEMABINDING: %s. SQL Server will not alter a column while a schema-bound module references it, and SchemaSmith will not drop a scripted object it cannot put back. Move the listed module(s) into a schema-bound object folder (QuenchSlot AfterTablesObjects) and set DropSchemaBoundDependents so the deploy can drop them around the table work and the after-tables object pass recreates them, or remove SCHEMABINDING from them.', 16, 1, @v_SbBlocked)

    IF @v_SbBlocked IS NOT NULL AND @DropSchemaBoundDependents = 1
    BEGIN
      -- The audit is written set-based here rather than generated into the script below, so this proc
      -- has two levels of quoting instead of three.
      INSERT SchemaSmith.ChangeAudit ([SessionId], [ObjectType], [ObjectName], [ActionType])
        SELECT @@SPID, 'schemaBoundModule',
               OBJECT_SCHEMA_NAME(c.ModuleId) + '.' + OBJECT_NAME(c.ModuleId),
               CASE WHEN @WhatIf = 1 THEN 'wouldDrop' ELSE 'dropped' END
          FROM #SbChain c

      -- Deepest first: an outer module bound to an inner one has to come out before it.
      DECLARE @v_SbDropSql NVARCHAR(MAX) =
        (SELECT 'RAISERROR(''  Drop schema-bound module '
                + OBJECT_SCHEMA_NAME(c.ModuleId) + '.' + OBJECT_NAME(c.ModuleId)
                + ' - blocks a column change; the after-tables object pass recreates it'', 10, 100) WITH NOWAIT' + CHAR(13) + CHAR(10)
                + 'DROP ' + CASE WHEN so.type = 'V' THEN 'VIEW' ELSE 'FUNCTION' END + ' '
                + QUOTENAME(OBJECT_SCHEMA_NAME(c.ModuleId)) + '.'
                + QUOTENAME(OBJECT_NAME(c.ModuleId)) + CHAR(13) + CHAR(10)
           FROM #SbChain c
           JOIN sys.objects so ON so.[object_id] = c.ModuleId
          ORDER BY c.Depth DESC, OBJECT_NAME(c.ModuleId)
            FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)')

      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SbDropSql ELSE EXEC(@v_SbDropSql)
    END
  END

  RAISERROR('Elect tables for rebuild', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#RebuildElection') IS NOT NULL DROP TABLE #RebuildElection
  SELECT p.[Schema], p.[TableName]
    INTO #RebuildElection
    FROM (SELECT t.[Schema], [TableName] = t.[Name],
                 -- The winning policy, taken WHOLE from one level.
                 [Mode] = UPPER(LTRIM(RTRIM(ISNULL(CASE WHEN ISNULL(t.[RebuildPolicySpecified], 0) = 1 THEN t.[RebuildPolicyMode] ELSE @RebuildPolicyMode END, 'NEVER')))),
                 [Threshold] = CASE WHEN ISNULL(t.[RebuildPolicySpecified], 0) = 1 THEN t.[RebuildPolicyThreshold] ELSE @RebuildPolicyThreshold END,
                 -- OnOrderMismatch COMPOSES with Mode rather than replacing it -- it is one more OR arm on
                 -- the WHERE below, not a fourth Mode value. { "Mode": "THRESHOLD", "Threshold": 3,
                 -- "OnOrderMismatch": true } therefore reads "rebuild once three modifications pile up OR
                 -- once the column order has drifted", and pairing it with the NEVER default asks for a
                 -- rebuild on order drift and nothing else -- the case the trigger mainly exists for.
                 [OnOrderMismatch] = CONVERT(BIT, ISNULL(CASE WHEN ISNULL(t.[RebuildPolicySpecified], 0) = 1 THEN t.[RebuildPolicyOnOrderMismatch] ELSE @RebuildPolicyOnOrderMismatch END, 0)),
                 [OrderMismatch] = CONVERT(BIT, CASE WHEN EXISTS (SELECT 1 FROM #RebuildOrderMismatch m WITH (NOLOCK)
                                                                    WHERE m.[Schema] = t.[Schema] AND m.[TableName] = t.[Name])
                                                     THEN 1 ELSE 0 END),
                 -- THE THRESHOLD COUNT: column-MODIFICATION passes only, which is what a rebuild actually
                 -- eliminates -- each #ColumnChanges row that is not a pure drop becomes its own ALTER.
                 -- DropOnly rows are excluded (a column drop is metadata-only here, so a rebuild saves
                 -- nothing by absorbing it); column ADDs never reach #ColumnChanges at all, having already
                 -- been applied by MissingTableAndColumnQuench before this procedure starts; index /
                 -- constraint / statistics churn is excluded for the same reason. Counting work a rebuild
                 -- does not save would fire rebuilds that cost data movement and buy nothing.
                 [ModificationPasses] = (SELECT COUNT(*) FROM #ColumnChanges cc WITH (NOLOCK)
                                           WHERE cc.[Schema] = t.[Schema] AND cc.[TableName] = t.[Name] AND cc.DropOnly = 0),
                 [AnyColumnChange] = CONVERT(BIT, CASE WHEN EXISTS (SELECT 1 FROM #ColumnChanges cc WITH (NOLOCK)
                                                                      WHERE cc.[Schema] = t.[Schema] AND cc.[TableName] = t.[Name])
                                                       THEN 1 ELSE 0 END)
            FROM #Tables t WITH (NOLOCK)
            WHERE t.NewTable = 0
              AND OBJECT_ID(t.[Schema] + '.' + t.[Name], 'U') IS NOT NULL
              -- A rebuild is a by-absence destroyer: the old table goes whole and only the DECLARED
              -- definition comes back, so anything this run deliberately declined to drop by absence would
              -- go anyway. #SuppressedColumnDrops is the data-losing case (a column retained by PreventDrop
              -- or by the per-table DropColumnsRemovedFromProduct opt-out), and @CaptureWouldDrop is set
              -- exactly when the environment is in protected mode (ProductQuench sets
              -- CaptureWouldDrop = _protectedMode), which promises to destroy nothing by absence at all.
              -- Either one outranks the policy: declining to rebuild costs an in-place ALTER, rebuilding
              -- anyway costs the user the data that protection existed to keep.
              AND @CaptureWouldDrop = 0
              AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                                WHERE s.[Schema] = t.[Schema] AND s.[TableName] = t.[Name])) p
    WHERE (p.[Mode] = 'ALWAYS' AND p.[AnyColumnChange] = 1)
       OR (p.[Mode] = 'THRESHOLD' AND p.[Threshold] IS NOT NULL AND p.[ModificationPasses] >= p.[Threshold])
       -- Deliberately NOT conjoined with any Mode or with [AnyColumnChange]: drifted column order is a
       -- standing reason to rebuild on its own, and on a table whose columns are merely in the wrong order
       -- there is no column CHANGE to detect -- requiring one would make the trigger unreachable in exactly
       -- the case it was added for.
       OR (p.[OnOrderMismatch] = 1 AND p.[OrderMismatch] = 1)

  IF EXISTS (SELECT 1 FROM #RebuildElection)
  BEGIN
    DECLARE @v_RebuildSchema NVARCHAR(500), @v_RebuildTable NVARCHAR(500)
    DECLARE rebuild_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT [Schema], [TableName] FROM #RebuildElection
    OPEN rebuild_cursor
    FETCH NEXT FROM rebuild_cursor INTO @v_RebuildSchema, @v_RebuildTable
    WHILE @@FETCH_STATUS = 0
    BEGIN
      -- @WhatIf goes straight through: RebuildTable prints its whole sequence and records 'wouldRebuild'
      -- without executing anything, and it applies its refusals in BOTH modes. So a policy that elects a
      -- rebuild on a BLOCKED table (temporal, CDC, replicated, Change Tracking) surfaces RebuildTable's
      -- refusal as an error and the quench fails -- deliberately. It does NOT quietly fall back to
      -- altering in place: that would let a package ask for a rebuild and silently get something else,
      -- and the states that block a rebuild are exactly the ones where that difference matters.
      EXEC SchemaSmith.RebuildTable @p_Schema = @v_RebuildSchema, @p_Table = @v_RebuildTable, @p_WhatIf = @WhatIf
      FETCH NEXT FROM rebuild_cursor INTO @v_RebuildSchema, @v_RebuildTable
    END
    CLOSE rebuild_cursor
    DEALLOCATE rebuild_cursor

    -- BYPASS THE IN-PLACE COLUMN PHASES. Every column pass below -- the data-preserving swaps, the
    -- computed-column drop-and-recreate, the by-absence column drops -- and every dependent cleanup that
    -- clears the way for one (index, statistics, fulltext, default, check) drives off #ColumnChanges.
    -- Emptying it for a rebuilt table is therefore the WHOLE bypass, in one place, instead of a
    -- "was it rebuilt?" test threaded through a dozen queries where one missed site leaves the table both
    -- rebuilt AND altered. It is also simply true: the replacement was built to the declared definition,
    -- so it has no pending column change left to apply.
    DELETE cc
      FROM #ColumnChanges cc
      JOIN #RebuildElection r WITH (NOLOCK) ON r.[Schema] = cc.[Schema] AND r.[TableName] = cc.[TableName]

    -- The rebuild drops the old table whole and its extended properties go with it. The ownership stamp
    -- further down re-adds ProductName only for tables MISSING from #TableProperties -- a snapshot taken
    -- before the rebuild -- so a rebuilt table would come back unowned and the next deploy would not
    -- recognise it as this product's. Drop its snapshot row so the stamp sees it as missing and re-applies.
    DELETE tp
      FROM #TableProperties tp
      JOIN #RebuildElection r WITH (NOLOCK) ON r.[Schema] = tp.[Schema]
                                           AND SchemaSmith.fn_StripBracketWrapping(r.[TableName]) = tp.TableName

    -- Same staleness on the index side: these rows describe indexes the rebuild took with the table.
    -- MissingIndexesAndConstraintsQuench re-collects index properties from the live catalog and re-stamps,
    -- so all these rows can still do here is describe objects that no longer exist.
    DELETE ip
      FROM #IndexProperties ip
      JOIN #RebuildElection r WITH (NOLOCK) ON r.[Schema] = ip.[Schema] AND r.[TableName] = ip.TableName
    DELETE ir
      FROM #IndexesRemovedFromProduct ir
      JOIN #RebuildElection r WITH (NOLOCK) ON r.[Schema] = ir.[Schema] AND r.[TableName] = ir.TableName
  END

  -- No-drop protection tier (#270): when protected mode is active the caller forces
  -- @DropForeignKeysRemovedFromProduct to 0 so the drop block below never runs. Record the foreign
  -- keys that WOULD have been dropped by absence (present on the table, absent from the package,
  -- and the per-table cascade tightening not opting out) to the ChangeAudit seam as 'dropSuppressed' so
  -- the run can surface a manifest. Audit rows only -- no DDL -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture foreign keys suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Foreign key ' + t.[Schema] + '.' + t.[Name] + '.' + fk.[name] + ' removed from product but PreventDrop is active -- skipping drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''' + t.[Schema] + '.' + t.[Name] + '.' + fk.[name] + ''', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM #Tables t WITH (NOLOCK)
      JOIN sys.foreign_keys fk ON fk.parent_object_id = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      WHERE NOT EXISTS (SELECT * FROM #ForeignKeys fk2 WITH (NOLOCK) WHERE t.[Schema] = fk2.[Schema] AND t.[Name] = fk2.[TableName] AND fk.[name] = SchemaSmith.fn_StripBracketWrapping(fk2.[KeyName]))
        AND ISNULL(t.[DropForeignKeysRemovedFromProduct], 1) = 1
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  RAISERROR('Collect Foreign Keys To Drop', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#FKsToDrop') IS NOT NULL DROP TABLE #FKsToDrop
  SELECT t.[Schema], [TableName] = t.[Name], [FKName] = fk.[Name]
    INTO #FKsToDrop
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.foreign_keys fk ON fk.parent_object_id = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    WHERE NOT EXISTS (SELECT * FROM #ForeignKeys fk2 WITH (NOLOCK) WHERE t.[Schema] = fk2.[Schema] AND t.[Name] = fk2.[TableName] AND fk.[name] = SchemaSmith.fn_StripBracketWrapping(fk2.[KeyName]))
      AND @DropForeignKeysRemovedFromProduct = 1
      AND ISNULL(t.[DropForeignKeysRemovedFromProduct], 1) = 1

  RAISERROR('Drop Foreign Keys No Longer Defined In The Product', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping foreign Key ' + df.[Schema] + '.' + df.[TableName] + '.' + df.[FKName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + df.[Schema] + '.[' + df.[FKName] + ']'') IS NOT NULL ALTER TABLE ' + df.[Schema] + '.' + df.[TableName] + ' DROP CONSTRAINT [' + df.[FKName] + '];' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''' + df.[Schema] + '.' + df.[TableName] + '.' + df.[FKName] + ''', ''dropped'');' AS NVARCHAR(MAX))
                           FROM #FKsToDrop df WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'foreignKey'/'dropped' audit above.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'foreignKey', df.[Schema] + '.' + df.[TableName] + '.' + df.[FKName], 'wouldDrop'
        FROM #FKsToDrop df WITH (NOLOCK)

  RAISERROR('Identify Fulltext Indexes To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#FTIndexesToDropForChanges') IS NOT NULL DROP TABLE #FTIndexesToDropForChanges
  SELECT DISTINCT cc.[Schema], cc.[TableName]
    INTO #FTIndexesToDropForChanges
    FROM sys.fulltext_index_columns ic
    JOIN #ColumnChanges cc WITH (NOLOCK) ON ic.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
                                        AND COL_NAME(ic.[object_id], ic.column_id) = SchemaSmith.fn_StripBracketWrapping(cc.ColumnName)
                                        AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                                                          WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])  -- #358

  RAISERROR('Drop FullText Indexes Referencing Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping fulltext index on ' + di.[Schema] + '.' + di.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP FULLTEXT INDEX ON ' + di.[Schema] + '.' + di.[TableName] + ';' AS NVARCHAR(MAX))
                           FROM #FTIndexesToDropForChanges di WITH (NOLOCK)
                           JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(di.[Schema] + '.' + di.[TableName])
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Collect Existing FullText Indexes', 10, 100) WITH NOWAIT
  -- sys.fulltext_index_columns.statistical_semantics is a 2012 column. This proc is NOT swapped on the
  -- legacy tier (see ForgeKindler's SqlServerXmlSwaps) so it is kindled on the 2008 floor too, where a
  -- static reference is a CREATE-time 'invalid column' error -- the whole proc fails to deploy, not just
  -- the semantics clause. Stage through a guarded dynamic INSERT (empty below 2012, where semantic
  -- indexing does not exist) and join to it below.
  IF OBJECT_ID('tempdb..#SemanticCols') IS NOT NULL DROP TABLE #SemanticCols
  CREATE TABLE #SemanticCols ([object_id] INT NOT NULL, column_id INT NOT NULL)
  IF SchemaSmith.fn_ServerMajorVersion() >= 11
    EXEC(N'INSERT INTO #SemanticCols ([object_id], column_id) SELECT [object_id], column_id FROM sys.fulltext_index_columns WHERE statistical_semantics = 1')
  IF OBJECT_ID('tempdb..#ExistingFullTextIndexes') IS NOT NULL DROP TABLE #ExistingFullTextIndexes
  SELECT t.[Schema], [TableName] = t.[Name],
         STUFF((SELECT ',' + '[' + COL_NAME(fc.[object_id], fc.column_id) + ']' +
                            CASE WHEN fc.type_column_id IS NOT NULL
                                 THEN ' TYPE COLUMN [' + COL_NAME(fc.[object_id], fc.type_column_id) + ']'
                                 ELSE '' END +
                            -- Full-text LANGUAGE churn: LANGUAGE only when it deviates from the column's own
                            -- collation-implied default -- stamping every column would churn every existing
                            -- full-text index once. Must render byte-identical to GenerateTableJson.sql's /
                            -- GenerateTableXml.sql's extraction and the declared-side parse in
                            -- ParseTableJsonIntoTempTables.sql / ParseTableXmlIntoTempTables.sql; drift
                            -- compares these as strings. Mirrors IndexOnlyQuench.sql's live-side build,
                            -- including the JOIN (not subquery) form -- the two rendering forms must never
                            -- be allowed to diverge again. NULL collation (non-character column) has no
                            -- default to compare against, so LANGUAGE is always emitted for it.
                            CASE WHEN c.collation_name IS NULL OR fc.language_id <> COLLATIONPROPERTY(c.collation_name, 'LCID')
                                 THEN ' LANGUAGE ' + CAST(fc.language_id AS NVARCHAR(10))
                                 ELSE '' END +
                            -- Mirrors the extractor's render exactly -- drift compares these as strings,
                            -- so a clause on one side only would drop and repopulate the index every deploy.
                            CASE WHEN EXISTS (SELECT 1 FROM #SemanticCols sc
                                                WHERE sc.[object_id] = fc.[object_id] AND sc.column_id = fc.column_id)
                                 THEN ' STATISTICAL_SEMANTICS' ELSE '' END
            FROM sys.fulltext_index_columns fc
            JOIN sys.columns c ON c.[object_id] = fc.[object_id] AND c.column_id = fc.column_id
            WHERE fi.[object_id] = fc.[object_id]
            ORDER BY COL_NAME(fc.[object_id], fc.column_id) FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') AS [Columns],
         FullTextCatalog = '[' + (SELECT c.[name] COLLATE DATABASE_DEFAULT FROM sys.fulltext_catalogs c WHERE c.fulltext_catalog_id = fi.fulltext_catalog_id) + ']',
         KeyIndex = '[' + (SELECT i.[Name] COLLATE DATABASE_DEFAULT FROM sys.indexes i WHERE i.[object_id] = fi.[object_id] AND i.[index_id] = fi.[unique_index_id]) + ']',
         ChangeTracking = change_tracking_state_desc COLLATE DATABASE_DEFAULT,
         [StopList] = '[' + COALESCE((SELECT fs.[name] COLLATE DATABASE_DEFAULT FROM sys.fulltext_stoplists fs WHERE fs.stoplist_id = fi.stoplist_id), 'SYSTEM') + ']'
    INTO #ExistingFullTextIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    WHERE t.NewTable = 0
  
  RAISERROR('Identify Indexes To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexesToDropForColumnChanges') IS NOT NULL DROP TABLE #IndexesToDropForColumnChanges
  SELECT DISTINCT cc.[Schema], cc.[TableName], IndexName = i.[name],
         IsConstraint = CAST(CASE WHEN i.is_primary_key = 1 OR i.is_unique_constraint = 1 THEN 1 ELSE 0 END AS BIT),
         IsUnique = i.is_unique,
         IsClustered = CAST(CASE WHEN i.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END AS BIT)
    INTO #IndexesToDropForColumnChanges
    FROM sys.indexes i
    JOIN #ColumnChanges cc WITH (NOLOCK) ON i.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) 
    AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                      WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])  -- #358
    LEFT JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id]
                                                AND ic.[index_id] = i.[index_id]
                                                AND COL_NAME(ic.[object_id], ic.column_id) = SchemaSmith.fn_StripBracketWrapping(cc.ColumnName)
    WHERE ic.column_id IS NOT NULL
       OR i.filter_definition LIKE '%' + SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) + '%'
  
  -- Handle table compression changes
  RAISERROR('Fixup Table Compression', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering table compression for ' + t.[Schema] + '.' + t.[Name] + ' TO ' + t.[CompressionType] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' REBUILD PARTITION=ALL WITH (DATA_COMPRESSION=' + t.[CompressionType] + ');' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + t.[Schema] + '.' + t.[Name] + ''', ''modified'');' AS NVARCHAR(MAX))
                           FROM #Tables t WITH (NOLOCK)
                           LEFT JOIN sys.partitions AS p WITH (NOLOCK) ON p.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                                                      AND p.index_id < 2
                           WHERE t.NewTable = 0
                             AND t.[CompressionType] IN ('NONE', 'ROW', 'PAGE')
                             AND COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') <> t.[CompressionType]
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'table'/'modified' (compression) audit above.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'table', t.[Schema] + '.' + t.[Name], 'wouldModify'
        FROM #Tables t WITH (NOLOCK)
        LEFT JOIN sys.partitions AS p WITH (NOLOCK) ON p.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name]) AND p.index_id < 2
        WHERE t.NewTable = 0 AND t.[CompressionType] IN ('NONE', 'ROW', 'PAGE')
          AND COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') <> t.[CompressionType]

  -- Handle index compression changes
  RAISERROR('Fixup Index Compression', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering index compression for ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ' TO ' + i.[CompressionType] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' REBUILD PARTITION=ALL WITH (DATA_COMPRESSION=' + i.[CompressionType] + ');' AS NVARCHAR(MAX))
                           FROM #Indexes i WITH (NOLOCK)
                           JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                                                            AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
                           LEFT JOIN sys.partitions p ON p.[object_id] = si.[object_id]
                                                                   AND p.index_id = si.index_id
                           WHERE COALESCE(p.data_compression_desc COLLATE DATABASE_DEFAULT, 'NONE') <> i.[CompressionType]
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Handle XML compression changes (table, then index).
  --
  -- ONLY WHERE THE CATALOG CAN ACTUALLY REPORT THE SETTING, and the guard is the SAME kindle-time
  -- decision that composed the column reference -- not a runtime version test, which can disagree with it. sys.partitions.xml_compression
  -- does not exist below 2025, so {{XmlCompressionRead}} resolves to a NULL literal there -- and without
  -- the guard the comparison below would read NULL as 'currently off' and REBUILD every declared-ON
  -- table on every single deploy. A full rebuild per deploy is far worse than not converging.
  --
  -- So on 2022-2024 this property is applied at CREATE and never re-evaluated: the server cannot say
  -- what the current setting is, so there is no drift to detect. Documented on SqlServerTable.
  IF {{XmlCompressionCanRead}} = 1
  BEGIN
    RAISERROR('Fixup Table XML Compression', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering table XML compression for ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                    'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' REBUILD PARTITION=ALL WITH (XML_COMPRESSION=' + CASE WHEN t.[XmlCompression] = 1 THEN 'ON' ELSE 'OFF' END + ');' + CHAR(13) + CHAR(10) +
                                    'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + t.[Schema] + '.' + t.[Name] + ''', ''modified'');' AS NVARCHAR(MAX))
                             FROM #Tables t WITH (NOLOCK)
                             LEFT JOIN sys.partitions AS p WITH (NOLOCK) ON p.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                                                        AND p.index_id < 2
                             WHERE t.NewTable = 0
                               AND COALESCE(CONVERT(TINYINT, {{XmlCompressionRead}}), 0) <> ISNULL(t.[XmlCompression], 0)
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    IF @WhatIf = 1
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'table', t.[Schema] + '.' + t.[Name], 'wouldModify'
          FROM #Tables t WITH (NOLOCK)
          LEFT JOIN sys.partitions AS p WITH (NOLOCK) ON p.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name]) AND p.index_id < 2
          WHERE t.NewTable = 0
            AND COALESCE(CONVERT(TINYINT, {{XmlCompressionRead}}), 0) <> ISNULL(t.[XmlCompression], 0)

    RAISERROR('Fixup Index XML Compression', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering index XML compression for ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                    'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' REBUILD PARTITION=ALL WITH (XML_COMPRESSION=' + CASE WHEN i.[XmlCompression] = 1 THEN 'ON' ELSE 'OFF' END + ');' AS NVARCHAR(MAX))
                             FROM #Indexes i WITH (NOLOCK)
                             JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName])
                                                              AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
                             LEFT JOIN sys.partitions p ON p.[object_id] = si.[object_id]
                                                                     AND p.index_id = si.index_id
                             WHERE COALESCE(CONVERT(TINYINT, {{XmlCompressionRead}}), 0) <> ISNULL(i.[XmlCompression], 0)
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  END

  RAISERROR('Collect Existing Index Definitions', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ExistingIndexes') IS NOT NULL DROP TABLE #ExistingIndexes
  SELECT xSchema = t.[Schema], [xTableName] = t.[Name], [xIndexName] = CAST(si.[Name] AS NVARCHAR(500)),
         IsConstraint = CAST(CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1 THEN 1 ELSE 0 END AS BIT),
         IsUnique = si.is_unique, IsClustered = CAST(CASE WHEN si.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END AS BIT), [FillFactor] = ISNULL(NULLIF(si.fill_factor, 0), 100),
         -- Filegroup placement (#filegroups): the index's LIVE filegroup name, for the declared-vs-deployed
         -- move check below. Deliberately NOT folded into [IndexScript] -- that string drives #IndexChanges'
         -- drop+recreate detection, and a placement difference must ERROR, never trigger a silent rebuild
         -- via the ordinary "index definition changed" path. NULL when data_space_id is a partition scheme
         -- rather than a plain filegroup, which is exactly when [xPartitionScheme] below is populated
         -- instead -- the two are mutually exclusive because an index lives on ONE data space.
         [xFileGroup] = fg.[name],
         -- Partition placement (#partitioning, K1): the same read for a scheme, kept out of [IndexScript]
         -- for the same reason. The column is read alongside the scheme because the same scheme applied to
         -- a different column is a different physical layout.
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
                            THEN ' (' + STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']' + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                                           FROM sys.index_columns ic
                                           WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 0
                                           ORDER BY key_ordinal FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')' +
                                 CASE WHEN EXISTS (SELECT * FROM sys.index_columns ic WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1)
                                      THEN ' INCLUDE (' +
                                           STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']'
                                              FROM sys.index_columns ic
                                              WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1
                                              ORDER BY COL_NAME(ic.[object_id], ic.column_id) FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')'
                                      ELSE '' END
                            WHEN si.[type] IN (6)
                            THEN ' (' + STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']'
                                           FROM sys.index_columns ic
                                           WHERE si.[object_id] = ic.[object_id] AND si.index_id = ic.index_id AND is_included_column = 1
                                           ORDER BY COL_NAME(ic.[object_id], ic.column_id) FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')'
                            ELSE '' END +
                       CASE WHEN si.has_filter = 1 THEN ' WHERE ' + SchemaSmith.fn_StripParenWrapping(si.filter_definition) ELSE '' END +
                       -- One WITH clause built from an option list. CRITICAL: this string and the
                       -- #IndexChanges comparison string below must be byte-identical, or an index
                       -- carrying one of these options is dropped and recreated on EVERY deploy. Each
                       -- option is emitted only when set, so an index without them produces exactly the
                       -- string it produced before they existed and deployed packages do not churn.
                       -- FILLFACTOR is deliberately absent from both -- it has its own opt-in rebuild
                       -- path (UpdateFillFactor), and folding it in here would rebuild on ordinary drift.
                       CASE WHEN o.[WithOptions] <> '' THEN ' WITH (' + STUFF(o.[WithOptions], 1, 2, '') + ')' ELSE '' END
    INTO #ExistingIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                     AND si.index_id > 0
                                     AND is_hypothetical = 0
                                     AND is_disabled = 0
    LEFT JOIN sys.partitions p ON p.[object_id] = si.[object_id]
                                            AND p.index_id = si.index_id
    LEFT JOIN sys.filegroups fg ON fg.data_space_id = si.data_space_id
    LEFT JOIN sys.data_spaces pds ON pds.data_space_id = si.data_space_id AND pds.[type] = 'PS'
    CROSS APPLY (SELECT [WithOptions] =
                   CASE WHEN (si.[type] NOT IN (5, 6) AND ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT IN ('NONE', 'ROW', 'PAGE'))
                             OR (si.[type] IN (5, 6) AND ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT IN ('COLUMNSTORE', 'COLUMNSTORE_ARCHIVE'))
                        THEN ', DATA_COMPRESSION=' + ISNULL(p.[data_compression_desc], 'NONE') COLLATE DATABASE_DEFAULT ELSE '' END +
                   CASE WHEN si.ignore_dup_key = 1 THEN ', IGNORE_DUP_KEY=ON' ELSE '' END +
                   CASE WHEN si.is_padded = 1 THEN ', PAD_INDEX=ON' ELSE '' END) o
    WHERE t.NewTable = 0
      AND NOT EXISTS (SELECT * FROM sys.xml_indexes xi WHERE xi.[object_id] = si.[object_id] AND xi.index_id = si.index_id)

  -- Filegroup placement (#filegroups): an EXISTING index (found live, still declared by name) whose
  -- declared filegroup differs from where it is currently deployed is a MOVE -- same "error, don't rebuild"
  -- contract as the table-level check above. Declared NULL means "the database's own default filegroup",
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
    RAISERROR('Index %s declares filegroup %s, but is currently deployed on filegroup %s. SchemaSmith does not move an existing index to a different filegroup (that is a rebuild) -- migrate it manually, or correct the declared filegroup to match.', 16, 1, @v_IdxMoveIndex, @v_IdxMoveDeclared, @v_IdxMoveLive)
  END

  -- Partition placement (#partitioning, K1): the index-level twin of the table check above, and refused for
  -- the same reason -- rebuilding an index onto a different scheme rewrites the whole index. Placement is
  -- deliberately absent from [IndexScript], so without this an index whose declared scheme changed would be
  -- SILENTLY left where it is: the ordinary drop-and-recreate path cannot see a difference it never
  -- compares.
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
    RAISERROR('Index %s declares partition placement %s, but is currently deployed on %s. SchemaSmith does not move an existing index onto or between partition schemes -- that rebuilds the whole index. Migrate it manually, or correct the declaration to match.', 16, 1, @v_IdxPsIndex, @v_IdxPsDeclared, @v_IdxPsLive)
  END

  RAISERROR('Detect Index Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexChanges') IS NOT NULL DROP TABLE #IndexChanges
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
  IF OBJECT_ID('tempdb..#IndexRenames') IS NOT NULL DROP TABLE #IndexRenames
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
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Renaming ' + [OldName] + ' to ' + [NewName] + ' ON ' + ir.[Schema] + '.' + ir.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN IsConstraint = 1
                                       THEN CASE WHEN OBJECT_ID(ir.[Schema] + '.' + ir.[NewName]) IS NULL
                                                 THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''OBJECT'';'
                                                 ELSE 'IF OBJECT_ID(''' + ir.[Schema] + '.[' + ir.[OldName] + ']'') IS NOT NULL ALTER TABLE ' + ir.[Schema] + '.' + ir.[TableName] + ' DROP CONSTRAINT [' + ir.[OldName] + '];'
                                                 END
                                       ELSE CASE WHEN INDEXPROPERTY(OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]), SchemaSmith.fn_StripBracketWrapping(ir.[NewName]), 'IndexID') IS NULL
                                                 THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(ir.[TableName]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''INDEX'';'
                                                 ELSE 'IF INDEXPROPERTY(OBJECT_ID(''' + ir.[Schema] + '.' + ir.[TableName] + '''), ''' + ir.[OldName] + ''', ''IndexID'') IS NOT NULL DROP INDEX [' + ir.[OldName] + '] ON ' + ir.[Schema] + '.' + ir.[TableName] + ';'
                                                 END
                                       END AS NVARCHAR(MAX))
                           FROM #IndexRenames ir WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Collect Existing XML Index Definitions', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ExistingXmlIndexes') IS NOT NULL DROP TABLE #ExistingXmlIndexes
  SELECT xSchema = t.[Schema], [xTableName] = t.[Name], [xIndexName] = CAST(i.[Name] COLLATE DATABASE_DEFAULT AS NVARCHAR(500)),
         IndexScript = 'CREATE ' + CASE WHEN i.using_xml_index_id IS NULL THEN 'PRIMARY ' ELSE '' END +
                       'XML INDEX [' + i.[name] COLLATE DATABASE_DEFAULT + '] ON [' + OBJECT_SCHEMA_NAME(i.[object_id]) + '].[' + OBJECT_NAME(i.[object_id]) + '] ' + 
                       '([' + COL_NAME(i.[Object_id], ic.column_id) + '])' + 
                       CASE WHEN i.using_xml_index_id IS NOT NULL
                            THEN ' USING XML INDEX [' + (SELECT [Name] FROM sys.xml_indexes i2 WHERE i2.[object_id] = i.[object_id] AND i2.index_id = i.using_xml_index_id) COLLATE DATABASE_DEFAULT + '] ' +
                                 'FOR ' + i.secondary_type_desc COLLATE DATABASE_DEFAULT 
                            ELSE '' END
    INTO #ExistingXmlIndexes
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.xml_indexes i ON i.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
    JOIN sys.index_columns ic ON i.[object_id] = ic.[object_id] AND i.index_id = ic.index_id
    WHERE t.NewTable = 0

  RAISERROR('Detect Xml Index Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#XmlIndexChanges') IS NOT NULL DROP TABLE #XmlIndexChanges
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
  IF OBJECT_ID('tempdb..#XmlIndexRenames') IS NOT NULL DROP TABLE #XmlIndexRenames
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
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Renaming ' + [OldName] + ' to ' + [NewName] + ' ON ' + ir.[Schema] + '.' + ir.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN INDEXPROPERTY(OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]), SchemaSmith.fn_StripBracketWrapping(ir.[NewName]), 'IndexID') IS NULL
                                       THEN 'EXEC sp_rename N''' + SchemaSmith.fn_StripBracketWrapping(ir.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(ir.[TableName]) + '.' + ir.[OldName] + ''', N''' + SchemaSmith.fn_StripBracketWrapping(ir.[NewName]) + ''', N''INDEX'';'
                                       ELSE 'IF INDEXPROPERTY(OBJECT_ID(''' + ir.[Schema] + '.' + ir.[TableName] + '''), ''' + ir.[OldName] + ''', ''IndexID'') IS NOT NULL DROP INDEX [' + ir.[OldName] + '] ON ' + ir.[Schema] + '.' + ir.[TableName] + ';'
                                       END AS NVARCHAR(MAX))
                           FROM #XmlIndexRenames ir WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- No-drop protection tier (#270): when protected mode is active the caller forces both
  -- @DropIndexesRemovedFromProduct and @DropUnknownIndexes to 0, so the by-absence arms of the
  -- #IndexesToDrop build below never populate. Record the indexes that WOULD have been dropped by
  -- absence -- exactly the three by-absence arms of that build, each minus its @Drop... env gate:
  -- (a) indexes removed from the product (ownership-stamped, per-table cascade tightening not opting
  -- out), (b) unknown relational indexes, (c) unknown XML indexes. The modified / for-change arms
  -- (#IndexesToDropForColumnChanges, #IndexChanges, #XmlIndexChanges, and the clustered-PK XML cascade)
  -- are transient drop-then-recreate for a declared change and are deliberately EXCLUDED -- capturing
  -- them would falsely report a transient drop as protection-withheld. Audit rows only -- no DDL -- so
  -- this runs regardless of @WhatIf. ObjectType / ObjectName match the 'dropped' index audit byte-for-byte.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture indexes suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  ' + [ObjName] + ' removed from product but PreventDrop is active -- skipping ' + [ObjType] + ' drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + [ObjType] + ''', ''' + [ObjName] + ''', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM (
        -- Arm (a): indexes removed from the product (ownership-stamped) -- @DropIndexesRemovedFromProduct
        -- env gate stripped; per-table cascade-tightening opt-out (ISNULL(...) = 1) kept.
        SELECT [ObjType] = CASE WHEN ir.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END,
               [ObjName] = ir.[Schema] + '.' + ir.[TableName] + '.' + SchemaSmith.fn_StripBracketWrapping(ir.[IndexName])
          FROM #IndexesRemovedFromProduct ir WITH (NOLOCK)
          JOIN sys.indexes i ON i.[object_id] = OBJECT_ID(ir.[Schema] + '.' + ir.[TableName]) AND i.[Name] = SchemaSmith.fn_StripBracketWrapping(ir.[IndexName])
          WHERE ISNULL((SELECT t.[DropIndexesRemovedFromProduct] FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = ir.[Schema] AND t.[Name] = ir.[TableName]), 1) = 1
        UNION
        -- Arm (b): unknown relational indexes (present in DB, absent from the product) -- @DropUnknownIndexes env gate stripped.
        SELECT [ObjType] = CASE WHEN ei.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END,
               [ObjName] = ei.[xSchema] + '.' + ei.[xTableName] + '.' + ei.[xIndexName]
          FROM #ExistingIndexes ei WITH (NOLOCK)
          WHERE NOT EXISTS (SELECT * FROM #Indexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
            -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
            -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
            AND NOT EXISTS (SELECT * FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
        UNION
        -- Arm (c): unknown XML indexes (present in DB, absent from the product) -- @DropUnknownIndexes env gate stripped; XML indexes are never constraints.
        SELECT [ObjType] = 'index',
               [ObjName] = ei.[xSchema] + '.' + ei.[xTableName] + '.' + ei.[xIndexName]
          FROM #ExistingXmlIndexes ei WITH (NOLOCK)
          WHERE NOT EXISTS (SELECT * FROM #XmlIndexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
            -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
            -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
            AND NOT EXISTS (SELECT * FROM #XmlIndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
      ) x
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  RAISERROR('Identify unknown and modified indexes to drop', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexesToDrop') IS NOT NULL DROP TABLE #IndexesToDrop
  SELECT [Schema] = CAST([Schema] AS NVARCHAR(500)), [TableName] = CAST([TableName] AS NVARCHAR(500)), 
         [IndexName] = CAST(SchemaSmith.fn_StripBracketWrapping([IndexName]) AS NVARCHAR(500)), [IsConstraint], [IsUnique] = i.[is_unique], 
         [IsClustered] = CAST(CASE WHEN i.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END AS BIT)
    INTO #IndexesToDrop
    FROM #IndexesRemovedFromProduct ir WITH (NOLOCK)
    JOIN sys.indexes i ON i.[object_id] = OBJECT_ID([Schema] + '.' + [TableName]) AND i.[Name] = SchemaSmith.fn_StripBracketWrapping([IndexName])
    -- Removed-from-product (ownership-stamped) drop is gated by the cascade flag + per-table
    -- tightening. The unknown (@DropUnknownIndexes) and modified branches below are unaffected.
    WHERE @DropIndexesRemovedFromProduct = 1
      AND ISNULL((SELECT t.[DropIndexesRemovedFromProduct] FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = ir.[Schema] AND t.[Name] = ir.[TableName]), 1) = 1
  UNION
  SELECT [Schema], [TableName], SchemaSmith.fn_StripBracketWrapping([IndexName]), [IsConstraint], [IsUnique], [IsClustered]
    FROM #IndexesToDropForColumnChanges WITH (NOLOCK)
  UNION
  SELECT [xSchema], [xTableName], [xIndexName], [IsConstraint], [IsUnique], [IsClustered]
    FROM #ExistingIndexes ei WITH (NOLOCK)
    WHERE @DropUnknownIndexes = 1
      AND NOT EXISTS (SELECT * FROM #Indexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
      -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
      -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
      AND NOT EXISTS (SELECT * FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
  UNION
  SELECT [Schema], [TableName], SchemaSmith.fn_StripBracketWrapping([IndexName]), [IsConstraint], [IsUnique], [IsClustered]
    FROM #IndexChanges WITH (NOLOCK)
  UNION
  SELECT [xSchema], [xTableName], [xIndexName], [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
    FROM #ExistingXmlIndexes ei WITH (NOLOCK)
    WHERE @DropUnknownIndexes = 1
      AND NOT EXISTS (SELECT * FROM #XmlIndexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
      -- Not an index that was just RENAMED: it is still in the pre-rename snapshot under its old name,
      -- and without this the deploy logged dropping it (or listed it as a suppressed drop) after renaming it.
      AND NOT EXISTS (SELECT * FROM #XmlIndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
  UNION
  SELECT [Schema], [TableName], SchemaSmith.fn_StripBracketWrapping([IndexName]), [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
    FROM #XmlIndexChanges WITH (NOLOCK)

  -- Need to drop all the XML indexes if we're removing the clustered PK
  INSERT #IndexesToDrop ([Schema], [TableName], [IndexName], [IsConstraint], [IsUnique], [IsClustered])
    SELECT [xSchema], [xTableName], [xIndexName], [IsConstraint] = 0, [IsUnique] = 0, [IsClustered] = 0
      FROM #ExistingXmlIndexes ei WITH (NOLOCK)
      WHERE EXISTS (SELECT * FROM #IndexesToDrop id WITH (NOLOCK) WHERE [xSchema] = [Schema] AND [xTableName] = [TableName] AND id.[IsClustered] = 1)
        AND NOT EXISTS (SELECT * FROM #IndexesToDrop id WITH (NOLOCK) WHERE [xSchema] = [Schema] AND [xTableName] = [TableName] AND [xIndexName] = [IndexName])
  
  RAISERROR('Drop Referencing Foreign Keys When Dropping Unique Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping foreign Key ' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '.' + OBJECT_NAME(fk.parent_object_id) + '.' + fk.[name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''[' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + fk.[name] + ']'') IS NOT NULL ALTER TABLE [' + OBJECT_SCHEMA_NAME(fk.parent_object_id) + '].[' + OBJECT_NAME(fk.parent_object_id) + '] DROP CONSTRAINT [' + fk.[name] + '];' AS NVARCHAR(MAX))
                           FROM #IndexesToDrop di WITH (NOLOCK)
                           JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(di.[Schema] + '.' + di.[TableName])
                           WHERE IsConstraint = 1 OR IsUnique = 1
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Drop FullText Indexes Referencing Unique Indexes That Will Be Dropped', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping fulltext index on ' + ef.[Schema] + '.' + ef.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP FULLTEXT INDEX ON ' + ef.[Schema] + '.' + ef.[TableName] + ';' AS NVARCHAR(MAX))
                           FROM #IndexesToDrop id WITH (NOLOCK)
                           JOIN #ExistingFullTextIndexes ef WITH (NOLOCK) ON id.[Schema] = ef.[Schema]
                                                                         AND id.[TableName] = ef.[TableName]
                                                                         AND id.[IndexName] = SchemaSmith.fn_StripBracketWrapping(ef.[KeyIndex])
                           JOIN sys.fulltext_indexes fi ON fi.[object_id] = OBJECT_ID(ef.[Schema] + '.' + ef.[TableName])
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Drop Unknown and Modified Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'RAISERROR(''  Dropping ' + CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + di.[Schema] + '.' + di.[TableName] + '.' + di.[IndexName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN IsConstraint = 1
                                       THEN 'IF OBJECT_ID(''' + di.[Schema] + '.[' + di.[IndexName] + ']'') IS NOT NULL ALTER TABLE ' + di.[Schema] + '.' + di.[TableName] + ' DROP CONSTRAINT [' + di.[IndexName] + '];'
                                       ELSE 'IF INDEXPROPERTY(OBJECT_ID(''' + di.[Schema] + '.' + di.[TableName] + '''), ''' + di.[IndexName] + ''', ''IndexID'') IS NOT NULL DROP INDEX [' + di.[IndexName] + '] ON ' + di.[Schema] + '.' + di.[TableName] + ';'
                                       END + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''' + CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END + ''', ''' + di.[Schema] + '.' + di.[TableName] + '.' + di.[IndexName] + ''', ''dropped'');'
                                  FROM #IndexesToDrop di WITH (NOLOCK)
                                  ORDER BY CASE WHEN [IsClustered] = 0 THEN 0 ELSE 1 END
                                  FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'index'/'constraint' 'dropped' audit above.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END, di.[Schema] + '.' + di.[TableName] + '.' + di.[IndexName], 'wouldDrop'
        FROM #IndexesToDrop di WITH (NOLOCK)

  RAISERROR('Fixup Modified Fillfactors', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Fixup ' + CASE WHEN IsConstraint = 1 THEN 'constraint' ELSE 'index' END + ' fillfactor in ' + i.[Schema] + '.' + i.[TableName] + '.' + i.[IndexName] + ''', 10, 100) WITH NOWAIT; ' +
                                  'ALTER INDEX ' + i.[IndexName] + ' ON ' + i.[Schema] + '.' + i.[TableName] + ' REBUILD WITH (FILLFACTOR = ' + CONVERT(NVARCHAR(5), i.[FillFactor]) + ', SORT_IN_TEMPDB = ON);' AS NVARCHAR(MAX))
                           FROM #ExistingIndexes ei WITH (NOLOCK)
                           JOIN #Indexes i WITH (NOLOCK) ON ei.[xSchema] = i.[Schema]
                                                        AND ei.[xTableName] = i.[TableName]
                                                        AND ei.[xIndexName] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName])
                           WHERE i.[UpdateFillFactor] = 1
                             AND ei.[FillFactor] <> i.[FillFactor]
                             AND INDEXPROPERTY(OBJECT_ID(i.[Schema] + '.' + i.[TableName]), ei.[xIndexName], 'IndexID') IS NOT NULL
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Identify Statistics To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#StatisticsToDropForChanges') IS NOT NULL DROP TABLE #StatisticsToDropForChanges
  SELECT DISTINCT cc.[Schema], cc.[TableName], [StatName] = i.[name]
    INTO #StatisticsToDropForChanges
    FROM sys.stats i 
    JOIN #ColumnChanges cc WITH (NOLOCK) ON i.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) 
    AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                      WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])  -- #358
    LEFT JOIN sys.stats_columns ic ON ic.[object_id] = i.[object_id]
                                                AND ic.[stats_id] = i.[stats_id]
                                                AND COL_NAME(ic.[object_id], ic.column_id) = SchemaSmith.fn_StripBracketWrapping(cc.ColumnName)
    WHERE ic.column_id IS NOT NULL
       OR i.filter_definition LIKE '%' + SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) + '%'
  
  RAISERROR('Drop Statistics Referencing Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping statistic ' + id.[Schema] + '.' + id.[TableName] + '.[' + [StatName] + ']'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP STATISTICS ' + id.[Schema] + '.' + id.[TableName] + '.[' + [StatName] + '];' AS NVARCHAR(MAX))
                           FROM #StatisticsToDropForChanges id WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Identify Foreign Keys To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#FKsToDropForChanges') IS NOT NULL DROP TABLE #FKsToDropForChanges
  SELECT DISTINCT cc.[Schema], cc.[TableName], FKName = fk.[name]
    INTO #FKsToDropForChanges
    FROM sys.foreign_key_columns fc
    LEFT JOIN sys.foreign_keys fk ON fk.object_id = fc.constraint_object_id
    JOIN #ColumnChanges cc WITH (NOLOCK) ON (OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) = fk.parent_object_id
                                         AND SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) = COL_NAME(fc.[parent_object_id], fc.parent_column_id))
                                         OR (OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) = fk.referenced_object_id
                                         AND SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) = COL_NAME(fc.[referenced_object_id], fc.referenced_column_id))
    WHERE NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)  -- #358
                        WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])

  RAISERROR('Drop Foreign Keys Referencing Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping foreign Key ' + df.[Schema] + '.' + df.[TableName] + '.' + df.[FKName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + df.[Schema] + '.[' + df.[FKName] + ']'') IS NOT NULL ALTER TABLE ' + df.[Schema] + '.' + df.[TableName] + ' DROP CONSTRAINT [' + df.[FKName] + '];' AS NVARCHAR(MAX))
                           FROM #FKsToDropForChanges df WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Identify Defaults To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#DefaultsToDropForChanges') IS NOT NULL DROP TABLE #DefaultsToDropForChanges
  SELECT cc.[Schema], cc.[TableName], DefaultName = dc.[name]
    INTO #DefaultsToDropForChanges
    FROM sys.default_constraints dc
    JOIN #ColumnChanges cc WITH (NOLOCK) ON dc.[parent_object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName])
                                        AND COL_NAME(dc.parent_object_id, dc.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(cc.ColumnName)
                                        AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                                                          WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])  -- #358

  RAISERROR('Drop Defaults Referencing Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping default ' + dd.[Schema] + '.' + dd.[TableName] + '.' + dd.[DefaultName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + dd.[Schema] + '.[' + dd.[DefaultName] + ']'') IS NOT NULL ALTER TABLE ' + dd.[Schema] + '.' + dd.[TableName] + ' DROP CONSTRAINT [' + dd.[DefaultName] + '];' AS NVARCHAR(MAX))
                           FROM #DefaultsToDropForChanges dd WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Identify Check Constraints To Drop Based On Column Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ChecksToDropForChanges') IS NOT NULL DROP TABLE #ChecksToDropForChanges
  SELECT cc.[Schema], cc.[TableName], CheckName = ck.[name]
    INTO #ChecksToDropForChanges
    FROM sys.check_constraints ck
    JOIN #ColumnChanges cc WITH (NOLOCK) ON ck.[parent_object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) 
                                        AND ((ck.parent_column_id <> 0 AND COL_NAME(ck.parent_object_id, ck.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(cc.ColumnName))
                                          OR (ck.parent_column_id = 0 AND ck.[definition] LIKE '%' + SchemaSmith.fn_StripBracketWrapping(cc.ColumnName) + '%'))
                                        AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s WITH (NOLOCK)
                                                          WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])  -- #358

  RAISERROR('Drop Check Constraints Referencing Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping check constraint ' + fc.[Schema] + '.' + fc.[TableName] + '.' + fc.CheckName + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + fc.[Schema] + '.[' + fc.CheckName + ']'') IS NOT NULL ALTER TABLE ' + fc.[Schema] + '.' + fc.[TableName] + ' DROP CONSTRAINT [' + fc.CheckName + '];' AS NVARCHAR(MAX))
                           FROM #ChecksToDropForChanges fc WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Verify CDC Capture-Instance Headroom For Tables With Column Changes', 10, 100) WITH NOWAIT
  -- CDC deliberately stays ON through the column work. Disabling it here used to drop the capture
  -- instance outright, discarding every captured change a downstream reader had not yet consumed.
  -- A second instance is created after the column work instead (rotation), and SQL Server permits
  -- only two per table -- so refuse up front rather than failing partway through the column work.
  -- A declared CdcFilegroup the newest capture instance is not on is a rotation reason too (#417): it can only be
  -- honoured by a new instance, and a new instance is exactly what a column change already creates. The same
  -- ceiling applies, for the same reason.
  CREATE TABLE #CdcRotate ([Schema] NVARCHAR(256), [TableName] NVARCHAR(256), OldCaptureInstance NVARCHAR(256),
                           NewFilegroup NVARCHAR(256), Reason NVARCHAR(20))
  IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
  BEGIN
    DECLARE @v_DefaultFilegroup SYSNAME = (SELECT [name] FROM sys.filegroups WHERE is_default = 1)

    IF OBJECT_ID('tempdb..#CdcCandidates') IS NOT NULL DROP TABLE #CdcCandidates
    SELECT t.[Schema], t.[Name],
           Instances = (SELECT COUNT(*) FROM cdc.change_tables c WITH (NOLOCK) WHERE c.source_object_id = st.[object_id]),
           newest.capture_instance AS NewestInstance,
           -- The catalog reports NULL for "the default filegroup"; resolve it so a comparison never passes on NULL.
           ISNULL(newest.filegroup_name, @v_DefaultFilegroup) AS NewestFilegroup,
           newest.filegroup_name AS NewestFilegroupRaw,
           SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup) AS DeclaredFilegroup,
           ColumnChange = CONVERT(BIT, CASE WHEN EXISTS (SELECT 1 FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = t.[Schema] AND cc.[TableName] = t.[Name])
                                              OR EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK) WHERE c.[Schema] = t.[Schema] AND c.[TableName] = t.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL(c.[ComputedExpression], '')) = '')
                                            THEN 1 ELSE 0 END)
      INTO #CdcCandidates
      FROM #Tables t WITH (NOLOCK)
      JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      CROSS APPLY (SELECT TOP 1 ct.capture_instance, ct.filegroup_name
                     FROM cdc.change_tables ct WITH (NOLOCK)
                    WHERE ct.source_object_id = st.[object_id]
                    ORDER BY ct.create_date DESC, ct.[object_id] DESC) newest
      WHERE st.is_tracked_by_cdc = 1 AND t.EnableCDC = 1

    -- Unset means unmanaged: only a DECLARED filegroup can mismatch.
    INSERT #CdcRotate ([Schema], [TableName], OldCaptureInstance, NewFilegroup, Reason)
      SELECT [Schema], [Name], NewestInstance,
             -- A rotation keeps the filegroup it is not told to change: the declared one, else where the old
             -- instance already is. Omitting it (the pre-#417 behaviour) moved a DBA-placed instance to the default.
             COALESCE(DeclaredFilegroup, NewestFilegroupRaw),
             CASE WHEN ColumnChange = 1 THEN 'column' ELSE 'filegroup' END
        FROM #CdcCandidates
       WHERE Instances = 1
         AND (ColumnChange = 1 OR (DeclaredFilegroup IS NOT NULL AND DeclaredFilegroup <> NewestFilegroup))

    DECLARE @v_CdcAtCeiling NVARCHAR(MAX) =
      STUFF((SELECT ', ' + [Schema] + '.' + [Name]
               FROM #CdcCandidates
              WHERE Instances >= 2
                AND (ColumnChange = 1 OR (DeclaredFilegroup IS NOT NULL AND DeclaredFilegroup <> NewestFilegroup))
               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_CdcAtCeiling IS NOT NULL
      RAISERROR('CDC capture-instance limit reached on: %s. SQL Server permits two capture instances per table and both are already in use, so this column or CdcFilegroup change cannot rotate without discarding change history. Drain the older instance on each listed table and drop it (EXEC sys.sp_cdc_disable_table @source_schema = N''<schema>'', @source_name = N''<table>'', @capture_instance = N''<name>''), then re-run.', 16, 1, @v_CdcAtCeiling)
  END

  RAISERROR('Swap Columns Requiring Data-Preserving Replacement', 10, 100) WITH NOWAIT
  DECLARE @v_SwapSchema NVARCHAR(256), @v_SwapTable NVARCHAR(256), @v_SwapColumn NVARCHAR(256), @v_SwapColumnScript NVARCHAR(MAX)
  DECLARE swap_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT cc.[Schema], cc.[TableName], SchemaSmith.fn_StripBracketWrapping(cc.ColumnName), cc.ColumnScript
      FROM #ColumnChanges cc WITH (NOLOCK)
      WHERE cc.MustSwapColumn = 1
  OPEN swap_cursor
  FETCH NEXT FROM swap_cursor INTO @v_SwapSchema, @v_SwapTable, @v_SwapColumn, @v_SwapColumnScript
  WHILE @@FETCH_STATUS = 0
  BEGIN
    DECLARE @v_TempColName NVARCHAR(256) = @v_SwapColumn + '_swap_temp'
    -- Extract data type without NULL/NOT NULL (used only by non-encrypted path, but declared here to avoid re-declare in cursor loop)
    DECLARE @v_SwapDataType NVARCHAR(MAX) = RTRIM(REPLACE(REPLACE(@v_SwapColumnScript, ' NOT NULL', ''), ' NULL', ''))
    -- Always Encrypted changes on existing columns cannot be performed server-side on a standard
    -- (non-enclave) deployment: the server holds no Column Master Key and cannot re-encrypt data.
    -- Fail closed immediately — before any DDL — in both live and WhatIf mode so the impossibility
    -- is visible in previews. The operator must use the Before/After full-table rebuild instead.
    IF @v_SwapColumnScript LIKE '%ENCRYPTED WITH%'
    BEGIN
      RAISERROR('Always Encrypted: in-place encryption change on %s.%s.[%s] cannot be performed on a standard (non-enclave) server. Use Before/After migration scripts to rebuild the table with the target encryption settings and copy data client-side over a Column Encryption Setting=Enabled connection.', 16, 1, @v_SwapSchema, @v_SwapTable, @v_SwapColumn)
    END
    ELSE
    BEGIN
      -- Non-encrypted swap: add as NULL, copy data, then enforce NOT NULL if needed
      -- Each step must execute separately so SQL Server can resolve column names after ADD
      -- Step 1: Add temp column as NULL (can't add NOT NULL to non-empty table without default)
      SET @v_SQL = 'RAISERROR(''  Swapping column ' + @v_SwapSchema + '.' + @v_SwapTable + '.[' + @v_SwapColumn + '] (data-preserving replacement)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                   'ALTER TABLE ' + @v_SwapSchema + '.' + @v_SwapTable + ' ADD [' + @v_TempColName + '] ' + @v_SwapDataType + ' NULL;'
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
      -- Step 2: Copy data from original to temp
      SET @v_SQL = 'UPDATE ' + @v_SwapSchema + '.' + @v_SwapTable + ' SET [' + @v_TempColName + '] = [' + @v_SwapColumn + '];'
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
      -- Step 3: If original was NOT NULL, enforce it on the temp column now that data is copied
      IF @v_SwapColumnScript LIKE '%NOT NULL%'
      BEGIN
        SET @v_SQL = 'ALTER TABLE ' + @v_SwapSchema + '.' + @v_SwapTable + ' ALTER COLUMN [' + @v_TempColName + '] ' + @v_SwapColumnScript + ';'
        IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
      END
    END
    -- Step 4: Drop original column (shared for both paths)
    SET @v_SQL = 'ALTER TABLE ' + @v_SwapSchema + '.' + @v_SwapTable + ' DROP COLUMN [' + @v_SwapColumn + '];'
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    -- Step 5: Rename temp to original
    SET @v_SQL = 'EXEC sp_rename ''' + SchemaSmith.fn_StripBracketWrapping(@v_SwapSchema) + '.' + SchemaSmith.fn_StripBracketWrapping(@v_SwapTable) + '.[' + @v_TempColName + ']'', ''' + @v_SwapColumn + ''', ''COLUMN'';'
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    FETCH NEXT FROM swap_cursor INTO @v_SwapSchema, @v_SwapTable, @v_SwapColumn, @v_SwapColumnScript
  END
  CLOSE swap_cursor
  DEALLOCATE swap_cursor

  RAISERROR('Drop Modified Computed Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping columns from ' + T.[Schema] + '.' + T.[Name] + ' (' + MessageColumns + ')'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' DROP ' + ScriptColumns + ';' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name],
                                        ScriptColumns = STUFF((SELECT ', ' + 'COLUMN ' + [ColumnName] FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.MustDropAndRecreate = 1 AND cc.MustSwapColumn = 0 ORDER BY cc.[ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''),
                                        MessageColumns = STUFF((SELECT ', ' + [ColumnName] FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.MustDropAndRecreate = 1 AND cc.MustSwapColumn = 0 ORDER BY cc.[ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE NewTable = 0
                                     AND EXISTS (SELECT * FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.MustDropAndRecreate = 1 AND cc.MustSwapColumn = 0)) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  -- No-drop protection tier (#270): when protected mode is active the caller forces
  -- @DropColumnsRemovedFromProduct to 0 so the drop block below never runs. Record the columns that
  -- WOULD have been dropped by absence (present on the table, absent from the package -- #ColumnChanges
  -- DropOnly rows -- and the per-table cascade tightening not opting out) to the ChangeAudit seam as
  -- 'dropSuppressed' so the run can surface a manifest. Audit rows only -- no DDL -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture columns suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Column ' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName] + ' removed from product but PreventDrop is active -- skipping drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''column'', ''' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName] + ''', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM #ColumnChanges cc WITH (NOLOCK)
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = cc.[Schema] AND t.[Name] = cc.[TableName]
      WHERE cc.DropOnly = 1
        AND t.NewTable = 0
        AND ISNULL(t.[DropColumnsRemovedFromProduct], 1) = 1
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  RAISERROR('Drop Columns No Longer Part of The Product Definition', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping columns from ' + T.[Schema] + '.' + T.[Name] + ' (' + MessageColumns + ')'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' DROP ' + ScriptColumns + ';' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name],
                                        ScriptColumns = STUFF((SELECT ', ' + 'COLUMN ' + [ColumnName] FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.DropOnly = 1 ORDER BY [ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''),
                                        MessageColumns = STUFF((SELECT ', ' + [ColumnName] FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.DropOnly = 1 ORDER BY [ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE NewTable = 0
                                     AND @DropColumnsRemovedFromProduct = 1
                                     AND ISNULL(T.[DropColumnsRemovedFromProduct], 1) = 1
                                     AND EXISTS (SELECT * FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = T.[Schema] AND cc.[TableName] = T.[Name] AND cc.DropOnly = 1)) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Detect Default Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#DefaultChanges') IS NOT NULL DROP TABLE #DefaultChanges
  SELECT C.[Schema], C.[TableName], C.[ColumnName],
         [DefaultName] = (SELECT [Name]
                          FROM sys.default_constraints dc
                          WHERE dc.parent_object_id = OBJECT_ID(c.[Schema] + '.' + c.[TableName])
                            AND COL_NAME(dc.parent_object_id, dc.parent_column_id) = SchemaSmith.fn_StripBracketWrapping(C.[ColumnName]))
  INTO #DefaultChanges
  FROM #Tables T WITH (NOLOCK)
           JOIN #Columns c WITH (NOLOCK) ON C.[Schema] = T.[Schema]
      AND C.[TableName] = T.[Name]
      AND C.[NewColumn] = 0
           JOIN INFORMATION_SCHEMA.COLUMNS ic ON ic.TABLE_SCHEMA = SchemaSmith.fn_StripBracketWrapping(C.[Schema])
      AND ic.TABLE_NAME = SchemaSmith.fn_StripBracketWrapping(C.[TableName])
      AND ic.COLUMN_NAME = SchemaSmith.fn_StripBracketWrapping(C.[ColumnName])
  WHERE t.NewTable = 0
    AND SchemaSmith.fn_StripParenWrapping(ic.COLUMN_DEFAULT) <> ISNULL(c.[Default], 'NULL')
    -- #242: the texts differ, but a reframed default always differs. Ask what was actually applied.
    AND SchemaSmith.fn_ExpressionMapUnchanged(C.[Schema], C.[TableName], 'COLUMN', C.[ColumnName], 'default',
          c.[Default], ISNULL(ic.COLUMN_DEFAULT, '')) = 0

  -- Truly new physical columns were added previously, now we need to determine which columns need to be added back due change from computed to physical columns
  UPDATE #Columns 
    SET NewColumn = 0 
    WHERE NewColumn = 1 
      AND RTRIM(ISNULL([ComputedExpression], '')) = ''
  UPDATE c
    SET NewColumn = 1
    FROM #Columns c
    WHERE EXISTS (SELECT * FROM #ColumnChanges cc WITH (NOLOCK) WHERE cc.[Schema] = c.[Schema] AND cc.[TableName] = c.[TableName] and cc.ColumnName = c.ColumnName AND cc.MustDropAndRecreate = 1 AND cc.MustSwapColumn = 0)
  
  RAISERROR('Add missing ProductName extended property to tables', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC sp_addextendedproperty @name = N''ProductName'', @value = ''' + @ProductName + ''', ' +
                                                              '@level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ' +
                                                              '@level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''';' AS NVARCHAR(MAX))
                           FROM #Tables t WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT * FROM #TableProperties tp WITH (NOLOCK) WHERE t.[Schema] = tp.[Schema] AND t.[Name] = '[' + tp.TableName + ']' AND tp.PropertyName = 'ProductName')
                             AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL  -- and the table physically exists
                             AND t.[MemoryOptimized] = 0  -- memory-optimized tables reject extended properties; their ownership is tracked in SchemaSmith.ProductOwnership below
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- Stamp/refresh the sticky PreventDrop protection marker so it tracks the package value each run
  -- (an existing table newly marked PreventDrop:true gets its property; one toggled back to false is
  -- updated). Covers the same present-table set as the ProductName stamp above (physical tables in #Tables).
  -- WHAT THE MARKER ALREADY SAYS, READ ONCE. The statement below generates one
  -- fn_listextendedproperty probe plus an sp_updateextendedproperty (or sp_addextendedproperty) PER
  -- TABLE, and then executes all of them -- unconditionally, so a re-deploy where nothing had changed
  -- still re-stamped every table in the package. On a 1,783-table model that is ~1,783 system-procedure
  -- calls to write values that already said what they were going to say, and it measured 5,541 ms.
  -- Reading the current values in one pass lets the generator skip tables that already agree, which on
  -- an unchanged package is all of them. The end state is identical either way.
  IF OBJECT_ID('tempdb..#ExistingPreventDrop') IS NOT NULL DROP TABLE #ExistingPreventDrop
  SELECT [MajorId] = ep.major_id, [Value] = CONVERT(NVARCHAR(100), ep.[value])
    INTO #ExistingPreventDrop
    FROM sys.extended_properties ep
   WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.[name] = 'PreventDrop'
  CREATE CLUSTERED INDEX [ixExistingPreventDrop] ON #ExistingPreventDrop ([MajorId])

  RAISERROR('Stamp/refresh PreventDrop protection marker', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
    'IF EXISTS (SELECT 1 FROM fn_listextendedproperty(N''PreventDrop'', N''Schema'', ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', N''Table'', ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', default, default)) ' +
    'EXEC sp_updateextendedproperty @name = N''PreventDrop'', @value = ''' + CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END + ''', @level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + '''; ' +
    'ELSE EXEC sp_addextendedproperty @name = N''PreventDrop'', @value = ''' + CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END + ''', @level0type = N''Schema'', @level0name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @level1type = N''Table'', @level1name = ''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''';' AS NVARCHAR(MAX))
    FROM #Tables t WITH (NOLOCK)
    WHERE OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL  -- table physically exists
      AND t.[MemoryOptimized] = 0  -- memory-optimized tables reject extended properties; PreventDrop is tracked in SchemaSmith.ProductOwnership below
      -- Only where the marker is missing or disagrees. The generated SQL still probes and branches
      -- add-vs-update itself, so this narrows WHICH tables are visited, not what happens when one is.
      AND ISNULL((SELECT x.[Value] FROM #ExistingPreventDrop x WHERE x.[MajorId] = OBJECT_ID(t.[Schema] + '.' + t.[Name])), '')
          <> CASE WHEN t.[PreventDrop] = 1 THEN 'true' ELSE 'false' END
    FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  IF OBJECT_ID('tempdb..#ExistingPreventDrop') IS NOT NULL DROP TABLE #ExistingPreventDrop

  -- Memory-optimized tables reject extended properties, so their ProductName / PreventDrop ownership is
  -- recorded in SchemaSmith.ProductOwnership instead of stamped on the table (the read-side fold into
  -- #TableProperties near the top of this proc turns these rows back into the ownership records every
  -- downstream check reads). Insert missing owner rows and refresh the marker each run so it tracks the
  -- package, mirroring the two extended-property stamps above. IndexName = '' is the table-level marker.
  -- Ownership bookkeeping is a real mutation, so like the other engines it is skipped under WhatIf.
  RAISERROR('Record memory-optimized table ownership in ProductOwnership', 10, 100) WITH NOWAIT
  IF @WhatIf = 0
  BEGIN
    INSERT INTO SchemaSmith.ProductOwnership ([Schema], [TableName], [IndexName], [ProductName], [PreventDrop])
      SELECT t.[Schema], SchemaSmith.fn_StripBracketWrapping(t.[Name]), '', @ProductName, ISNULL(t.[PreventDrop], 0)
        FROM #Tables t WITH (NOLOCK)
        WHERE t.[MemoryOptimized] = 1
          AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM SchemaSmith.ProductOwnership po WITH (NOLOCK)
                            WHERE po.[Schema] = t.[Schema] COLLATE DATABASE_DEFAULT
                              AND po.[TableName] = SchemaSmith.fn_StripBracketWrapping(t.[Name]) COLLATE DATABASE_DEFAULT
                              AND po.[IndexName] = '')

    UPDATE po
      SET po.[ProductName] = @ProductName, po.[PreventDrop] = ISNULL(t.[PreventDrop], 0)
      FROM SchemaSmith.ProductOwnership po
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] COLLATE DATABASE_DEFAULT = po.[Schema]
                                  AND SchemaSmith.fn_StripBracketWrapping(t.[Name]) COLLATE DATABASE_DEFAULT = po.[TableName]
      WHERE t.[MemoryOptimized] = 1
        AND po.[IndexName] = ''
        AND OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NOT NULL
        AND (po.[ProductName] <> @ProductName OR po.[PreventDrop] <> ISNULL(t.[PreventDrop], 0))
  END

  -- Prune ownership rows for memory-optimized tables that no longer exist -- dropped by absence earlier in
  -- this proc, or removed out of band. Mirrors the catalog-existence prune the other engines run; a
  -- PreventDrop-retained table still physically exists, so its row survives. Scoped to this product and
  -- this run's schemas. Runs after the drops above, so a table removed this run is already gone here.
  RAISERROR('Prune ProductOwnership for memory-optimized tables no longer present', 10, 100) WITH NOWAIT
  IF @WhatIf = 0
    DELETE po
      FROM SchemaSmith.ProductOwnership po
      JOIN #SchemaList sl WITH (NOLOCK) ON sl.[Schema] = po.[Schema] COLLATE DATABASE_DEFAULT
      WHERE po.[ProductName] = @ProductName COLLATE DATABASE_DEFAULT
        AND OBJECT_ID(po.[Schema] + '.[' + po.[TableName] + ']') IS NULL

  RAISERROR('Add Missing Physical Columns', 10, 100) WITH NOWAIT
  -- Need to do this a second time for the edge case of replacing a computed column with a physical column
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding ' + CAST(ColumnCount AS NVARCHAR(100)) + ' new columns to ' + T.[Schema] + '.' + T.[Name] +
                                  CASE WHEN RTRIM(ISNULL(VariantList, '')) <> '' THEN ' (variant: ' + VariantList + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' ADD ' + ColumnScripts + ';' AS NVARCHAR(MAX))
                           FROM (SELECT T.[Schema], T.[Name],
                                        ColumnScripts = STUFF((SELECT ', ' + CAST([ColumnScript] AS NVARCHAR(MAX)) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '' ORDER BY c.[ColumnName] FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, ''),
                                        ColumnCount = (SELECT COUNT(*) FROM #Columns C WITH (NOLOCK) WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = ''),
                                        VariantList = STUFF((SELECT ', ' + CAST(REPLACE(RTRIM(c.[VariantName]), '''', '''''') AS NVARCHAR(MAX))
                                                               FROM #Columns C WITH (NOLOCK)
                                                               WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = ''
                                                                 AND RTRIM(ISNULL(c.[VariantName], '')) <> '' FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
                                   FROM #Tables T WITH (NOLOCK)
                                   WHERE NewTable = 0
                                     AND EXISTS (SELECT * FROM #Columns c WHERE C.[Schema] = T.[Schema] AND C.[TableName] = T.[Name] AND c.NewColumn = 1 AND RTRIM(ISNULL([ComputedExpression], '')) = '')) T
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Drop Modified Defaults', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping default ' + dc.[Schema] + '.' + dc.[TableName] + '.' + dc.[DefaultName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + dc.[Schema] + '.[' + dc.[DefaultName] + ']'') IS NOT NULL ALTER TABLE ' + dc.[Schema] + '.' + dc.[TableName] + ' DROP CONSTRAINT [' + dc.[DefaultName] + '];' AS NVARCHAR(MAX))
                           FROM #DefaultChanges dc WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Collect Existing Foreign Keys', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ExistingFKs') IS NOT NULL DROP TABLE #ExistingFKs
  SELECT t.[Schema], [TableName] = t.[Name],
         FKName = fk.[Name],
         FKScript = '(' + STUFF((SELECT ',' + '[' + COL_NAME(fc.[parent_object_id], fc.parent_column_id) + ']'
                             FROM sys.foreign_key_columns fc
                             WHERE fk.[object_id] = fc.[constraint_object_id]
                             ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')' +
                    ' REFERENCES [' + OBJECT_SCHEMA_NAME(referenced_object_id) + '].[' + OBJECT_NAME(referenced_object_id) + '] ' +
                    '(' + STUFF((SELECT ',' + '[' + COL_NAME(fc.[referenced_object_id], fc.referenced_column_id) + ']'
                             FROM sys.foreign_key_columns fc
                             WHERE fk.[object_id] = fc.[constraint_object_id]
                             ORDER BY fc.constraint_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')' +
                    ' ON DELETE ' + REPLACE(fk.delete_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT +
                    ' ON UPDATE ' + REPLACE(fk.update_referential_action_desc, '_', ' ') COLLATE DATABASE_DEFAULT
    INTO #ExistingFKs
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.foreign_keys fk ON fk.parent_object_id = OBJECT_ID(t.[Schema] + '.' + t.[Name]) 
    WHERE t.NewTable = 0

  RAISERROR('Detect Foreign Key Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#FKChanges') IS NOT NULL DROP TABLE #FKChanges
  SELECT ek.[Schema], ek.[TableName], ek.[FKName]
    INTO #FKChanges
    FROM #ExistingFKs ek WITH (NOLOCK)
    JOIN #ForeignKeys fk WITH (NOLOCK) ON ek.[TableName] = fk.[TableName]
                                      AND ek.[Schema] = fk.[Schema]
                                      AND ek.[FKName] = SchemaSmith.fn_StripBracketWrapping(fk.[KeyName])
    WHERE ek.FKScript <> '(' + [Columns] + ') REFERENCES ' + [RelatedTableSchema] + '.' + [RelatedTable] + ' (' + [RelatedColumns] + ')' +
                         ' ON DELETE ' + [DeleteAction] +
                         ' ON UPDATE ' + [UpdateAction]
  
  RAISERROR('Drop Modified Foreign Keys', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping Foreign Key ' + fc.[Schema] + '.' + fc.[TableName] + '.' + fc.[FKName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + fc.[Schema] + '.[' + fc.[FKName] + ']'') IS NOT NULL ALTER TABLE ' + fc.[Schema] + '.' + fc.[TableName] + ' DROP CONSTRAINT [' + fc.[FKName] + '];' AS NVARCHAR(MAX))
                           FROM #FKChanges fc WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Collect Existing Statistics Definitions', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ExistingStats') IS NOT NULL DROP TABLE #ExistingStats
  SELECT t.[Schema], [TableName] = t.[Name], [StatsName] = si.[Name],
         StatisticScript = 'CREATE STATISTICS ' +
                           '[' + si.[Name] + '] ON ' + t.[Schema] + '.' + t.[Name] + ' (' +
                           STUFF((SELECT ',' + '[' + COL_NAME(ic.[object_id], ic.column_id) + ']'
                              FROM sys.stats_columns ic
                              WHERE si.[object_id] = ic.[object_id] AND si.stats_id = ic.stats_id
                              ORDER BY ic.stats_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 1, '') + ')' +
                           CASE WHEN si.has_filter = 1 THEN ' WHERE ' + SchemaSmith.fn_StripParenWrapping(si.filter_definition) ELSE '' END 
    INTO #ExistingStats 
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.stats si ON si.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
                                   AND auto_created = 0
                                   AND user_created = 1
                                   -- is_temporary (2012 col) omitted: temporary stats exist only on readable secondaries; SchemaSmith targets a writable primary where it is always 0
                                   AND si.[Name] NOT LIKE 'stat[_]%'
                                   AND si.[Name] NOT LIKE 'hind[_]%'
    WHERE t.NewTable = 0
  
  RAISERROR('Detect Statistics Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#StatsChanges') IS NOT NULL DROP TABLE #StatsChanges
  SELECT s.[Schema], s.[TableName], s.[StatisticName]
    INTO #StatsChanges
    FROM #Statistics s WITH (NOLOCK)
    JOIN #ExistingStats es WITH (NOLOCK) ON s.[Schema] = es.[Schema]
                                        AND s.[TableName] = es.[TableName]
                                        AND SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]) = es.[StatsName]
    WHERE es.StatisticScript <> 'CREATE STATISTICS ' + s.[StatisticName] + ' ON ' + s.[Schema] + '.' + s.[TableName] + ' (' + s.[Columns] + ')' +
                                CASE WHEN RTRIM(ISNULL(s.[FilterExpression], '')) <> '' THEN ' WHERE ' + SchemaSmith.fn_ExpressionMapEffective(s.[Schema], s.[TableName], 'STATISTIC', s.[StatisticName], 'filter', s.[FilterExpression], SchemaSmith.fn_StripParenWrapping((SELECT st2.filter_definition FROM sys.stats st2 WHERE st2.[object_id] = OBJECT_ID(s.[Schema] + '.' + s.[TableName]) AND st2.[name] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName])))) ELSE '' END
  
  RAISERROR('Drop Modified Statistics', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping statistics ' + sc.[Schema] + '.' + sc.[TableName] + '.' + sc.[StatisticName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP STATISTICS ' + sc.[Schema] + '.' + sc.[TableName] + '.' + sc.[StatisticName] + ';' AS NVARCHAR(MAX))
                           FROM #StatsChanges sc WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- No-drop protection tier (#270): when protected mode is active the caller forces
  -- @DropStatisticsRemovedFromProduct to 0 so the drop block below never runs. Record the user-created
  -- statistics that WOULD have been dropped by absence (same by-absence set as the drop pass -- absent
  -- from the product, not already handled by the modified or column-change stats passes, per-table
  -- cascade tightening not opting out) to the ChangeAudit seam as 'dropSuppressed' so the run can surface a
  -- manifest. Audit rows only -- no DDL -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture statistics suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Statistic ' + es.[Schema] + '.' + es.[TableName] + '.[' + es.[StatsName] + '] removed from product but PreventDrop is active -- skipping drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''statistic'', ''' + es.[Schema] + '.' + es.[TableName] + '.[' + es.[StatsName] + ']'', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM #ExistingStats es WITH (NOLOCK)
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = es.[Schema] AND t.[Name] = es.[TableName]
      WHERE t.NewTable = 0
        AND ISNULL(t.[DropStatisticsRemovedFromProduct], 1) = 1
        AND NOT EXISTS (SELECT * FROM #Statistics s WITH (NOLOCK)
                          WHERE es.[Schema] = s.[Schema]
                            AND es.[TableName] = s.[TableName]
                            AND es.[StatsName] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]))
        AND NOT EXISTS (SELECT * FROM #StatisticsToDropForChanges sd WITH (NOLOCK)
                          WHERE es.[Schema] = sd.[Schema]
                            AND es.[TableName] = sd.[TableName]
                            AND es.[StatsName] = sd.[StatName])
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  -- Drop user-created statistics removed from the product (by-absence), gated by the cascade flag
  -- and per-table tightening. Excludes stats already dropped by the modified pass (#StatsChanges,
  -- still in the product by name) and the column-change pass (#StatisticsToDropForChanges) to avoid
  -- a double DROP STATISTICS. Auto-created stats are already excluded from #ExistingStats.
  RAISERROR('Drop Statistics No Longer Part of The Product Definition', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping statistics ' + es.[Schema] + '.' + es.[TableName] + '.[' + es.[StatsName] + ']'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP STATISTICS ' + es.[Schema] + '.' + es.[TableName] + '.[' + es.[StatsName] + '];' AS NVARCHAR(MAX))
                           FROM #ExistingStats es WITH (NOLOCK)
                           JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = es.[Schema] AND t.[Name] = es.[TableName]
                           WHERE t.NewTable = 0
                             AND @DropStatisticsRemovedFromProduct = 1
                             AND ISNULL(t.[DropStatisticsRemovedFromProduct], 1) = 1
                             AND NOT EXISTS (SELECT * FROM #Statistics s WITH (NOLOCK)
                                               WHERE es.[Schema] = s.[Schema]
                                                 AND es.[TableName] = s.[TableName]
                                                 AND es.[StatsName] = SchemaSmith.fn_StripBracketWrapping(s.[StatisticName]))
                             AND NOT EXISTS (SELECT * FROM #StatisticsToDropForChanges sd WITH (NOLOCK)
                                               WHERE es.[Schema] = sd.[Schema]
                                                 AND es.[TableName] = sd.[TableName]
                                                 AND es.[StatsName] = sd.[StatName])
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  RAISERROR('Collect Existing Check Constraints', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ExistingCheckConstraints') IS NOT NULL DROP TABLE #ExistingCheckConstraints
  SELECT t.[Schema], [TableName] = t.[Name], [CheckName] = ck.[name], 
         [CheckColumn] = CASE WHEN ck.parent_column_id <> 0 THEN COL_NAME(ck.parent_object_id, ck.parent_column_id) ELSE NULL END,
         [CheckDefinition] = SchemaSmith.fn_NormalizeCheckExpression(ck.[definition]),
         [LiveDefinition] = ck.[definition]
    INTO #ExistingCheckConstraints
    FROM #Tables t WITH (NOLOCK)
    JOIN sys.check_constraints ck ON ck.[parent_object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
  
  RAISERROR('Detect Column Level Check Constraint Changes', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#CheckChanges') IS NOT NULL DROP TABLE #CheckChanges
  SELECT ec.[Schema], ec.[TableName], ec.[CheckName]
    INTO #CheckChanges
    FROM #ExistingCheckConstraints ec WITH (NOLOCK)
    JOIN #Columns c WITH (NOLOCK) ON ec.[Schema] = c.[Schema]
                                 AND ec.[TableName] = c.[TableName]
                                 AND ec.[CheckColumn] = SchemaSmith.fn_StripBracketWrapping(c.[ColumnName])
    WHERE ec.[CheckColumn] IS NOT NULL
      AND ISNULL(c.[CheckExpression], '') <> ''
      AND ec.[CheckDefinition] <> SchemaSmith.fn_NormalizeCheckExpression(ISNULL(c.[CheckExpression], ''))
      -- #242: the texts differ, but they always differ once the engine has rewritten the expression. Ask what
      -- was actually applied before calling it a change.
      AND SchemaSmith.fn_ExpressionMapUnchanged(ec.[Schema], ec.[TableName], 'CHECK', ec.[CheckName],
            'expression', c.[CheckExpression], ec.[LiveDefinition]) = 0
      AND NOT EXISTS (SELECT *
                        FROM #CheckConstraints cc WITH (NOLOCK)
                        WHERE ec.[Schema] = cc.[Schema]
                          AND ec.[TableName] = cc.[TableName]
                          AND ec.[CheckName] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))

  RAISERROR('Detect Table Level Check Constraint Changes', 10, 100) WITH NOWAIT
  INSERT #CheckChanges ([Schema], [TableName], [CheckName])
    SELECT ec.[Schema], ec.[TableName], ec.[CheckName]
      FROM #ExistingCheckConstraints ec WITH (NOLOCK)
      JOIN #CheckConstraints cc WITH (NOLOCK) ON ec.[Schema] = cc.[Schema]
                                             AND ec.[TableName] = cc.[TableName]
                                             AND ec.[CheckName] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName])
      WHERE ec.[CheckDefinition] <> SchemaSmith.fn_NormalizeCheckExpression(cc.[Expression])
        AND SchemaSmith.fn_ExpressionMapUnchanged(ec.[Schema], ec.[TableName], 'CHECK', ec.[CheckName],
              'expression', cc.[Expression], ec.[LiveDefinition]) = 0
  
  RAISERROR('Drop Modified Check Constraints', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping check constraint ' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[CheckName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + cc.[Schema] + '.[' + cc.[CheckName] + ']'') IS NOT NULL ALTER TABLE ' + cc.[Schema] + '.' + cc.[TableName] + ' DROP CONSTRAINT [' + cc.[CheckName] + '];' AS NVARCHAR(MAX))
                           FROM #CheckChanges cc WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- No-drop protection tier (#270): when protected mode is active the caller forces
  -- @DropCheckConstraintsRemovedFromProduct to 0 so the drop block below never runs. Record the
  -- table-level check constraints that WOULD have been dropped by absence (same by-absence set as the
  -- drop pass -- table-level only, absent from the product, per-table cascade tightening not opting out)
  -- to the ChangeAudit seam as 'dropSuppressed' so the run can surface a manifest. Audit rows only -- no DDL
  -- so this runs regardless of @WhatIf.
  IF @CaptureWouldDrop = 1
  BEGIN
    RAISERROR('Capture check constraints suppressed by PreventDrop (would drop by absence)', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST(
      'RAISERROR(''  Check constraint ' + ec.[Schema] + '.' + ec.[TableName] + '.' + ec.[CheckName] + ' removed from product but PreventDrop is active -- skipping drop (protected)'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''constraint'', ''' + ec.[Schema] + '.' + ec.[TableName] + '.' + ec.[CheckName] + ''', ''dropSuppressed'');' AS NVARCHAR(MAX))
      FROM #ExistingCheckConstraints ec WITH (NOLOCK)
      JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = ec.[Schema] AND t.[Name] = ec.[TableName]
      WHERE (ec.[CheckColumn] IS NULL
             OR EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                          WHERE c.[Schema] = ec.[Schema]
                            AND c.[TableName] = ec.[TableName]
                            AND SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]) = ec.[CheckColumn]
                            AND ISNULL(c.[CheckExpression], '') = ''))
        AND t.NewTable = 0
        AND ISNULL(t.[DropCheckConstraintsRemovedFromProduct], 1) = 1
        AND NOT EXISTS (SELECT * FROM #CheckConstraints cc WITH (NOLOCK)
                          WHERE ec.[Schema] = cc.[Schema]
                            AND ec.[TableName] = cc.[TableName]
                            AND ec.[CheckName] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))
      FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_SQL IS NOT NULL EXEC(@v_SQL)
  END

  -- Drop named check constraints removed from the product (by-absence). Covers table-level checks
  -- (CheckColumn IS NULL) AND single-column named checks SQL Server stored as column-associated
  -- (CheckColumn set) whose column still exists but no longer carries a CheckExpression -- a genuine
  -- removal, not a modification (a changed non-empty expression stays on the column modify pass above).
  -- Gated by the cascade flag + the per-table tightening (a table may set
  -- DropCheckConstraintsRemovedFromProduct:false to protect its own).
  RAISERROR('Drop Check Constraints No Longer Part of The Product Definition', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping check constraint ' + ec.[Schema] + '.' + ec.[TableName] + '.' + ec.[CheckName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'IF OBJECT_ID(''' + ec.[Schema] + '.[' + ec.[CheckName] + ']'') IS NOT NULL ALTER TABLE ' + ec.[Schema] + '.' + ec.[TableName] + ' DROP CONSTRAINT [' + ec.[CheckName] + '];' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''constraint'', ''' + ec.[Schema] + '.' + ec.[TableName] + '.' + ec.[CheckName] + ''', ''dropped'');' AS NVARCHAR(MAX))
                           FROM #ExistingCheckConstraints ec WITH (NOLOCK)
                           JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = ec.[Schema] AND t.[Name] = ec.[TableName]
                           WHERE (ec.[CheckColumn] IS NULL
                                  OR EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                                               WHERE c.[Schema] = ec.[Schema]
                                                 AND c.[TableName] = ec.[TableName]
                                                 AND SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]) = ec.[CheckColumn]
                                                 AND ISNULL(c.[CheckExpression], '') = ''))
                             AND t.NewTable = 0
                             AND @DropCheckConstraintsRemovedFromProduct = 1
                             AND ISNULL(t.[DropCheckConstraintsRemovedFromProduct], 1) = 1
                             AND NOT EXISTS (SELECT * FROM #CheckConstraints cc WITH (NOLOCK)
                                               WHERE ec.[Schema] = cc.[Schema]
                                                 AND ec.[TableName] = cc.[TableName]
                                                 AND ec.[CheckName] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'constraint'/'dropped' (check) audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'constraint', ec.[Schema] + '.' + ec.[TableName] + '.' + ec.[CheckName], 'wouldDrop'
        FROM #ExistingCheckConstraints ec WITH (NOLOCK)
        JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = ec.[Schema] AND t.[Name] = ec.[TableName]
        WHERE (ec.[CheckColumn] IS NULL
               OR EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK)
                            WHERE c.[Schema] = ec.[Schema]
                              AND c.[TableName] = ec.[TableName]
                              AND SchemaSmith.fn_StripBracketWrapping(c.[ColumnName]) = ec.[CheckColumn]
                              AND ISNULL(c.[CheckExpression], '') = ''))
          AND t.NewTable = 0
          AND @DropCheckConstraintsRemovedFromProduct = 1
          AND ISNULL(t.[DropCheckConstraintsRemovedFromProduct], 1) = 1
          AND NOT EXISTS (SELECT * FROM #CheckConstraints cc WITH (NOLOCK)
                            WHERE ec.[Schema] = cc.[Schema]
                              AND ec.[TableName] = cc.[TableName]
                              AND ec.[CheckName] = SchemaSmith.fn_StripBracketWrapping(cc.[ConstraintName]))

  RAISERROR('Alter Modified Columns', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Altering Column ' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + cc.[Schema] + '.' + cc.[TableName] + ' ALTER COLUMN ' + cc.[ColumnName] + ' ' +
                                  CASE WHEN RTRIM([SpecialColumnScript]) <> '' THEN [SpecialColumnScript] ELSE [ColumnScript] END + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''column'', ''' + cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName] + ''', ''modified'');' AS NVARCHAR(MAX))
                           FROM #ColumnChanges cc WITH (NOLOCK)
                           WHERE [MustDropAndRecreate] = 0
                             AND [MustSwapColumn] = 0
                             AND [DropOnly] = 0
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'column'/'modified' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'column', cc.[Schema] + '.' + cc.[TableName] + '.' + cc.[ColumnName], 'wouldModify'
        FROM #ColumnChanges cc WITH (NOLOCK)
        WHERE [MustDropAndRecreate] = 0
          AND [MustSwapColumn] = 0
          AND [DropOnly] = 0

  RAISERROR('Identify Existing Clustered Index Conflicts', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#MissingClusteredIndexTables') IS NOT NULL DROP TABLE #MissingClusteredIndexTables
  SELECT DISTINCT i.[Schema], i.[TableName]
    INTO #MissingClusteredIndexTables
    FROM #Indexes i WITH (NOLOCK)
    WHERE i.[Clustered] = 1
      AND NOT EXISTS (SELECT * 
                        FROM sys.indexes si
                        WHERE si.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) 
                          AND si.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
  
  RAISERROR('Drop Conflicting Clustered Index', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping ' + CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1 THEN 'constraint' ELSE 'index' END + ' ' + mct.[Schema] + '.' + mct.[TableName] + '.' + si.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  CASE WHEN si.is_primary_key = 1 OR si.is_unique_constraint = 1
                                       THEN 'IF OBJECT_ID(''' + mct.[Schema] + '.[' + si.[Name] + ']'') IS NOT NULL ALTER TABLE ' + mct.[Schema] + '.' + mct.[TableName] + ' DROP CONSTRAINT [' + si.[Name] + '];'
                                       ELSE 'IF INDEXPROPERTY(OBJECT_ID(''' + mct.[Schema] + '.' + mct.[TableName] + '''), ''' + si.[Name] + ''', ''IndexID'') IS NOT NULL DROP INDEX [' + si.[Name] + '] ON ' + mct.[Schema] + '.' + mct.[TableName] + ';'
                                       END AS NVARCHAR(MAX))
                           FROM #MissingClusteredIndexTables mct WITH (NOLOCK)
                           JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(mct.[Schema] + '.' + mct.[TableName])
                                                            AND si.[type] IN (1, 5)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Drop Modified or Removed FullText Indexes', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping fulltext index on ' + ei.[Schema] + '.' + ei.[TableName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'DROP FULLTEXT INDEX ON ' + ei.[Schema] + '.' + ei.[TableName] + ';' AS NVARCHAR(MAX))
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
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  
  RAISERROR('Enable/Disable CDC', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
  BEGIN
    SET @v_SQL = ''
    SELECT @v_SQL = @v_SQL +
      CASE WHEN t.EnableCDC = 1 AND st.is_tracked_by_cdc = 0
           THEN 'RAISERROR(''  Enable CDC on ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                'EXEC sys.sp_cdc_enable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', @role_name = NULL' + ISNULL(', @filegroup_name = N''' + SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup) + '''', '') + ';' + CHAR(13) + CHAR(10)
           WHEN t.EnableCDC = 0 AND st.is_tracked_by_cdc = 1
           THEN 'RAISERROR(''  Disable CDC on ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                'EXEC sys.sp_cdc_disable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', @capture_instance = N''' + ct.capture_instance + ''';' + CHAR(13) + CHAR(10)
           ELSE '' END
      FROM #Tables t WITH (NOLOCK)
      JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      LEFT JOIN cdc.change_tables ct WITH (NOLOCK) ON ct.source_object_id = st.[object_id]
      WHERE (t.EnableCDC = 1 AND st.is_tracked_by_cdc = 0)
         OR (t.EnableCDC = 0 AND st.is_tracked_by_cdc = 1)
    IF @v_SQL <> ''
    BEGIN
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    END
  END

  RAISERROR('Rotate CDC Capture Instances For Tables With Column Changes', 10, 100) WITH NOWAIT
  -- The pre-existing instance keeps the history it already captured and is deliberately NOT dropped:
  -- only the operator knows when downstream readers have drained it. It does occupy one of the two
  -- slots, so the guard above will refuse the NEXT column change until it is dropped.
  IF EXISTS (SELECT 1 FROM #CdcRotate)
  BEGIN
    SET @v_SQL = ''
    SELECT @v_SQL = @v_SQL +
      'RAISERROR(''  CDC ROTATED on ' + r.[Schema] + '.' + r.[TableName] + ': new capture instance ' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ' now captures ' + CASE WHEN r.Reason = 'filegroup' THEN 'this table on filegroup ' + r.NewFilegroup ELSE 'the new column set' END + '. The previous instance ' + r.OldCaptureInstance + ' STILL HOLDS ITS HISTORY and was NOT dropped -- drain it, then drop it with EXEC sys.sp_cdc_disable_table @capture_instance = N''''' + r.OldCaptureInstance + '''''. Until then the next column change on this table WILL FAIL: SQL Server allows only two capture instances.'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'EXEC sys.sp_cdc_enable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(r.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(r.[TableName]) + ''', @capture_instance = N''' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ''', @role_name = NULL' + ISNULL(', @filegroup_name = N''' + r.NewFilegroup + '''', '') + ';' + CHAR(13) + CHAR(10)
      FROM #CdcRotate r WITH (NOLOCK)
      CROSS APPLY (SELECT SchemaSmith.fn_StripBracketWrapping(r.[Schema]) + '_' + SchemaSmith.fn_StripBracketWrapping(r.[TableName]) AS BaseName) b
    IF @v_SQL <> ''
    BEGIN
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    END
  END

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH