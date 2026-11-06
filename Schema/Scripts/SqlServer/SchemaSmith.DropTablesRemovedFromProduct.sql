-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DropTablesRemovedFromProduct', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DropTablesRemovedFromProduct
GO
CREATE PROCEDURE SchemaSmith.DropTablesRemovedFromProduct
  @ProductName NVARCHAR(50),
  @WhatIf BIT = 0
AS
BEGIN
  -- Drops the tables this product owns that the package no longer declares, from the caller's #Tables and
  -- #TableProperties. ModifiedTableQuench calls it after every refusal of its step, so a refused deploy drops nothing.
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX) = ''


  -- The caller (ModifiedTableQuench) identifies the tables, once, for this and for the destructive-change gate.

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
                          WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.TableName) + '')
                            AND p.index_id < 2) > 1)
    BEGIN
      SELECT @v_SQL = STUFF((SELECT ', ' + t.[Schema] + '.' + QUOTENAME(t.TableName) + ''
                               FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                               WHERE (SELECT COUNT(*) FROM sys.partitions p
                                        WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.TableName) + '')
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
                                    'ALTER TABLE ' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ' SET (SYSTEM_VERSIONING = OFF);' AS NVARCHAR(MAX))
                             FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                             WHERE OBJECTPROPERTY(OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ''), 'TableTemporalType') = 2
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    RAISERROR('Drop inbound foreign keys referencing tables removed from the product', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping inbound foreign key ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + '.' + QUOTENAME(fk.[name]) + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                    'IF OBJECT_ID(''' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(fk.[name]) + ''') IS NOT NULL ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + ' DROP CONSTRAINT ' + QUOTENAME(fk.[name]) + ';' + CHAR(13) + CHAR(10) +
                                    'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + '.' + QUOTENAME(fk.[name]) + ''', ''dropped'');' AS NVARCHAR(MAX))
                             FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                             JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.[TableName]) + '')
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    -- #363: WhatIf twin of the embedded 'foreignKey'/'dropped' audit above (which rides the DROP
    -- CONSTRAINT DDL, executed only on a real run). Same source, same ObjectName shape.
    IF @WhatIf = 1
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'foreignKey', '' + QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id)) + '.' + QUOTENAME(fk.[name]) + '', 'wouldDrop'
          FROM #TablesRemovedFromProduct t WITH (NOLOCK)
          JOIN sys.foreign_keys fk ON fk.referenced_object_id = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.[TableName]) + '')

    -- #549: a hook that moves the table (ALTER SCHEMA TRANSFER, sp_rename) keeps its ProductName property, so the next
    -- deploy found the moved copy, routed it through the hook again and recycled the recycled table under a new name.
    -- Each table's object_id is taken before the hook runs; one still there after it, under another name, was moved,
    -- and is no longer the product's.
    IF OBJECT_ID('tempdb..#TablesHandedToHook') IS NOT NULL DROP TABLE #TablesHandedToHook
    SELECT t.[Schema], t.[TableName], [ObjectId] = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.[TableName]))
      INTO #TablesHandedToHook
      FROM #TablesRemovedFromProduct t WITH (NOLOCK)
      WHERE OBJECT_ID('SchemaSmith.CustomTableDrop') IS NOT NULL

    RAISERROR('Drop tables removed from the product', 10, 100) WITH NOWAIT
    SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  ' + CASE WHEN OBJECT_ID('SchemaSmith.CustomTableDrop') IS NOT NULL THEN 'Handing table ' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ' to SchemaSmith.CustomTableDrop (no longer in product)' ELSE 'Dropping table ' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                    CASE WHEN OBJECT_ID('SchemaSmith.CustomTableDrop') IS NOT NULL
                                         THEN 'EXEC SchemaSmith.CustomTableDrop ''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', ''' + t.[TableName] + ''';'
                                         ELSE 'IF OBJECT_ID(''' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ''') IS NOT NULL DROP TABLE ' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ';'
                                         END + CHAR(13) + CHAR(10) +
                                    'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''table'', ''' + t.[Schema] + '.' + QUOTENAME(t.[TableName]) + ''', ''dropped'');' AS NVARCHAR(MAX))
                             FROM #TablesRemovedFromProduct t WITH (NOLOCK)
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

    -- #363: WhatIf twin of the embedded 'table'/'dropped' audit above.
    IF @WhatIf = 1
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'table', t.[Schema] + '.' + QUOTENAME(t.[TableName]) + '', 'wouldDrop'
          FROM #TablesRemovedFromProduct t WITH (NOLOCK)

    IF @WhatIf = 0
    BEGIN
      SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('EXEC sys.sp_dropextendedproperty @name = N''ProductName'', @level0type = N''SCHEMA'', @level0name = N'
                                      + QUOTENAME(OBJECT_SCHEMA_NAME(h.[ObjectId]), '''') + ', @level1type = N''TABLE'', @level1name = N'
                                      + QUOTENAME(OBJECT_NAME(h.[ObjectId]), '''') + ';' AS NVARCHAR(MAX))
                               FROM #TablesHandedToHook h WITH (NOLOCK)
                               WHERE h.[ObjectId] IS NOT NULL AND OBJECT_NAME(h.[ObjectId]) IS NOT NULL
                                 AND ISNULL(OBJECT_ID(h.[Schema] + '.' + QUOTENAME(h.[TableName])), 0) <> h.[ObjectId]
                                 AND EXISTS (SELECT 1 FROM sys.extended_properties ep
                                              WHERE ep.class = 1 AND ep.major_id = h.[ObjectId] AND ep.minor_id = 0 AND ep.[name] = 'ProductName')
                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      IF @v_SQL IS NOT NULL EXEC(@v_SQL)
    END

    -- Drop the now-orphaned history tables of removed temporal tables (versioning was turned off
    -- above). Skipped when a CustomTableDrop hook is installed -- that hook owns table removal,
    -- including any history handling for the recycle path.
    IF OBJECT_ID('SchemaSmith.CustomTableDrop') IS NULL
    BEGIN
      RAISERROR('Drop history tables of temporal tables removed from the product', 10, 100) WITH NOWAIT
      SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping history table ' + h.HistSchema + '.' + h.HistName + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                      'IF OBJECT_ID(''' + QUOTENAME(h.HistSchema) + '.' + QUOTENAME(h.HistName) + ''') IS NOT NULL DROP TABLE ' + QUOTENAME(h.HistSchema) + '.' + QUOTENAME(h.HistName) + ';' AS NVARCHAR(MAX))
                               FROM #RemovedTemporalHistory h WITH (NOLOCK)
                               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
    END
  END
END
