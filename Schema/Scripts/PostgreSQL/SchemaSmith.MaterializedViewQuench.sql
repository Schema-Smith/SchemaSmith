-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- ProductOwnership reads and writes are scoped to p_TemplateName, including the inline
-- view-removed-from-product detection, so one template's prune pass never considers another
-- template's materialized views.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."MaterializedViewQuench"
  (p_ProductName VARCHAR(50),
   p_ViewDefinitions TEXT,
   p_WhatIf BOOLEAN = FALSE,
   p_UpdateFillFactor BOOLEAN = TRUE,
   p_TemplateName VARCHAR(256) = '',
   p_SchemaName VARCHAR(256) = '')
  LANGUAGE plpgsql
AS $$
DECLARE
  view_json TEXT = CASE WHEN LEFT(p_ViewDefinitions, 1) = '[' THEN p_ViewDefinitions ELSE '[' || p_ViewDefinitions || ']' END;
  sql_script TEXT = '';
BEGIN
  -- Ensure temp_tables exists for FixupMaterializedViewOwnership
  -- (may not exist if product has no tables, IndexOnlyTableQuenches=true, or UpdateTables=false)
  CREATE TEMPORARY TABLE IF NOT EXISTS temp_tables ("Schema" TEXT, "Name" TEXT);

  -- Parse materialized view definitions into temp tables
  DROP TABLE IF EXISTS temp_materialized_views;
  CREATE TEMPORARY TABLE temp_materialized_views AS
  WITH my_views(arr) AS (VALUES(view_json::JSON))
  SELECT elem ->> 'Schema' AS "Schema",
         elem ->> 'Name' AS "Name",
         COALESCE(elem ->> 'Definition', '') AS "Definition",
         COALESCE((elem ->> 'WithData')::BOOLEAN, true) AS "WithData",
         COALESCE(elem ->> 'Tablespace', '') AS "Tablespace",
         COALESCE(elem ->> 'AccessMethod', 'heap') AS "AccessMethod",
         COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
         COALESCE(elem ->> 'VariantName', '') AS "VariantName"
  FROM my_views, JSON_ARRAY_ELEMENTS(arr) AS elem;

  -- Evaluate ShouldApplyExpression: remove views whose expression evaluates to false
  SELECT STRING_AGG('DELETE FROM temp_materialized_views WHERE "Schema" = ''' || "Schema" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
    INTO sql_script
    FROM temp_materialized_views
    WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

  -- Parse indexes into temp table
  DROP TABLE IF EXISTS temp_mv_indexes;
  CREATE TEMPORARY TABLE temp_mv_indexes AS
  WITH my_views(arr) AS (VALUES(view_json::JSON))
  SELECT elem ->> 'Schema' AS "ViewSchema",
         elem ->> 'Name' AS "ViewName",
         celem ->> 'Name' AS "Name",
         COALESCE((celem ->> 'Unique')::BOOLEAN, false) AS "Unique",
         COALESCE((celem ->> 'Clustered')::BOOLEAN, false) AS "Clustered",
         REGEXP_REPLACE(COALESCE(celem ->> 'IndexColumns', ''), '\s*,\s*', ',', 'g') AS "IndexColumns",
         COALESCE(celem ->> 'IncludeColumns', '') AS "IncludeColumns",
         COALESCE(celem ->> 'AccessMethod', 'btree') AS "AccessMethod",
         COALESCE(celem ->> 'FilterExpression', '') AS "FilterExpression",
         -- Below PG15 NULLS NOT DISTINCT does not exist: the effective column drives compare + emit
         -- (coerced false so an old target neither churns nor emits an unsupported clause); the raw
         -- declared value drives the unsupported-feature policy in MissingMaterializedViewIndexesQuench.
         CASE WHEN "SchemaSmith"."ServerVersionNum"() >= 15 THEN COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) ELSE false END AS "NullsNotDistinct",
         COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) AS "NullsNotDistinctDeclared",
         COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
         CASE WHEN p_UpdateFillFactor THEN true ELSE false END AS "UpdateFillFactor",
         COALESCE(NULLIF((celem ->> 'FillFactor')::INT2, 0), 90) AS "FillFactor"
    FROM my_views, JSON_ARRAY_ELEMENTS(arr) AS elem
    CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS(COALESCE((elem ->> 'Indexes')::JSON, '[]'::JSON)) AS celem(value);

  -- Evaluate index ShouldApplyExpression
  SELECT STRING_AGG('DELETE FROM temp_mv_indexes WHERE "ViewSchema" = ''' || "ViewSchema" || ''' AND "ViewName" = ''' || "ViewName" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
    INTO sql_script
    FROM temp_mv_indexes
    WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

  -- Validate ownership
  CALL "SchemaSmith"."ValidateMaterializedViewOwnership"(p_ProductName, p_WhatIf, p_TemplateName);
  -- (ValidateMaterializedViewOwnership does not own temp_product_ownership; the inline
  -- drop-detection below is what enforces per-template/per-schema scope.)

  -- Drop/recreate changed and removed materialized views
  RAISE NOTICE 'Materialized View Diff — Drop Changed/Removed';

  -- Drop indexes on views that will be dropped (changed definition or removed from product)
  sql_script = '';
  SELECT STRING_AGG('DROP INDEX IF EXISTS "' || n.nspname || '"."' || i.relname || '";', CHR(10))
    INTO sql_script
    -- Fence to this iteration's schema: pg_matviews.definition is pg_get_viewdef(oid), which OPENS
    -- the relation. Reading a *sibling* tenant's mat-view definition races with that sibling's
    -- concurrent DROP MATERIALIZED VIEW under parallel fan-out and errors XX000 "could not open
    -- relation with OID". The OFFSET 0 fence guarantees the schema filter runs before pg_get_viewdef.
    FROM (SELECT schemaname, matviewname, definition
            FROM pg_matviews
            WHERE p_SchemaName = '' OR schemaname = p_SchemaName
            OFFSET 0) mv
    JOIN pg_class c ON c.relname = mv.matviewname
                   AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = mv.schemaname)
                   AND c.relkind = 'm'
    JOIN pg_index idx ON idx.indrelid = c.oid
    JOIN pg_class i ON i.oid = idx.indexrelid
    JOIN pg_namespace n ON n.oid = i.relnamespace
    WHERE (
      -- View removed from product. Strict on template_name (legacy '' rows excluded).
      -- p_SchemaName non-empty (schema-template iteration) further restricts to the
      -- iterations schema so other tenants views are never dropped.
      EXISTS (SELECT 1 FROM "SchemaSmith"."ProductOwnership" po
               WHERE po."Schema" = mv.schemaname AND po."TableName" = mv.matviewname
                 AND po."IndexName" IS NULL AND po."ProductName" = p_ProductName
                 AND po.template_name = p_TemplateName
                 AND (p_SchemaName = '' OR po."Schema" = p_SchemaName))
      AND NOT EXISTS (SELECT 1 FROM temp_materialized_views t
                       WHERE t."Schema" = mv.schemaname AND t."Name" = mv.matviewname)
    ) OR (
      -- View definition changed (whitespace-collapsed, case-insensitive comparison)
      EXISTS (SELECT 1 FROM temp_materialized_views t
               WHERE t."Schema" = mv.schemaname AND t."Name" = mv.matviewname
                 AND UPPER(REGEXP_REPLACE(TRIM(RTRIM(RTRIM(t."Definition"), ';')), '\s+', ' ', 'g')) != UPPER(REGEXP_REPLACE(TRIM(RTRIM(RTRIM(mv.definition), ';')), '\s+', ' ', 'g'))
                 -- #242: PostgreSQL rewrites a matview body when it stores it (schema-qualifying, re-aliasing),
                 -- so a hand-authored body never matches and the view was rebuilt on every deploy -- which
                 -- re-runs its query. Ask what was actually applied before calling it a change.
                 AND NOT "SchemaSmith"."ExpressionMapUnchanged"(t."Schema", t."Name", 'MATVIEW', t."Name",
                       'definition', t."Definition", mv.definition))
    );
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Drop changed/removed views
  sql_script = '';
  SELECT STRING_AGG('RAISE NOTICE ''  Dropping materialized view ' || mv.schemaname || '.' || mv.matviewname || ''';' || CHR(10) ||
                    'DROP MATERIALIZED VIEW IF EXISTS "' || mv.schemaname || '"."' || mv.matviewname || '";', CHR(10))
    INTO sql_script
    -- Fence to this iteration's schema (see the note on the index-drop query above): keeps
    -- pg_get_viewdef (mv.definition) off sibling tenants' mat-views, which race with their
    -- concurrent DROP MATERIALIZED VIEW under parallel fan-out (XX000 could not open relation).
    FROM (SELECT schemaname, matviewname, definition
            FROM pg_matviews
            WHERE p_SchemaName = '' OR schemaname = p_SchemaName
            OFFSET 0) mv
    WHERE (
      -- View removed from product. Strict on template_name (legacy '' rows excluded).
      -- p_SchemaName non-empty (schema-template iteration) further restricts to the
      -- iterations schema so other tenants views are never dropped.
      EXISTS (SELECT 1 FROM "SchemaSmith"."ProductOwnership" po
               WHERE po."Schema" = mv.schemaname AND po."TableName" = mv.matviewname
                 AND po."IndexName" IS NULL AND po."ProductName" = p_ProductName
                 AND po.template_name = p_TemplateName
                 AND (p_SchemaName = '' OR po."Schema" = p_SchemaName))
      AND NOT EXISTS (SELECT 1 FROM temp_materialized_views t
                       WHERE t."Schema" = mv.schemaname AND t."Name" = mv.matviewname)
    ) OR (
      -- View definition changed
      EXISTS (SELECT 1 FROM temp_materialized_views t
               WHERE t."Schema" = mv.schemaname AND t."Name" = mv.matviewname
                 AND UPPER(REGEXP_REPLACE(TRIM(RTRIM(RTRIM(t."Definition"), ';')), '\s+', ' ', 'g')) != UPPER(REGEXP_REPLACE(TRIM(RTRIM(RTRIM(mv.definition), ';')), '\s+', ' ', 'g'))
                 -- #242: PostgreSQL rewrites a matview body when it stores it (schema-qualifying, re-aliasing),
                 -- so a hand-authored body never matches and the view was rebuilt on every deploy -- which
                 -- re-runs its query. Ask what was actually applied before calling it a change.
                 AND NOT "SchemaSmith"."ExpressionMapUnchanged"(t."Schema", t."Name", 'MATVIEW', t."Name",
                       'definition', t."Definition", mv.definition))
    );
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Create missing materialized views (new or just dropped because definition changed)
  RAISE NOTICE 'Materialized View Diff — Create Missing';
  sql_script = '';
  SELECT STRING_AGG(
    'RAISE NOTICE ''  Creating materialized view ' || t."Schema" || '.' || t."Name" || CASE WHEN COALESCE(t."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(t."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
    'CREATE MATERIALIZED VIEW "' || t."Schema" || '"."' || t."Name" || '"'
    || CASE WHEN NULLIF(t."AccessMethod", '') IS NOT NULL AND t."AccessMethod" != 'heap' THEN ' USING ' || t."AccessMethod" ELSE '' END
    || CASE WHEN NULLIF(t."Tablespace", '') IS NOT NULL THEN ' TABLESPACE ' || t."Tablespace" ELSE '' END
    || ' AS ' || RTRIM(RTRIM(t."Definition"), ';')
    || CASE WHEN t."WithData" THEN ' WITH DATA' ELSE ' WITH NO DATA' END
    || ';',
    CHR(10))
    INTO sql_script
    FROM temp_materialized_views t
    WHERE NOT EXISTS (SELECT 1 FROM pg_matviews mv
                       WHERE mv.schemaname = t."Schema" AND mv.matviewname = t."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #242: record what each body was applied with, after the create pass, so a view created moments ago is
  -- recorded on this run rather than rebuilding once more on the next.
  IF NOT p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ExpressionMap" AS em
      ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot", "AuthoredText", "CanonicalText",
       "PlatformName", "EngineVersion", "CompatLevel", "UpdatedUtc")
    SELECT t."Schema", t."Name", 'MATVIEW', t."Name", 'definition', t."Definition", mv.definition,
           'PostgreSQL', current_setting('server_version'), NULL, now()
      FROM temp_materialized_views t
      JOIN pg_matviews mv ON mv.schemaname = t."Schema" AND mv.matviewname = t."Name"
     WHERE COALESCE(t."Definition", '') != ''
    ON CONFLICT ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot")
    DO UPDATE SET "AuthoredText" = EXCLUDED."AuthoredText",
                  "CanonicalText" = EXCLUDED."CanonicalText",
                  "EngineVersion" = EXCLUDED."EngineVersion",
                  "UpdatedUtc" = now()
     WHERE em."AuthoredText" != EXCLUDED."AuthoredText"
        OR em."CanonicalText" != EXCLUDED."CanonicalText"
        OR em."EngineVersion" != EXCLUDED."EngineVersion";
  END IF;

  -- Handle indexes
  CALL "SchemaSmith"."MissingMaterializedViewIndexesQuench"(p_WhatIf, p_UpdateFillFactor);

  -- Fixup ownership
  CALL "SchemaSmith"."FixupMaterializedViewOwnership"(p_ProductName, p_WhatIf, p_TemplateName, p_SchemaName);
END $$;
