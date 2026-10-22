-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.DegradeUnsupportedJsonIndexes', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DegradeUnsupportedJsonIndexes
GO
CREATE PROCEDURE SchemaSmith.DegradeUnsupportedJsonIndexes
AS
BEGIN
  -- JSON indexes arrive in SQL Server 2025 with sys.json_indexes, which is the gate. Below it a declared JSON index is
  -- skipped and the table deploys without it; 'fail' aborts naming every such index. Called from
  -- DegradeUnsupportedFeatures (the TableQuench path) and IndexOnly[Xml]Quench (the --IndexOnly path).
  IF OBJECT_ID('sys.json_indexes') IS NOT NULL OR NOT EXISTS (SELECT 1 FROM #JsonIndexes WITH (NOLOCK)) RETURN

  IF SchemaSmith.UnsupportedFeaturePolicy() = 'fail'
  BEGIN
    DECLARE @v_List NVARCHAR(MAX) = STUFF((SELECT ', ' + j.[Schema] + '.' + j.[TableName] + '.' + j.[IndexName]
                                             FROM #JsonIndexes j WITH (NOLOCK)
                                             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    DECLARE @v_FailMsg NVARCHAR(2048) = 'JSON indexes require SQL Server 2025 (detected major ' +
                                        CONVERT(NVARCHAR(10), SchemaSmith.fn_ServerMajorVersion()) + '); JSON index(es): ' + LEFT(@v_List, 1700) + '.'
    RAISERROR(@v_FailMsg, 16, 1)
    RETURN
  END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT @@SPID, 'JSON index (SQL Server 2025)', j.[Schema] + '.' + j.[TableName] + '.' + j.[IndexName], 'downgraded'
      FROM #JsonIndexes j WITH (NOLOCK)
  RAISERROR('  JSON index skipped: requires SQL Server 2025 - the table deploys without it (downgraded)', 10, 100) WITH NOWAIT

  DELETE FROM #JsonIndexes
END
