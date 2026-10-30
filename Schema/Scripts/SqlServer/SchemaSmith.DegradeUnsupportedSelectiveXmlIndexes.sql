-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedSelectiveXmlIndexes', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedSelectiveXmlIndexes
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedSelectiveXmlIndexes
AS
BEGIN
  -- Selective XML indexes arrive in SQL Server 2012 SP1, with sys.xml_indexes.xml_index_type, so the column is the gate: the
  -- major version cannot tell 2012 SP1 from RTM. On 2012 the feature is also off in each database until
  -- sys.sp_db_selective_xml_index turns it on (from 2014 it is always on); that is the database's setting, not the
  -- package's, so it is asked rather than changed. Where either says no, a declared selective index and every secondary
  -- on one (ForPath) are skipped, and the table deploys without them. 'fail' aborts naming every such index. Called from
  -- DegradeUnsupportedFeatures (the TableQuench path) and IndexOnly[Xml]Quench (the --IndexOnly path).
  IF NOT EXISTS (SELECT 1 FROM #XmlIndexes WITH (NOLOCK) WHERE [Selective] IS NOT NULL OR [ForPath] IS NOT NULL) RETURN
  DECLARE @v_Reason NVARCHAR(200) = CASE WHEN COL_LENGTH('sys.xml_indexes', 'xml_index_type') IS NULL THEN 'requires SQL Server 2012 SP1' END
  IF @v_Reason IS NULL AND SchemaSmith.fn_ServerMajorVersion() = 11
  BEGIN
    DECLARE @v_Enabled TABLE ([Enabled] INT)
    DECLARE @v_Db SYSNAME = DB_NAME()
    INSERT @v_Enabled EXEC sys.sp_db_selective_xml_index @v_Db, NULL
    IF NOT EXISTS (SELECT 1 FROM @v_Enabled WHERE [Enabled] = 1)
      SET @v_Reason = 'is off in this database (enable it with sys.sp_db_selective_xml_index)'
  END
  IF @v_Reason IS NULL RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_List NVARCHAR(MAX) = STUFF((SELECT ', ' + x.[Schema] + '.' + x.[TableName] + '.' + x.[IndexName]
                                             FROM #XmlIndexes x WITH (NOLOCK)
                                             WHERE x.[Selective] IS NOT NULL OR x.[ForPath] IS NOT NULL
                                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    DECLARE @v_FailMsg NVARCHAR(2048) = 'Selective XML index ' + @v_Reason + ' (detected major ' +
                                        CONVERT(NVARCHAR(10), SchemaSmith.fn_ServerMajorVersion()) + '); XML index(es): ' + LEFT(@v_List, 1500) + '.'
    RAISERROR(@v_FailMsg, 16, 1)
    RETURN
  END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, 'selective XML index (SQL Server 2012 SP1)', x.[Schema] + '.' + x.[TableName] + '.' + x.[IndexName], 'downgraded'
      FROM #XmlIndexes x WITH (NOLOCK)
      WHERE x.[Selective] IS NOT NULL OR x.[ForPath] IS NOT NULL
  DECLARE @v_Msg NVARCHAR(400) = '  Selective XML index skipped: the feature ' + @v_Reason + ' - the table deploys without it (downgraded)'
  DECLARE @v_Skipped NVARCHAR(MAX) = STUFF((SELECT ', ' + x.[Schema] + '.' + x.[TableName] + '.' + x.[IndexName] FROM #XmlIndexes x WITH (NOLOCK)
                                            WHERE x.[Selective] IS NOT NULL OR x.[ForPath] IS NOT NULL
                                            FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  EXEC SchemaSmith.PrintDowngrade @v_Msg, @v_Skipped

  DELETE FROM #XmlIndexes WHERE [Selective] IS NOT NULL OR [ForPath] IS NOT NULL
END
