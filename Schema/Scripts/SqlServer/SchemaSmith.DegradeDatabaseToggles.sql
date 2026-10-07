-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeDatabaseToggles', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeDatabaseToggles
GO
CREATE PROCEDURE SchemaSmith.DegradeDatabaseToggles
AS
BEGIN
  -- The database-scoped half of SchemaSmith.DegradeUnsupportedFeatures: CDC and table Change Tracking. Split out
  -- because a template's Before script can enable either one, so they must be judged after that slot when it has
  -- any scripts (#432). Operates on the caller's #Tables.
  DECLARE @v_policy VARCHAR(4) = SchemaSmith.UnsupportedFeaturePolicy()
  DECLARE @v_list NVARCHAR(MAX)
  DECLARE @v_msg NVARCHAR(2048)

  -- Change Data Capture -- gated by a DATABASE-scoped toggle rather than a server version, which is why
  -- it sits apart from the version blocks above.
  --
  -- THIS BLOCK EXISTS BECAUSE ITS ABSENCE WAS A SILENT NO-OP. ModifiedTableQuench wraps its whole
  -- enable/disable pass in IF EXISTS (... is_cdc_enabled = 1) with no ELSE, so a package declaring
  -- "EnableCDC": true against a database where CDC is off deployed green, left the table untracked, and
  -- said nothing anywhere. The user found out when someone asked where the change history went.
  --
  -- SchemaSmith deliberately does NOT run sp_cdc_enable_db to fix it up. That call changes retention,
  -- cleanup jobs and storage for the entire database; enabling it because one table asked would trade a
  -- silent no-op for a silent side effect on every other table in it. Refusing loudly is the safe half
  -- of that trade, and it is only safe because this block makes the refusal visible.
  --
  -- Clearing EnableCDC afterwards is what makes the later pass's behaviour deliberate rather than
  -- incidental: it then finds nothing to do because the declaration was withdrawn here and recorded,
  -- not because a guard happened to skip it.
  IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
     AND EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE EnableCDC = 1)
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE T.EnableCDC = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Change Data Capture requires CDC enabled on the database (EXEC sys.sp_cdc_enable_db), ' +
                   'which SchemaSmith does not do for you because it is database-wide; table(s): ' +
                   LEFT(@v_list, 1700) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'CDC (database not enabled)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE T.EnableCDC = 1
      RAISERROR('  CDC skipped: not enabled on this database (EXEC sys.sp_cdc_enable_db to allow it - downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET EnableCDC = 0 WHERE EnableCDC = 1
    END
  END


  -- Table-level Change Tracking -- the second feature gated by a DATABASE-scoped toggle, and written
  -- this way from the start precisely because the CDC block above had to be retrofitted after shipping
  -- the silent version of it. Not the full-text CHANGE_TRACKING option, which is unrelated.
  --
  -- Same refusal for the same reason: ALTER DATABASE ... SET CHANGE_TRACKING = ON sets retention and
  -- auto-cleanup for the whole database, so SchemaSmith names the tables that asked instead of
  -- reconfiguring the database around one declaration.
  IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
     AND EXISTS (SELECT 1 FROM #Tables WITH (NOLOCK) WHERE EnableChangeTracking = 1)
  BEGIN
    IF @v_policy = 'fail'
    BEGIN
      SET @v_list = STUFF((SELECT ', ' + T.[Schema] + '.' + T.[Name] FROM #Tables T WITH (NOLOCK) WHERE T.EnableChangeTracking = 1
                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      SET @v_msg = 'Change Tracking requires it enabled on the database (ALTER DATABASE ... SET CHANGE_TRACKING = ON), ' +
                   'which SchemaSmith does not do for you because it is database-wide; table(s): ' +
                   LEFT(@v_list, 1700) + '.'
      RAISERROR(@v_msg, 16, 1)
    END
    ELSE
    BEGIN
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT @@SPID, 'Change Tracking (database not enabled)', T.[Schema] + '.' + T.[Name], 'downgraded'
          FROM #Tables T WITH (NOLOCK) WHERE T.EnableChangeTracking = 1
      RAISERROR('  Change Tracking skipped: not enabled on this database (ALTER DATABASE ... SET CHANGE_TRACKING = ON to allow it - downgraded)', 10, 100) WITH NOWAIT
      UPDATE #Tables SET EnableChangeTracking = NULL WHERE EnableChangeTracking = 1
    END
  END
END
