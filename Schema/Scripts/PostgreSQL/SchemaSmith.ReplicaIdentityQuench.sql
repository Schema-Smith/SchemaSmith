-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- REPLICA IDENTITY is applied here rather than in ModifiedTableQuench's "Fixup Table Attributes" block
-- because the USING INDEX form names an index, and ModifiedTableQuench runs BEFORE
-- MissingIndexesAndConstraintsQuench -- so on the first deploy of a table the index does not exist yet.
-- Same dependency, and same resolution, as SQL Server's ChangeTrackingQuench needing a primary key.
-- Callers must therefore invoke this AFTER the index pass. #407
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ReplicaIdentityQuench"(p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
    sql_script TEXT;
    bad_row RECORD;
BEGIN
    -- An undeclared ReplicaIdentity means "leave the server alone", so this is a hard no-op for every
    -- package authored before this shipped -- including every package extraction produces for a table
    -- still at the default.
    IF NOT EXISTS (SELECT 1 FROM temp_tables WHERE COALESCE("ReplicaIdentity", '') != '') THEN
        RETURN;
    END IF;

    FOR bad_row IN
        SELECT "Schema", "Name"
          FROM temp_tables
         WHERE "ReplicaIdentity" = 'INDEX'
           AND COALESCE("ReplicaIdentityIndex", '') = ''
    LOOP
        RAISE EXCEPTION 'Table %.% declares ReplicaIdentity INDEX but no ReplicaIdentityIndex. Name the unique index that carries the identity.',
            bad_row."Schema", bad_row."Name";
    END LOOP;

    -- Under WhatIf nothing has been created, so a not-yet-existing index is expected rather than wrong.
    IF NOT p_WhatIf THEN
        FOR bad_row IN
            SELECT t."Schema", t."Name", t."ReplicaIdentityIndex"
              FROM temp_tables t
             WHERE t."ReplicaIdentity" = 'INDEX'
               AND NOT EXISTS (SELECT 1
                                 FROM pg_class ic
                                 JOIN pg_namespace n ON n.oid = ic.relnamespace
                                WHERE ic.relname = t."ReplicaIdentityIndex"
                                  AND n.nspname = t."Schema"
                                  AND ic.relkind = 'i')
        LOOP
            RAISE EXCEPTION 'Table %.% declares ReplicaIdentityIndex "%" but no such index exists. PostgreSQL requires a unique, non-partial index over NOT NULL columns.',
                bad_row."Schema", bad_row."Name", bad_row."ReplicaIdentityIndex";
        END LOOP;
    END IF;

    RAISE NOTICE 'Fixup Replica Identity';
    SELECT STRING_AGG('RAISE NOTICE ''  Setting replica identity for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                      'ALTER TABLE "' || t."Schema" || '"."' || t."Name" || '" REPLICA IDENTITY ' ||
                      CASE t."ReplicaIdentity"
                           WHEN 'INDEX' THEN 'USING INDEX "' || t."ReplicaIdentityIndex" || '"'
                           ELSE t."ReplicaIdentity"
                           END, ';' || CHR(10)) || ';'
      INTO sql_script
      FROM temp_tables t
      JOIN pg_class c ON c.relname = t."Name"
                     AND c.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
                     AND c.relkind = 'r'
     WHERE t."ReplicaIdentity" != ''
       -- Compare the MODE, and for INDEX also the index actually carrying the identity: switching the
       -- identity from one unique index to another leaves relreplident at 'i' and would otherwise look
       -- like no change at all.
       AND (CASE c.relreplident WHEN 'd' THEN 'DEFAULT' WHEN 'f' THEN 'FULL' WHEN 'n' THEN 'NOTHING' WHEN 'i' THEN 'INDEX' END
              IS DISTINCT FROM t."ReplicaIdentity"
            OR (t."ReplicaIdentity" = 'INDEX'
                AND COALESCE((SELECT ic.relname
                                FROM pg_index ix
                                JOIN pg_class ic ON ic.oid = ix.indexrelid
                               WHERE ix.indrelid = c.oid
                                 AND ix.indisreplident), '') IS DISTINCT FROM t."ReplicaIdentityIndex"));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
END $$;
