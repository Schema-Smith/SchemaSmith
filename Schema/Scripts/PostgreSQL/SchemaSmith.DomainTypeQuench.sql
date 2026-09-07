-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."DomainTypeQuench"(
    p_ProductName VARCHAR(50),
    p_DomainTypes TEXT,
    p_WhatIf BOOLEAN DEFAULT FALSE)
    LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT;
  bad RECORD;
BEGIN
  -- Converges declared domain types.
  --
  -- WHY THIS EXISTS. A domain has STORAGE in the sense that matters: real columns are typed by it. That is
  -- the test for promoting a scripted object -- a scripted object re-runs unconditionally on every deploy,
  -- which is cheap for a procedure and is not cheap for something columns depend on.
  --
  -- AND THE SCRIPTED FORM HERE CANNOT BE MADE IDEMPOTENT. There is no CREATE OR REPLACE DOMAIN, so a
  -- scripted domain is a guarded CREATE DOMAIN, and once the domain exists that guard skips. Verified live:
  -- re-running a guarded create carrying CHECK (VALUE > 100) left the domain with its original
  -- CHECK (VALUE > 0), silently, reporting success. Same trap the enum promotion closed.
  --
  -- WHAT CONVERGES, AND WHAT IS REFUSED. Unlike an enum, almost everything here is alterable in place
  -- WITHOUT dropping the domain or touching a dependent column -- constraints, default, NOT NULL. The base
  -- type is the exception: there is no ALTER DOMAIN ... TYPE at all (a syntax error, verified), so changing
  -- it would mean dropping the domain and every column using it. That is refused by name.
  --
  -- Dropping a CHECK is safe in a way dropping an enum value is not: it removes a validation rule,
  -- destroys no data, and cascades to nothing. That asymmetry is the entire reason this type converges
  -- where the enum reports.
  DROP TABLE IF EXISTS temp_domain_types;
  CREATE TEMPORARY TABLE temp_domain_types AS
  WITH src(arr) AS (VALUES(p_DomainTypes::JSON))
  SELECT COALESCE(elem ->> 'Schema', 'public') AS "Schema",
         elem ->> 'Name' AS "Name",
         elem ->> 'DataType' AS "DataType",
         COALESCE((elem ->> 'NotNull')::BOOLEAN, false) AS "NotNull",
         elem ->> 'Default' AS "Default",
         COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
         elem -> 'CheckConstraints' AS "CheckConstraints"
    FROM src, JSON_ARRAY_ELEMENTS(arr) AS elem;

  -- Evaluate ShouldApplyExpression BEFORE the constraints are flattened below: drop domain types whose
  -- condition is false so a conditional variant is SKIPPED rather than silently always applied. Always
  -- executes, even under --WhatIf -- this filters the internal working set, it is not user-visible DDL.
  SELECT STRING_AGG('DELETE FROM temp_domain_types WHERE "Schema" = ''' || "Schema" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
    INTO sql_script
    FROM temp_domain_types
    WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

  -- The declared constraints, flattened once so every pass below reads the same working set.
  DROP TABLE IF EXISTS temp_domain_checks;
  CREATE TEMPORARY TABLE temp_domain_checks AS
  SELECT t."Schema", t."Name",
         c ->> 'Name' AS "ConstraintName",
         c ->> 'Expression' AS "Expression"
    FROM temp_domain_types t,
         JSON_ARRAY_ELEMENTS(COALESCE(t."CheckConstraints", '[]'::JSON)) AS c;

  -- Create the ones that do not exist yet, with their constraints inline: a domain created with its checks
  -- in one statement never exists in a half-declared state, which a follow-up ALTER pass would allow.
  RAISE NOTICE 'Add Missing Domain Types';
  SELECT STRING_AGG('RAISE NOTICE ''  Create domain type ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                    'CREATE DOMAIN "' || t."Schema" || '"."' || t."Name" || '" AS ' || t."DataType" ||
                    CASE WHEN t."Default" IS NOT NULL THEN ' DEFAULT ' || t."Default" ELSE '' END ||
                    CASE WHEN t."NotNull" THEN ' NOT NULL' ELSE '' END ||
                    COALESCE((SELECT STRING_AGG(' CONSTRAINT "' || ck."ConstraintName" || '" CHECK (' || ck."Expression" || ')', '')
                                FROM temp_domain_checks ck
                               WHERE ck."Schema" = t."Schema" AND ck."Name" = t."Name"), '') || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''domain type'', ''' || t."Schema" || '.' || t."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_domain_types t
   WHERE NOT EXISTS (SELECT 1 FROM pg_type ty
                       JOIN pg_namespace n ON n.oid = ty.typnamespace
                      WHERE ty.typtype = 'd' AND ty.typname = t."Name" AND n.nspname = t."Schema");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- REFUSE a base-type change before anything else is altered. There is no ALTER DOMAIN ... TYPE, so the
  -- only way to deliver this is to drop the domain -- which drops every column that uses it. Refusing by
  -- name is the same posture placement takes: never do the destructive thing because a string changed in
  -- a file. Checked AFTER the create pass so a brand-new domain never trips it.
  --
  -- format_type() renders the modifier too (character varying(20)), which is exactly what extraction
  -- emits, so a round-tripped package compares equal rather than looking like a type change.
  FOR bad IN
    SELECT t."Schema", t."Name", t."DataType" AS declared,
           FORMAT_TYPE(ty.typbasetype, ty.typtypmod) AS deployed
      FROM temp_domain_types t
      JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'd'
      JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
     WHERE LOWER(TRIM(t."DataType")) <> LOWER(FORMAT_TYPE(ty.typbasetype, ty.typtypmod))
  LOOP
    RAISE EXCEPTION 'Domain type %.% declares base type "%", but is currently deployed as "%". PostgreSQL has no ALTER DOMAIN ... TYPE -- changing it means dropping the domain and every column that uses it. Migrate it with a script, or correct the declared type to match.',
      bad."Schema", bad."Name", bad.declared, bad.deployed;
  END LOOP;

  -- NOT NULL and DEFAULT, each emitted only when it actually differs so an unchanged domain produces no
  -- statement at all. Both are in-place and touch no dependent column.
  RAISE NOTICE 'Fixup Modified Domain Types';
  SELECT STRING_AGG(stmt, CHR(10))
    INTO sql_script
    FROM (SELECT 'RAISE NOTICE ''  Altering domain type ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                 'ALTER DOMAIN "' || t."Schema" || '"."' || t."Name" || '" ' ||
                 CASE WHEN t."NotNull" THEN 'SET NOT NULL' ELSE 'DROP NOT NULL' END || ';' || CHR(10) ||
                 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                 'VALUES (pg_backend_pid(), ''domain type'', ''' || t."Schema" || '.' || t."Name" || ''', ''modified'');' AS stmt
            FROM temp_domain_types t
            JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'd'
            JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
           WHERE ty.typnotnull <> t."NotNull"
           UNION ALL
          -- A declared default replaces whatever is there; a cleared one drops it. Compared against
          -- pg_get_expr of the stored default, which is how the catalog renders it back.
          SELECT 'RAISE NOTICE ''  Altering domain type ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                 'ALTER DOMAIN "' || t."Schema" || '"."' || t."Name" || '" ' ||
                 CASE WHEN t."Default" IS NOT NULL THEN 'SET DEFAULT ' || t."Default" ELSE 'DROP DEFAULT' END || ';' || CHR(10) ||
                 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                 'VALUES (pg_backend_pid(), ''domain type'', ''' || t."Schema" || '.' || t."Name" || ''', ''modified'');'
            FROM temp_domain_types t
            JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'd'
            JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
           WHERE COALESCE(t."Default", '') <> COALESCE(PG_GET_EXPR(ty.typdefaultbin, 0), '')) s;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Constraints, converged as a set.
  --
  -- contype = 'c' IS LOAD-BEARING, NOT TIDINESS. PostgreSQL 17 reports a domain's NOT NULL as a
  -- pg_constraint row of its own (contype = 'n', named <domain>_not_null); PostgreSQL 12 -- the supported
  -- floor -- does not. Without this filter a domain read on 17 would see a phantom "check constraint" the
  -- package never declared, drop it on every deploy, and a package extracted there would carry a
  -- constraint that cannot be created anywhere. NOT NULL is read from pg_type.typnotnull above instead,
  -- which both versions report identically.
  RAISE NOTICE 'Add Missing Domain Constraints';
  SELECT STRING_AGG('RAISE NOTICE ''  Add constraint ' || ck."ConstraintName" || ' on domain type ' || ck."Schema" || '.' || ck."Name" || ''';' || CHR(10) ||
                    'ALTER DOMAIN "' || ck."Schema" || '"."' || ck."Name" || '" ADD CONSTRAINT "' || ck."ConstraintName" ||
                    '" CHECK (' || ck."Expression" || ');' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''domain constraint'', ''' || ck."Schema" || '.' || ck."Name" || '.' || ck."ConstraintName" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_domain_checks ck
    JOIN pg_type ty ON ty.typname = ck."Name" AND ty.typtype = 'd'
    JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = ck."Schema"
   WHERE NOT EXISTS (SELECT 1 FROM pg_constraint c
                      WHERE c.contypid = ty.oid AND c.contype = 'c' AND c.conname = ck."ConstraintName");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- A constraint the package no longer declares is DROPPED, which is the one place this type differs from
  -- the enum -- and safely so: it removes a rule, not data, and nothing depends on it.
  RAISE NOTICE 'Drop Domain Constraints Removed From Product';
  SELECT STRING_AGG('RAISE NOTICE ''  Drop constraint ' || c.conname || ' from domain type ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                    'ALTER DOMAIN "' || t."Schema" || '"."' || t."Name" || '" DROP CONSTRAINT "' || c.conname || '";' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''domain constraint'', ''' || t."Schema" || '.' || t."Name" || '.' || c.conname || ''', ''dropped'');', CHR(10))
    INTO sql_script
    FROM temp_domain_types t
    JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'd'
    JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
    JOIN pg_constraint c ON c.contypid = ty.oid AND c.contype = 'c'
   WHERE NOT EXISTS (SELECT 1 FROM temp_domain_checks ck
                      WHERE ck."Schema" = t."Schema" AND ck."Name" = t."Name" AND ck."ConstraintName" = c.conname);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
END $$;
