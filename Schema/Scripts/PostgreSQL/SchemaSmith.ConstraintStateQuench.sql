-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The enforcement and validation state of every declared foreign key and check constraint, converged once the
-- constraint exists. On a foreign key, Enforced false stops enforcement (ALTER CONSTRAINT ... NOT ENFORCED, PostgreSQL
-- 18) and Enforced true over a NOT ENFORCED key enforces it, which checks the existing rows; PostgreSQL cannot alter a
-- CHECK constraint's enforceability, so for a check that is create-time state and ModifiedTableQuench converges it by
-- drop and re-create, as it does NoInherit. Validated true over a constraint created NOT VALID validates it (VALIDATE
-- CONSTRAINT), which fails when rows violate it -- the engine reporting the truth. Validated false over a validated
-- constraint changes nothing: PostgreSQL cannot un-validate. An omitted attribute is its default, so a package that
-- says nothing enforces and validates. Below 18, NOT ENFORCED follows UnsupportedFeaturePolicy: 'fail' aborts naming
-- the constraints, 'warn' records a downgraded manifest row per constraint and leaves it enforced.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ConstraintStateQuench"(p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT;
  v_pg18 BOOLEAN := "SchemaSmith"."ServerVersionNum"() >= 18;
BEGIN
  RAISE NOTICE 'Converge constraint enforcement and validation';
  DROP TABLE IF EXISTS temp_constraint_states;
  CREATE TEMPORARY TABLE temp_constraint_states AS
    SELECT d."ObjectType", d."TableSchema", d."TableName", d."Name", d."Enforced", d."Validated",
           "SchemaSmith"."ConstraintEnforced"(con.oid) AS "LiveEnforced",
           con.convalidated AS "LiveValidated"
      FROM (SELECT 'constraint' AS "ObjectType", "TableSchema", "TableName", "Name", "Enforced", "Validated" FROM temp_checks
            UNION ALL
            SELECT 'foreignKey', "TableSchema", "TableName", "Name", "Enforced", "Validated" FROM temp_fks) d
      JOIN pg_catalog.pg_constraint con
        ON con.conrelid = to_regclass('"' || d."TableSchema" || '"."' || d."TableName" || '"')
       AND con.conname = d."Name"
       AND con.contype IN ('c', 'f');

  IF NOT v_pg18 AND EXISTS (SELECT 1 FROM temp_constraint_states WHERE NOT "Enforced") THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail' THEN
      RAISE EXCEPTION 'NOT ENFORCED constraints require PostgreSQL 18 (detected major %); constraint(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_constraint_states WHERE NOT "Enforced");
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'NOT ENFORCED constraint (PG18)',
               "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
          FROM temp_constraint_states
          WHERE NOT "Enforced";
    END IF;
  END IF;

  SELECT STRING_AGG(
           CASE WHEN s."ObjectType" = 'foreignKey' AND NOT s."Enforced" AND s."LiveEnforced" AND v_pg18 THEN
                  'RAISE NOTICE ''  Not enforcing ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''';' || CHR(10) ||
                  'ALTER TABLE "' || s."TableSchema" || '"."' || s."TableName" || '" ALTER CONSTRAINT "' || s."Name" || '" NOT ENFORCED;'
                WHEN s."ObjectType" = 'foreignKey' AND s."Enforced" AND NOT s."LiveEnforced" THEN
                  'RAISE NOTICE ''  Enforcing ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''';' || CHR(10) ||
                  'ALTER TABLE "' || s."TableSchema" || '"."' || s."TableName" || '" ALTER CONSTRAINT "' || s."Name" || '" ENFORCED;'
                ELSE
                  'RAISE NOTICE ''  Validating ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''';' || CHR(10) ||
                  'ALTER TABLE "' || s."TableSchema" || '"."' || s."TableName" || '" VALIDATE CONSTRAINT "' || s."Name" || '";'
           END || CHR(10) ||
           'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || s."ObjectType" || ''', ''' ||
           s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_constraint_states s
    WHERE (s."ObjectType" = 'foreignKey' AND NOT s."Enforced" AND s."LiveEnforced" AND v_pg18)
       OR (s."ObjectType" = 'foreignKey' AND s."Enforced" AND NOT s."LiveEnforced")
       OR (s."Enforced" AND s."Validated" AND s."LiveEnforced" AND NOT s."LiveValidated");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  DROP TABLE IF EXISTS temp_constraint_states;
END $$;
