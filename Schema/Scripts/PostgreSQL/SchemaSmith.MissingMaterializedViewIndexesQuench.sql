-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingMaterializedViewIndexesQuench"
  (p_WhatIf BOOLEAN = FALSE,
   p_UpdateFillFactor BOOLEAN = TRUE)
  LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT = '';
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

  RAISE NOTICE 'Materialized View Indexes — Drop Changed';

  -- Drop indexes that have changed properties (will be recreated below). Built dynamically so the
  -- PG15-only pg_index.indnullsnotdistinct comparison is present only on servers that have the column
  -- — referencing it on an older server is a plan-time error even inside a never-taken branch. Below
  -- 15 the NND term is a constant FALSE, matching the declared value neutralised to false in
  -- MaterializedViewQuench (so an old target neither churns nor emits an unsupported clause).
  EXECUTE format($mv$
  SELECT STRING_AGG('RAISE NOTICE ''  Dropping changed materialized view index ' || n.nspname || '.' || i.relname || ''';' || CHR(10) ||
                    'DROP INDEX IF EXISTS "' || n.nspname || '"."' || i.relname || '";', CHR(10))
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
      OR COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '') != t."FilterExpression"
      -- FillFactor changed (when UpdateFillFactor is true)
      OR (t."UpdateFillFactor" AND
          COALESCE(NULLIF((REGEXP_MATCH(ARRAY_TO_STRING(i.reloptions, ','), 'fillfactor=(\d+)'))[1]::int, 0), 90) != t."FillFactor")
      -- IndexColumns changed (declared side normalised to the unquoted, default-free form read back here)
      OR (SELECT STRING_AGG(TRIM(BOTH '"' FROM PG_GET_INDEXDEF(idx.indexrelid, u.idx::int4, true)) ||
                            CASE WHEN (idx.indoption[u.idx-1] & 1) = 1 THEN ' DESC' || CASE WHEN (idx.indoption[u.idx-1] & 2) = 2 THEN '' ELSE ' NULLS LAST' END
                                 ELSE CASE WHEN (idx.indoption[u.idx-1] & 2) = 2 THEN ' NULLS FIRST' ELSE '' END
                                END, ',' ORDER BY u.idx)
            FROM UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
            WHERE u.idx <= idx.indnkeyatts) != "SchemaSmith"."NormalizeIndexColumnList"(t."IndexColumns")
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

  RAISE NOTICE 'Materialized View Indexes — Drop Removed';

  -- Drop indexes that exist in DB but not in definitions (for views that still exist)
  SELECT STRING_AGG('RAISE NOTICE ''  Dropping removed materialized view index ' || n.nspname || '.' || i.relname || ''';' || CHR(10) ||
                    'DROP INDEX IF EXISTS "' || n.nspname || '"."' || i.relname || '";', CHR(10))
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

  RAISE NOTICE 'Materialized View Indexes — Create Missing';

  -- Create indexes that are defined but don't exist in DB
  SELECT STRING_AGG(
    'RAISE NOTICE ''  Creating materialized view index ' || t."ViewSchema" || '.' || t."ViewName" || '.' || t."Name" || ''';' || CHR(10) ||
    'CREATE' || CASE WHEN t."Unique" THEN ' UNIQUE' ELSE '' END
    || ' INDEX "' || t."Name" || '" ON "' || t."ViewSchema" || '"."' || t."ViewName" || '"'
    || CASE WHEN t."AccessMethod" != 'btree' THEN ' USING ' || t."AccessMethod" ELSE '' END
    || ' (' || t."IndexColumns" || ')'
    || CASE WHEN NULLIF(t."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || t."IncludeColumns" || ')' ELSE '' END
    || CASE WHEN t."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END
    || CASE WHEN NULLIF(t."FilterExpression", '') IS NOT NULL THEN ' WHERE ' || t."FilterExpression" ELSE '' END
    -- Positive gate, not a deny-list: an extension AM (e.g. pgvector's hnsw/ivfflat) can't be
    -- enumerated in advance, so allow-listing the AMs verified to accept fillfactor fails safe
    -- (no clause) instead of failing loud (PostgreSQL's "unrecognized parameter" error).
    || CASE WHEN COALESCE(t."AccessMethod", 'btree') IN ('btree', 'gist', 'hash')
            THEN ' WITH (fillfactor = ' || t."FillFactor" || ')'
            ELSE '' END
    || ';',
    CHR(10))
    INTO sql_script
    FROM temp_mv_indexes t
    JOIN pg_class c ON c.relname = t."ViewName"
                   AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
                   AND c.relkind = 'm'
    WHERE NOT EXISTS (SELECT 1 FROM pg_index idx
                       JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
                       WHERE idx.indrelid = c.oid);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Cluster if specified
  SELECT STRING_AGG('ALTER INDEX "' || t."ViewSchema" || '"."' || t."Name" || '" SET (fillfactor = ' || t."FillFactor" || ');'
    || CASE WHEN t."Clustered" THEN CHR(10) || 'CLUSTER "' || t."ViewSchema" || '"."' || t."ViewName" || '" USING "' || t."Name" || '";' ELSE '' END,
    CHR(10))
    INTO sql_script
    FROM temp_mv_indexes t
    WHERE t."Clustered"
      -- Positive gate on AMs verified to accept fillfactor (see the create-path comment above).
      AND COALESCE(t."AccessMethod", 'btree') IN ('btree', 'gist', 'hash')
      AND EXISTS (SELECT 1 FROM pg_class c
                   JOIN pg_index idx ON idx.indrelid = c.oid
                   JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = t."Name"
                   WHERE c.relname = t."ViewName"
                     AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."ViewSchema")
                     AND NOT idx.indisclustered);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
END $$;
