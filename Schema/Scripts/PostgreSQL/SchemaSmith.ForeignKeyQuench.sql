-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."ForeignKeyQuench"(p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
BEGIN
  RAISE NOTICE 'Add Missing Foreign Keys';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing foreign key ' || fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name" || CASE WHEN COALESCE(fk."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(fk."VariantName", '''', '''''') || ')' ELSE '' END || ''';' || CHR(10) ||
                    'ALTER TABLE  "' || fk."TableSchema" || '"."' || fk."TableName" || '" ADD CONSTRAINT "' || fk."Name" || '" FOREIGN KEY (' || "SchemaSmith"."QuoteColumnList"(fk."Columns") || ')' ||
                    ' REFERENCES "' || fk."RelatedTableSchema" || '"."' || fk."RelatedTable" || '" (' || "SchemaSmith"."QuoteColumnList"(fk."RelatedColumns") || ')' ||
                    CASE WHEN NULLIF(fk."MatchType", '') IS NOT NULL THEN ' MATCH ' || fk."MatchType" ELSE '' END ||
                    CASE WHEN fk."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                    CASE WHEN fk."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END ||
                    CASE WHEN NULLIF(fk."DeleteAction", '') IS NOT NULL THEN ' ON DELETE ' || fk."DeleteAction" ELSE '' END ||
                    CASE WHEN NULLIF(fk."UpdateAction", '') IS NOT NULL THEN ' ON UPDATE ' || fk."UpdateAction" ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ''' || fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_fks fk
    WHERE NOT EXISTS (SELECT 1
                        FROM pg_constraint con
                        JOIN pg_class rel ON rel.oid = con.conrelid
                        JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                             AND nsp.nspname = fk."TableSchema"
                                             AND rel.relname = fk."TableName"
                        WHERE con.contype = 'f'
                          AND con.conname = fk."Name");
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- #363: WhatIf twin of the embedded 'foreignKey'/'created' audit above; same predicate.
  IF p_WhatIf THEN
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'foreignKey', fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name", 'wouldCreate'
        FROM temp_fks fk
        WHERE NOT EXISTS (SELECT 1
                            FROM pg_constraint con
                            JOIN pg_class rel ON rel.oid = con.conrelid
                            JOIN pg_namespace nsp ON nsp.oid = con.connamespace
                                                 AND nsp.nspname = fk."TableSchema"
                                                 AND rel.relname = fk."TableName"
                            WHERE con.contype = 'f'
                              AND con.conname = fk."Name");
  END IF;
END
$$;
