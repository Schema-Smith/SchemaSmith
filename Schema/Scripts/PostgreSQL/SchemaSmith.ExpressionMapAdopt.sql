-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- #508, PostgreSQL. The expression map only knows what SchemaSmith applied, so a database another tool built -- or a
-- declaration reworded without changing its meaning -- has every expression whose stored text differs from the declared
-- text re-applied once: a CHECK re-validated, a generated column dropped and re-added, a partial index rebuilt. Before
-- that happens, the DECLARED expression is applied to an empty copy of the table; if PostgreSQL stores it as exactly
-- the live object's text, the live object IS that expression, and the map row the recorder would write is written now.
-- Anything uncertain -- a different rendering, a probe error -- falls back to applying, as before. It runs before any
-- table or column is created, so only objects that already exist are candidates.
--
-- The copy is a temporary table (LIKE copies the columns and nothing that would collide), inside a block that always
-- ends by raising, so the copy and everything done to it roll back. That is also why it runs under WhatIf: the rows it
-- writes then are listed in temp_expression_map_adopt_undo and put back by ExpressionMapRecord at the end of the run.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ExpressionMapAdopt"(p_WhatIf BOOLEAN DEFAULT FALSE)
LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql.
  SET jit = 'off'
AS $$
DECLARE
  v_version TEXT := current_setting('server_version');
  v_rendered TEXT;
  v_adopted INTEGER := 0;
  r RECORD;
BEGIN
  IF to_regclass('pg_temp.temp_columns') IS NULL AND to_regclass('pg_temp.temp_indexes') IS NULL THEN RETURN; END IF;

  CREATE TEMPORARY TABLE IF NOT EXISTS temp_expression_map_adopt_undo (
    "ObjectSchema" VARCHAR(256) NOT NULL, "ObjectTable" VARCHAR(256) NOT NULL, "ObjectKind" VARCHAR(32) NOT NULL,
    "ObjectName" VARCHAR(256) NOT NULL, "Slot" VARCHAR(32) NOT NULL, "WasPresent" BOOLEAN NOT NULL,
    "AuthoredText" TEXT, "CanonicalText" TEXT, "EngineVersion" TEXT, "UpdatedUtc" TIMESTAMP);
  CREATE TEMPORARY TABLE IF NOT EXISTS temp_expression_map_candidates (
    "ObjectSchema" VARCHAR(256) NOT NULL, "ObjectTable" VARCHAR(256) NOT NULL, "ObjectKind" VARCHAR(32) NOT NULL,
    "ObjectName" VARCHAR(256) NOT NULL, "Slot" VARCHAR(32) NOT NULL,
    "AuthoredText" TEXT NOT NULL, "LiveText" TEXT NOT NULL,
    -- The DDL the engine would run against the copy ({probe}), and the read that returns what it stored.
    "Render" TEXT NOT NULL, "ReadBack" TEXT NOT NULL);
  TRUNCATE temp_expression_map_candidates;

  -- Each block takes the live text exactly as ExpressionMapRecord reads it, and only where the compare that follows
  -- would apply: no row vouches for it, and the texts differ.
  IF to_regclass('pg_temp.temp_columns') IS NOT NULL AND to_regclass('pg_temp.temp_checks') IS NOT NULL THEN
    INSERT INTO temp_expression_map_candidates
      SELECT c."TableSchema", c."TableName", 'CHECK', c."Name", 'expression', c."Expression", "SchemaSmith"."CheckPredicate"(con.oid),
             'ALTER TABLE {probe} ADD CHECK (' || c."Expression" || ')',
             'SELECT "SchemaSmith"."CheckPredicate"(oid) FROM pg_catalog.pg_constraint WHERE conrelid = ''pg_temp.schemasmith_probe''::regclass AND contype = ''c'''
        FROM temp_checks c
        JOIN pg_catalog.pg_constraint con
          ON con.conrelid = to_regclass("SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName")) AND con.conname = c."Name" AND con.contype = 'c'
       WHERE COALESCE(c."Expression", '') != ''
         AND c."Expression" != "SchemaSmith"."CheckPredicate"(con.oid)
         AND NOT "SchemaSmith"."ExpressionMapUnchanged"(c."TableSchema", c."TableName", 'CHECK', c."Name", 'expression', c."Expression", "SchemaSmith"."CheckPredicate"(con.oid));

    INSERT INTO temp_expression_map_candidates
      SELECT col."TableSchema", col."TableName", 'CHECK', con.conname, 'expression', col."CheckExpression", "SchemaSmith"."CheckPredicate"(con.oid),
             'ALTER TABLE {probe} ADD CHECK (' || col."CheckExpression" || ')',
             'SELECT "SchemaSmith"."CheckPredicate"(oid) FROM pg_catalog.pg_constraint WHERE conrelid = ''pg_temp.schemasmith_probe''::regclass AND contype = ''c'''
        FROM temp_columns col
        JOIN pg_catalog.pg_constraint con
          ON con.conrelid = to_regclass("SchemaSmith"."QuoteName"(col."TableSchema") || '.' || "SchemaSmith"."QuoteName"(col."TableName"))
         AND con.conname = 'CK_' || col."TableName" || '_' || col."Name" AND con.contype = 'c'
       WHERE COALESCE(col."CheckExpression", '') != ''
         AND col."CheckExpression" != "SchemaSmith"."CheckPredicate"(con.oid)
         AND NOT "SchemaSmith"."ExpressionMapUnchanged"(col."TableSchema", col."TableName", 'CHECK', con.conname, 'expression', col."CheckExpression", "SchemaSmith"."CheckPredicate"(con.oid))
         AND NOT EXISTS (SELECT 1 FROM temp_expression_map_candidates d
                          WHERE d."ObjectSchema" = col."TableSchema" AND d."ObjectTable" = col."TableName" AND d."ObjectName" = con.conname);

    -- Generated columns, added to the copy under another name: the copy already has the column, as a plain one.
    INSERT INTO temp_expression_map_candidates
      SELECT col."TableSchema", col."TableName", 'COLUMN', col."Name", 'generated', col."GenerationExpression", a.generation_expression,
             'ALTER TABLE {probe} ADD COLUMN schemasmith_probe_column ' || col."DataType" || ' GENERATED ALWAYS AS (' || col."GenerationExpression" || ') STORED',
             'SELECT pg_catalog.pg_get_expr(d.adbin, d.adrelid) FROM pg_catalog.pg_attrdef d JOIN pg_catalog.pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum WHERE d.adrelid = ''pg_temp.schemasmith_probe''::regclass AND a.attname = ''schemasmith_probe_column'''
        FROM temp_columns col
        JOIN information_schema.columns a
          ON a.table_schema = col."TableSchema" AND a.table_name = col."TableName" AND a.column_name = col."Name"
       WHERE COALESCE(col."GenerationExpression", '') != '' AND a.generation_expression IS NOT NULL
         AND col."GenerationExpression" != a.generation_expression
         AND NOT "SchemaSmith"."ExpressionMapUnchanged"(col."TableSchema", col."TableName", 'COLUMN', col."Name", 'generated', col."GenerationExpression", a.generation_expression);
  END IF;

  -- Partial-index filters. The key does not shape the predicate, so the copy's index is keyed on a constant.
  IF to_regclass('pg_temp.temp_indexes') IS NOT NULL THEN
    INSERT INTO temp_expression_map_candidates
      SELECT i."TableSchema", i."TableName", 'INDEX', i."Name", 'filter', i."FilterExpression", PG_GET_EXPR(idx.indpred, idx.indrelid),
             'CREATE INDEX ON {probe} ((1)) WHERE ' || i."FilterExpression",
             'SELECT pg_catalog.pg_get_expr(indpred, indrelid) FROM pg_catalog.pg_index WHERE indrelid = ''pg_temp.schemasmith_probe''::regclass AND indpred IS NOT NULL'
        FROM temp_indexes i
        JOIN pg_catalog.pg_index idx ON idx.indrelid = to_regclass("SchemaSmith"."QuoteName"(i."TableSchema") || '.' || "SchemaSmith"."QuoteName"(i."TableName"))
        JOIN pg_catalog.pg_class ic ON ic.oid = idx.indexrelid AND ic.relname = i."Name" AND ic.relkind IN ('i', 'I')
       WHERE COALESCE(i."FilterExpression", '') != '' AND idx.indpred IS NOT NULL
         AND i."FilterExpression" != PG_GET_EXPR(idx.indpred, idx.indrelid)
         AND NOT "SchemaSmith"."ExpressionMapUnchanged"(i."TableSchema", i."TableName", 'INDEX', i."Name", 'filter', i."FilterExpression", PG_GET_EXPR(idx.indpred, idx.indrelid));
  END IF;

  FOR r IN SELECT * FROM temp_expression_map_candidates LOOP
    v_rendered := NULL;
    BEGIN
      EXECUTE format('CREATE TEMPORARY TABLE schemasmith_probe (LIKE %I.%I)', r."ObjectSchema", r."ObjectTable");
      EXECUTE replace(r."Render", '{probe}', 'pg_temp.schemasmith_probe');
      EXECUTE r."ReadBack" INTO v_rendered;
      -- Always roll the copy back; the rendering survives in the variable.
      RAISE EXCEPTION USING ERRCODE = 'SSPRB';
    EXCEPTION
      WHEN SQLSTATE 'SSPRB' THEN NULL;
      WHEN OTHERS THEN v_rendered := NULL;
    END;

    -- Byte for byte: the C collation compares bytes whatever the database's collation is.
    CONTINUE WHEN v_rendered IS NULL OR v_rendered COLLATE "C" != r."LiveText" COLLATE "C";

    IF p_WhatIf THEN
      INSERT INTO temp_expression_map_adopt_undo
        SELECT r."ObjectSchema", r."ObjectTable", r."ObjectKind", r."ObjectName", r."Slot", m."Slot" IS NOT NULL,
               m."AuthoredText", m."CanonicalText", m."EngineVersion", m."UpdatedUtc"
          FROM (SELECT 1) one
          LEFT JOIN "SchemaSmith"."ExpressionMap" m
            ON m."ObjectSchema" = r."ObjectSchema" AND m."ObjectTable" = r."ObjectTable" AND m."ObjectKind" = r."ObjectKind"
           AND m."ObjectName" = r."ObjectName" AND m."Slot" = r."Slot"
         WHERE NOT EXISTS (SELECT 1 FROM temp_expression_map_adopt_undo u
                            WHERE u."ObjectSchema" = r."ObjectSchema" AND u."ObjectTable" = r."ObjectTable" AND u."ObjectKind" = r."ObjectKind"
                              AND u."ObjectName" = r."ObjectName" AND u."Slot" = r."Slot");
    END IF;

    INSERT INTO "SchemaSmith"."ExpressionMap" AS em
      ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot", "AuthoredText", "CanonicalText",
       "PlatformName", "EngineVersion", "CompatLevel", "UpdatedUtc")
    VALUES (r."ObjectSchema", r."ObjectTable", r."ObjectKind", r."ObjectName", r."Slot", r."AuthoredText", r."LiveText",
            'PostgreSQL', v_version, NULL, now())
    ON CONFLICT ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot")
    DO UPDATE SET "AuthoredText" = EXCLUDED."AuthoredText", "CanonicalText" = EXCLUDED."CanonicalText",
                  "EngineVersion" = EXCLUDED."EngineVersion", "UpdatedUtc" = now();
    v_adopted := v_adopted + 1;
  END LOOP;

  IF v_adopted > 0 THEN
    RAISE NOTICE '  Verified % expression(s) on a scratch copy of their table: the declared text renders to exactly what the database holds, so they are recorded rather than re-applied.', v_adopted;
  END IF;
  TRUNCATE temp_expression_map_candidates;
END;
$$;
