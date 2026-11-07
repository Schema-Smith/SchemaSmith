-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- OnlineApply (OFF, PREFER, REQUIRE; a table's own value over it): on an existing table a key is added NOT VALID, which
-- holds its locks for an instant, and validated after this procedure (DeferOnlineWork) under the weaker lock VALIDATE takes.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ForeignKeyQuench"(p_WhatIf BOOLEAN = FALSE, p_OnlineApply TEXT = 'OFF')
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
  v_Key RECORD;
  v_Sql TEXT;
  v_Failed TEXT;
BEGIN
  -- Unsupported-feature policy: ON DELETE SET NULL (columns) is PostgreSQL 15. Below it the key is created with a plain
  -- SET NULL, which sets every key column; 'fail' aborts before anything is created, 'warn' (default) records one
  -- downgraded manifest row per key.
  IF "SchemaSmith"."ServerVersionNum"() < 15 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_fks WHERE "SetNullColumnsDeclared" <> '') THEN
      RAISE EXCEPTION 'ON DELETE SET NULL (columns) requires PostgreSQL 15 (detected major %); key(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_fks WHERE "SetNullColumnsDeclared" <> '');
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'ON DELETE SET NULL (columns) (PG15)',
               "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
          FROM temp_fks
          WHERE "SetNullColumnsDeclared" <> '';
    END IF;
  END IF;

  RAISE NOTICE 'Add Missing Foreign Keys';
  SELECT STRING_AGG('RAISE NOTICE ''  Add missing foreign key %.%.%'', ''' || REPLACE((fk."TableSchema")::TEXT, '''', '''''') || ''', ''' || REPLACE((fk."TableName")::TEXT, '''', '''''') || ''', ''' || REPLACE((fk."Name" || CASE WHEN COALESCE(fk."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(fk."VariantName", '''', '''''') || ')' ELSE '' END ||
                    CASE WHEN v."Online" THEN ' (online, validated after this pass)'
                         WHEN v."Form" = 'unavailable' THEN ' (online unavailable: ' || "SchemaSmith"."OnlineUnavailableReason"(fk."TableSchema", fk."TableName", 'foreignKey') || ')'
                         ELSE '' END)::TEXT, '''', '''''') || ''';' || CHR(10) ||
                    'ALTER TABLE  ' || "SchemaSmith"."QuoteName"(fk."TableSchema") || '.' || "SchemaSmith"."QuoteName"(fk."TableName") || ' ADD CONSTRAINT ' || "SchemaSmith"."QuoteName"(fk."Name") || ' FOREIGN KEY (' || "SchemaSmith"."QuoteColumnList"(fk."Columns") || ')' ||
                    ' REFERENCES ' || "SchemaSmith"."QuoteName"(fk."RelatedTableSchema") || '.' || "SchemaSmith"."QuoteName"(fk."RelatedTable") || ' (' || "SchemaSmith"."QuoteColumnList"(fk."RelatedColumns") || ')' ||
                    CASE WHEN NULLIF(fk."MatchType", '') IS NOT NULL THEN ' MATCH ' || fk."MatchType" ELSE '' END ||
                    -- The referential actions precede DEFERRABLE in the grammar; after it they are a syntax error.
                    CASE WHEN NULLIF(fk."DeleteAction", '') IS NOT NULL THEN ' ON DELETE ' || fk."DeleteAction" ELSE '' END ||
                    -- The subset SET NULL or SET DEFAULT applies to (15+); without it every key column is set.
                    CASE WHEN fk."DeleteAction" IN ('SET NULL', 'SET DEFAULT') AND NULLIF(fk."SetNullColumns", '') IS NOT NULL THEN ' (' || "SchemaSmith"."QuoteColumnList"(fk."SetNullColumns") || ')' ELSE '' END ||
                    CASE WHEN NULLIF(fk."UpdateAction", '') IS NOT NULL THEN ' ON UPDATE ' || fk."UpdateAction" ELSE '' END ||
                    CASE WHEN fk."Deferrable" THEN ' DEFERRABLE' ELSE '' END ||
                    CASE WHEN fk."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE '' END ||
                    -- NOT ENFORCED is PostgreSQL 18 (below it the key deploys enforced, see MissingIndexesAndConstraintsQuench);
                    -- NOT VALID leaves the existing rows unchecked, and a NOT ENFORCED key is not validated by definition.
                    CASE WHEN NOT fk."Enforced" AND "SchemaSmith"."ServerVersionNum"() >= 18 THEN ' NOT ENFORCED' ELSE '' END ||
                    CASE WHEN NOT COALESCE(fk."ValidatedDeclared", sa."WasTrusted", TRUE) AND NOT (NOT fk."Enforced" AND "SchemaSmith"."ServerVersionNum"() >= 18) THEN ' NOT VALID'
                         WHEN v."Online" THEN ' NOT VALID' ELSE '' END || ';' || CHR(10) ||
                    CASE WHEN v."Online" AND p_WhatIf THEN v."Validate" || CHR(10)
                         WHEN v."Online" THEN 'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Validating foreign key ' || fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name") || ', ' || quote_literal(v."Validate") || ', NULL, NULL);' || CHR(10)
                         ELSE '' END ||
                    CASE WHEN v."Form" IS NULL
                         THEN 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ''' || REPLACE((fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name")::TEXT, '''', '''''') || ''', ''created'');'
                         ELSE 'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''foreignKey'', ''' || REPLACE((fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name")::TEXT, '''', '''''') || ''', ''created'', ''' || REPLACE((v."Form")::TEXT, '''', '''''') || ''');' END, CHR(10))
    INTO sql_script
    FROM temp_fks fk
    -- A key a table rebuild set aside (RebuildTable) keeps the trust it had, where the package does not declare Validated.
    LEFT JOIN "SchemaSmith"."RebuildSetAsideForeignKeys" sa ON sa."ChildSchema" = fk."TableSchema" AND sa."ChildTable" = fk."TableName" AND sa."KeyName" = fk."Name"
    -- Online needs a validation to defer: a key declared not validated, or not enforced, is already the light form.
    CROSS JOIN LATERAL (SELECT f."Form", f."Form" = 'online' AND COALESCE(fk."ValidatedDeclared", sa."WasTrusted", TRUE) AND fk."Enforced" AS "Online",
                               'ALTER TABLE ' || "SchemaSmith"."QuoteName"(fk."TableSchema") || '.' || "SchemaSmith"."QuoteName"(fk."TableName") ||
                               ' VALIDATE CONSTRAINT ' || "SchemaSmith"."QuoteName"(fk."Name") || ';' AS "Validate"
                          FROM (SELECT "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, fk."TableSchema", fk."TableName", 'foreignKey') AS "Form") f) v
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
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
      SELECT pg_backend_pid(), 'foreignKey', fk."TableSchema" || '.' || fk."TableName" || '.' || fk."Name", 'wouldCreate',
             "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, fk."TableSchema", fk."TableName", 'foreignKey')
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

  -- The inbound keys a table rebuild set aside (RebuildTable). A key back on its table -- declared and added above, or
  -- re-created by hand -- is done with; one whose table is gone cannot come back. Every other one is added again from
  -- its captured definition, NOT VALID if it was. Any run restores any of them, and one that fails stays recorded,
  -- named in the error, for the next run.
  IF EXISTS (SELECT 1 FROM "SchemaSmith"."RebuildSetAsideForeignKeys") THEN
    RAISE NOTICE 'Restore Foreign Keys Set Aside By A Table Rebuild';
    FOR v_Key IN SELECT * FROM "SchemaSmith"."RebuildSetAsideForeignKeys" ORDER BY "ChildSchema", "ChildTable", "KeyName" LOOP
      IF to_regclass(quote_ident(v_Key."ChildSchema") || '.' || quote_ident(v_Key."ChildTable")) IS NULL
         OR EXISTS (SELECT 1 FROM pg_constraint con
                     WHERE con.contype = 'f' AND con.conname = v_Key."KeyName"
                       AND con.conrelid = to_regclass(quote_ident(v_Key."ChildSchema") || '.' || quote_ident(v_Key."ChildTable"))) THEN
        IF to_regclass(quote_ident(v_Key."ChildSchema") || '.' || quote_ident(v_Key."ChildTable")) IS NULL THEN
          RAISE NOTICE '  Not restoring foreign key % on %.%: the table no longer exists', v_Key."KeyName", v_Key."ChildSchema", v_Key."ChildTable";
        END IF;
        IF NOT p_WhatIf THEN
          DELETE FROM "SchemaSmith"."RebuildSetAsideForeignKeys"
           WHERE "ChildSchema" = v_Key."ChildSchema" AND "ChildTable" = v_Key."ChildTable" AND "KeyName" = v_Key."KeyName";
        END IF;
        CONTINUE;
      END IF;
      v_Sql := 'ALTER TABLE ' || quote_ident(v_Key."ChildSchema") || '.' || quote_ident(v_Key."ChildTable") ||
               ' ADD CONSTRAINT ' || quote_ident(v_Key."KeyName") || ' ' || v_Key."Definition" ||
               CASE WHEN v_Key."WasTrusted" THEN '' ELSE ' NOT VALID' END || ';';
      RAISE NOTICE '  Restoring foreign key %.%.%', v_Key."ChildSchema", v_Key."ChildTable", v_Key."KeyName";
      IF p_WhatIf THEN
        RAISE NOTICE '%', v_Sql;
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          VALUES (pg_backend_pid(), 'foreignKey', v_Key."ChildSchema" || '.' || v_Key."ChildTable" || '.' || v_Key."KeyName", 'wouldCreate');
      ELSE
        BEGIN
          EXECUTE v_Sql;
          INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
            VALUES (pg_backend_pid(), 'foreignKey', v_Key."ChildSchema" || '.' || v_Key."ChildTable" || '.' || v_Key."KeyName", 'created');
          DELETE FROM "SchemaSmith"."RebuildSetAsideForeignKeys"
           WHERE "ChildSchema" = v_Key."ChildSchema" AND "ChildTable" = v_Key."ChildTable" AND "KeyName" = v_Key."KeyName";
        EXCEPTION
          -- The rebuilt table does not have the key this one references yet: the run that set it aside stopped before
          -- its index pass, and this may be another product's deploy. Its record waits for the run that puts that key back.
          WHEN invalid_foreign_key THEN
            RAISE NOTICE '  Foreign key %.%.% waits: %.% has no key for it to reference yet', v_Key."ChildSchema", v_Key."ChildTable", v_Key."KeyName", v_Key."ParentSchema", v_Key."ParentTable";
          WHEN OTHERS THEN
            v_Failed := COALESCE(v_Failed || '; ', '') || v_Key."ChildSchema" || '.' || v_Key."ChildTable" || '.' || v_Key."KeyName" || ' (' || SQLERRM || ')';
        END;
      END IF;
    END LOOP;
    IF v_Failed IS NOT NULL THEN
      RAISE EXCEPTION 'Foreign keys set aside by a table rebuild could not be restored, and stay recorded for the next run: %', v_Failed;
    END IF;
  END IF;
END
$$;
