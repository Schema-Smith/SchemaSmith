-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The declared Description of every table, column, index, policy and extended statistics object, converged with
-- COMMENT ON once the objects exist; a constraint's is ConstraintDescriptionQuench's, below. A declared value is
-- set, '' removes the comment, and an omitted value (NULL in the temp tables)
-- leaves whatever is there alone: a package that never mentions descriptions never touches the ones a DBA
-- wrote, the same rule the permission carry-across keeps. Under p_IndexesOnly only temp_indexes and
-- temp_statistics are read, because IndexOnlyQuench builds no temp_columns or constraint tables.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."DescriptionQuench"(p_WhatIf BOOLEAN, p_IndexesOnly BOOLEAN DEFAULT FALSE)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
BEGIN
  RAISE NOTICE 'Fixup Descriptions';
  IF NOT p_IndexesOnly THEN
    SELECT STRING_AGG(stmt, CHR(10))
      INTO sql_script
      FROM (SELECT 'RAISE NOTICE ''  % the description of table %'', ''' || REPLACE((CASE WHEN t."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((t."Schema" || '.' || t."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                   'COMMENT ON TABLE ' || "SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name") || ' IS ' ||
                   CASE WHEN t."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(t."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''table'', ''' || REPLACE(t."Schema" || '.' || t."Name", '''', '''''') || ''', ''modified'');' AS stmt
              FROM temp_tables t
              JOIN pg_class c ON c.relname = t."Name" AND c.relkind IN ('r', 'p')
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
             WHERE t."Description" IS NOT NULL
               AND COALESCE(OBJ_DESCRIPTION(c.oid, 'pg_class'), '') IS DISTINCT FROM t."Description"
            UNION ALL
            SELECT 'RAISE NOTICE ''  % the description of column %'', ''' || REPLACE((CASE WHEN tc."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                   'COMMENT ON COLUMN ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || '.' || "SchemaSmith"."QuoteName"(tc."Name") || ' IS ' ||
                   CASE WHEN tc."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(tc."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''column'', ''' || REPLACE(tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", '''', '''''') || ''', ''modified'');'
              FROM temp_columns tc
              JOIN pg_class c ON c.relname = tc."TableName" AND c.relkind IN ('r', 'p')
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = tc."TableSchema")
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped
             WHERE tc."Description" IS NOT NULL
               AND COALESCE(COL_DESCRIPTION(c.oid, a.attnum), '') IS DISTINCT FROM tc."Description"
            UNION ALL
            SELECT 'RAISE NOTICE ''  % the description of policy %'', ''' || REPLACE((CASE WHEN p."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((p."TableSchema" || '.' || p."TableName" || '.' || p."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                   'COMMENT ON POLICY ' || "SchemaSmith"."QuoteName"(p."Name") || ' ON ' || "SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."TableName") || ' IS ' ||
                   CASE WHEN p."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(p."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''policy'', ''' || REPLACE(p."TableSchema" || '.' || p."TableName" || '.' || p."Name", '''', '''''') || ''', ''modified'');'
              FROM temp_policies p
              JOIN pg_policy pol ON pol.polname = p."Name"
                                AND pol.polrelid = to_regclass("SchemaSmith"."QuoteName"(p."TableSchema") || '.' || "SchemaSmith"."QuoteName"(p."TableName"))
             WHERE p."Description" IS NOT NULL
               AND COALESCE(OBJ_DESCRIPTION(pol.oid, 'pg_policy'), '') IS DISTINCT FROM p."Description") s;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
  END IF;

  SELECT STRING_AGG('RAISE NOTICE ''  % the description of index %'', ''' || REPLACE((CASE WHEN ti."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'COMMENT ON INDEX ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."Name") || ' IS ' ||
                    CASE WHEN ti."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(ti."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''index'', ''' || REPLACE(ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name", '''', '''''') || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_indexes ti
    JOIN pg_class tc ON tc.relname = ti."TableName" AND tc.relkind IN ('r', 'p')
                    AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
    JOIN pg_index idx ON idx.indrelid = tc.oid
    JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = ti."Name"
   WHERE ti."Description" IS NOT NULL
     AND COALESCE(OBJ_DESCRIPTION(i.oid, 'pg_class'), '') IS DISTINCT FROM ti."Description";
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;

  SELECT STRING_AGG(stmt, CHR(10))
    INTO sql_script
    FROM (SELECT 'RAISE NOTICE ''  % the description of statistics %'', ''' || REPLACE((CASE WHEN st."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((COALESCE(st."StatisticsSchema", st."TableSchema") || '.' || st."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
                   'COMMENT ON STATISTICS ' || "SchemaSmith"."QuoteName"(COALESCE(st."StatisticsSchema", st."TableSchema")) || '.' || "SchemaSmith"."QuoteName"(st."Name") || ' IS ' ||
                   CASE WHEN st."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(st."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''statistic'', ''' || REPLACE(COALESCE(st."StatisticsSchema", st."TableSchema") || '.' || st."Name", '''', '''''') || ''', ''modified'');'
            FROM temp_statistics st
            JOIN pg_statistic_ext se ON se.stxname = st."Name"
                                    AND se.stxnamespace = (SELECT oid FROM pg_namespace WHERE nspname = COALESCE(st."StatisticsSchema", st."TableSchema"))
           WHERE st."Description" IS NOT NULL
             AND COALESCE(OBJ_DESCRIPTION(se.oid, 'pg_statistic_ext'), '') IS DISTINCT FROM st."Description") s(stmt);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
END $$;

-- A foreign key's, check constraint's and exclusion constraint's Description. Its own step because foreign keys are
-- created after DescriptionQuench, and ConstraintStateQuench re-creates a check constraint whose enforceability
-- changes: run after both, each sees the constraint the deploy leaves.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ConstraintDescriptionQuench"(p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
BEGIN
  RAISE NOTICE 'Fixup Constraint Descriptions';
  SELECT STRING_AGG(stmt, CHR(10))
    INTO sql_script
    FROM (SELECT 'RAISE NOTICE ''  % the description of constraint %'', ''' || REPLACE((CASE WHEN k."Description" = '' THEN 'Removing' ELSE 'Setting' END)::TEXT, '''', '''''') || ''', ''' || REPLACE((k."TableSchema" || '.' || k."TableName" || '.' || k."Name")::TEXT, '''', '''''') || ''';' || CHR(10) ||
               'COMMENT ON CONSTRAINT ' || "SchemaSmith"."QuoteName"(k."Name") || ' ON ' || "SchemaSmith"."QuoteName"(k."TableSchema") || '.' || "SchemaSmith"."QuoteName"(k."TableName") || ' IS ' ||
               CASE WHEN k."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(k."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
               'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
               'VALUES (pg_backend_pid(), ''' || k."AuditType" || ''', ''' || REPLACE(k."TableSchema" || '.' || k."TableName" || '.' || k."Name", '''', '''''') || ''', ''modified'');'
          FROM (SELECT "TableSchema", "TableName", "Name", "Description", 'foreignKey' AS "AuditType" FROM temp_fks
                UNION ALL SELECT "TableSchema", "TableName", "Name", "Description", 'constraint' FROM temp_checks
                UNION ALL SELECT "TableSchema", "TableName", "Name", "Description", 'constraint' FROM temp_excludes) k
          JOIN pg_constraint con ON con.conname = k."Name"
                                AND con.conrelid = to_regclass("SchemaSmith"."QuoteName"(k."TableSchema") || '.' || "SchemaSmith"."QuoteName"(k."TableName"))
         WHERE k."Description" IS NOT NULL
           AND COALESCE(OBJ_DESCRIPTION(con.oid, 'pg_constraint'), '') IS DISTINCT FROM k."Description") s(stmt);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
END $$;
