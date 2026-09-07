-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingIndexesAndConstraintsQuench"(p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT = '';
BEGIN
  -- Column existence / default checks below read pg_catalog directly rather than
  -- information_schema.columns. Under a correlated EXISTS over the per-iteration temp
  -- tables, the planner materialises the whole information_schema.columns view, which
  -- AccessShare-locks every table in the database and deadlocks parallel template
  -- iterations against siblings creating their own tables. pg_catalog scoped to the
  -- named schema+table locks nothing it doesn't need.

  -- #332: temp_existing_indexes is normally built by ModifiedTableQuench earlier in the
  -- same session. On a checkpoint-resumed run the ModifiedTables step is skipped, so the
  -- fresh session has no snapshot; rebuild it here when absent. Guarded so the normal path
  -- keeps the snapshot ModifiedTableQuench already built.
  IF to_regclass('pg_temp.temp_existing_indexes') IS NULL THEN
    CALL "SchemaSmith"."BuildExistingIndexesSnapshot"();
  END IF;

  -- Unsupported-feature policy: VIRTUAL generated columns require PostgreSQL 18. Below it the emit
  -- below skips the column entirely (STORED remains available and unaffected); 'fail' aborts naming
  -- the offending column(s), 'warn' (default) records a downgrade manifest row per declared-but-
  -- unsupported column. Same routing spine as the NULLS NOT DISTINCT / expression-statistics policies.
  IF "SchemaSmith"."ServerVersionNum"() < 18 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1
                     FROM temp_columns tc
                     WHERE tc."Virtual" AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
                       AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)) THEN
      RAISE EXCEPTION 'VIRTUAL generated columns require PostgreSQL 18 (detected major %); column(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG(tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", ', ')
           FROM temp_columns tc
           WHERE tc."Virtual" AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
             AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped));
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'VIRTUAL generated column (PG18)',
               tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", 'downgraded'
          FROM temp_columns tc
          WHERE tc."Virtual" AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
            AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped);
    END IF;
  END IF;

  RAISE NOTICE 'Add New Computed Columns';
  SELECT STRING_AGG('RAISE NOTICE ''  Add new computed columns to ' || tt."Schema" || '.' || tt."Name" || ' (' ||
                    (SELECT STRING_AGG(tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END, ', ')
                     FROM temp_columns tc
                     WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                       AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
                       AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)
                       AND NOT (tc."Virtual" AND "SchemaSmith"."ServerVersionNum"() < 18)) ||
                    ')'';' || CHR(10) ||
                    'ALTER TABLE "' || tt."Schema" || '"."' || tt."Name" || '" ' ||
                    (SELECT STRING_AGG('ADD COLUMN "' || tc."Name" || '" ' || tc."DataType" || ' GENERATED ' || tc."Generated" || ' AS (' || tc."GenerationExpression" || ') ' || CASE WHEN tc."Virtual" THEN 'VIRTUAL' ELSE 'STORED' END, ', ')
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
                         AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)
                         AND NOT (tc."Virtual" AND "SchemaSmith"."ServerVersionNum"() < 18)) || ';' || CHR(10) ||
                    -- Object-change audit (#243 E5): one row per computed column added (folded ALTER above).
                    COALESCE((SELECT STRING_AGG('INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''column'', ''' || tt."Schema" || '.' || tt."Name" || '.' || tc."Name" || ''', ''created'');', CHR(10))
                                FROM temp_columns tc
                                WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                                  AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
                                  AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)
                                  AND NOT (tc."Virtual" AND "SchemaSmith"."ServerVersionNum"() < 18)), ''), CHR(10))
    INTO sql_script
    FROM temp_tables tt
    WHERE EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
      AND EXISTS (SELECT 1
                    FROM temp_columns tc
                    WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                      AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
                      AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)
                      AND NOT (tc."Virtual" AND "SchemaSmith"."ServerVersionNum"() < 18));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded computed-column 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'column', tt."Schema" || '.' || tt."Name" || '.' || tc."Name", 'wouldCreate'
        FROM temp_tables tt
        JOIN temp_columns tc ON tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
        WHERE EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
          AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> ''
          AND NOT EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped)
          AND NOT (tc."Virtual" AND "SchemaSmith"."ServerVersionNum"() < 18);
  END IF;

  -- Unsupported-feature policy: NULLS NOT DISTINCT requires PostgreSQL 15. Below it the clause is
  -- omitted from the emit below (effective column coerced false in the parse); 'fail' aborts naming
  -- the offending index(es), 'warn' (default) records an unsupportedDowngrade manifest row per
  -- declared-but-unsupported index. Same routing as the --IndexOnly path (IndexOnlyQuench).
  IF "SchemaSmith"."ServerVersionNum"() < 15 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_indexes WHERE "NullsNotDistinctDeclared") THEN
      RAISE EXCEPTION 'NULLS NOT DISTINCT requires PostgreSQL 15 (detected major %); index(es): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_indexes WHERE "NullsNotDistinctDeclared");
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'NULLS NOT DISTINCT (PG15)',
               "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
          FROM temp_indexes
          WHERE "NullsNotDistinctDeclared";
    END IF;
  END IF;

  RAISE NOTICE 'Add Missing Indexes'; -- Includes Primary Keys and Unique Constraints
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing ' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'Constraint ' ELSE 'Index ' END || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || CASE WHEN COALESCE(ti."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(ti."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey"
                         THEN 'ALTER TABLE "' || ti."TableSchema" || '"."' || ti."TableName" || '" ADD CONSTRAINT "' || ti."Name" || '" ' ||
                              CASE WHEN ti."PrimaryKey" THEN 'PRIMARY KEY ' ELSE 'UNIQUE ' || CASE WHEN ti."NullsNotDistinct" THEN 'NULLS NOT DISTINCT ' ELSE '' END END ||
                              '(' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || ')' ||
                              -- Positive gate on the AMs verified to accept fillfactor, not a deny-list of ones
                              -- that don't: an extension AM (e.g. pgvector's hnsw/ivfflat) can't be enumerated in
                              -- advance, and a deny-list defaults an unknown AM into the clause, breaking CREATE
                              -- with PostgreSQL's own "unrecognized parameter" error. An allow-list defaults an
                              -- unknown AM OUT of the clause instead — no fillfactor tuning, but no hard failure.
                              CASE WHEN COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash')
                                   THEN ' WITH (fillfactor = ' || ti."FillFactor" || ')'
                                   ELSE '' END ||
                              -- USING INDEX TABLESPACE precedes DEFERRABLE per the table-constraint grammar
                              -- (verified live on 16). Emitted only when declared: unset means placement is
                              -- not managed, so the backing index follows default_tablespace as before.
                              CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' USING INDEX TABLESPACE "' || ti."Tablespace" || '"' ELSE '' END ||
                              CASE WHEN ti."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                              CASE WHEN ti."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';'
                         ELSE 'CREATE ' || CASE WHEN ti."Unique" THEN 'UNIQUE ' ELSE '' END || 'INDEX "' || ti."Name" || '" ON "' || ti."TableSchema" || '"."' || ti."TableName" || '" ' ||
                              'USING ' || COALESCE(ti."AccessMethod", 'btree') || ' ' ||
                              '(' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || ')' ||
                              CASE WHEN NULLIF(ti."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(ti."IncludeColumns") || ')' ELSE '' END ||
                              -- NULLS NOT DISTINCT belongs after INCLUDE and before WITH per the CREATE INDEX grammar.
                              CASE WHEN ti."Unique" AND ti."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END ||
                              -- One WITH clause: fillfactor (gated to the AMs that accept it) plus
                              -- StorageParameters (any AM -- a vector index's m / ef_construction / lists).
                              -- StorageParameters is already canonical key=value,key=value.
                              CASE
                                WHEN COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash') AND COALESCE(ti."StorageParameters", '') <> ''
                                     THEN ' WITH (fillfactor = ' || ti."FillFactor" || ', ' || ti."StorageParameters" || ') '
                                WHEN COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash')
                                     THEN ' WITH (fillfactor = ' || ti."FillFactor" || ') '
                                WHEN COALESCE(ti."StorageParameters", '') <> ''
                                     THEN ' WITH (' || ti."StorageParameters" || ') '
                                ELSE ' ' END ||
                              -- TABLESPACE follows WITH and precedes WHERE per the CREATE INDEX grammar
                              -- (verified live on 16).
                              CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' TABLESPACE "' || ti."Tablespace" || '"' ELSE '' END ||
                              CASE WHEN NULLIF(ti."FilterExpression", '') IS NOT NULL THEN ' WHERE ' || ti."FilterExpression" ELSE '' END || ';'
                         END || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_indexes ti
    WHERE NOT EXISTS (SELECT * 
                        FROM pg_index idx
                        JOIN pg_class tc ON tc.oid = idx.indrelid
                                        AND tc.relkind = 'r'
                                        AND tc.relname = ti."TableName"
                                        AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                        JOIN pg_class i ON i.oid = idx.indexrelid
                        WHERE i.relname = ti."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'index'/'constraint' 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END,
             ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name", 'wouldCreate'
        FROM temp_indexes ti
        WHERE NOT EXISTS (SELECT *
                            FROM pg_index idx
                            JOIN pg_class tc ON tc.oid = idx.indrelid
                                            AND tc.relkind = 'r'
                                            AND tc.relname = ti."TableName"
                                            AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            WHERE i.relname = ti."Name");
  END IF;

  RAISE NOTICE 'Fixup Table Cluster';
  SELECT STRING_AGG('RAISE NOTICE ''  Fixing up attributes for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                    'ALTER TABLE ' || '"' || t."Schema" || '"."' || t."Name" || '" ' ||
                    CASE WHEN new_clust."NewCluster" IS NOT NULL 
                         THEN 'CLUSTER ON ' || '"' || new_clust."NewCluster" || '"'
                         ELSE 'SET WITHOUT CLUSTER' END || ';', CHR(10))
    INTO sql_script
    FROM temp_tables t
    LEFT JOIN (SELECT ti."TableSchema", ti."TableName", ti."Name" AS "NewCluster"
                 FROM temp_indexes ti
                 WHERE ti."Clustered") AS new_clust ON new_clust."TableSchema" = t."Schema"
                                                   AND new_clust."TableName" = t."Name"
    LEFT JOIN (SELECT ei."TableSchema", ei."TableName", ei."IndexName" AS "OldCluster"
                 FROM temp_existing_indexes ei
                 WHERE ei."Clustered") AS old_clust ON old_clust."TableSchema" = t."Schema"
                                                   AND old_clust."TableName" = t."Name"
    WHERE COALESCE("NewCluster", '') != COALESCE("OldCluster", '');
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Unsupported-feature policy: expression statistics (CREATE STATISTICS on an expression, detected by a
  -- parenthesised StatisticsColumns) require PostgreSQL 14 — below it PG rejects them (0A000 "only simple
  -- column references are allowed in CREATE STATISTICS"). 'fail' aborts naming the offending statistic(s);
  -- 'warn' (default) records a downgrade manifest row and the emit below skips them. Same routing spine as
  -- the NULLS NOT DISTINCT policy above.
  IF "SchemaSmith"."ServerVersionNum"() < 14 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_statistics WHERE "StatisticsColumns" LIKE '%(%') THEN
      RAISE EXCEPTION 'Expression statistics require PostgreSQL 14 (detected major %); statistic(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_statistics WHERE "StatisticsColumns" LIKE '%(%');
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'expression statistics (PG14)',
               ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name", 'downgraded'
          FROM temp_statistics ts
          WHERE ts."StatisticsColumns" LIKE '%(%'
            AND NOT EXISTS (SELECT 1 FROM pg_statistic_ext ste JOIN pg_class rel ON rel.oid = ste.stxrelid
                              JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace AND nsp.nspname = ts."TableSchema" AND rel.relname = ts."TableName"
                              WHERE ste.stxname = ts."Name");
    END IF;
  END IF;

  RAISE NOTICE 'Add Missing Statistics';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing statistics ' || ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name" || CASE WHEN COALESCE(ts."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(ts."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'CREATE STATISTICS "' || ts."TableSchema" || '"."' || ts."Name" || '"' ||
                    CASE WHEN NULLIF(TRIM(ts."Kind"), '') IS NOT NULL THEN ' (' || ts."Kind" ||')' ELSE '' END ||
                    ' ON ' || "SchemaSmith"."QuoteIndexColumnList"(ts."StatisticsColumns") ||
                    ' FROM "' || ts."TableSchema" || '"."' || ts."TableName" || '";' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''statistic'', ''' || ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_statistics ts
    WHERE NOT EXISTS (SELECT 1
                        FROM pg_statistic_ext ste
                        JOIN pg_class rel ON rel.oid = ste.stxrelid
                        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                                             AND nsp.nspname = ts."TableSchema"
                                             AND rel.relname = ts."TableName"
                        WHERE ste.stxname = ts."Name")
      -- Skip expression statistics below PostgreSQL 14 (routed through the unsupported-feature policy above).
      AND NOT ("SchemaSmith"."ServerVersionNum"() < 14 AND ts."StatisticsColumns" LIKE '%(%');
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'statistic'/'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'statistic', ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name", 'wouldCreate'
        FROM temp_statistics ts
        WHERE NOT EXISTS (SELECT 1
                            FROM pg_statistic_ext ste
                            JOIN pg_class rel ON rel.oid = ste.stxrelid
                            JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                                                 AND nsp.nspname = ts."TableSchema"
                                                 AND rel.relname = ts."TableName"
                            WHERE ste.stxname = ts."Name")
          AND NOT ("SchemaSmith"."ServerVersionNum"() < 14 AND ts."StatisticsColumns" LIKE '%(%');
  END IF;

  RAISE NOTICE 'Add Missing Exclude Constraints';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing exclude constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  "' || tc."TableSchema" || '"."' || tc."TableName" || '" ADD CONSTRAINT "' || tc."Name" || '" EXCLUDE' || 
                    CASE WHEN NULLIF(TRIM(tc."AccessMethod"), '') IS NOT NULL THEN ' USING ' || tc."AccessMethod" ELSE '' END ||
                    ' (' || (SELECT STRING_AGG("SchemaSmith"."QuoteIndexColumnList"((celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                               FROM JSON_ARRAY_ELEMENTS(tc."ExcludeColumns"::JSON) AS celem) || ')' ||
                    CASE WHEN NULLIF(TRIM(tc."FilterExpression"), '') IS NOT NULL THEN ' WHERE (' || tc."FilterExpression" || ')' ELSE '' END ||
                    CASE WHEN tc."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                    CASE WHEN tc."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_excludes tc
    WHERE NOT EXISTS (SELECT 1
                        FROM pg_constraint con
                        JOIN pg_class rel ON rel.oid = con.conrelid
                        JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                             AND nsp.nspname = tc."TableSchema"
                                             AND rel.relname = tc."TableName"
                        WHERE con.contype = 'x'
                          AND con.conname = tc."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded exclude-constraint 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", 'wouldCreate'
        FROM temp_excludes tc
        WHERE NOT EXISTS (SELECT 1
                            FROM pg_constraint con
                            JOIN pg_class rel ON rel.oid = con.conrelid
                            JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                                 AND nsp.nspname = tc."TableSchema"
                                                 AND rel.relname = tc."TableName"
                            WHERE con.contype = 'x'
                              AND con.conname = tc."Name");
  END IF;

  -- Row-level security policies (#rls, gap item D1). Created when absent.
  --
  -- SCOPE, stated rather than implied: this converges the SET of policies -- a declared policy that does
  -- not exist is created, and (in ModifiedTableQuench) one that exists but is no longer declared is
  -- dropped. It does NOT detect a change to an existing policy's USING / WITH CHECK expression, because
  -- PostgreSQL stores those normalised and comparing them against the declared text is the same
  -- false-change problem the roadmap tracks separately. Rename the policy, or drop and re-add it, to
  -- change an expression today.
  RAISE NOTICE 'Add Missing Row Level Security Policies';
  SELECT STRING_AGG('CREATE POLICY ' || QUOTE_IDENT(tp."Name") ||
                    ' ON ' || QUOTE_IDENT(tp."TableSchema") || '.' || QUOTE_IDENT(tp."TableName") ||
                    ' AS ' || tp."Permissive" ||
                    ' FOR ' || tp."Command" ||
                    ' TO ' || tp."Roles" ||
                    CASE WHEN NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL
                         THEN ' USING (' || tp."UsingExpression" || ')' ELSE '' END ||
                    CASE WHEN NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL
                         THEN ' WITH CHECK (' || tp."WithCheckExpression" || ')' ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''policy'', ''' || tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_policies tp
    WHERE NOT EXISTS (SELECT 1 FROM pg_policies pol
                       WHERE pol.schemaname = tp."TableSchema"
                         AND pol.tablename = tp."TableName"
                         AND pol.policyname = tp."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'policy', tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name", 'wouldCreate'
        FROM temp_policies tp
        WHERE NOT EXISTS (SELECT 1 FROM pg_policies pol
                           WHERE pol.schemaname = tp."TableSchema"
                             AND pol.tablename = tp."TableName"
                             AND pol.policyname = tp."Name");
  END IF;

  -- Re-converge an EXISTING policy whose exact-comparable attributes drifted from the declaration:
  -- Permissive (PERMISSIVE/RESTRICTIVE), Command (ALL/SELECT/...) and the Roles set. These are real,
  -- security-relevant changes that previously no-op'd silently. PostgreSQL has no ALTER for them, so it is
  -- DROP + CREATE -- which also reapplies the declared USING / WITH CHECK expressions. An expression-ONLY
  -- change is still not detected (comparing normalised expression text is the separate false-change problem
  -- noted above); Roles is compared as a normalised, order-insensitive, lower-cased set.
  RAISE NOTICE 'Re-converge Changed Row Level Security Policies';
  SELECT STRING_AGG('DROP POLICY ' || QUOTE_IDENT(tp."Name") || ' ON ' || QUOTE_IDENT(tp."TableSchema") || '.' || QUOTE_IDENT(tp."TableName") || ';' || CHR(10) ||
                    'CREATE POLICY ' || QUOTE_IDENT(tp."Name") ||
                    ' ON ' || QUOTE_IDENT(tp."TableSchema") || '.' || QUOTE_IDENT(tp."TableName") ||
                    ' AS ' || tp."Permissive" ||
                    ' FOR ' || tp."Command" ||
                    ' TO ' || tp."Roles" ||
                    CASE WHEN NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL
                         THEN ' USING (' || tp."UsingExpression" || ')' ELSE '' END ||
                    CASE WHEN NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL
                         THEN ' WITH CHECK (' || tp."WithCheckExpression" || ')' ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''policy'', ''' || tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name" || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_policies tp
    JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
    WHERE UPPER(pol.permissive) <> tp."Permissive"
       OR UPPER(pol.cmd) <> tp."Command"
       OR (SELECT ARRAY(SELECT LOWER(TRIM(x)) FROM UNNEST(string_to_array(tp."Roles", ',')) AS x ORDER BY 1))
          <> (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'policy', tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name", 'wouldModify'
        FROM temp_policies tp
        JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
        WHERE UPPER(pol.permissive) <> tp."Permissive"
           OR UPPER(pol.cmd) <> tp."Command"
           OR (SELECT ARRAY(SELECT LOWER(TRIM(x)) FROM UNNEST(string_to_array(tp."Roles", ',')) AS x ORDER BY 1))
              <> (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1));
  END IF;

  -- A policy that is no longer declared is DROPPED, and deliberately without an opt-out flag: a stale
  -- policy is a live access-control rule, so leaving one behind is a security posture nobody declared.
  -- That is a stronger reason to drop than exists for an index or a statistic.
  RAISE NOTICE 'Drop Removed Row Level Security Policies';
  SELECT STRING_AGG('DROP POLICY ' || QUOTE_IDENT(pol.policyname) ||
                    ' ON ' || QUOTE_IDENT(pol.schemaname) || '.' || QUOTE_IDENT(pol.tablename) || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''policy'', ''' || pol.schemaname || '.' || pol.tablename || '.' || pol.policyname || ''', ''dropped'');', CHR(10))
    INTO sql_script
    FROM pg_policies pol
    JOIN temp_tables tt ON tt."Schema" = pol.schemaname AND tt."Name" = pol.tablename
    WHERE NOT EXISTS (SELECT 1 FROM temp_policies tp
                       WHERE tp."TableSchema" = pol.schemaname
                         AND tp."TableName" = pol.tablename
                         AND tp."Name" = pol.policyname);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'policy', pol.schemaname || '.' || pol.tablename || '.' || pol.policyname, 'wouldDrop'
        FROM pg_policies pol
        JOIN temp_tables tt ON tt."Schema" = pol.schemaname AND tt."Name" = pol.tablename
        WHERE NOT EXISTS (SELECT 1 FROM temp_policies tp
                           WHERE tp."TableSchema" = pol.schemaname
                             AND tp."TableName" = pol.tablename
                             AND tp."Name" = pol.policyname);
  END IF;

  RAISE NOTICE 'Add Missing Defaults';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing default for ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  "' || tc."TableSchema" || '"."' || tc."TableName" || '" ALTER COLUMN "' || tc."Name" || '" SET DEFAULT ' || tc."Default" ||';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ' (default)'', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_columns tc
    WHERE NULLIF(tc."Default", '') IS NOT NULL
      AND EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped AND NOT a.atthasdef);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded default-constraint 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ' (default)', 'wouldCreate'
        FROM temp_columns tc
        WHERE NULLIF(tc."Default", '') IS NOT NULL
          AND EXISTS (SELECT 1 FROM pg_attribute a JOIN pg_class rc ON rc.oid = a.attrelid JOIN pg_namespace nn ON nn.oid = rc.relnamespace WHERE nn.nspname = tc."TableSchema" AND rc.relname = tc."TableName" AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped AND NOT a.atthasdef);
  END IF;

  RAISE NOTICE 'Add Missing Check Constraints';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing check constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  "' || tc."TableSchema" || '"."' || tc."TableName" || '" ADD CONSTRAINT "' || tc."Name" || '" CHECK (' || tc."Expression" || ')' ||
                    CASE WHEN tc."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                    CASE WHEN tc."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_checks tc
    WHERE NOT EXISTS (SELECT 1 
                        FROM pg_constraint con
                        JOIN pg_class rel ON rel.oid = con.conrelid
                        JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                             AND nsp.nspname = tc."TableSchema"
                                             AND rel.relname = tc."TableName"
                        WHERE con.contype = 'c'
                          AND con.conname = tc."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded check-constraint 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", 'wouldCreate'
        FROM temp_checks tc
        WHERE NOT EXISTS (SELECT 1
                            FROM pg_constraint con
                            JOIN pg_class rel ON rel.oid = con.conrelid
                            JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                                 AND nsp.nspname = tc."TableSchema"
                                                 AND rel.relname = tc."TableName"
                            WHERE con.contype = 'c'
                              AND con.conname = tc."Name");
  END IF;

  -- Column-level checks get a deterministic name (CK_<table>_<column>) so create-idempotency
  -- and modify-detection (in ModifiedTableQuench) can both key on it.
  RAISE NOTICE 'Add Missing Column Check Constraints';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing column check constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  "' || tc."TableSchema" || '"."' || tc."TableName" || '" ADD CONSTRAINT "CK_' || tc."TableName" || '_' || tc."Name" || '" CHECK (' || tc."CheckExpression" || ');' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_columns tc
    WHERE NULLIF(tc."CheckExpression", '') IS NOT NULL
      AND NOT EXISTS (SELECT 1
                        FROM pg_constraint con
                        JOIN pg_class rel ON rel.oid = con.conrelid
                        JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                             AND nsp.nspname = tc."TableSchema"
                                             AND rel.relname = tc."TableName"
                        WHERE con.contype = 'c'
                          AND con.conname = 'CK_' || tc."TableName" || '_' || tc."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded column-check 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name", 'wouldCreate'
        FROM temp_columns tc
        WHERE NULLIF(tc."CheckExpression", '') IS NOT NULL
          AND NOT EXISTS (SELECT 1
                            FROM pg_constraint con
                            JOIN pg_class rel ON rel.oid = con.conrelid
                            JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                                 AND nsp.nspname = tc."TableSchema"
                                                 AND rel.relname = tc."TableName"
                            WHERE con.contype = 'c'
                              AND con.conname = 'CK_' || tc."TableName" || '_' || tc."Name");
  END IF;

END
$$;