-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."SequenceQuench"(
    p_ProductName VARCHAR(50),
    p_Sequences TEXT,
    p_WhatIf BOOLEAN DEFAULT FALSE)
    LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT;
BEGIN
  -- Converges declared sequences.
  --
  -- Unlike enum types, every attribute here is genuinely alterable in place, so this converges properly
  -- and has nothing to refuse.
  --
  -- THE CURRENT VALUE IS NEVER TOUCHED, and that is the important line. A sequence's current position is
  -- DATA -- it records which numbers have already been handed out. "Start" is the declared starting
  -- point, which only applies at creation. Managing the current value would mean a deploy resetting a
  -- live sequence and re-issuing keys that are already in use, so nothing here reads or writes it.
  --
  -- ENGINE-OWNED SEQUENCES ARE INVISIBLE HERE by construction: this only ever considers what the package
  -- declares, and extraction excludes any sequence owned by a serial/IDENTITY column. Those belong to the
  -- column that generated them.
  DROP TABLE IF EXISTS temp_sequences;
  CREATE TEMPORARY TABLE temp_sequences AS
  WITH src(arr) AS (VALUES(p_Sequences::JSON))
  SELECT COALESCE(elem ->> 'Schema', 'public') AS "Schema",
         elem ->> 'Name' AS "Name",
         COALESCE(elem ->> 'DataType', 'bigint') AS "DataType",
         (elem ->> 'Start')::BIGINT AS "Start",
         COALESCE((elem ->> 'Increment')::BIGINT, 1) AS "Increment",
         (elem ->> 'MinValue')::BIGINT AS "MinValue",
         (elem ->> 'MaxValue')::BIGINT AS "MaxValue",
         COALESCE((elem ->> 'Cache')::BIGINT, 1) AS "Cache",
         COALESCE((elem ->> 'Cycle')::BOOLEAN, false) AS "Cycle",
         COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression"
    FROM src, JSON_ARRAY_ELEMENTS(arr) AS elem;

  -- Evaluate ShouldApplyExpression: drop sequences whose condition is false so a conditional variant is
  -- SKIPPED rather than silently always applied. Always executes, even under --WhatIf -- this filters the
  -- internal working set, it is not user-visible DDL.
  SELECT STRING_AGG('DELETE FROM temp_sequences WHERE "Schema" = ''' || "Schema" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
    INTO sql_script
    FROM temp_sequences
    WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

  RAISE NOTICE 'Add Missing Sequences';
  SELECT STRING_AGG('RAISE NOTICE ''  Create sequence ' || s."Schema" || '.' || s."Name" || ''';' || CHR(10) ||
                    'CREATE SEQUENCE "' || s."Schema" || '"."' || s."Name" || '" AS ' || s."DataType" ||
                    ' INCREMENT BY ' || s."Increment" ||
                    CASE WHEN s."MinValue" IS NOT NULL THEN ' MINVALUE ' || s."MinValue" ELSE ' NO MINVALUE' END ||
                    CASE WHEN s."MaxValue" IS NOT NULL THEN ' MAXVALUE ' || s."MaxValue" ELSE ' NO MAXVALUE' END ||
                    CASE WHEN s."Start" IS NOT NULL THEN ' START WITH ' || s."Start" ELSE '' END ||
                    ' CACHE ' || s."Cache" ||
                    CASE WHEN s."Cycle" THEN ' CYCLE' ELSE ' NO CYCLE' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''sequence'', ''' || s."Schema" || '.' || s."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_sequences s
   WHERE NOT EXISTS (SELECT 1 FROM pg_class c
                       JOIN pg_namespace n ON n.oid = c.relnamespace
                      WHERE c.relname = s."Name" AND n.nspname = s."Schema" AND c.relkind = 'S');
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Converge the ones that exist. Every clause below is emitted only when it actually differs, so an
  -- unchanged sequence produces no statement at all rather than a no-op ALTER on every deploy.
  --
  -- START is compared but NOT restarted: ALTER SEQUENCE ... START WITH changes the declared start for a
  -- future RESTART and leaves the current value alone, which is exactly the intent. Nothing here emits
  -- RESTART, because that WOULD move the current value.
  RAISE NOTICE 'Fixup Modified Sequences';
  SELECT STRING_AGG('RAISE NOTICE ''  Altering sequence ' || s."Schema" || '.' || s."Name" || ''';' || CHR(10) ||
                    'ALTER SEQUENCE "' || s."Schema" || '"."' || s."Name" || '"' ||
                    CASE WHEN FORMAT_TYPE(q.seqtypid, NULL) <> s."DataType" THEN ' AS ' || s."DataType" ELSE '' END ||
                    CASE WHEN q.seqincrement <> s."Increment" THEN ' INCREMENT BY ' || s."Increment" ELSE '' END ||
                    CASE WHEN s."MinValue" IS NOT NULL AND q.seqmin <> s."MinValue" THEN ' MINVALUE ' || s."MinValue"
                         -- Declared value CLEARED (back to unset) but the server still carries a non-default
                         -- bound: reset it. NO MINVALUE/NO MAXVALUE restore PostgreSQL's own default (1 or the
                         -- type min for ascending/descending), gated on "not already default" so an unchanged
                         -- sequence emits nothing. (Start has no NO-START primitive and only affects a future
                         -- RESTART, so a cleared Start is deliberately not reset here.)
                         WHEN s."MinValue" IS NULL AND q.seqmin <> (CASE WHEN q.seqincrement > 0 THEN 1 ELSE (CASE FORMAT_TYPE(q.seqtypid, NULL) WHEN 'smallint' THEN -32768 WHEN 'integer' THEN -2147483648 ELSE -9223372036854775808 END) END) THEN ' NO MINVALUE'
                         ELSE '' END ||
                    CASE WHEN s."MaxValue" IS NOT NULL AND q.seqmax <> s."MaxValue" THEN ' MAXVALUE ' || s."MaxValue"
                         WHEN s."MaxValue" IS NULL AND q.seqmax <> (CASE WHEN q.seqincrement > 0 THEN (CASE FORMAT_TYPE(q.seqtypid, NULL) WHEN 'smallint' THEN 32767 WHEN 'integer' THEN 2147483647 ELSE 9223372036854775807 END) ELSE -1 END) THEN ' NO MAXVALUE'
                         ELSE '' END ||
                    CASE WHEN s."Start" IS NOT NULL AND q.seqstart <> s."Start" THEN ' START WITH ' || s."Start" ELSE '' END ||
                    CASE WHEN q.seqcache <> s."Cache" THEN ' CACHE ' || s."Cache" ELSE '' END ||
                    CASE WHEN q.seqcycle <> s."Cycle" THEN CASE WHEN s."Cycle" THEN ' CYCLE' ELSE ' NO CYCLE' END ELSE '' END ||
                    ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''sequence'', ''' || s."Schema" || '.' || s."Name" || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_sequences s
    JOIN pg_class c ON c.relname = s."Name" AND c.relkind = 'S'
    JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = s."Schema"
    JOIN pg_sequence q ON q.seqrelid = c.oid
   WHERE FORMAT_TYPE(q.seqtypid, NULL) <> s."DataType"
      OR q.seqincrement <> s."Increment"
      OR (s."MinValue" IS NOT NULL AND q.seqmin <> s."MinValue")
      OR (s."MinValue" IS NULL AND q.seqmin <> (CASE WHEN q.seqincrement > 0 THEN 1 ELSE (CASE FORMAT_TYPE(q.seqtypid, NULL) WHEN 'smallint' THEN -32768 WHEN 'integer' THEN -2147483648 ELSE -9223372036854775808 END) END))
      OR (s."MaxValue" IS NOT NULL AND q.seqmax <> s."MaxValue")
      OR (s."MaxValue" IS NULL AND q.seqmax <> (CASE WHEN q.seqincrement > 0 THEN (CASE FORMAT_TYPE(q.seqtypid, NULL) WHEN 'smallint' THEN 32767 WHEN 'integer' THEN 2147483647 ELSE 9223372036854775807 END) ELSE -1 END))
      OR (s."Start" IS NOT NULL AND q.seqstart <> s."Start")
      OR q.seqcache <> s."Cache"
      OR q.seqcycle <> s."Cycle";
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
END $$;
