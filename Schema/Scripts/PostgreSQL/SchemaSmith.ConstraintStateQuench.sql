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
-- OnlineApply (OFF, PREFER, REQUIRE; a table's own value over it): on an existing table a validation runs after this
-- procedure (DeferOnlineWork), alone, so it scans under VALIDATE's own weaker lock rather than inside this transaction.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ConstraintStateQuench"(p_WhatIf BOOLEAN, p_OnlineApply TEXT = 'OFF')
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
        ON con.conrelid = to_regclass("SchemaSmith"."QuoteName"(d."TableSchema") || '.' || "SchemaSmith"."QuoteName"(d."TableName"))
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
                  'ALTER TABLE ' || "SchemaSmith"."QuoteName"(s."TableSchema") || '.' || "SchemaSmith"."QuoteName"(s."TableName") || ' ALTER CONSTRAINT ' || "SchemaSmith"."QuoteName"(s."Name") || ' NOT ENFORCED;'
                WHEN s."ObjectType" = 'foreignKey' AND s."Enforced" AND NOT s."LiveEnforced" THEN
                  'RAISE NOTICE ''  Enforcing ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''';' || CHR(10) ||
                  'ALTER TABLE ' || "SchemaSmith"."QuoteName"(s."TableSchema") || '.' || "SchemaSmith"."QuoteName"(s."TableName") || ' ALTER CONSTRAINT ' || "SchemaSmith"."QuoteName"(s."Name") || ' ENFORCED;'
                WHEN v."Online" AND NOT p_WhatIf THEN
                  'RAISE NOTICE ''  Validating ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ' (online, after this pass)'';' || CHR(10) ||
                  'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Validating constraint ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name") || ', ' ||
                  quote_literal(v."Validate") || ', NULL, ' || quote_literal(v."Audit") || ');'
                ELSE
                  'RAISE NOTICE ''  Validating ' || s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || CASE WHEN v."Online" THEN ' (online, after this pass)' ELSE '' END || ''';' || CHR(10) ||
                  v."Validate"
           END ||
           CASE WHEN v."Online" AND NOT p_WhatIf THEN ''
                WHEN v."Online" THEN CHR(10) || v."Audit"
                ELSE CHR(10) || 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || s."ObjectType" || ''', ''' ||
                     s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''', ''modified'');' END, CHR(10))
    INTO sql_script
    FROM temp_constraint_states s
    -- Only the validation arm is deferred; enforcing or not enforcing a key changes no rows' lock.
    CROSS JOIN LATERAL (SELECT NOT ((s."ObjectType" = 'foreignKey' AND NOT s."Enforced" AND s."LiveEnforced" AND v_pg18)
                                    OR (s."ObjectType" = 'foreignKey' AND s."Enforced" AND NOT s."LiveEnforced"))
                               AND "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, s."TableSchema", s."TableName", 'check') = 'online' AS "Online",
                               'ALTER TABLE ' || "SchemaSmith"."QuoteName"(s."TableSchema") || '.' || "SchemaSmith"."QuoteName"(s."TableName") ||
                               ' VALIDATE CONSTRAINT ' || "SchemaSmith"."QuoteName"(s."Name") || ';' AS "Validate",
                               'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''' || s."ObjectType" || ''', ''' ||
                               s."TableSchema" || '.' || s."TableName" || '.' || s."Name" || ''', ''modified'', ''online'');' AS "Audit") v
    WHERE (s."ObjectType" = 'foreignKey' AND NOT s."Enforced" AND s."LiveEnforced" AND v_pg18)
       OR (s."ObjectType" = 'foreignKey' AND s."Enforced" AND NOT s."LiveEnforced")
       OR (s."Enforced" AND s."Validated" AND s."LiveEnforced" AND NOT s."LiveValidated");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  DROP TABLE IF EXISTS temp_constraint_states;
END $$;
