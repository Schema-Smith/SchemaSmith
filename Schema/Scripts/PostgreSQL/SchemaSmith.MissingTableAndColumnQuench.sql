-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."MissingTableAndColumnQuench"(p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE 
  sql_script TEXT = '';
BEGIN
  RAISE NOTICE 'Handle Table Renames';
  SELECT STRING_AGG('RAISE NOTICE ''  Rename Table ' || tt."Schema" || '.' || tt."OldName" || ' to ' || tt."Name" || ''';' || CHR(10) ||
                    'ALTER TABLE "' || tt."Schema" || '"."' || tt."OldName" || '" RENAME TO "' || tt."Name" || '";', CHR(10))
    INTO sql_script
    FROM temp_tables tt
    WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
      AND tt."OldName" IS NOT NULL
      AND EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."OldName");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  RAISE NOTICE 'Handle Column Renames';
  SELECT STRING_AGG('RAISE NOTICE ''  Rename Column ' || tc."TableSchema" || '.' || tc."TableName" || '.' || tc."OldName" || ' to ' || tc."Name" || ''';' || CHR(10) ||
                    'ALTER TABLE "' || tc."TableSchema" || '"."' || tc."TableName" || '" RENAME COLUMN "' || tc."OldName" || '" TO "' || tc."Name" || '";', CHR(10))
    INTO sql_script
    FROM temp_columns tc
    WHERE NOT EXISTS(SELECT * FROM information_schema.columns c WHERE c.table_schema = tc."TableSchema" AND c.table_name = tc."TableName" AND c.column_name = tc."Name")
      AND tc."OldName" IS NOT NULL
      AND EXISTS(SELECT * FROM information_schema.columns c WHERE c.table_schema = tc."TableSchema" AND c.table_name = tc."TableName" AND c.column_name = tc."OldName");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  IF EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid WHERE p.proname = 'CustomTableRestore' AND n.nspname = 'SchemaSmith' ) THEN
    RAISE NOTICE 'Handle Custom Table Restore';
    SELECT STRING_AGG('RAISE NOTICE ''  Attempt custom restore of ' || tt."Schema" || '.' || tt."Name" || ''';' || CHR(10) ||
                      'CALL "SchemaSmith"."CustomTableRestore"(''' || tt."Schema" || ''', ''' || tt."Name" || ''');', CHR(10))
      INTO sql_script
      FROM temp_tables tt
      WHERE NOT EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  END IF;
  
  RAISE NOTICE 'Add New Tables';
  SELECT STRING_AGG('RAISE NOTICE ''  Create new table ' || tt."Schema" || '.' || tt."Name" || CASE WHEN COALESCE(tt."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tt."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'CREATE TABLE "' || tt."Schema" || '"."' || tt."Name" || '" (' ||
                    (SELECT STRING_AGG('"' || tc."Name" || '" ' || tc."DataType" ||
                            CASE WHEN COALESCE(tc."Collation", '') != '' THEN ' COLLATE "' || tc."Collation" || '"' ELSE '' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' THEN ' ' || tc."Generated" ELSE '' END ||
                            CASE WHEN tc."Nullable" THEN '' ELSE ' NOT NULL' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%' AND COALESCE(tc."Default", '') != '' THEN ' DEFAULT ' || tc."Default" ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         -- Defer computed (GENERATED ALWAYS AS expression) columns to the "Add New Computed
                         -- Columns" step (parity with SQL Server / MySQL) — they may reference columns not yet
                         -- present at CREATE time, and inlining them here created a plain column then churned
                         -- it to generated via a drop-and-re-add. Identity columns are NOT deferred.
                         AND NOT (COALESCE(tc."Generated", 'NEVER') = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> '')) || ')' ||
                    ' WITH (fillfactor = ' || tt."FillFactor" || ')' ||
                    -- TABLESPACE follows WITH in the CREATE TABLE grammar. Emitted only when declared:
                    -- an unset Tablespace means placement is not managed, so the table lands wherever
                    -- default_tablespace puts it, exactly as it did before this shipped.
                    CASE WHEN COALESCE(tt."Tablespace", '') <> '' THEN ' TABLESPACE "' || tt."Tablespace" || '"' ELSE '' END ||
                    ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''table'', ''' || tt."Schema" || '.' || tt."Name" || ''', ''created'');', CHR(10))
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

  RAISE NOTICE 'Add New Physical Columns';
  SELECT STRING_AGG('RAISE NOTICE ''  Add new physical columns to ' || tt."Schema" || '.' || tt."Name" || ' (' ||
                    (SELECT STRING_AGG(tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                         AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")) ||
                    ')'';' || CHR(10) ||
                    'ALTER TABLE "' || tt."Schema" || '"."' || tt."Name" || '" ' ||
                    (SELECT STRING_AGG('ADD "' || tc."Name" || '" ' || tc."DataType" ||
                            CASE WHEN COALESCE(tc."Collation", '') != '' THEN ' COLLATE "' || tc."Collation" || '"' ELSE '' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' THEN ' ' || tc."Generated" ELSE '' END ||
                            CASE WHEN tc."Nullable" THEN '' ELSE ' NOT NULL' END ||
                            CASE WHEN COALESCE(tc."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%' AND COALESCE(tc."Default", '') != '' THEN ' DEFAULT ' || tc."Default" ELSE '' END, ', ' ORDER BY tc."_RowId")
                       FROM temp_columns tc
                       WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                         AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                         AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")) || ';' || CHR(10) ||
                    -- Object-change audit (#243 E5): one row per physical column added (folded ALTER above).
                    COALESCE((SELECT STRING_AGG('INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''column'', ''' || tt."Schema" || '.' || tt."Name" || '.' || tc."Name" || ''', ''created'');', CHR(10))
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