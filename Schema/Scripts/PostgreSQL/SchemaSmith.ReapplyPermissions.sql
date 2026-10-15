-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Runs what CapturePermissions rendered, once the replacement object exists, and records it in the change audit
-- as 'reapplied' -- or, under WhatIf, prints it and records 'wouldReapply'. Nothing happens for a NULL script.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ReapplyPermissions"(p_Script TEXT, p_ObjectType TEXT, p_ObjectName TEXT, p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
AS $$
BEGIN
  IF NULLIF(p_Script, '') IS NULL THEN
    RETURN;
  END IF;
  IF p_WhatIf THEN
    RAISE NOTICE '  Would re-apply the privileges, owner and comments on %', p_ObjectName;
    CALL "SchemaSmith"."ExecuteOrDebug"(p_Script, TRUE);
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      VALUES (pg_backend_pid(), p_ObjectType, p_ObjectName, 'wouldReapply');
  ELSE
    RAISE NOTICE '  Re-applying the privileges, owner and comments on %', p_ObjectName;
    CALL "SchemaSmith"."ExecuteOrDebug"(p_Script, FALSE);
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      VALUES (pg_backend_pid(), p_ObjectType, p_ObjectName, 'reapplied');
  END IF;
END $$;
