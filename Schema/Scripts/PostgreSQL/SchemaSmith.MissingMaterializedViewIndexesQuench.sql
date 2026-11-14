-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingMaterializedViewIndexesQuench"
  (p_WhatIf BOOLEAN = FALSE,
   p_UpdateFillFactor BOOLEAN = TRUE)
  LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
  r RECORD;
  v_keys TEXT;
  v_filter TEXT;
  v_probe_keys BOOLEAN;
  v_probe_filter BOOLEAN;
BEGIN
  -- Unsupported-feature policy: NULLS NOT DISTINCT requires PostgreSQL 15. Below it the effective
  -- column (coerced in MaterializedViewQuench) already omits the clause; the policy decides how to
  -- surface that — 'fail' aborts naming the offending materialized-view index(es), 'warn' (default)
  -- records an unsupportedDowngrade manifest row per declared-but-unsupported index.
  IF "SchemaSmith"."ServerVersionNum"() < 15 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_mv_indexes WHERE "NullsNotDistinctDeclared") THEN
      RAISE EXCEPTION 'NULLS NOT DISTINCT requires PostgreSQL 15 (detected major %); materialized view index(es): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("ViewSchema" || '.' || "ViewName" || '.' || "Name", ', ')
           FROM temp_mv_indexes WHERE "NullsNotDistinctDeclared");
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'NULLS NOT DISTINCT (PG15)',
               "ViewSchema" || '.' || "ViewName" || '.' || "Name", 'downgraded'
          FROM temp_mv_indexes
          WHERE "NullsNotDistinctDeclared";
    END IF;
  END IF;

  -- An index's keys and predicate through the expression map, verified here as domains are: PostgreSQL stores
  -- lower(name) on a varchar column as lower((name)::text) and amount > 0 as (amount > (0)::numeric). A pair the map
  -- vouches for, or that the declared index built on a scratch copy of the view (LIKE, always rolled back) stores as
  -- exactly the live text, is left alone. Keyed on the view.
  DROP TABLE IF EXISTS temp_mv_index_vouched;
  CREATE TEMPORARY TABLE temp_mv_index_vouched ("ViewSchema" TEXT, "ViewName" TEXT, "Name" TEXT, "Slot" TEXT);
  FOR r IN SELECT t."ViewSchema", t."ViewName", t."Name", COALESCE(NULLIF(t."AccessMethod", ''), 'btree') AS "AccessMethod",
                  t."IndexColumns", t."FilterExpression",
                  "SchemaSmith"."NormalizeIndexColumnList"(t."ViewSchema", t."ViewName", t."AccessMethod", t."IndexColumns") AS "DeclaredKeys",
                  "SchemaSmith"."IndexKeyList"(idx.indexrelid) AS "LiveKeys",
                  COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '') AS "LiveFilter"
             FROM temp_mv_indexes t
             JOIN pg_class c ON c.relname = t."ViewName" AND c.relkind = 'm'
                            AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
             JOIN pg_index idx ON idx.indrelid = c.oid
             JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name" LOOP
    -- Each slot that reads differently: vouched by the map, or left for the copy to decide.
    v_probe_keys := r."IndexColumns" LIKE '%(%' AND r."DeclaredKeys" != r."LiveKeys";
    IF v_probe_keys AND "SchemaSmith"."ExpressionMapUnchanged"(r."ViewSchema", r."ViewName", 'INDEX', r."Name", 'keys', r."DeclaredKeys", r."LiveKeys") THEN
      INSERT INTO temp_mv_index_vouched VALUES (r."ViewSchema", r."ViewName", r."Name", 'keys');
      v_probe_keys := FALSE;
    END IF;
    v_probe_filter := COALESCE(r."FilterExpression", '') != '' AND r."FilterExpression" != r."LiveFilter";
    IF v_probe_filter AND "SchemaSmith"."ExpressionMapUnchanged"(r."ViewSchema", r."ViewName", 'INDEX', r."Name", 'filter', r."FilterExpression", r."LiveFilter") THEN
      INSERT INTO temp_mv_index_vouched VALUES (r."ViewSchema", r."ViewName", r."Name", 'filter');
      v_probe_filter := FALSE;
    END IF;
    CONTINUE WHEN NOT (v_probe_keys OR v_probe_filter);
    v_keys := NULL;
    v_filter := NULL;
    BEGIN
      EXECUTE format('CREATE TEMPORARY TABLE schemasmith_probe (LIKE %I.%I)', r."ViewSchema", r."ViewName");
      EXECUTE 'CREATE INDEX ON pg_temp.schemasmith_probe USING ' || r."AccessMethod" || ' (' || "SchemaSmith"."QuoteIndexColumnList"(r."IndexColumns") || ')' ||
              CASE WHEN COALESCE(r."FilterExpression", '') != '' THEN ' WHERE ' || r."FilterExpression" ELSE '' END;
      SELECT "SchemaSmith"."IndexKeyList"(indexrelid), COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(indpred, indrelid)), '')
        INTO v_keys, v_filter
        FROM pg_catalog.pg_index WHERE indrelid = 'pg_temp.schemasmith_probe'::regclass;
      RAISE EXCEPTION USING ERRCODE = 'SSPRB';
    EXCEPTION
      WHEN SQLSTATE 'SSPRB' THEN NULL;
      WHEN OTHERS THEN v_keys := NULL; v_filter := NULL;
    END;
    IF v_probe_keys AND v_keys COLLATE "C" = r."LiveKeys" COLLATE "C" THEN
      INSERT INTO temp_mv_index_vouched VALUES (r."ViewSchema", r."ViewName", r."Name", 'keys');
      RAISE NOTICE '  Verified the keys of materialized view index %.%.% on a scratch copy.', r."ViewSchema", r."ViewName", r."Name";
    END IF;
    IF v_probe_filter AND v_filter COLLATE "C" = r."LiveFilter" COLLATE "C" THEN
      INSERT INTO temp_mv_index_vouched VALUES (r."ViewSchema", r."ViewName", r."Name", 'filter');
      RAISE NOTICE '  Verified the predicate of materialized view index %.%.% on a scratch copy.', r."ViewSchema", r."ViewName", r."Name";
    END IF;
  END LOOP;

  RAISE NOTICE 'Materialized View Indexes — Drop Changed';

  -- Drop indexes that have changed properties (will be recreated below). Built dynamically so the
  -- PG15-only pg_index.indnullsnotdistinct comparison is present only on servers that have the column
  -- — referencing it on an older server is a plan-time error even inside a never-taken branch. Below
  -- 15 the NND term is a constant FALSE, matching the declared value neutralised to false in
  -- MaterializedViewQuench (so an old target neither churns nor emits an unsupported clause).
  EXECUTE format($mv$
  SELECT STRING_AGG('RAISE NOTICE ''  Dropping changed materialized view index %%.%%'', ''' || REPLACE((n.nspname)::TEXT, '''', '''''') || ''', ''' || REPLACE((i.relname)::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'DROP INDEX IF EXISTS ' || "SchemaSmith"."QuoteName"(n.nspname) || '.' || "SchemaSmith"."QuoteName"(i.relname) || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''index'', ''' || REPLACE((n.nspname || '.' || c.relname || '.' || i.relname)::TEXT, '''', '''''') || ''', ''dropped'');', CHR(10))
    FROM temp_mv_indexes t
    JOIN pg_class c ON c.relname = t."ViewName"
                   AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
                   AND c.relkind = 'm'
    JOIN pg_index idx ON idx.indrelid = c.oid
    JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
    JOIN pg_namespace n ON n.oid = i.relnamespace
    WHERE (
      -- Unique changed
      idx.indisunique != t."Unique"
      -- Clustered changed
      OR idx.indisclustered != t."Clustered"
      -- Access method changed
      OR (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid) != t."AccessMethod"
      -- NullsNotDistinct changed (PG15+ column; constant FALSE below 15)
      OR %s
      -- Filter expression changed
      OR (COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '') != t."FilterExpression"
          AND NOT EXISTS (SELECT 1 FROM temp_mv_index_vouched v WHERE v."ViewSchema" = t."ViewSchema" AND v."ViewName" = t."ViewName" AND v."Name" = t."Name" AND v."Slot" = 'filter'))
      -- FillFactor changed (when UpdateFillFactor is true): the live side is the option when set, else the engine's
      -- default for the access method, which is what an undeclared FillFactor resolves to.
      OR (t."UpdateFillFactor" AND
          COALESCE((REGEXP_MATCH(ARRAY_TO_STRING(i.reloptions, ','), 'fillfactor=(\d+)'))[1]::int2,
                   "SchemaSmith"."IndexFillFactorDefault"((SELECT am.amname FROM pg_am am WHERE i.relam = am.oid))) IS DISTINCT FROM t."FillFactor")
      -- IndexColumns changed: both sides in the engine's canonical form (IndexKeyForms), operator class, COLLATE and
      -- parameters included, with the view in hand so a declared default operator class drops away.
      OR ("SchemaSmith"."IndexKeyList"(idx.indexrelid) != "SchemaSmith"."NormalizeIndexColumnList"(t."ViewSchema", t."ViewName", t."AccessMethod", t."IndexColumns")
          AND NOT EXISTS (SELECT 1 FROM temp_mv_index_vouched v WHERE v."ViewSchema" = t."ViewSchema" AND v."ViewName" = t."ViewName" AND v."Name" = t."Name" AND v."Slot" = 'keys'))
      -- Storage parameters changed: several cannot be ALTERed in place, so a differing set rebuilds, as on a table.
      OR COALESCE((SELECT STRING_AGG(opt, ',' ORDER BY opt) FROM UNNEST(i.reloptions) AS o(opt) WHERE opt NOT LIKE 'fillfactor=%%'), '') != t."StorageParameters"
      -- IncludeColumns changed
      OR COALESCE((SELECT STRING_AGG(a.attname, ',' ORDER BY u.idx)
                     FROM pg_attribute a
                     CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                     WHERE a.attrelid = idx.indrelid AND u.idx > idx.indnkeyatts AND a.attnum = u.element), '') != "SchemaSmith"."NormalizeIndexColumnList"(t."IncludeColumns")
    )
  $mv$, CASE WHEN (current_setting('server_version_num')::int / 10000) >= 15
             THEN 'idx.indnullsnotdistinct != t."NullsNotDistinct"'
             ELSE 'FALSE' END)
  INTO sql_script;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  RAISE NOTICE 'Materialized View Indexes — Drop Removed';

  -- Drop indexes that exist in DB but not in definitions (for views that still exist)
  SELECT STRING_AGG('RAISE NOTICE ''  Dropping removed materialized view index %.%'', ''' || REPLACE((n.nspname)::TEXT, '''', '''''') || ''', ''' || REPLACE((i.relname)::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'DROP INDEX IF EXISTS ' || "SchemaSmith"."QuoteName"(n.nspname) || '.' || "SchemaSmith"."QuoteName"(i.relname) || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''index'', ''' || REPLACE((n.nspname || '.' || c.relname || '.' || i.relname)::TEXT, '''', '''''') || ''', ''dropped'');', CHR(10))
    INTO sql_script
    FROM temp_materialized_views tv
    JOIN pg_class c ON c.relname = tv."Name"
                   AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = tv."Schema")
                   AND c.relkind = 'm'
    JOIN pg_index idx ON idx.indrelid = c.oid
    JOIN pg_class i ON i.oid = idx.indexrelid
    JOIN pg_namespace n ON n.oid = i.relnamespace
    WHERE NOT EXISTS (SELECT 1 FROM temp_mv_indexes t
                       WHERE t."ViewSchema" = tv."Schema" AND t."ViewName" = tv."Name" AND t."Name" = i.relname);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  RAISE NOTICE 'Materialized View Indexes — Create Missing';

  -- Create indexes that are defined but don't exist in DB
  SELECT STRING_AGG(
    'RAISE NOTICE ''  Creating materialized view index %.%.%'', ''' || REPLACE((t."ViewSchema")::TEXT, '''', '''''') || ''', ''' || REPLACE((t."ViewName")::TEXT, '''', '''''') || ''', ''' || REPLACE((t."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
    'CREATE' || CASE WHEN t."Unique" THEN ' UNIQUE' ELSE '' END
    || ' INDEX ' || "SchemaSmith"."QuoteName"(t."Name") || ' ON ' || "SchemaSmith"."QuoteName"(t."ViewSchema") || '.' || "SchemaSmith"."QuoteName"(t."ViewName")
    || CASE WHEN t."AccessMethod" != 'btree' THEN ' USING ' || t."AccessMethod" ELSE '' END
    || ' (' || "SchemaSmith"."QuoteIndexColumnList"(t."IndexColumns") || ')'
    || CASE WHEN NULLIF(t."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(t."IncludeColumns") || ')' ELSE '' END
    || CASE WHEN t."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END
    -- CREATE INDEX grammar order: WITH, then TABLESPACE, then WHERE. One WITH clause: fillfactor only when declared
    -- (and only for the access methods that take it) plus the storage parameters (IndexWithClause).
    || "SchemaSmith"."IndexWithClause"(t."FillFactorDeclared", t."FillFactor", t."AccessMethod", t."StorageParameters")
    || CASE WHEN COALESCE(t."Tablespace", '') <> '' THEN ' TABLESPACE ' || "SchemaSmith"."QuoteName"(t."Tablespace") ELSE '' END
    || CASE WHEN NULLIF(t."FilterExpression", '') IS NOT NULL THEN ' WHERE ' || t."FilterExpression" ELSE '' END
    || ';' || CHR(10)
    || 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''index'', ''' || REPLACE((t."ViewSchema" || '.' || t."ViewName" || '.' || t."Name")::TEXT, '''', '''''') || ''', ''created'');',
    CHR(10))
    INTO sql_script
    FROM temp_mv_indexes t
    JOIN pg_class c ON c.relname = t."ViewName"
                   AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
                   AND c.relkind = 'm'
    WHERE NOT EXISTS (SELECT 1 FROM pg_index idx
                       JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
                       WHERE idx.indrelid = c.oid)
       -- Under WhatIf a changed index was not really dropped above, but the real run re-creates it.
       OR (p_WhatIf AND EXISTS (SELECT 1 FROM "SchemaSmith"."ChangeAudit" a
                                 WHERE a."SessionId" = pg_backend_pid() AND a."ActionType" = 'wouldDrop' AND a."ObjectType" = 'index'
                                   AND a."ObjectName" = t."ViewSchema" || '.' || t."ViewName" || '.' || t."Name"));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  -- Cluster if specified
  SELECT STRING_AGG('ALTER INDEX ' || "SchemaSmith"."QuoteName"(t."ViewSchema") || '.' || "SchemaSmith"."QuoteName"(t."Name") || ' SET (fillfactor = ' || t."FillFactor" || ');'
    || CASE WHEN t."Clustered" THEN CHR(10) || 'CLUSTER ' || "SchemaSmith"."QuoteName"(t."ViewSchema") || '.' || "SchemaSmith"."QuoteName"(t."ViewName") || ' USING ' || "SchemaSmith"."QuoteName"(t."Name") || ';' ELSE '' END,
    CHR(10))
    INTO sql_script
    FROM temp_mv_indexes t
    WHERE t."Clustered"
      -- Positive gate on the access methods that accept fillfactor, as IndexWithClause.
      AND COALESCE(t."AccessMethod", 'btree') IN ('btree', 'gist', 'hash', 'spgist')
      AND EXISTS (SELECT 1 FROM pg_class c
                   JOIN pg_index idx ON idx.indrelid = c.oid
                   JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
                   WHERE c.relname = t."ViewName"
                     AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
                     AND NOT idx.indisclustered);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Record each index's expression keys and predicate against what PostgreSQL now stores. Not in a preview.
  IF NOT p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ExpressionMap" AS em
      ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot", "AuthoredText", "CanonicalText",
       "PlatformName", "EngineVersion", "CompatLevel", "UpdatedUtc")
    SELECT x."ViewSchema", x."ViewName", 'INDEX', x."Name", x."Slot", x."Authored", x."Canonical",
           'PostgreSQL', current_setting('server_version'), NULL, now()
      FROM (SELECT t."ViewSchema", t."ViewName", t."Name", 'keys' AS "Slot",
                   "SchemaSmith"."NormalizeIndexColumnList"(t."ViewSchema", t."ViewName", t."AccessMethod", t."IndexColumns") AS "Authored",
                   "SchemaSmith"."IndexKeyList"(idx.indexrelid) AS "Canonical"
              FROM temp_mv_indexes t
              JOIN pg_class c ON c.relname = t."ViewName" AND c.relkind = 'm'
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
              JOIN pg_index idx ON idx.indrelid = c.oid
              JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
             WHERE t."IndexColumns" LIKE '%(%'
            UNION ALL
            SELECT t."ViewSchema", t."ViewName", t."Name", 'filter', t."FilterExpression",
                   COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '')
              FROM temp_mv_indexes t
              JOIN pg_class c ON c.relname = t."ViewName" AND c.relkind = 'm'
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
              JOIN pg_index idx ON idx.indrelid = c.oid
              JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
             WHERE COALESCE(t."FilterExpression", '') != '') x
    ON CONFLICT ("ObjectSchema", "ObjectTable", "ObjectKind", "ObjectName", "Slot")
    DO UPDATE SET "AuthoredText" = EXCLUDED."AuthoredText", "CanonicalText" = EXCLUDED."CanonicalText",
                  "EngineVersion" = EXCLUDED."EngineVersion", "UpdatedUtc" = now()
      WHERE em."AuthoredText" IS DISTINCT FROM EXCLUDED."AuthoredText" OR em."CanonicalText" IS DISTINCT FROM EXCLUDED."CanonicalText"
         OR em."EngineVersion" IS DISTINCT FROM EXCLUDED."EngineVersion";
  END IF;
END $$;
