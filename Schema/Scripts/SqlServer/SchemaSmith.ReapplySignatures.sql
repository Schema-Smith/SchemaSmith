-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[ReapplySignatures]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[ReapplySignatures]
GO
-- Signs a module again with what CaptureSignatures rendered, once the change is applied, and records it in the
-- change audit as 'resigned' -- or, under WhatIf, prints it and records 'wouldResign'. Nothing happens for a NULL script.
CREATE PROCEDURE [SchemaSmith].[ReapplySignatures]
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
    RAISERROR('  Would sign %s again', 10, 100, @ObjectName) WITH NOWAIT
    EXEC SchemaSmith.PrintWithNoWait @Script
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      VALUES (@@SPID, @ObjectType, @ObjectName, 'wouldResign')
    RETURN
  END

  RAISERROR('  Signing %s again', 10, 100, @ObjectName) WITH NOWAIT
  EXEC(@Script)
  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    VALUES (@@SPID, @ObjectType, @ObjectName, 'resigned')
END
