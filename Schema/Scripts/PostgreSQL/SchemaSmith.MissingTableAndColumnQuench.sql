-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingTableAndColumnQuench"(p_WhatIf BOOLEAN = FALSE, p_OnlineApply TEXT = 'OFF')
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE 
  sql_script TEXT = '';
  restore_candidates TEXT[];
  restored_table TEXT;
  saved_time_zone TEXT;
  refused_partitions TEXT;
  -- The database's own default tablespace, which a partitioned table may not name.
  db_default_tablespace TEXT = (SELECT t.spcname FROM pg_catalog.pg_tablespace t JOIN pg_catalog.pg_database d ON d.dattablespace = t.oid
                              WHERE d.datname = current_database());
BEGIN
  RAISE NOTICE 'Handle Table Renames';
  SELECT STRING_AGG('RAISE NOTICE ''  Rename Table %.% to %'', ''' || REPLACE((tt."Schema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."OldName")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tt."Schema") || '.' || "SchemaSmith"."QuoteName"(tt."OldName") || ' RENAME TO ' || "SchemaSmith"."QuoteName"(tt."Name") || ';', CHR(10))
    INTO sql_script
    FROM temp_tables tt
    WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
      AND tt."OldName" IS NOT NULL
      AND EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."OldName");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  RAISE NOTICE 'Handle Column Renames';
  SELECT STRING_AGG('RAISE NOTICE ''  Rename Column %.%.% to %'', ''' || REPLACE((tc."TableSchema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tc."TableName")::TEXT, '''', '''''') || ''', ''' || REPLACE((tc."OldName")::TEXT, '''', '''''') || ''', ''' || REPLACE((tc."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || ' RENAME COLUMN ' || "SchemaSmith"."QuoteName"(tc."OldName") || ' TO ' || "SchemaSmith"."QuoteName"(tc."Name") || ';', CHR(10))
    INTO sql_script
    FROM temp_columns tc
    WHERE NOT EXISTS(SELECT * FROM information_schema.columns c WHERE c.table_schema = tc."TableSchema" AND c.table_name = tc."TableName" AND c.column_name = tc."Name")
      AND tc."OldName" IS NOT NULL
      AND EXISTS(SELECT * FROM information_schema.columns c WHERE c.table_schema = tc."TableSchema" AND c.table_name = tc."TableName" AND c.column_name = tc."OldName");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid WHERE p.proname = 'CustomTableRestore' AND n.nspname = 'SchemaSmith' ) THEN
    RAISE NOTICE 'Handle Custom Table Restore';
    SELECT STRING_AGG('RAISE NOTICE ''  Attempt custom restore of %.%'', ''' || REPLACE((tt."Schema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                      'CALL "SchemaSmith"."CustomTableRestore"(''' || REPLACE((tt."Schema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."Name")::TEXT, '''', '''''') || ''');', CHR(10))
      INTO sql_script
      FROM temp_tables tt
      WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
    -- Which of them the hook brought back, as SQL Server says: the create step below now leaves them alone.
    SELECT ARRAY_AGG(tt."Schema" || '.' || tt."Name")
      INTO restore_candidates
      FROM temp_tables tt
      WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    IF NOT p_WhatIf THEN
      FOR restored_table IN SELECT tt."Schema" || '.' || tt."Name"
                              FROM temp_tables tt
                              WHERE tt."Schema" || '.' || tt."Name" = ANY(restore_candidates)
                                AND EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
      LOOP
        RAISE NOTICE '  % Restored', restored_table;
      END LOOP;
    END IF;
  END IF;
  
  -- The columns this run creates, with their tables. Each is created with its declared default, so
  -- ModifiedTableQuench need not compare that default with the text PostgreSQL stores for it on this run;
  -- ExpressionMapRecord records the pair at the end of it.
  DROP TABLE IF EXISTS temp_created_columns;
  CREATE TEMPORARY TABLE temp_created_columns AS
    SELECT c."TableSchema", c."TableName", c."Name"
      FROM temp_columns c
     WHERE NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                        WHERE a.attrelid = to_regclass("SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName"))
                          AND a.attname = c."Name" AND a.attnum > 0 AND NOT a.attisdropped);

  -- Unsupported-feature policy: a partitioned table takes an access method from PostgreSQL 17 (the default for its new
  -- partitions). Below it the create leaves USING out; 'fail' aborts naming the table, 'warn' records a downgrade.
  IF "SchemaSmith"."ServerVersionNum"() < 17
     AND EXISTS (SELECT 1 FROM temp_tables tt
                  WHERE tt."PartitionMethod" <> '' AND COALESCE(tt."AccessMethod", '') <> ''
                    AND NOT EXISTS (SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")) THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail' THEN
      RAISE EXCEPTION 'A table access method on a partitioned table requires PostgreSQL 17 (detected major %); table(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG(tt."Schema" || '.' || tt."Name", ', ')
           FROM temp_tables tt
          WHERE tt."PartitionMethod" <> '' AND COALESCE(tt."AccessMethod", '') <> ''
            AND NOT EXISTS (SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name"));
    END IF;
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'table access method on a partitioned table (PG17)', tt."Schema" || '.' || tt."Name", 'downgraded'
        FROM temp_tables tt
       WHERE tt."PartitionMethod" <> '' AND COALESCE(tt."AccessMethod", '') <> ''
         AND NOT EXISTS (SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
  END IF;

  RAISE NOTICE 'Add New Tables';
  SELECT STRING_AGG('RAISE NOTICE ''  Create new table %.%'', ''' || REPLACE((tt."Schema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."Name" || CASE WHEN COALESCE(tt."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tt."VariantName", '''', '''''') || ')' ELSE '' END)::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'CREATE TABLE ' || "SchemaSmith"."QuoteName"(tt."Schema") || '.' || "SchemaSmith"."QuoteName"(tt."Name") || ' (' ||
                    (SELECT STRING_AGG("SchemaSmith"."QuoteName"(tc."Name") || ' ' || CASE WHEN tc."IsSerial" THEN tc."SerialKeyword" ELSE tc."DataType" END ||
                            "SchemaSmith"."CollateClause"(tc."Collation") ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' THEN ' ' || tc."Generated" ELSE '' END ||
                            CASE WHEN tc."Nullable" THEN '' ELSE ' NOT NULL' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%' AND NOT tc."IsSerial" AND COALESCE(tc."Default", '') != '' THEN ' DEFAULT ' || tc."Default" ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         -- Defer computed (GENERATED ALWAYS AS expression) columns to the "Add New Computed
                         -- Columns" step (parity with SQL Server / MySQL) — they may reference columns not yet
                         -- present at CREATE time, and inlining them here created a plain column then churned
                         -- it to generated via a drop-and-re-add. Identity columns are NOT deferred.
                         AND NOT (COALESCE(tc."Generated", 'NEVER') = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> '')) || ')' ||
                    CASE WHEN tt."PartitionMethod" <> '' THEN ' PARTITION BY ' || tt."PartitionMethod" || ' (' || tt."PartitionExpression" || ')' ELSE '' END ||
                    -- USING precedes WITH in the grammar and has existed since PostgreSQL 12, so a new table gets its
                    -- declared access method on every supported version. Only changing an existing table's method
                    -- needs 15 (SET ACCESS METHOD, in ModifiedTableQuench). A partitioned table takes one from 17.
                    CASE WHEN COALESCE(tt."AccessMethod", '') <> '' AND (tt."PartitionMethod" = '' OR "SchemaSmith"."ServerVersionNum"() >= 17)
                         THEN ' USING ' || "SchemaSmith"."QuoteName"(tt."AccessMethod") ELSE '' END ||
                    -- A partitioned table holds no rows and takes no storage parameters.
                    CASE WHEN tt."PartitionMethod" = '' THEN ' WITH (fillfactor = ' || tt."FillFactor" || ')' ELSE '' END ||
                    -- TABLESPACE follows WITH in the CREATE TABLE grammar. Emitted only when declared:
                    -- an unset Tablespace means placement is not managed, so the table lands wherever
                    -- default_tablespace puts it, exactly as it did before this shipped.
                    -- A partitioned table refuses the database's default tablespace by name ("cannot specify default
                    -- tablespace for partitioned relations"), and left out it lands there all the same.
                    CASE WHEN COALESCE(tt."Tablespace", '') <> ''
                          AND NOT (tt."PartitionMethod" <> '' AND tt."Tablespace" = db_default_tablespace)
                         THEN ' TABLESPACE ' || "SchemaSmith"."QuoteName"(tt."Tablespace") ELSE '' END ||
                    ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''table'', ''' || REPLACE((tt."Schema" || '.' || tt."Name")::TEXT, '''', '''''') || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_tables tt
    WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'table'/'created' audit above; same source.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'table', tt."Schema" || '.' || tt."Name", 'wouldCreate'
        FROM temp_tables tt
        WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
  END IF;

  -- Each declared partition the database lacks, parents before children and DEFAULT last at each level (a partition
  -- added beside a DEFAULT scans it). Before every index and key pass, so the ones declared on the parent reach each
  -- partition through the engine. Under UTC, so a timestamp with time zone written without an offset means the same
  -- instant on every deploy, as extraction writes it.
  RAISE NOTICE 'Add Missing Partitions';
  -- Adding a partition to a table that already exists locks the partitioned table, and there is no lower-lock form, so
  -- OnlineApply Require refuses it by name before anything is created (a table this run created is OFF: it is empty).
  SELECT STRING_AGG(p."TableSchema" || '.' || p."Name" || ' (' || "SchemaSmith"."OnlineUnavailableReason"(p."TableSchema", p."TableName", 'partition') || ')', '; '
                    ORDER BY p."TableSchema", p."Name")
    INTO refused_partitions
    FROM temp_partitions p
   WHERE to_regclass("SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."Name")) IS NULL
     AND "SchemaSmith"."OnlineApplyMode"(p_OnlineApply, p."TableSchema", p."TableName") = 'REQUIRE'
     AND "SchemaSmith"."OnlineUnavailableReason"(p."TableSchema", p."TableName", 'partition') IS NOT NULL;
  IF refused_partitions IS NOT NULL THEN
    RAISE EXCEPTION 'OnlineApply is Require, and these cannot be built online: %. Set the table''s OnlineApply to Prefer to build them with the plain statement.', refused_partitions;
  END IF;
  saved_time_zone := current_setting('TimeZone');
  PERFORM set_config('TimeZone', 'UTC', true);
  SELECT STRING_AGG('RAISE NOTICE ''  Create partition %.% of %'', ''' || REPLACE(p."TableSchema", '''', '''''') || ''', ''' || REPLACE(p."Name", '''', '''''') || ''', ''' || REPLACE(p."ParentName", '''', '''''') || ''';' || CHR(10) ||
                    'CREATE TABLE ' || "SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."Name") ||
                    ' PARTITION OF ' || "SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."ParentName") || ' ' || p."Bound" ||
                    CASE WHEN p."Method" <> '' THEN ' PARTITION BY ' || p."Method" || ' (' || p."Expression" || ')' ELSE '' END ||
                    CASE WHEN p."Tablespace" IS NOT NULL AND NOT (p."Method" <> '' AND p."Tablespace" = db_default_tablespace)
                         THEN ' TABLESPACE ' || "SchemaSmith"."QuoteName"(p."Tablespace") ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''partition'', ''' ||
                    REPLACE(p."TableSchema" || '.' || p."ParentName" || '.' || p."Name", '''', '''''') || ''', ''created'', ' ||
                    COALESCE('''' || REPLACE("SchemaSmith"."OnlineApplyForm"(p_OnlineApply, p."TableSchema", p."TableName", 'partition'), '''', '''''') || '''', 'NULL') || ');', CHR(10)
                    ORDER BY p."TableSchema", p."TableName", p."Depth", p."IsDefault", p."Position")
    INTO sql_script
    FROM temp_partitions p
    WHERE to_regclass("SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."Name")) IS NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), 'partition', p."TableSchema" || '.' || p."ParentName" || '.' || p."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, p."TableSchema", p."TableName", 'partition')
        FROM temp_partitions p
        WHERE to_regclass("SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."Name")) IS NULL;
  END IF;
  PERFORM set_config('TimeZone', saved_time_zone, true);

  RAISE NOTICE 'Add New Physical Columns';
  SELECT STRING_AGG('RAISE NOTICE ''  Add new physical columns to %.% (%)'', ''' || REPLACE((tt."Schema")::TEXT, '''', '''''') || ''', ''' || REPLACE((tt."Name")::TEXT, '''', '''''') || ''', ''' || REPLACE(((SELECT STRING_AGG(tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                         AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")))::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tt."Schema") || '.' || "SchemaSmith"."QuoteName"(tt."Name") || ' ' ||
                    (SELECT STRING_AGG('ADD ' || "SchemaSmith"."QuoteName"(tc."Name") || ' ' || CASE WHEN tc."IsSerial" THEN tc."SerialKeyword" ELSE tc."DataType" END ||
                            "SchemaSmith"."CollateClause"(tc."Collation") ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' THEN ' ' || tc."Generated" ELSE '' END ||
                            CASE WHEN tc."Nullable" THEN '' ELSE ' NOT NULL' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%' AND NOT tc."IsSerial" AND COALESCE(tc."Default", '') != '' THEN ' DEFAULT ' || tc."Default" ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                         AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")) || ';' || CHR(10) ||
                    -- Object-change audit (#243 E5): one row per physical column added (folded ALTER above).
                    COALESCE((SELECT STRING_AGG('INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''column'', ''' || REPLACE((tt."Schema" || '.' || tt."Name" || '.' || tc."Name")::TEXT, '''', '''''') || ''', ''created'');', CHR(10))
                                FROM temp_columns tc
                                WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                                  AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                                  AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")), ''), CHR(10))
    INTO sql_script
    FROM temp_tables tt
    WHERE EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
      AND EXISTS (SELECT 1
                    FROM temp_columns tc
                    WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                      AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                      AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name"));
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'column'/'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'column', tt."Schema" || '.' || tt."Name" || '.' || tc."Name", 'wouldCreate'
        FROM temp_tables tt
        JOIN temp_columns tc ON tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
        WHERE EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
          AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
          AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name");
  END IF;
END
$$;