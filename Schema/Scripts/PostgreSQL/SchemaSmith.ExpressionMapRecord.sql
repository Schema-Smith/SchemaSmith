-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- #242, PostgreSQL. Records what was applied for every expression-bearing object this run declared: the
-- authored text, the canonical text the engine reports NOW, and the engine version that decides
-- canonicalisation. "SchemaSmith"."ExpressionMapUnchanged" reads it on the next deploy.
--
-- Runs after the apply passes, so an object created moments ago is recorded on the same run rather than
-- churning once more on the next. Re-baselining is the same statement: a row whose engine version no longer
-- matches is overwritten with the current reading, without touching the object.
--
-- The live text is read through exactly the normalisation the comparison uses (StripParenWrapping over
-- pg_get_constraintdef minus its "CHECK " prefix). If the two ever diverge, every constraint would look
-- changed forever -- which is the defect this whole item exists to remove.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ExpressionMapRecord"(p_WhatIf BOOLEAN DEFAULT FALSE)
LANGUAGE plpgsql
AS $$
DECLARE
  v_version TEXT := current_setting('server_version');
  v_rebaselined INTEGER := 0;
BEGIN
  IF p_WhatIf THEN RETURN; END IF;
  -- Each source is guarded on its own: an index-only quench builds temp_indexes but no temp_columns or
  -- temp_checks, and a full quench may run with no indexes declared.
  IF to_regclass('pg_temp.temp_columns') IS NULL AND to_regclass('pg_temp.temp_indexes') IS NULL THEN RETURN; END IF;

  CREATE TEMPORARY TABLE IF NOT EXISTS temp_expression_map_declared (
    "ObjectSchema" VARCHAR(256) NOT NULL, "ObjectTable" VARCHAR(256) NOT NULL, "ObjectKind" VARCHAR(32) NOT NULL,
    "ObjectName" VARCHAR(256) NOT NULL, "Slot" VARCHAR(32) NOT NULL,
    "AuthoredText" TEXT NOT NULL, "CanonicalText" TEXT NOT NULL);
  TRUNCATE temp_expression_map_declared;

  IF to_regclass('pg_temp.temp_columns') IS NOT NULL AND to_regclass('pg_temp.temp_checks') IS NOT NULL THEN
  -- Check constraints, table- and column-level. PostgreSQL stores both identically; the column form is
  -- recognised by the CK_<table>_<column> name the create pass gives it.
  INSERT INTO temp_expression_map_declared
    SELECT c."TableSchema", c."TableName", 'CHECK', c."Name", 'expression', c."Expression",
           "SchemaSmith"."StripParenWrapping"(SUBSTRING(pg_catalog.PG_GET_CONSTRAINTDEF(con.oid) FROM 6))
      FROM temp_checks c
      JOIN pg_catalog.pg_constraint con
        ON con.conrelid = to_regclass('"' || c."TableSchema" || '"."' || c."TableName" || '"')
       AND con.conname = c."Name"
       AND con.contype = 'c'
     WHERE COALESCE(c."Expression", '') != '';

  INSERT INTO temp_expression_map_declared
    SELECT col."TableSchema", col."TableName", 'CHECK', con.conname, 'expression', col."CheckExpression",
           "SchemaSmith"."StripParenWrapping"(SUBSTRING(pg_catalog.PG_GET_CONSTRAINTDEF(con.oid) FROM 6))
      FROM temp_columns col
      JOIN pg_catalog.pg_constraint con
        ON con.conrelid = to_regclass('"' || col."TableSchema" || '"."' || col."TableName" || '"')
       AND con.conname = 'CK_' || col."TableName" || '_' || col."Name"
       AND con.contype = 'c'
     WHERE COALESCE(col."CheckExpression", '') != ''
       AND NOT EXISTS (SELECT 1 FROM temp_expression_map_declared d
                        WHERE d."ObjectTable" = col."TableName" AND d."ObjectName" = con.conname);

  -- Generated columns: compared raw today, so any non-trivial expression churned on every deploy.
  INSERT INTO temp_expression_map_declared
    SELECT col."TableSchema", col."TableName", 'COLUMN', col."Name", 'generated', col."GenerationExpression",
           COALESCE(a.generation_expression, '')
      FROM temp_columns col
      JOIN information_schema.columns a
        ON a.table_schema = col."TableSchema" AND a.table_name = col."TableName" AND a.column_name = col."Name"
     WHERE COALESCE(col."GenerationExpression", '') != '';
  END IF;

  IF to_regclass('pg_temp.temp_indexes') IS NOT NULL THEN
  -- Partial-index filter expressions.
  INSERT INTO temp_expression_map_declared
    -- Read EXACTLY as BuildExistingIndexesSnapshot reads it -- raw PG_GET_EXPR, no paren stripping. Recording
    -- a differently-normalised form than the comparison uses would make every partial index look changed
    -- forever, which is the defect this whole item exists to remove.
    SELECT i."TableSchema", i."TableName", 'INDEX', i."Name", 'filter', i."FilterExpression",
           COALESCE(PG_GET_EXPR(idx.indpred, idx.indrelid), '')
      FROM temp_indexes i
      JOIN pg_catalog.pg_class ic ON ic.relname = i."Name" AND ic.relkind = 'i'
      JOIN pg_catalog.pg_index idx ON idx.indexrelid = ic.oid
     WHERE COALESCE(i."FilterExpression", '') != '';
  END IF;

  -- Extended statistics carrying an expression: the whole column list, in the normalised live form the
  -- comparison reads (SchemaSmith.StatisticsLiveColumns).
  IF to_regclass('pg_temp.temp_statistics') IS NOT NULL THEN
    INSERT INTO temp_expression_map_declared
      SELECT ts."TableSchema", ts."TableName", 'STATISTIC', ts."Name", 'columns', ts."StatisticsColumns",
             "SchemaSmith"."StatisticsLiveColumns"(se.oid)
        FROM temp_statistics ts
        JOIN pg_namespace n ON n.nspname = ts."TableSchema"
        JOIN pg_class rel ON rel.relnamespace = n.oid AND rel.relname = ts."TableName"
        JOIN pg_statistic_ext se ON se.stxrelid = rel.oid AND se.stxname = ts."Name"
       WHERE ts."StatisticsColumns" LIKE '%(%';
  END IF;

  -- Row-level security policy expressions, one slot per clause. Raw pg_policies text, as the comparison reads it.
  IF to_regclass('pg_temp.temp_policies') IS NOT NULL THEN
    INSERT INTO temp_expression_map_declared
      SELECT tp."TableSchema", tp."TableName", 'POLICY', tp."Name", 'using', tp."UsingExpression", pol.qual
        FROM temp_policies tp
        JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
       WHERE NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL AND pol.qual IS NOT NULL;
    INSERT INTO temp_expression_map_declared
      SELECT tp."TableSchema", tp."TableName", 'POLICY', tp."Name", 'check', tp."WithCheckExpression", pol.with_check
        FROM temp_policies tp
        JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
       WHERE NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL AND pol.with_check IS NOT NULL;
  END IF;

  -- Say so when a re-baseline happens (Paul, 2026-09-08: "re-baseline, and say so in the log -- log the count so
  -- it is visible rather than silent"). A re-baseline is a row whose DECLARATION is unchanged but whose engine
  -- context moved -- an engine upgrade or a compatibility-level change. A row whose declaration also changed was
  -- APPLIED, not re-baselined, and is not counted.
  SELECT COUNT(*) INTO v_rebaselined
    FROM temp_expression_map_declared d
    JOIN "SchemaSmith"."ExpressionMap" m
      ON m."ObjectSchema" = d."ObjectSchema" AND m."ObjectTable" = d."ObjectTable" AND m."ObjectKind" = d."ObjectKind"
     AND m."ObjectName" = d."ObjectName" AND m."Slot" = d."Slot"
   WHERE m."AuthoredText" = d."AuthoredText"
     AND m."EngineVersion" != v_version;
  IF v_rebaselined > 0 THEN
    RAISE NOTICE '  Re-baselined % recorded expression(s): they were recorded under a different PostgreSQL version, and their stored canonical text is now refreshed for %. No object was changed.', v_rebaselined, v_version;
  END IF;

  INSERT INTO "SchemaSmith"."ExpressionMap" AS em
    ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot", "AuthoredText", "CanonicalText",
     "PlatformName", "EngineVersion", "CompatLevel", "UpdatedUtc")
  SELECT d."ObjectSchema", d."ObjectTable", d."ObjectKind", d."ObjectName", d."Slot", d."AuthoredText",
         d."CanonicalText", 'PostgreSQL', v_version, NULL, now()
    FROM temp_expression_map_declared d
  ON CONFLICT ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot")
  DO UPDATE SET "AuthoredText" = EXCLUDED."AuthoredText",
                "CanonicalText" = EXCLUDED."CanonicalText",
                "EngineVersion" = EXCLUDED."EngineVersion",
                "UpdatedUtc" = now()
   WHERE em."AuthoredText" != EXCLUDED."AuthoredText"
      OR em."CanonicalText" != EXCLUDED."CanonicalText"
      OR em."EngineVersion" != EXCLUDED."EngineVersion";

  DROP TABLE IF EXISTS temp_expression_map_declared;
END;
$$;
