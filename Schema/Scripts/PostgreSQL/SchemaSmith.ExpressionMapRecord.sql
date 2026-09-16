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
BEGIN
  IF p_WhatIf THEN RETURN; END IF;
  -- An index-only quench never builds the declared-column working set; nothing to record then.
  IF to_regclass('pg_temp.temp_columns') IS NULL THEN RETURN; END IF;

  CREATE TEMPORARY TABLE IF NOT EXISTS temp_expression_map_declared (
    "ObjectSchema" VARCHAR(256) NOT NULL, "ObjectTable" VARCHAR(256) NOT NULL, "ObjectKind" VARCHAR(32) NOT NULL,
    "ObjectName" VARCHAR(256) NOT NULL, "Slot" VARCHAR(32) NOT NULL,
    "AuthoredText" TEXT NOT NULL, "CanonicalText" TEXT NOT NULL);
  TRUNCATE temp_expression_map_declared;

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
