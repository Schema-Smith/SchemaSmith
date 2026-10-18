-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The declared Description of every table, column and index, converged with COMMENT ON once the objects
-- exist. A declared value is set, '' removes the comment, and an omitted value (NULL in the temp tables)
-- leaves whatever is there alone: a package that never mentions descriptions never touches the ones a DBA
-- wrote, the same rule the permission carry-across keeps. Under p_IndexesOnly only temp_indexes is read,
-- because IndexOnlyQuench builds no temp_columns.
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
      FROM (SELECT 'RAISE NOTICE ''  ' || CASE WHEN t."Description" = '' THEN 'Removing' ELSE 'Setting' END || ' the description of table ' ||
                   REPLACE(REPLACE(t."Schema" || '.' || t."Name", '''', ''''''), '%', '%%') || ''';' || CHR(10) ||
                   'COMMENT ON TABLE ' || "SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name") || ' IS ' ||
                   CASE WHEN t."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(t."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''table'', ''' || REPLACE(t."Schema" || '.' || t."Name", '''', '''''') || ''', ''modified'');' AS stmt
              FROM temp_tables t
              JOIN pg_class c ON c.relname = t."Name" AND c.relkind = 'r'
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
             WHERE t."Description" IS NOT NULL
               AND COALESCE(OBJ_DESCRIPTION(c.oid, 'pg_class'), '') IS DISTINCT FROM t."Description"
            UNION ALL
            SELECT 'RAISE NOTICE ''  ' || CASE WHEN tc."Description" = '' THEN 'Removing' ELSE 'Setting' END || ' the description of column ' ||
                   REPLACE(REPLACE(tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", '''', ''''''), '%', '%%') || ''';' || CHR(10) ||
                   'COMMENT ON COLUMN ' || "SchemaSmith"."QuoteName"(tc."TableSchema") || '.' || "SchemaSmith"."QuoteName"(tc."TableName") || '.' || "SchemaSmith"."QuoteName"(tc."Name") || ' IS ' ||
                   CASE WHEN tc."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(tc."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                   'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                   'VALUES (pg_backend_pid(), ''column'', ''' || REPLACE(tc."TableSchema" || '.' || tc."TableName" || '.' || tc."Name", '''', '''''') || ''', ''modified'');'
              FROM temp_columns tc
              JOIN pg_class c ON c.relname = tc."TableName" AND c.relkind = 'r'
                             AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = tc."TableSchema")
              JOIN pg_attribute a ON a.attrelid = c.oid AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped
             WHERE tc."Description" IS NOT NULL
               AND COALESCE(COL_DESCRIPTION(c.oid, a.attnum), '') IS DISTINCT FROM tc."Description") s;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
  END IF;

  SELECT STRING_AGG('RAISE NOTICE ''  ' || CASE WHEN ti."Description" = '' THEN 'Removing' ELSE 'Setting' END || ' the description of index ' ||
                    REPLACE(REPLACE(ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name", '''', ''''''), '%', '%%') || ''';' || CHR(10) ||
                    'COMMENT ON INDEX ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."Name") || ' IS ' ||
                    CASE WHEN ti."Description" = '' THEN 'NULL' ELSE '''' || REPLACE(ti."Description", '''', '''''') || '''' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''index'', ''' || REPLACE(ti."TableSchema" || '.' || ti."TableName" || '.' || ti."Name", '''', '''''') || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_indexes ti
    JOIN pg_class tc ON tc.relname = ti."TableName" AND tc.relkind = 'r'
                    AND tc.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
    JOIN pg_index idx ON idx.indrelid = tc.oid
    JOIN pg_class i ON i.oid = idx.indexrelid AND i.relname = ti."Name"
   WHERE ti."Description" IS NOT NULL
     AND COALESCE(OBJ_DESCRIPTION(i.oid, 'pg_class'), '') IS DISTINCT FROM ti."Description";
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
END $$;
