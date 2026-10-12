-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Under WhatIf a quench script is printed, not run, so the change rows it carries are never written. This writes
-- each one with its preview action instead (created -> wouldCreate, modified -> wouldModify, dropped -> wouldDrop),
-- so the preview lists exactly the changes the real run would record, from the same statement.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."RecordWouldChanges"(p_Script TEXT)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
    m TEXT[];
BEGIN
    FOR m IN
        SELECT regexp_matches(COALESCE(p_Script, ''),
            'INSERT INTO "SchemaSmith"\."ChangeAudit"\s*\("SessionId",\s*"ObjectType",\s*"ObjectName",\s*"ActionType"\)\s*VALUES\s*\(pg_backend_pid\(\),\s*''((?:[^'']|'''')*)'',\s*''((?:[^'']|'''')*)'',\s*''(created|modified|dropped)''\)',
            'g')
    LOOP
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
            VALUES (pg_backend_pid(), REPLACE(m[1], '''''', ''''), REPLACE(m[2], '''''', ''''),
                    CASE m[3] WHEN 'created' THEN 'wouldCreate' WHEN 'modified' THEN 'wouldModify' ELSE 'wouldDrop' END);
    END LOOP;
END $$;
