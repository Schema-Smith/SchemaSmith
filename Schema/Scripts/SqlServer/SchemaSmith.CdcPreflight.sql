-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- CDC work that must happen before any table or column is created, on every run -- a resumed run included, which
-- skips ModifiedTableQuench. Resolves the template-level CDC defaults, and refuses a change the two-capture-instance
-- limit will not let CdcQuench rotate for, while refusing still changes nothing (#427). Refused any later, a new
-- column already exists, and once the operator frees a slot nothing in the next run's column diff says to rotate.
IF OBJECT_ID('SchemaSmith.CdcPreflight', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.CdcPreflight
GO
CREATE PROCEDURE SchemaSmith.CdcPreflight
    @CdcFilegroup NVARCHAR(128) = NULL,
    @CdcSupportsNetChanges BIT = NULL
AS
BEGIN
  SET NOCOUNT ON
  IF @CdcFilegroup IS NOT NULL
    UPDATE #Tables SET CdcFilegroup = SchemaSmith.fn_SafeBracketWrap(@CdcFilegroup)
     WHERE EnableCDC = 1 AND CdcFilegroup IS NULL
  IF @CdcSupportsNetChanges IS NOT NULL
    UPDATE #Tables SET CdcSupportsNetChanges = @CdcSupportsNetChanges
     WHERE EnableCDC = 1 AND CdcSupportsNetChanges IS NULL

  IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1) RETURN

  DECLARE @v_DefaultFilegroup SYSNAME = (SELECT [name] FROM sys.filegroups WHERE is_default = 1)
  DECLARE @v_AtCeiling NVARCHAR(MAX) =
    STUFF((SELECT ', ' + t.[Schema] + '.' + t.[Name]
             FROM #Tables t WITH (NOLOCK)
             JOIN sys.tables st ON st.[object_id] = OBJECT_ID(t.[Schema] + '.' + t.[Name])
             CROSS APPLY (SELECT TOP 1 ct.filegroup_name, ct.supports_net_changes, ct.index_name
                            FROM cdc.change_tables ct WITH (NOLOCK)
                           WHERE ct.source_object_id = st.[object_id]
                           ORDER BY ct.create_date DESC, ct.[object_id] DESC) newest
            WHERE st.is_tracked_by_cdc = 1 AND t.EnableCDC = 1
              AND (SELECT COUNT(*) FROM cdc.change_tables c WITH (NOLOCK) WHERE c.source_object_id = st.[object_id]) >= 2
              AND (EXISTS (SELECT 1 FROM #Columns c WITH (NOLOCK) WHERE c.[Schema] = t.[Schema] AND c.[TableName] = t.[Name] AND c.NewColumn = 1)
                   OR (t.CdcFilegroup IS NOT NULL AND SchemaSmith.fn_StripBracketWrapping(t.CdcFilegroup) <> ISNULL(newest.filegroup_name, @v_DefaultFilegroup))
                   OR (t.CdcSupportsNetChanges IS NOT NULL AND t.CdcSupportsNetChanges <> newest.supports_net_changes)
                   OR (t.CdcIndexName IS NOT NULL AND SchemaSmith.fn_StripBracketWrapping(t.CdcIndexName) <> ISNULL(newest.index_name, '')))
              FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @v_AtCeiling IS NOT NULL
    RAISERROR('CDC capture-instance limit reached on: %s. SQL Server permits two capture instances per table and both are already in use, so this new column, CdcFilegroup, CdcSupportsNetChanges or CdcIndexName change cannot rotate without discarding change history. Nothing has been changed. Drain the older instance on each listed table and drop it (EXEC sys.sp_cdc_disable_table @source_schema = N''<schema>'', @source_name = N''<table>'', @capture_instance = N''<name>''), then re-run.', 16, 1, @v_AtCeiling)
END
