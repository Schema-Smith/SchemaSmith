-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- OnlineApply (OFF, PREFER, REQUIRE; a table's own value over it): on an existing table an index or key is built
-- CONCURRENTLY and a check is added NOT VALID and validated, both after this procedure (DeferOnlineWork).
CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingIndexesAndConstraintsQuench"(p_WhatIf BOOLEAN = FALSE, p_OnlineApply TEXT = 'OFF')
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
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
                    'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tt."Schema") || '.' || "SchemaSmith"."QuoteName"(tt."Name") || ' ' ||
                    -- Nullability belongs in the ADD: leaving it out built every generated column nullable, and a declared
                    -- NOT NULL then arrived one deploy late as a SET NOT NULL. PostgreSQL 18 accepts it on VIRTUAL too.
                    (SELECT STRING_AGG('ADD COLUMN ' || "SchemaSmith"."QuoteName"(tc."Name") || ' ' || tc."DataType" || ' GENERATED ' || tc."Generated" || ' AS (' || tc."GenerationExpression" || ') ' || CASE WHEN tc."Virtual" THEN 'VIRTUAL' ELSE 'STORED' END
                                       || CASE WHEN tc."Nullable" THEN '' ELSE ' NOT NULL' END, ', ')
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

  IF "SchemaSmith"."ServerVersionNum"() < 18 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_indexes WHERE "WithoutOverlapsDeclared") THEN
      RAISE EXCEPTION 'WITHOUT OVERLAPS requires PostgreSQL 18 (detected major %); key(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_indexes WHERE "WithoutOverlapsDeclared");
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'WITHOUT OVERLAPS (PG18)',
               "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
          FROM temp_indexes
          WHERE "WithoutOverlapsDeclared";
    END IF;
  END IF;

  RAISE NOTICE 'Add Missing Indexes'; -- Includes Primary Keys and Unique Constraints
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing ' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'Constraint ' ELSE 'Index ' END || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || CASE WHEN COALESCE(ti."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(ti."VariantName", '''', '''''') || ')' ELSE '' END ||
                    CASE f."Form" WHEN 'online' THEN ' (online, after this pass)'
                                  WHEN 'unavailable' THEN ' (online unavailable: ' || "SchemaSmith"."OnlineUnavailableReason"(ti."TableSchema", ti."TableName", k."Kind") || ')'
                                  ELSE '' END || ''';' || CHR(10) ||
                    CASE WHEN f."Form" = 'online' AND NOT p_WhatIf
                         THEN 'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Building ' || CASE WHEN k."Kind" = 'index' THEN 'Index ' ELSE 'Constraint ' END || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || ' concurrently') || ', ' ||
                              quote_literal(o."Statement") || ', ' || quote_literal("SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."Name")) || ', ' ||
                              quote_literal(o."FollowUp") || ');'
                         WHEN f."Form" = 'online' THEN o."Statement" || CHR(10) || o."FollowUp"
                         ELSE x."Statement" || CHR(10) ||
                              CASE WHEN f."Form" IS NULL
                                   THEN 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || ''', ''created'');'
                                   ELSE 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || ''', ''created'', ''' || f."Form" || ''');' END
                    END, CHR(10))
    INTO sql_script
    FROM temp_indexes ti
    CROSS JOIN LATERAL (SELECT CASE WHEN ti."WithoutOverlaps" THEN 'withoutOverlaps' WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END AS "Kind") k
    CROSS JOIN LATERAL (SELECT "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, ti."TableSchema", ti."TableName", k."Kind") AS "Form") f
    CROSS JOIN LATERAL (SELECT CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey"
                         THEN 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"(ti."Name") || ' ' ||
                              CASE WHEN ti."PrimaryKey" THEN 'PRIMARY KEY ' ELSE 'UNIQUE ' || CASE WHEN ti."NullsNotDistinct" THEN 'NULLS NOT DISTINCT ' ELSE '' END END ||
                              '(' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || CASE WHEN ti."WithoutOverlaps" THEN ' WITHOUT OVERLAPS' ELSE '' END || ')' ||
                              -- INCLUDE and the storage parameters, as the CREATE INDEX branch below emits them.
                              -- Left out, the deployed constraint never matched its declaration, and every deploy
                              -- dropped it CASCADE, taking its inbound foreign keys, and added it back (PG-031).
                              CASE WHEN NULLIF(ti."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(ti."IncludeColumns") || ')' ELSE '' END ||
                              -- fillfactor only when declared, and only for the access methods that take it (IndexWithClause).
                              "SchemaSmith"."IndexWithClause"(ti."FillFactorDeclared", ti."FillFactor", ti."AccessMethod", ti."StorageParameters") ||
                              -- USING INDEX TABLESPACE precedes DEFERRABLE per the table-constraint grammar
                              -- (verified live on 16). Emitted only when declared: unset means placement is
                              -- not managed, so the backing index follows default_tablespace as before.
                              CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' USING INDEX TABLESPACE ' || "SchemaSmith"."QuoteName"(ti."Tablespace") ELSE '' END ||
                              CASE WHEN ti."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                              CASE WHEN ti."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';'
                         ELSE 'CREATE ' || CASE WHEN ti."Unique" THEN 'UNIQUE ' ELSE '' END || 'INDEX ' || "SchemaSmith"."QuoteName"(ti."Name") || ' ON ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."TableName") || ' ' ||
                              'USING ' || COALESCE(ti."AccessMethod", 'btree') || ' ' ||
                              '(' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || ')' ||
                              CASE WHEN NULLIF(ti."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(ti."IncludeColumns") || ')' ELSE '' END ||
                              -- NULLS NOT DISTINCT belongs after INCLUDE and before WITH per the CREATE INDEX grammar.
                              CASE WHEN ti."Unique" AND ti."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END ||
                              -- One WITH clause: fillfactor only when declared (and only for the access methods that
                              -- take it) plus StorageParameters, any access method (IndexWithClause).
                              "SchemaSmith"."IndexWithClause"(ti."FillFactorDeclared", ti."FillFactor", ti."AccessMethod", ti."StorageParameters") || ' ' ||
                              -- TABLESPACE follows WITH and precedes WHERE per the CREATE INDEX grammar
                              -- (verified live on 16).
                              CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' TABLESPACE ' || "SchemaSmith"."QuoteName"(ti."Tablespace") ELSE '' END ||
                              CASE WHEN NULLIF(ti."FilterExpression", '') IS NOT NULL THEN ' WHERE ' || ti."FilterExpression" ELSE '' END || ';'
                         END AS "Statement") x
    -- The online forms. A key is built as its unique index and attached USING INDEX, which keeps the index's INCLUDE,
    -- storage parameters, tablespace and NULLS NOT DISTINCT, and takes DEFERRABLE on the attach.
    CROSS JOIN LATERAL (SELECT CASE WHEN k."Kind" = 'index'
                                    THEN 'CREATE ' || CASE WHEN ti."Unique" THEN 'UNIQUE ' ELSE '' END || 'INDEX CONCURRENTLY ' || SUBSTRING(x."Statement" FROM POSITION('INDEX ' IN x."Statement") + 6)
                                    ELSE 'CREATE UNIQUE INDEX CONCURRENTLY ' || "SchemaSmith"."QuoteName"(ti."Name") || ' ON ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."TableName") ||
                                         ' USING btree (' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || ')' ||
                                         CASE WHEN NULLIF(ti."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(ti."IncludeColumns") || ')' ELSE '' END ||
                                         CASE WHEN NOT ti."PrimaryKey" AND ti."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END ||
                                         "SchemaSmith"."IndexWithClause"(ti."FillFactorDeclared", ti."FillFactor", ti."AccessMethod", ti."StorageParameters") ||
                                         CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' TABLESPACE ' || "SchemaSmith"."QuoteName"(ti."Tablespace") ELSE '' END || ';'
                               END AS "Statement",
                               CASE WHEN k."Kind" = 'index' THEN ''
                                    ELSE 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"(ti."Name") ||
                                         CASE WHEN ti."PrimaryKey" THEN ' PRIMARY KEY' ELSE ' UNIQUE' END || ' USING INDEX ' || "SchemaSmith"."QuoteName"(ti."Name") ||
                                         CASE WHEN ti."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                                         CASE WHEN ti."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';' || CHR(10) END ||
                               'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''' ||
                               CASE WHEN k."Kind" = 'index' THEN 'index' ELSE 'constraint' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || ''', ''created'', ''online'');' AS "FollowUp") o
    WHERE NOT EXISTS (SELECT * 
                        FROM pg_index idx
                        JOIN pg_class tc ON tc.oid = idx.indrelid
                                        AND tc.relkind = 'r'
                                        AND tc.relname = ti."TableName"
                                        AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                        JOIN pg_class i ON i.oid = idx.indexrelid
                        WHERE i.relname = ti."Name")
       -- Under WhatIf a modified index was not really dropped, but the real run re-creates it.
       OR (p_WhatIf AND EXISTS (SELECT 1 FROM "SchemaSmith"."ChangeAudit" a
                                 WHERE a."SessionId" = pg_backend_pid() AND a."ActionType" = 'wouldDrop'
                                   AND a."ObjectType" IN ('index', 'constraint')
                                   AND a."ObjectName" = ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name"));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'index'/'constraint' 'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END,
             ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, ti."TableSchema", ti."TableName",
                                             CASE WHEN ti."WithoutOverlaps" THEN 'withoutOverlaps' WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'constraint' ELSE 'index' END)
        FROM temp_indexes ti
        WHERE NOT EXISTS (SELECT *
                            FROM pg_index idx
                            JOIN pg_class tc ON tc.oid = idx.indrelid
                                            AND tc.relkind = 'r'
                                            AND tc.relname = ti."TableName"
                                            AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                            JOIN pg_class i ON i.oid = idx.indexrelid
                            WHERE i.relname = ti."Name")
           -- Under WhatIf a modified index was not really dropped, but the real run re-creates it.
           OR (p_WhatIf AND EXISTS (SELECT 1 FROM "SchemaSmith"."ChangeAudit" a
                                     WHERE a."SessionId" = pg_backend_pid() AND a."ActionType" = 'wouldDrop'
                                       AND a."ObjectType" IN ('index', 'constraint')
                                       AND a."ObjectName" = ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name"));
  END IF;

  RAISE NOTICE 'Fixup Table Cluster';
  SELECT STRING_AGG('RAISE NOTICE ''  Fixing up attributes for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                    'ALTER TABLE ' || "SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name") || ' ' ||
                    CASE WHEN new_clust."NewCluster" IS NOT NULL 
                         THEN 'CLUSTER ON ' || "SchemaSmith"."QuoteName"(new_clust."NewCluster")
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
                    'CREATE STATISTICS ' || "SchemaSmith"."QuoteName"(COALESCE(ts."StatisticsSchema", ts."TableSchema")) || '.' || "SchemaSmith"."QuoteName"(ts."Name") ||
                    -- EXPRESSIONS is not a kind CREATE STATISTICS accepts; an extracted package carries it.
                    "SchemaSmith"."StatisticsKindClause"(ts."Kind") ||
                    ' ON ' || "SchemaSmith"."QuoteIndexColumnList"(ts."StatisticsColumns") ||
                    ' FROM ' || "SchemaSmith"."QuoteName"(ts."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ts."TableName") || ';' || CHR(10) ||
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

  CALL "SchemaSmith"."StatisticsTargetQuench"(p_WhatIf);

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
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing exclude constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END ||
                    CASE WHEN f."Form" IS NOT NULL THEN ' (online unavailable: ' || "SchemaSmith"."OnlineUnavailableReason"(tc."TableSchema", tc."TableName", 'exclusion') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"(tc."Name") || ' EXCLUDE' || 
                    CASE WHEN NULLIF(TRIM(tc."AccessMethod"), '') IS NOT NULL THEN ' USING ' || tc."AccessMethod" ELSE '' END ||
                    ' (' || (SELECT STRING_AGG("SchemaSmith"."QuoteIndexColumnList"((celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                               FROM JSON_ARRAY_ELEMENTS(tc."ExcludeColumns"::JSON) AS celem) || ')' ||
                    -- The backing index's extras, in the grammar's order: INCLUDE, WITH, USING INDEX TABLESPACE, WHERE.
                    CASE WHEN NULLIF(tc."IncludeColumns", '') IS NOT NULL THEN ' INCLUDE (' || "SchemaSmith"."QuoteColumnList"(tc."IncludeColumns") || ')' ELSE '' END ||
                    CASE WHEN NULLIF(tc."StorageParameters", '') IS NOT NULL THEN ' WITH (' || tc."StorageParameters" || ')' ELSE '' END ||
                    CASE WHEN NULLIF(tc."Tablespace", '') IS NOT NULL THEN ' USING INDEX TABLESPACE ' || "SchemaSmith"."QuoteName"(tc."Tablespace") ELSE '' END ||
                    CASE WHEN NULLIF(TRIM(tc."FilterExpression"), '') IS NOT NULL THEN ' WHERE (' || tc."FilterExpression" || ')' ELSE '' END ||
                    CASE WHEN tc."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                    CASE WHEN tc."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END || ';' || CHR(10) ||
                    CASE WHEN f."Form" IS NULL
                         THEN 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'');'
                         ELSE 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'', ''unavailable'');' END, CHR(10))
    INTO sql_script
    FROM temp_excludes tc
    CROSS JOIN LATERAL (SELECT "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'exclusion') AS "Form") f
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
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'exclusion')
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

  -- Expression convergence (#242). A change to an existing policy's USING or WITH CHECK text is applied with
  -- ALTER POLICY: non-destructive, so there is never an instant where the table has RLS on and the rule missing
  -- -- which is exactly what DROP + CREATE would open. It runs BEFORE the add-missing pass so a policy created on
  -- this run (which has no mapping row yet) is not ALTERed on the same run. A policy whose Permissive, Command,
  -- Roles, or clause presence changed is left to the DROP + CREATE pass below: ALTER cannot change those, and
  -- cannot remove a clause.
  --
  -- First deploy after upgrade: an existing policy has no mapping row, so the comparison falls back to text,
  -- and one authored in a form PostgreSQL rewrites is ALTERed once and then recorded. That is a one-time
  -- re-application of the SAME rule, not a behaviour change to it.
  RAISE NOTICE 'Alter Row Level Security Policies Whose Expressions Changed';
  SELECT STRING_AGG('RAISE NOTICE ''  Altering policy ' || tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name" || ''';' || CHR(10) ||
                    'ALTER POLICY ' || QUOTE_IDENT(tp."Name") ||
                    ' ON ' || QUOTE_IDENT(tp."TableSchema") || '.' || QUOTE_IDENT(tp."TableName") ||
                    CASE WHEN NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL
                         THEN ' USING (' || tp."UsingExpression" || ')' ELSE '' END ||
                    CASE WHEN NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL
                         THEN ' WITH CHECK (' || tp."WithCheckExpression" || ')' ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''policy'', ''' || tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name" || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_policies tp
    JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
   WHERE UPPER(pol.permissive) = tp."Permissive"
       AND UPPER(pol.cmd) = tp."Command"
       AND (SELECT ARRAY(SELECT LOWER(TRIM(x)) FROM UNNEST(string_to_array(tp."Roles", ',')) AS x ORDER BY 1))
           = (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1))
       AND (NULLIF(TRIM(tp."UsingExpression"), '') IS NULL) = (pol.qual IS NULL)
       AND (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NULL) = (pol.with_check IS NULL)
     AND ((NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL AND pol.qual IS NOT NULL
             AND "SchemaSmith"."StripParenWrapping"(pol.qual) <> TRIM(tp."UsingExpression")
             AND NOT "SchemaSmith"."ExpressionMapUnchanged"(tp."TableSchema", tp."TableName", 'POLICY', tp."Name",
                   'using', tp."UsingExpression", pol.qual))
         OR (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL AND pol.with_check IS NOT NULL
             AND "SchemaSmith"."StripParenWrapping"(pol.with_check) <> TRIM(tp."WithCheckExpression")
             AND NOT "SchemaSmith"."ExpressionMapUnchanged"(tp."TableSchema", tp."TableName", 'POLICY', tp."Name",
                   'check', tp."WithCheckExpression", pol.with_check)));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'policy', tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name", 'wouldModify'
        FROM temp_policies tp
        JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
       WHERE UPPER(pol.permissive) = tp."Permissive"
       AND UPPER(pol.cmd) = tp."Command"
       AND (SELECT ARRAY(SELECT LOWER(TRIM(x)) FROM UNNEST(string_to_array(tp."Roles", ',')) AS x ORDER BY 1))
           = (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1))
       AND (NULLIF(TRIM(tp."UsingExpression"), '') IS NULL) = (pol.qual IS NULL)
       AND (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NULL) = (pol.with_check IS NULL)
         AND ((NULLIF(TRIM(tp."UsingExpression"), '') IS NOT NULL AND pol.qual IS NOT NULL
             AND "SchemaSmith"."StripParenWrapping"(pol.qual) <> TRIM(tp."UsingExpression")
             AND NOT "SchemaSmith"."ExpressionMapUnchanged"(tp."TableSchema", tp."TableName", 'POLICY', tp."Name",
                   'using', tp."UsingExpression", pol.qual))
         OR (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NOT NULL AND pol.with_check IS NOT NULL
             AND "SchemaSmith"."StripParenWrapping"(pol.with_check) <> TRIM(tp."WithCheckExpression")
             AND NOT "SchemaSmith"."ExpressionMapUnchanged"(tp."TableSchema", tp."TableName", 'POLICY', tp."Name",
                   'check', tp."WithCheckExpression", pol.with_check)));
  END IF;

  -- Row-level security policies (#rls, gap item D1). Created when absent.
  --
  -- A declared policy that does not exist is created; one no longer declared is dropped; a changed Permissive,
  -- Command, Roles or clause presence is a DROP + CREATE; and a changed USING / WITH CHECK expression is an
  -- ALTER POLICY (the pass immediately above), compared through the expression map so PostgreSQL's rewrite of
  -- the text is not mistaken for a change.
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
  -- DROP + CREATE -- which also reapplies the declared USING / WITH CHECK expressions -- and so is adding or
  -- removing a clause. An expression-only change is the ALTER POLICY pass above. Roles is compared as a
  -- normalised, order-insensitive, lower-cased set.
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
          <> (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1))
       -- A clause added or removed: ALTER POLICY can set a clause but never remove one.
       OR (NULLIF(TRIM(tp."UsingExpression"), '') IS NULL) <> (pol.qual IS NULL)
       OR (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NULL) <> (pol.with_check IS NULL);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'policy', tp."TableSchema" || '.' || tp."TableName" || '.' || tp."Name", 'wouldModify'
        FROM temp_policies tp
        JOIN pg_policies pol ON pol.schemaname = tp."TableSchema" AND pol.tablename = tp."TableName" AND pol.policyname = tp."Name"
        WHERE UPPER(pol.permissive) <> tp."Permissive"
           OR UPPER(pol.cmd) <> tp."Command"
           OR (SELECT ARRAY(SELECT LOWER(TRIM(x)) FROM UNNEST(string_to_array(tp."Roles", ',')) AS x ORDER BY 1))
              <> (SELECT ARRAY(SELECT LOWER(r::TEXT) FROM UNNEST(pol.roles) AS r ORDER BY 1))
           OR (NULLIF(TRIM(tp."UsingExpression"), '') IS NULL) <> (pol.qual IS NULL)
           OR (NULLIF(TRIM(tp."WithCheckExpression"), '') IS NULL) <> (pol.with_check IS NULL);
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
                    'ALTER TABLE  ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(tc."Name") || ' SET DEFAULT ' || tc."Default" ||';' || CHR(10) ||
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

  -- Unsupported-feature policy: NOT ENFORCED requires PostgreSQL 18. Below it the clause is omitted here and in
  -- ForeignKeyQuench (the constraint deploys enforced); 'fail' aborts before anything is created, naming the
  -- constraints; 'warn' (default) is recorded by ConstraintStateQuench once the constraints exist, one downgraded
  -- manifest row each.
  IF "SchemaSmith"."ServerVersionNum"() < 18 AND "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
     AND (EXISTS (SELECT 1 FROM temp_checks WHERE NOT "Enforced") OR EXISTS (SELECT 1 FROM temp_fks WHERE NOT "Enforced")) THEN
    RAISE EXCEPTION 'NOT ENFORCED constraints require PostgreSQL 18 (detected major %); constraint(s): %',
      "SchemaSmith"."ServerVersionNum"(),
      (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
         FROM (SELECT "TableSchema", "TableName", "Name" FROM temp_checks WHERE NOT "Enforced"
               UNION ALL SELECT "TableSchema", "TableName", "Name" FROM temp_fks WHERE NOT "Enforced") x);
  END IF;

  RAISE NOTICE 'Add Missing Check Constraints';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing check constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END ||
                    CASE WHEN v."Online" THEN ' (online, validated after this pass)' ELSE '' END || ''';' || CHR(10) ||
                    -- NO INHERIT and NOT ENFORCED are part of the definition; NOT VALID is the ADD option that leaves the
                    -- existing rows unchecked. A NOT ENFORCED constraint is not validated by definition, so NOT VALID is
                    -- left off it. PostgreSQL rejects DEFERRABLE on a CHECK, so the deprecated keys are never emitted.
                    'ALTER TABLE  ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"(tc."Name") || ' CHECK (' || tc."Expression" || ')' ||
                    CASE WHEN tc."NoInherit" THEN ' NO INHERIT' ELSE '' END ||
                    CASE WHEN NOT tc."Enforced" AND "SchemaSmith"."ServerVersionNum"() >= 18 THEN ' NOT ENFORCED' ELSE '' END ||
                    CASE WHEN NOT tc."Validated" AND NOT (NOT tc."Enforced" AND "SchemaSmith"."ServerVersionNum"() >= 18) THEN ' NOT VALID'
                         WHEN v."Online" THEN ' NOT VALID' ELSE '' END || ';' || CHR(10) ||
                    -- OnlineApply: the check is added NOT VALID, which holds its lock for an instant, and validated after this
                    -- pass under the weaker lock VALIDATE takes.
                    CASE WHEN v."Online" AND p_WhatIf THEN v."Validate" || CHR(10)
                         WHEN v."Online" THEN 'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Validating constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name") || ', ' || quote_literal(v."Validate") || ', NULL, NULL);' || CHR(10)
                         ELSE '' END ||
                    CASE WHEN v."Form" IS NOT NULL
                         THEN 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'', ''' || v."Form" || ''');'
                         ELSE 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || ''', ''created'');' END, CHR(10))
    INTO sql_script
    FROM temp_checks tc
    CROSS JOIN LATERAL (SELECT f."Form", tc."Validated" AND tc."Enforced" AND f."Form" = 'online' AS "Online",
                               'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") ||
                               ' VALIDATE CONSTRAINT ' || "SchemaSmith"."QuoteName"(tc."Name") || ';' AS "Validate"
                          FROM (SELECT "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'check') AS "Form") f) v
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
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'check')
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
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing column check constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END ||
                    CASE WHEN v."Online" THEN ' (online, validated after this pass)' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"('CK_' || tc."TableName" || '_' || tc."Name") || ' CHECK (' || tc."CheckExpression" || ')' ||
                    CASE WHEN v."Online" THEN ' NOT VALID' ELSE '' END || ';' || CHR(10) ||
                    -- OnlineApply: the check is added NOT VALID, which holds its lock for an instant, and validated after this
                    -- pass under the weaker lock VALIDATE takes.
                    CASE WHEN v."Online" AND p_WhatIf THEN v."Validate" || CHR(10)
                         WHEN v."Online" THEN 'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Validating constraint ' || tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name") || ', ' || quote_literal(v."Validate") || ', NULL, NULL);' || CHR(10)
                         ELSE '' END ||
                    CASE WHEN v."Form" IS NOT NULL
                         THEN 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name" || ''', ''created'', ''' || v."Form" || ''');'
                         ELSE 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name" || ''', ''created'');' END, CHR(10))
    INTO sql_script
    FROM temp_columns tc
    CROSS JOIN LATERAL (SELECT f."Form", f."Form" = 'online' AS "Online",
                               'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") ||
                               ' VALIDATE CONSTRAINT ' || "SchemaSmith"."QuoteName"('CK_' || tc."TableName" || '_' || tc."Name") || ';' AS "Validate"
                          FROM (SELECT "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'check') AS "Form") f) v
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
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), 'constraint', tc."TableSchema" || '.' || tc."TableName" || '.CK_' || tc."TableName" || '_' || tc."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, tc."TableSchema", tc."TableName", 'check')
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