-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- OnlineApply REQUIRE: called once the drop passes have run, so a declared index, key or constraint that is missing now
-- is one this deploy will build (under WhatIf, one recorded as wouldDrop is too). Each that cannot be built online stops
-- the deploy here, naming it, and the caller's transaction takes back what it had already changed. The index-only path
-- has no constraint tables, so p_IndexesOnly reads the indexes alone.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."RefuseUnmetOnlineRequire"(p_OnlineApply TEXT, p_WhatIf BOOLEAN, p_IndexesOnly BOOLEAN)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
  v_refused TEXT;
BEGIN
  DROP TABLE IF EXISTS temp_online_builds;
  CREATE TEMPORARY TABLE temp_online_builds AS
    SELECT ti."TableSchema", ti."TableName", ti."Name",
           CASE WHEN ti."WithoutOverlaps" THEN 'withoutOverlaps' WHEN ti."PrimaryKey" OR ti."UniqueConstraint" THEN 'constraint' ELSE 'index' END AS "Kind",
           NOT EXISTS (SELECT 1 FROM pg_index idx
                         JOIN pg_class tc ON tc.oid = idx.indrelid
                         JOIN pg_namespace tn ON tn.oid = tc.relnamespace
                         JOIN pg_class i ON i.oid = idx.indexrelid
                        WHERE tn.nspname = ti."TableSchema" AND tc.relname = ti."TableName" AND i.relname = ti."Name") AS "Missing"
      FROM temp_indexes ti;
  IF NOT p_IndexesOnly THEN
    INSERT INTO temp_online_builds
      SELECT tx."TableSchema", tx."TableName", tx."Name", 'exclusion',
             NOT EXISTS (SELECT 1 FROM pg_constraint con
                           JOIN pg_class rel ON rel.oid = con.conrelid
                           JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                          WHERE con.contype = 'x' AND nsp.nspname = tx."TableSchema" AND rel.relname = tx."TableName" AND con.conname = tx."Name")
        FROM temp_excludes tx;
    INSERT INTO temp_online_builds
      SELECT fk."TableSchema", fk."TableName", fk."Name", 'foreignKey',
             NOT EXISTS (SELECT 1 FROM pg_constraint con
                           JOIN pg_class rel ON rel.oid = con.conrelid
                           JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                          WHERE con.contype = 'f' AND nsp.nspname = fk."TableSchema" AND rel.relname = fk."TableName" AND con.conname = fk."Name")
        FROM temp_fks fk;
  END IF;

  SELECT STRING_AGG(b."TableSchema" || '.' || b."TableName" || '.' || b."Name" || ' (' ||
                    "SchemaSmith"."OnlineUnavailableReason"(b."TableSchema", b."TableName", b."Kind") || ')', '; '
                    ORDER BY b."TableSchema", b."TableName", b."Name")
    INTO v_refused
    FROM temp_online_builds b
   WHERE "SchemaSmith"."OnlineApplyMode"(p_OnlineApply, b."TableSchema", b."TableName") = 'REQUIRE'
     AND "SchemaSmith"."OnlineUnavailableReason"(b."TableSchema", b."TableName", b."Kind") IS NOT NULL
     AND (b."Missing"
          OR (p_WhatIf AND EXISTS (SELECT 1 FROM "SchemaSmith"."ChangeAudit" a
                                    WHERE a."SessionId" = pg_backend_pid() AND a."ActionType" = 'wouldDrop'
                                      AND a."ObjectName" = b."TableSchema" || '.' || b."TableName" || '.' || b."Name")));
  DROP TABLE IF EXISTS temp_online_builds;
  IF v_refused IS NOT NULL THEN
    RAISE EXCEPTION 'OnlineApply is Require, and these cannot be built online: %. Set the table''s OnlineApply to Prefer to build them with the plain statement.', v_refused;
  END IF;
END $$;
