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
                -- Unset reproduces what earlier versions produced (#426). They enabled CDC before a new table's primary key
                -- existed, so SQL Server's default (ON once there is a key) gave OFF for a new table and ON for a keyed
                -- existing one. The key now exists by the time this runs, so a new table has to be told.
                CASE WHEN t.CdcSupportsNetChanges IS NOT NULL THEN ', @supports_net_changes = ' + CASE WHEN t.CdcSupportsNetChanges = 1 THEN '1' ELSE '0' END
                     WHEN t.NewTable = 1 THEN ', @supports_net_changes = 0'
                     ELSE '' END +
                ISNULL(', @index_name = N''' + REPLACE(SchemaSmith.fn_StripBracketWrapping(t.CdcIndexName), '''', '''''') + '''', '') + ';' + CHAR(13) + CHAR(10)
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

  -- A capture instance whose columns are not the table's own is rotated even when this run changed no column (#427).
  -- The column diff only sees what THIS run changes, so a column added by a run that stopped before this step, or
  -- while the table was at the two-instance limit, would otherwise never be captured. Rotation always captures
  -- every column, so once it has run the sets agree and this finds nothing.
  IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1) AND @WhatIf = 0
  BEGIN
    IF OBJECT_ID('tempdb..#CdcCaptureDrift') IS NOT NULL DROP TABLE #CdcCaptureDrift
    SELECT t.[Schema], t.[Name], newest.capture_instance, newest.filegroup_name, newest.supports_net_changes, newest.index_name,
           SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup) AS DeclaredFilegroup, t.CdcSupportsNetChanges AS DeclaredNetChanges,
           SchemaSmith.fn_StripBracketWrapping(t.CdcIndexName) AS DeclaredIndexName,
           Instances = (SELECT COUNT(*) FROM cdc.change_tables c WITH (NOLOCK) WHERE c.source_object_id = st.[object_id])
      INTO #CdcCaptureDrift
      FROM #Tables t WITH (NOLOCK)
      JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
      CROSS APPLY (SELECT TOP 1 ct.capture_instance, ct.filegroup_name, ct.supports_net_changes, ct.index_name, ct.[object_id] AS ChangeTableId
                     FROM cdc.change_tables ct WITH (NOLOCK)
                    WHERE ct.source_object_id = st.[object_id]
                    ORDER BY ct.create_date DESC, ct.[object_id] DESC) newest
      WHERE st.is_tracked_by_cdc = 1 AND t.EnableCDC = 1
        AND (EXISTS (SELECT 1 FROM sys.columns sc
                      WHERE sc.[object_id] = st.[object_id] AND sc.is_column_set = 0
                        AND NOT EXISTS (SELECT 1 FROM cdc.captured_columns cc WITH (NOLOCK)
                                         WHERE cc.[object_id] = newest.ChangeTableId AND cc.column_name = sc.[name]))
             OR EXISTS (SELECT 1 FROM cdc.captured_columns cc WITH (NOLOCK)
                         WHERE cc.[object_id] = newest.ChangeTableId
                           AND NOT EXISTS (SELECT 1 FROM sys.columns sc WHERE sc.[object_id] = st.[object_id] AND sc.[name] = cc.column_name)))

    INSERT #CdcRotate ([Schema], [TableName], OldCaptureInstance, NewFilegroup, NewNetChanges, NewIndexName, Reason)
      SELECT d.[Schema], d.[Name], d.capture_instance, COALESCE(d.DeclaredFilegroup, d.filegroup_name),
             COALESCE(d.DeclaredNetChanges, d.supports_net_changes), COALESCE(d.DeclaredIndexName, d.index_name), 'capture'
        FROM #CdcCaptureDrift d
       WHERE d.Instances = 1
         AND NOT EXISTS (SELECT 1 FROM #CdcRotate r WHERE r.[Schema] = d.[Schema] AND r.[TableName] = d.[Name])

    DECLARE @v_Uncaptured NVARCHAR(MAX) =
      STUFF((SELECT ', ' + d.[Schema] + '.' + d.[Name] FROM #CdcCaptureDrift d WHERE d.Instances >= 2
               FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF @v_Uncaptured IS NOT NULL
      RAISERROR('  WARNING: the newest CDC capture instance on %s does not capture the table''s current columns, and both capture-instance slots are in use. Drain the older instance and drop it (EXEC sys.sp_cdc_disable_table @capture_instance = N''<name>''); the next deploy then rotates to an instance that captures every column.', 10, 100, @v_Uncaptured) WITH NOWAIT
  END

  RAISERROR('Rotate CDC Capture Instances For Tables With Column Changes', 10, 100) WITH NOWAIT
  -- The pre-existing instance keeps the history it already captured and is deliberately NOT dropped:
  -- only the operator knows when downstream readers have drained it. It does occupy one of the two
  -- slots, so the guard above will refuse the NEXT column change until it is dropped.
  IF EXISTS (SELECT 1 FROM #CdcRotate)
  BEGIN
    SET @v_SQL = ''
    SELECT @v_SQL = @v_SQL +
      'RAISERROR(''  CDC ROTATED on ' + r.[Schema] + '.' + r.[TableName] + ': new capture instance ' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ' now captures ' + CASE WHEN r.Reason = 'filegroup' THEN 'this table on filegroup ' + r.NewFilegroup WHEN r.Reason = 'netchanges' THEN 'with net changes ' + CASE WHEN r.NewNetChanges = 1 THEN 'ON' ELSE 'OFF' END WHEN r.Reason = 'index' THEN 'identifying rows by index ' + REPLACE(r.NewIndexName, '''', '''''') WHEN r.Reason = 'capture' THEN 'every current column (the previous instance did not)' ELSE 'the new column set' END + '. The previous instance ' + r.OldCaptureInstance + ' STILL HOLDS ITS HISTORY and was NOT dropped -- drain it, then drop it with EXEC sys.sp_cdc_disable_table @capture_instance = N''''' + r.OldCaptureInstance + '''''. Until then the next column change on this table WILL FAIL: SQL Server allows only two capture instances.'', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
      'EXEC sys.sp_cdc_enable_table @source_schema = N''' + SchemaSmith.fn_StripBracketWrapping(r.[Schema]) + ''', @source_name = N''' + SchemaSmith.fn_StripBracketWrapping(r.[TableName]) + ''', @capture_instance = N''' + CASE WHEN r.OldCaptureInstance = b.BaseName THEN b.BaseName + '_2' ELSE b.BaseName END + ''', @role_name = NULL' + ISNULL(', @filegroup_name = N''' + r.NewFilegroup + '''', '') +
      ISNULL(', @supports_net_changes = ' + CAST(r.NewNetChanges AS VARCHAR(1)), '') +
      -- The declared index, else the one the old instance identifies rows by. Without it sp_cdc_enable_table picks the
      -- primary key, so an instance on a unique index would move off it, and net changes with no primary key would fail.
      CASE WHEN r.NewIndexName IS NOT NULL
                AND EXISTS (SELECT 1 FROM sys.indexes i WHERE i.[object_id] = OBJECT_ID(r.[Schema] + '.' + r.[TableName]) AND i.[name] = r.NewIndexName)
           THEN ', @index_name = N''' + REPLACE(r.NewIndexName, '''', '''''') + ''''
           ELSE '' END + ';' + CHAR(13) + CHAR(10)
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
