-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedFeatures', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedFeatures
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedFeatures
  @DeferDatabaseToggles BIT = 0
AS
BEGIN
  -- Single choke point for the below-floor emit-guards: sanitize the parsed working set (#Tables / #Columns /
  -- #Indexes) so a model feature the DETECTED target version cannot support is either refused ('fail') or
  -- neutralized in place ('warn', default) BEFORE any emit or modified-detection pass runs. Because the working
  -- set is made floor-appropriate here, every downstream emit site stays gate-free -- it simply operates on a
  -- model with no unsupported feature declared. 'warn' records one 'downgraded' ChangeAudit row per object
  -- (surfaced in the run summary's Unsupported Feature Downgrades section) then clears the declaration; 'fail'
  -- aborts naming the required version (RAISERROR, not THROW which is 2012+, so this CREATEs on the 2008 floor;
  -- TableQuench's CATCH re-raises the message). At/above each feature's intro version this is a no-op. Called
  -- from TableQuench right after the parse; the --IndexOnly path calls DegradeUnsupportedColumnStore directly
  -- (it has only #Indexes). Operates on the caller's temp tables (deferred name resolution at CREATE time).
  DECLARE @v_policy VARCHAR(4) = SchemaSmith.UnsupportedFeaturePolicy()
  DECLARE @v_major INT = SchemaSmith.fn_ServerMajorVersion()
  DECLARE @v_list NVARCHAR(MAX)
  DECLARE @v_msg NVARCHAR(2048)

  -- Temporal (SYSTEM_VERSIONING / PERIOD FOR SYSTEM_TIME) -- SQL Server 2016 (major 13).
  IF @v_major < 13 AND EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE IsTemporal = 1)
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE T.IsTemporal = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'System-versioned temporal (SYSTEM_VERSIONING) requires SQL Server 2016 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); table(s): ' + LEFT(@v_list, 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'temporal (SQL Server 2016)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE T.IsTemporal = 1
      RAISERROR('  Temporal tracking skipped (requires SQL Server 2016 - downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET IsTemporal = 0 WHERE IsTemporal = 1
    END
  END

  -- Ledger and IsTemporal describe overlapping engine state and cannot both be declared. An updatable
  -- ledger table is created WITH (SYSTEM_VERSIONING = ON, LEDGER = ON), while IsTemporal turns system
  -- versioning on separately -- and sys.tables reports a ledger table as NON_TEMPORAL_TABLE, so the two
  -- together leave the package permanently disagreeing with the target. Refused rather than degraded:
  -- neither reading is more correct than the other, so guessing would be worse than stopping.
  IF EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE ISNULL([Ledger], 'Off') <> 'Off' AND IsTemporal = 1)
  BEGIN
    SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK)
                          WHERE ISNULL(T.[Ledger], 'Off') <> 'Off' AND T.IsTemporal = 1
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    SET @v_msg = 'Ledger and IsTemporal cannot both be declared on the same table -- a ledger table manages its own history, and SQL Server reports it as non-temporal; table(s): ' + LEFT(@v_list, 1700) + '.'
    RAISERROR(@v_msg, 16, 1)
  END

  -- Graph tables (AS NODE / AS EDGE) -- SQL Server 2017 (major 14). Below it the clause is not syntax at
  -- all, so an ungated emit fails with a bare parser error naming nothing useful. Clearing GraphType
  -- deploys the table as an ordinary one, which keeps its columns and data shape intact and loses only
  -- the graph semantics -- a Reduced degrade rather than a skipped table.
  IF @v_major < 14 AND EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE [GraphType] IN ('Node', 'Edge'))
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE T.[GraphType] IN ('Node', 'Edge')
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Graph tables (AS NODE / AS EDGE) require SQL Server 2017 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); table(s): ' + LEFT(@v_list, 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'graph table (SQL Server 2017)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE T.[GraphType] IN ('Node', 'Edge')
      RAISERROR('  Graph node/edge skipped: requires SQL Server 2017 - the table deploys as an ordinary table (downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET [GraphType] = 'None' WHERE [GraphType] IN ('Node', 'Edge')
    END
  END

  -- XML compression -- SQL Server 2022 (major 16). Below it the clause is a parser error, so the emit
  -- sites gate on the same version and this block only reports what they suppressed. Clearing it
  -- deploys the table or index uncompressed: the schema is identical and only the storage saving is
  -- lost, which is the mildest Reduced degrade in this file -- nothing an application can observe.
  --
  -- Both #Tables and #Indexes carry the property, so both are reported; an index inherits nothing from
  -- its table here.
  IF @v_major < 16 AND (EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE ISNULL([XmlCompression], 0) = 1)
                     OR EXISTS (SELECT 1 FROM #Indexes WITH (NOLOCK) WHERE ISNULL([XmlCompression], 0) = 1))
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE ISNULL(T.[XmlCompression], 0) = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'XML_COMPRESSION requires SQL Server 2022 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); object(s): ' + LEFT(ISNULL(@v_list, '(indexes only)'), 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'XML compression (SQL Server 2022)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE ISNULL(T.[XmlCompression], 0) = 1
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'XML compression (SQL Server 2022)', I.[Schema] + '.' + I.[TableName] + '.' + I.[IndexName], 'downgraded'
          FROM #Indexes I WITH (NOLOCK) WHERE ISNULL(I.[XmlCompression], 0) = 1
      RAISERROR('  XML compression skipped: requires SQL Server 2022 - the object deploys uncompressed (downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET [XmlCompression] = 0 WHERE ISNULL([XmlCompression], 0) = 1
      UPDATE #Indexes SET [XmlCompression] = 0 WHERE ISNULL([XmlCompression], 0) = 1
    END
  END

  -- Ledger tables -- SQL Server 2022 (major 16). Below it the clause is not syntax, so an ungated emit
  -- fails with a bare parser error. Clearing Ledger deploys an ordinary table, which keeps the columns
  -- and data shape and loses only the tamper-evidence -- a Reduced degrade.
  --
  -- Worth knowing which way this one degrades: a ledger table cannot later be converted or dropped, so
  -- accidentally CREATING one is much harder to undo than not creating one. Degrading down is the safe
  -- direction here in a way it is not for most features.
  IF @v_major < 16 AND EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE ISNULL([Ledger], 'Off') <> 'Off')
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE ISNULL(T.[Ledger], 'Off') <> 'Off'
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Ledger tables require SQL Server 2022 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); table(s): ' + LEFT(@v_list, 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'ledger table (SQL Server 2022)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE ISNULL(T.[Ledger], 'Off') <> 'Off'
      RAISERROR('  Ledger skipped: requires SQL Server 2022 - the table deploys as an ordinary table (downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET [Ledger] = 'Off' WHERE ISNULL([Ledger], 'Off') <> 'Off'
    END
  END



  -- Dynamic data masking (MASKED WITH) -- SQL Server 2016 (major 13).
  IF @v_major < 13 AND EXISTS (SELECT 1 FROM #Columns WITH (NOLOCK) WHERE RTRIM(ISNULL([DataMaskFunction], '')) <> '')
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] FROM #Columns c WITH (NOLOCK)
                             WHERE RTRIM(ISNULL(c.[DataMaskFunction], '')) <> ''
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Dynamic data masking (MASKED WITH) requires SQL Server 2016 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); column(s): ' + LEFT(@v_list, 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'data masking (SQL Server 2016)', c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName], 'downgraded'
          FROM #Columns c WITH (NOLOCK) WHERE RTRIM(ISNULL(c.[DataMaskFunction], '')) <> ''
      RAISERROR('  Dynamic data masking skipped (requires SQL Server 2016 - downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Columns SET [DataMaskFunction] = '' WHERE RTRIM(ISNULL([DataMaskFunction], '')) <> ''
    END
  END

  -- Always Encrypted (ENCRYPTED WITH) -- SQL Server 2016 (major 13).
  IF @v_major < 13 AND EXISTS (SELECT 1 FROM #Columns WITH (NOLOCK) WHERE RTRIM(ISNULL([EncryptionType], 'NONE')) <> 'NONE')
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName] FROM #Columns c WITH (NOLOCK)
                             WHERE RTRIM(ISNULL(c.[EncryptionType], 'NONE')) <> 'NONE'
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Always Encrypted (ENCRYPTED WITH) requires SQL Server 2016 (detected major ' +
                   CONVERT(NVARCHAR(10), @v_major) + '); column(s): ' + LEFT(@v_list, 1800) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'Always Encrypted (SQL Server 2016)', c.[Schema] + '.' + c.[TableName] + '.' + c.[ColumnName], 'downgraded'
          FROM #Columns c WITH (NOLOCK) WHERE RTRIM(ISNULL(c.[EncryptionType], 'NONE')) <> 'NONE'
      RAISERROR('  Always Encrypted skipped (requires SQL Server 2016 - downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Columns SET [EncryptionType] = 'NONE' WHERE RTRIM(ISNULL([EncryptionType], 'NONE')) <> 'NONE'
    END
  END

  -- Columnstore (nonclustered 2012 / clustered 2014) -- drops the unsupported index rows from #Indexes.
  -- Shared with the --IndexOnly path, which calls this proc directly (it has only #Indexes).
  EXEC SchemaSmith.DegradeUnsupportedColumnStore

  -- Change Data Capture and table Change Tracking -- gated by DATABASE-scoped toggles, which a template's Before
  -- script can turn on. When the template has Before scripts the caller defers these two to just after that slot
  -- (#432); otherwise they are judged here, so a 'fail' still refuses before anything is created.
  IF @DeferDatabaseToggles = 0 EXEC SchemaSmith.DegradeDatabaseToggles


  -- FILESTREAM columns -- the third database-scoped prerequisite, and the only one that degrades to a
  -- still-usable column rather than to a missing capability. Dropping FILESTREAM leaves a plain
  -- VARBINARY(MAX): the data still stores and reads, it just lives in-row instead of on the filegroup.
  -- That is a storage difference, not a correctness one, so the column is kept and the change reported.
  --
  -- Two prerequisites, neither of which SchemaSmith creates: FILESTREAM enabled on the SERVER
  -- (sp_configure + a Windows-level setting, which is not even reachable from T-SQL) and a FILESTREAM
  -- filegroup on the DATABASE. Creating the filegroup would mean choosing a filesystem path on the
  -- target, which belongs to whoever owns the database.
  --
  -- Clearing [FileStream] is what re-routes these columns: MissingTableAndColumnQuench withholds
  -- FileStream = 1 columns from the create/add pass, so zeroing the flag puts them back on the normal
  -- path. ColumnScript is rewritten to match, because it was assembled at parse time.
  IF EXISTS (SELECT 1 FROM #Columns WITH (NOLOCK) WHERE [FileStream] = 1)
     AND (CONVERT(INT, ISNULL(SERVERPROPERTY('FilestreamEffectiveLevel'), 0)) = 0
          OR NOT EXISTS (SELECT 1 FROM sys.filegroups WHERE [type] = 'FD'))
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + C.[Schema] + '.' + C.[TableName] + '.' + C.[ColumnName]
                             FROM #Columns C WITH (NOLOCK) WHERE C.[FileStream] = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'FILESTREAM requires it enabled on the server and a FILESTREAM filegroup on the database, ' +
                   'neither of which SchemaSmith creates for you; column(s): ' + LEFT(@v_list, 1700) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'FILESTREAM (server or filegroup not available)',
               C.[Schema] + '.' + C.[TableName] + '.' + C.[ColumnName], 'downgraded'
          FROM #Columns C WITH (NOLOCK) WHERE C.[FileStream] = 1
      RAISERROR('  FILESTREAM skipped: not enabled on this server, or the database has no FILESTREAM filegroup. The column(s) deploy as plain VARBINARY(MAX) - downgraded', 10, 100) WITH NOWAIT
      UPDATE #Columns
        SET [ColumnScript] = REPLACE([ColumnScript], ' FILESTREAM', ''), [FileStream] = 0
        WHERE [FileStream] = 1
    END
  END

END
