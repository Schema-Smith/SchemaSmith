-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table-level Change Tracking convergence.
--
-- Its own procedure because of WHERE it has to run, not merely for tidiness: enabling change tracking
-- requires a primary key on the table ("Change tracking requires a primary key on the table"), and for a
-- table SchemaSmith is creating in this same run the primary key does not exist until
-- SchemaSmith.MissingIndexesAndConstraintsQuench has run. Sitting alongside the EnableCDC pass in
-- ModifiedTableQuench -- the obvious home, since both are table-level toggles -- fails every new table.
-- CDC has no such prerequisite, so mirroring its placement is exactly the trap here.
--
-- TableQuench therefore calls this AFTER MissingIndexesAndConstraintsQuench.
IF OBJECT_ID('SchemaSmith.ChangeTrackingQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ChangeTrackingQuench
GO
CREATE PROCEDURE SchemaSmith.ChangeTrackingQuench
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  RAISERROR('Enable/Disable Change Tracking', 10, 100) WITH NOWAIT
  -- Table-level Change Tracking. NOT the full-text index option spelled WITH CHANGE_TRACKING = AUTO,
  -- which is unrelated and handled in the index scripts.
  --
  -- The database toggle is a prerequisite SchemaSmith does not own: ALTER DATABASE ... SET
  -- CHANGE_TRACKING = ON sets retention and auto-cleanup for every table in the database. When it is
  -- off, this pass does nothing and SchemaSmith.DegradeUnsupportedFeatures reports the tables that
  -- asked -- rather than deploying green and leaving them untracked, which is what EnableCDC did.
  IF EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
  BEGIN
    SET @v_SQL = ''
    SELECT @v_SQL = @v_SQL +
      CASE
        WHEN t.EnableChangeTracking = 1 AND ctt.[object_id] IS NULL
        THEN 'RAISERROR(''  Enable Change Tracking on ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
             'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' ENABLE CHANGE_TRACKING' +
             CASE WHEN t.TrackColumnsUpdated = 1 THEN ' WITH (TRACK_COLUMNS_UPDATED = ON)' ELSE '' END + ';' + CHAR(13) + CHAR(10)

        WHEN t.EnableChangeTracking = 0 AND ctt.[object_id] IS NOT NULL
        THEN 'RAISERROR(''  Disable Change Tracking on ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
             'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' DISABLE CHANGE_TRACKING;' + CHAR(13) + CHAR(10)

        -- TRACK_COLUMNS_UPDATED cannot be altered in place. Codebase-certified against SQL Server:
        -- re-running ENABLE on a tracked table fails 4996 ("Change tracking is already enabled"), and
        -- neither ALTER TABLE ... CHANGE_TRACKING = ON (...) nor ALTER TABLE ... SET (...) parses.
        -- Disable-then-enable is the only path, and it DISCARDS the tracking baseline: consumers must
        -- re-synchronize from scratch. That consequence is announced by name rather than applied
        -- quietly -- the same posture as the CDC rotation message below.
        WHEN t.EnableChangeTracking = 1 AND ctt.[object_id] IS NOT NULL AND ctt.is_track_columns_updated_on <> t.TrackColumnsUpdated
        THEN 'RAISERROR(''  CHANGE TRACKING RESET on ' + t.[Schema] + '.' + t.[Name] + ': TRACK_COLUMNS_UPDATED is now ' +
             CASE WHEN t.TrackColumnsUpdated = 1 THEN 'ON' ELSE 'OFF' END +
             '. SQL Server cannot alter that option in place, so change tracking was disabled and re-enabled and THE TRACKING BASELINE WAS DISCARDED -- every consumer of this table must re-synchronize in full (CHANGE_TRACKING_MIN_VALID_VERSION reports the new baseline).'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
             'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' DISABLE CHANGE_TRACKING;' + CHAR(13) + CHAR(10) +
             'ALTER TABLE ' + t.[Schema] + '.' + t.[Name] + ' ENABLE CHANGE_TRACKING' +
             CASE WHEN t.TrackColumnsUpdated = 1 THEN ' WITH (TRACK_COLUMNS_UPDATED = ON)' ELSE '' END + ';' + CHAR(13) + CHAR(10)
        ELSE '' END
      FROM #Tables t WITH (NOLOCK)
      JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      LEFT JOIN sys.change_tracking_tables ctt ON ctt.[object_id] = st.[object_id]
      WHERE (t.EnableChangeTracking = 1 AND ctt.[object_id] IS NULL)
         OR (t.EnableChangeTracking = 0 AND ctt.[object_id] IS NOT NULL)
         OR (t.EnableChangeTracking = 1 AND ctt.[object_id] IS NOT NULL AND ctt.is_track_columns_updated_on <> t.TrackColumnsUpdated)
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
