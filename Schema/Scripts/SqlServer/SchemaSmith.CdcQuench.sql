-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table-level CDC: enable, disable, and the rotation ModifiedTableQuench decided on (#CdcRotate, owned by
-- TableQuench). A capture instance records the columns that exist when it is created, so this runs after every
-- pass that adds one -- computed columns (MissingIndexesAndConstraintsQuench) and FILESTREAM columns
-- (FileStreamColumnQuench). Run earlier, a new table's instance left out every computed column for good (#420).
IF OBJECT_ID('SchemaSmith.CdcQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.CdcQuench
GO
CREATE PROCEDURE SchemaSmith.CdcQuench
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  RAISERROR('Enable/Disable CDC', 10, 100) WITH NOWAIT
  IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
  BEGIN
    SET @v_SQL = ''
    SELECT @v_SQL = @v_SQL +
      CASE WHEN t.EnableCDC = 1 AND st.is_tracked_by_cdc = 0
           THEN 'RAISERROR(''  Enable CDC on ' + t.[Schema] + '.' + t.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                'EXEC sys.sp_cdc_enable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(t.[Name]) + ''', @role_name = NULL' + ISNULL(', @filegroup_name = N''' + SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup) + '''', '') +
                -- Always explicit: SQL Server's own default is ON once the table has a primary key, and a new table's key
                -- now exists by the time this runs. Unset stays OFF, which is what every earlier version produced (#426).
                ', @supports_net_changes = ' + CASE WHEN t.CdcSupportsNetChanges = 1 THEN '1' ELSE '0' END + ';' + CHAR(13) + CHAR(10)
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
      'RAISERROR(''  CDC ROTATED on ' + r.[Schema] + '.' + r.[TableName] + ': new capture instance ' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ' now captures ' + CASE WHEN r.Reason = 'filegroup' THEN 'this table on filegroup ' + r.NewFilegroup WHEN r.Reason = 'netchanges' THEN 'with net changes ' + CASE WHEN r.NewNetChanges = 1 THEN 'ON' ELSE 'OFF' END ELSE 'the new column set' END + '. The previous instance ' + r.OldCaptureInstance + ' STILL HOLDS ITS HISTORY and was NOT dropped -- drain it, then drop it with EXEC sys.sp_cdc_disable_table @capture_instance = N''''' + r.OldCaptureInstance + '''''. Until then the next column change on this table WILL FAIL: SQL Server allows only two capture instances.'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'EXEC sys.sp_cdc_enable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(r.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(r.[TableName]) + ''', @capture_instance = N''' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ''', @role_name = NULL' + ISNULL(', @filegroup_name = N''' + r.NewFilegroup + '''', '') +
      ISNULL(', @supports_net_changes = ' + CAST(r.NewNetChanges AS VARCHAR(1)), '') + ';' + CHAR(13) + CHAR(10)
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
