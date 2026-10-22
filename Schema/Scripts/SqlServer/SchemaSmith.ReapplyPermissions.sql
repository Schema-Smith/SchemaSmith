-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[ReapplyPermissions]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[ReapplyPermissions]
GO
-- Runs what CapturePermissions rendered, once the replacement object exists, and records it in the change audit
-- as 'reapplied' -- or, under WhatIf, prints it and records 'wouldReapply'. Nothing happens for a NULL script.
CREATE PROCEDURE [SchemaSmith].[ReapplyPermissions]
  @Script NVARCHAR(MAX),
  @ObjectType NVARCHAR(64),
  @ObjectName NVARCHAR(512),
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  IF @Script IS NULL OR DATALENGTH(@Script) = 0 RETURN

  IF @WhatIf = 1
  BEGIN
    RAISERROR('  Would re-apply the permissions and extended properties on %s', 10, 100, @ObjectName) WITH NOWAIT
    EXEC SchemaSmith.PrintWithNoWait @Script
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      VALUES (@@SPID, @ObjectType, @ObjectName, 'wouldReapply')
    RETURN
  END

  RAISERROR('  Re-applying the permissions and extended properties on %s', 10, 100, @ObjectName) WITH NOWAIT
  EXEC(@Script)
  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    VALUES (@@SPID, @ObjectType, @ObjectName, 'reapplied')
END
