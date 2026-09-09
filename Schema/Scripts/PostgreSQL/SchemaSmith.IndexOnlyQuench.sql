-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."IndexOnlyQuench"
(p_ProductName VARCHAR(50),
 p_TableDefinitions TEXT,
 p_WhatIf BOOLEAN = FALSE,
 p_DropUnknownIndexes BOOLEAN = FALSE,
 p_DropIndexesRemovedFromProduct BOOLEAN = TRUE,
 p_UpdateFillFactor BOOLEAN = TRUE,
 p_CaptureWouldDrop BOOLEAN = FALSE)
    LANGUAGE plpgsql
AS $$
DECLARE
  table_json TEXT = CASE WHEN LEFT(p_TableDefinitions, 1) = '[' THEN p_TableDefinitions ELSE '[' || p_TableDefinitions || ']' END;
  sql_script TEXT = '';
BEGIN
    -- Dropped first like every other temp table in this procedure. A PostgreSQL TEMPORARY table lives for
    -- the SESSION, not the call, so without this a second IndexOnlyQuench on the same connection dies with
    -- 42P07, "relation temp_tables already exists" -- the first call works and every later one fails.
    -- temp_indexes, temp_statistics and temp_indexes_to_drop below have always had their DROP; this one
    -- was the odd one out, and the omission was unreachable while the emitted CALL was missing
    -- p_ProductName and the procedure could never run at all on PostgreSQL.
    DROP TABLE IF EXISTS temp_tables;
    CREATE TEMPORARY TABLE temp_tables AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT elem ->> 'Schema' AS "Schema",
           elem ->> 'Name' AS "Name",
           COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(elem ->> 'OldName', '') AS "OldName",
           COALESCE((elem ->> 'RowLevelSecurity')::BOOLEAN, false) AS "RowLevelSecurity",
           COALESCE((elem ->> 'ForceRowLevelSecurity')::BOOLEAN, false) AS "ForceRowLevelSecurity",
           COALESCE(elem ->> 'AccessMethod', '') AS "AccessMethod",
           COALESCE(elem ->> 'PersistenceType', '') AS "PersistenceType",
           CASE WHEN p_UpdateFillFactor THEN true ELSE COALESCE((elem ->> 'UpdateFillFactor')::BOOLEAN, false) END AS "UpdateFillFactor",
           (elem ->> 'DropIndexesRemovedFromProduct')::BOOLEAN AS "DropIndexesRemovedFromProduct"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem;

    SELECT STRING_AGG('DELETE FROM temp_tables WHERE "Schema" = ''' || "Schema" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_tables
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_indexes;
    CREATE TEMPORARY TABLE temp_indexes AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT COALESCE(elem ->> 'Schema', '') AS "TableSchema",
           COALESCE(elem ->> 'Name', '') AS "TableName",
           COALESCE(celem ->> 'Name', '') AS "Name",
           COALESCE((celem ->> 'PrimaryKey')::BOOLEAN, false) AS "PrimaryKey",
           COALESCE((celem ->> 'Unique')::BOOLEAN, false) AS "Unique",
           COALESCE((celem ->> 'UniqueConstraint')::BOOLEAN, false) AS "UniqueConstraint",
           COALESCE((celem ->> 'Clustered')::BOOLEAN, false) AS "Clustered",
           REGEXP_REPLACE(COALESCE(celem ->> 'IndexColumns', ''), '\s*,\s*', ',', 'g') AS "IndexColumns",
           COALESCE(celem ->> 'IncludeColumns', '') AS "IncludeColumns",
           COALESCE(celem ->> 'AccessMethod', 'btree') AS "AccessMethod",
           -- IndexOnlyQuench builds its OWN temp_indexes rather than reusing the one
           -- ParseTableJsonIntoTempTables makes, so every index column has to be declared in both.
           COALESCE(celem ->> 'Tablespace', '') AS "Tablespace",
           COALESCE(celem ->> 'FilterExpression', '') AS "FilterExpression",
           COALESCE((celem ->> 'Deferrable')::BOOLEAN, false) AS "Deferrable",
           COALESCE((celem ->> 'InitiallyDeferred')::BOOLEAN, false) AS "InitiallyDeferred",
           -- Below PG15 NULLS NOT DISTINCT does not exist: the effective column drives compare + emit
           -- (coerced false so an old target neither churns nor emits an unsupported clause), while the
           -- raw declared value drives the unsupported-feature policy (fail | warn-with-downgrade) below.
           CASE WHEN "SchemaSmith"."ServerVersionNum"() >= 15 THEN COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) ELSE false END AS "NullsNotDistinct",
           COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) AS "NullsNotDistinctDeclared",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName",
           CASE WHEN p_UpdateFillFactor THEN true ELSE COALESCE((celem ->> 'UpdateFillFactor')::BOOLEAN, false) END AS "UpdateFillFactor",
           COALESCE(NULLIF((celem ->> 'FillFactor')::INT2, 0), 90) AS "FillFactor",
           -- Index storage parameters (the WITH clause) canonicalised for comparison: key=value pairs
           -- sorted by key, because reloptions reorders itself and the declared map has no order. fillfactor
           -- is excluded here -- FillFactor above owns it -- so a package declaring only fillfactor produces
           -- an empty StorageParameters and compares equal to a live index that has only fillfactor.
           COALESCE((SELECT STRING_AGG(sp.k || '=' || sp.v, ',' ORDER BY sp.k)
                       FROM JSON_EACH_TEXT(COALESCE(celem -> 'StorageParameters', '{}'::JSON)) AS sp(k, v)
                      WHERE sp.k <> 'fillfactor'), '') AS "StorageParameters"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Indexes')::JSON) AS celem(value);

    UPDATE temp_indexes -- Table-level setting overrides index-level setting when true
       SET "UpdateFillFactor" = true
       WHERE NOT "UpdateFillFactor"
         AND EXISTS (SELECT * 
                       FROM temp_tables t 
                       WHERE t."Schema" = "TableSchema"
                         AND t."Name" = "TableName"
                         AND t."UpdateFillFactor" = true);

    SELECT STRING_AGG('DELETE FROM temp_indexes WHERE "TableSchema" = ''' || "TableSchema" || ''' AND "TableName" = ''' || "TableName" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_indexes
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    -- Unsupported-feature policy: NULLS NOT DISTINCT requires PostgreSQL 15. On an older target the
    -- effective column above already omits the clause; here the policy decides how to surface that.
    -- 'fail' aborts pre-emptively naming the offending index(es); 'warn' (default) records an
    -- unsupportedDowngrade manifest row per declared-but-unsupported index and lets the deploy proceed.
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

    DROP TABLE IF EXISTS temp_statistics;
    CREATE TEMPORARY TABLE temp_statistics AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           COALESCE(celem ->> 'Kind', '') AS "Kind",
           COALESCE(celem ->> 'StatisticsColumns', '') AS "StatisticsColumns",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Statistics')::JSON) AS celem(value);

    SELECT STRING_AGG('DELETE FROM temp_statistics WHERE "TableSchema" = ''' || "TableSchema" || ''' AND "TableName" = ''' || "TableName" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_statistics
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    RAISE NOTICE 'Collect Existing Indexes';
    CALL "SchemaSmith"."BuildExistingIndexesSnapshot"();

    RAISE NOTICE 'Handle Renamed Indexes And Unique Constraints';
    SELECT STRING_AGG('RAISE NOTICE ''  Renaming ' || CASE WHEN ei."PrimaryKey" OR ei."UniqueConstraint" THEN 'Constraint' ELSE 'Index' END || ' ' || ei."TableSchema" || '.' || ei."TableName" || '.' || ei."IndexName" || ' to ' || i."Name" || ''';' || CHR(10) ||
                      CASE WHEN NOT (ei."PrimaryKey" OR ei."UniqueConstraint")
                           THEN 'ALTER INDEX IF EXISTS "' || ei."TableSchema" || '"."' || ei."IndexName" || '" RENAME TO "' || i."Name" || '";'
                           ELSE 'ALTER TABLE "' || ei."TableSchema" || '"."' || ei."TableName" || '" RENAME CONSTRAINT "' || ei."IndexName" || '" TO "' || i."Name" || '";' END, CHR(10))
      INTO sql_script
      FROM temp_existing_indexes ei
      JOIN temp_indexes i ON i."TableSchema" = ei."TableSchema"
                         AND i."TableName" = ei."TableName"
                         AND i."Name" != ei."IndexName"
                         AND i."IndexColumns" = ei."IndexColumns"
                         AND COALESCE(i."IncludeColumns", '') = COALESCE(ei."IncludeColumns", '')
                         AND COALESCE(i."Unique", FALSE) = ei."Unique"
                         AND COALESCE(i."UniqueConstraint", FALSE) = ei."UniqueConstraint"
                         AND COALESCE(i."PrimaryKey", FALSE) = ei."PrimaryKey"
                         AND COALESCE(i."FilterExpression", '') = COALESCE(ei."FilterExpression", '')
                         AND COALESCE(i."AccessMethod", 'btree') = COALESCE(ei."AccessMethod", 'btree')
                         AND COALESCE(i."NullsNotDistinct", false) = COALESCE(ei."NullsNotDistinct", false)
                         AND COALESCE(i."Deferrable", false) = COALESCE(ei."Deferrable", false)
                         AND COALESCE(i."InitiallyDeferred", false) = COALESCE(ei."InitiallyDeferred", false)
                         AND COALESCE(i."StorageParameters", '') = COALESCE(ei."StorageParameters", '')
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_indexes i
                          WHERE i."TableSchema" = ei."TableSchema"
                            AND i."TableName" = ei."TableName"
                            AND i."Name" = ei."IndexName");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Identify Unknown, Removed, and Modified Indexes to Drop';
    DROP TABLE IF EXISTS temp_indexes_to_drop;
    CREATE TEMPORARY TABLE temp_indexes_to_drop AS
      SELECT ei."TableSchema",
             ei."TableName",
             ei."IndexName",
             ei."PrimaryKey" OR ei."UniqueConstraint" AS "IsConstraint"
        FROM temp_existing_indexes ei
        WHERE (p_DropUnknownIndexes 
           AND NOT EXISTS (SELECT 1 -- Unknown Index
                             FROM temp_indexes i
                             WHERE i."TableSchema" = ei."TableSchema"
                               AND i."TableName" = ei."TableName"
                               AND i."Name" = ei."IndexName"))
           OR EXISTS (SELECT 1 -- Modified Index
                        FROM temp_indexes i
                        WHERE i."TableSchema" = ei."TableSchema"
                          AND i."TableName" = ei."TableName"
                          AND i."Name" = ei."IndexName"
                          AND (i."IndexColumns" != ei."IndexColumns"
                            OR COALESCE(i."IncludeColumns", '') != COALESCE(ei."IncludeColumns", '')
                            OR (COALESCE(i."Unique", FALSE) OR COALESCE(i."PrimaryKey", FALSE) OR COALESCE(i."UniqueConstraint", FALSE)) != ei."Unique"
                            OR COALESCE(i."UniqueConstraint", FALSE) != ei."UniqueConstraint"
                            OR COALESCE(i."PrimaryKey", FALSE) != ei."PrimaryKey"
                            OR COALESCE(i."FilterExpression", '') != COALESCE(ei."FilterExpression", '')
                            OR COALESCE(i."AccessMethod", 'btree') != COALESCE(ei."AccessMethod", 'btree'))
                            OR COALESCE(i."NullsNotDistinct", false) != COALESCE(ei."NullsNotDistinct", false)
                            OR COALESCE(i."Deferrable", false) != COALESCE(ei."Deferrable", false)
                            OR COALESCE(i."InitiallyDeferred", false) != COALESCE(ei."InitiallyDeferred", false)
                            -- A storage-parameter change (hnsw m, ivfflat lists, brin pages_per_range, ...)
                            -- rebuilds: several cannot be ALTERed in place, so the whole index drops and is
                            -- recreated by the missing-index pass, which always works.
                            OR COALESCE(i."StorageParameters", '') != COALESCE(ei."StorageParameters", ''))
           OR (p_DropIndexesRemovedFromProduct -- Index Removed from Product Definition (gated)
               AND COALESCE((SELECT tt."DropIndexesRemovedFromProduct" FROM temp_tables tt WHERE tt."Schema" = ei."TableSchema" AND tt."Name" = ei."TableName"), TRUE)
               AND EXISTS (SELECT 1
                        FROM "SchemaSmith"."ProductOwnership" tp
                        WHERE tp."ProductName" = p_ProductName
                          AND tp."IndexName" = ei."IndexName"
                          AND tp."Schema" = ei."TableSchema"
                          AND tp."TableName" = ei."TableName"
                          AND NOT EXISTS (SELECT 1
                                            FROM temp_indexes i
                                            WHERE i."TableSchema" = ei."TableSchema"
                                              AND i."TableName" = ei."TableName"
                                              AND i."Name" = ei."IndexName")));

    -- No-drop protection tier (#270): under protected mode the caller forces p_DropUnknownIndexes and
    -- p_DropIndexesRemovedFromProduct to FALSE, so the by-absence branches of temp_indexes_to_drop above
    -- stay empty and the drop pass below skips them. Record the indexes that WOULD be dropped by absence
    -- -- unknown/out-of-band, and product-owned indexes removed from the definition with the per-table
    -- cascade tightening still honored -- as 'dropSuppressed'. Same by-absence predicates as those branches
    -- minus the env gates; the modified branch is not by-absence and is never suppressed, so it is
    -- excluded. ObjectName/ObjectType mirror the drop pass's 'dropped' index audit form.
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture indexes suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(),
               CASE WHEN ei."PrimaryKey" OR ei."UniqueConstraint" THEN 'constraint' ELSE 'index' END,
               ei."TableSchema" || '.' || ei."TableName" || '.' || ei."IndexName",
               'dropSuppressed'
          FROM temp_existing_indexes ei
          WHERE NOT EXISTS (SELECT 1 -- Unknown Index (minus the p_DropUnknownIndexes gate)
                              FROM temp_indexes i
                              WHERE i."TableSchema" = ei."TableSchema"
                                AND i."TableName" = ei."TableName"
                                AND i."Name" = ei."IndexName")
             OR (COALESCE((SELECT tt."DropIndexesRemovedFromProduct" FROM temp_tables tt WHERE tt."Schema" = ei."TableSchema" AND tt."Name" = ei."TableName"), TRUE) -- Index Removed from Product (minus the p_DropIndexesRemovedFromProduct gate; per-table opt-out kept)
                 AND EXISTS (SELECT 1
                               FROM "SchemaSmith"."ProductOwnership" tp
                               WHERE tp."ProductName" = p_ProductName
                                 AND tp."IndexName" = ei."IndexName"
                                 AND tp."Schema" = ei."TableSchema"
                                 AND tp."TableName" = ei."TableName")
                 AND NOT EXISTS (SELECT 1
                                   FROM temp_indexes i
                                   WHERE i."TableSchema" = ei."TableSchema"
                                     AND i."TableName" = ei."TableName"
                                     AND i."Name" = ei."IndexName"));
    END IF;

    RAISE NOTICE 'Drop Unknown, Removed, and Modified Indexes';
    SELECT STRING_AGG('RAISE NOTICE ''  Dropping ' || CASE WHEN "IsConstraint" THEN 'Constraint' ELSE 'Index' END || ' ' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ''';' || CHR(10) ||
                      CASE WHEN "IsConstraint"
                           THEN 'ALTER TABLE "' || ti."TableSchema" || '"."' || ti."TableName" || '" DROP CONSTRAINT IF EXISTS "' || ti."IndexName" || '" CASCADE;'
                           ELSE 'DROP INDEX IF EXISTS "' || ti."TableSchema" || '"."' || ti."IndexName" || '";' END, CHR(10))
      INTO sql_script
      FROM temp_indexes_to_drop ti;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Fixup Any Modified Index Fill Factors';
    SELECT STRING_AGG('RAISE NOTICE ''  Modify Fillfactor for ' || ti."TableSchema" || '.' || ti."Name" || ''';' || CHR(10) ||
                      'ALTER INDEX "' || ti."TableSchema" || '"."' || ti."Name" || '" SET (fillfactor = ' || ti."FillFactor" || ');', CHR(10))
      INTO sql_script
      FROM temp_indexes ti
      JOIN temp_existing_indexes ei ON ei."TableSchema" = ti."TableSchema"
                                   AND ei."TableName" = ti."TableName"
                                   AND ei."IndexName" = ti."Name"
      JOIN pg_index idx ON idx.indrelid = ('"' || ti."TableSchema" || '"' ||  '.' || '"' ||  ti."TableName" || '"')::regclass
      JOIN pg_class i ON i.oid = idx.indexrelid
                     AND i.relname = ti."Name"
      WHERE ti."UpdateFillFactor"
        AND ei."FillFactor" != ti."FillFactor"
        -- Positive gate on AMs verified to accept fillfactor (see the CREATE-path comment below) —
        -- an extension AM that rejects the option never reaches an ALTER INDEX SET here either.
        AND COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash');
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Add Missing Indexes'; -- Includes Primary Keys and Unique Constraints
    SELECT STRING_AGG('RAISE NOTICE ''  Add missing ' || CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey" THEN 'Constraint ' ELSE 'Index ' END || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name" || CASE WHEN COALESCE(ti."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(ti."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                      CASE WHEN ti."UniqueConstraint" OR ti."PrimaryKey"
                           THEN 'ALTER TABLE "' || ti."TableSchema" || '"."' || ti."TableName" || '" ADD CONSTRAINT "' || ti."Name" || '" ' ||
                                CASE WHEN ti."PrimaryKey" 
                                     THEN 'PRIMARY KEY ' 
                                     ELSE 'UNIQUE ' || CASE WHEN ti."NullsNotDistinct" THEN 'NULLS NOT DISTINCT ' ELSE '' END
                                     END ||
                                '(' || "SchemaSmith"."QuoteIndexColumnList"(ti."IndexColumns") || ')' ||
                                -- Positive gate, not a deny-list: an extension AM (e.g. pgvector's hnsw/ivfflat)
                                -- can't be enumerated in advance, so allow-listing the AMs verified to accept
                                -- fillfactor fails safe (no clause) instead of failing loud (PostgreSQL's own
                                -- "unrecognized parameter" error) for anything not on the list.
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
                                -- CREATE INDEX grammar order: (cols) INCLUDE [NULLS NOT DISTINCT] [WITH] [WHERE].
                                CASE WHEN ti."Unique" AND ti."NullsNotDistinct" THEN ' NULLS NOT DISTINCT' ELSE '' END ||
                                -- ONE WITH clause carrying fillfactor (still gated to the AMs that accept it)
                                -- AND StorageParameters (any AM -- this is how a vector index gets its m /
                                -- ef_construction / lists). StorageParameters is already canonical
                                -- key=value,key=value, which is WITH-clause syntax, so it drops straight in.
                                CASE
                                  WHEN COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash') AND COALESCE(ti."StorageParameters", '') <> ''
                                       THEN ' WITH (fillfactor = ' || ti."FillFactor" || ', ' || ti."StorageParameters" || ')'
                                  WHEN COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash')
                                       THEN ' WITH (fillfactor = ' || ti."FillFactor" || ')'
                                  WHEN COALESCE(ti."StorageParameters", '') <> ''
                                       THEN ' WITH (' || ti."StorageParameters" || ')'
                                  ELSE '' END ||
                                -- TABLESPACE follows WITH and precedes WHERE per the CREATE INDEX grammar
                              -- (verified live on 16).
                              CASE WHEN COALESCE(ti."Tablespace", '') <> '' THEN ' TABLESPACE "' || ti."Tablespace" || '"' ELSE '' END ||
                              CASE WHEN NULLIF(ti."FilterExpression", '') IS NOT NULL THEN ' WHERE ' || ti."FilterExpression" ELSE '' END || ';'
                           END, CHR(10))
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


    RAISE NOTICE 'Fixup Table Cluster';
    SELECT STRING_AGG('RAISE NOTICE ''  Fixing up attributes for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                      'ALTER TABLE ' || '"' || t."Schema" || '"."' || t."Name" || '" ' ||
                      CASE WHEN new_clust."NewCluster" IS NOT NULL 
                           THEN 'CLUSTER ON "' || "NewCluster" || '"'
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

  -- Unsupported-feature policy: expression statistics require PostgreSQL 14 (see MissingIndexesAndConstraintsQuench).
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
                    ' FROM "' || ts."TableSchema" || '"."' || ts."TableName" || '";', CHR(10))
    INTO sql_script
    FROM temp_statistics ts
    WHERE NOT EXISTS (SELECT 1
                        FROM pg_statistic_ext ste
                        JOIN pg_class rel ON rel.oid = ste.stxrelid
                        JOIN pg_namespace nsp ON nsp.oid = rel.relnamespace
                                             AND nsp.nspname = ts."TableSchema"
                                             AND rel.relname = ts."TableName"
                        WHERE ste.stxname = ts."Name")
      AND NOT ("SchemaSmith"."ServerVersionNum"() < 14 AND ts."StatisticsColumns" LIKE '%(%');
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

END $$;