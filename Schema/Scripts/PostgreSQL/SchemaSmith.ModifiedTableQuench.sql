-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- INDEX REMOVAL LIVES HERE on PostgreSQL (p_DropIndexesRemovedFromProduct, ANDed with the table's own
-- value). Placement differs by engine: SQL Server also removes here; MySQL/MariaDB remove in
-- MissingIndexesAndConstraintsQuench instead. Do not infer capability from a missing parameter.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ModifiedTableQuench"
  (p_WhatIf BOOLEAN = FALSE,
   p_DropUnknownIndexes BOOLEAN = FALSE,
   p_DropTablesRemovedFromProduct BOOLEAN = TRUE,
   p_DropColumnsRemovedFromProduct BOOLEAN = TRUE,
   p_DropForeignKeysRemovedFromProduct BOOLEAN = TRUE,
   p_DropCheckConstraintsRemovedFromProduct BOOLEAN = TRUE,
   p_DropExcludeConstraintsRemovedFromProduct BOOLEAN = TRUE,
   p_DropStatisticsRemovedFromProduct BOOLEAN = TRUE,
   p_DropIndexesRemovedFromProduct BOOLEAN = TRUE,
   p_CaptureWouldDrop BOOLEAN = FALSE,
   -- The RESOLVED upper-tier RebuildPolicy (environment -> product -> template), already collapsed to a
   -- single whole policy by ProductQuench.ResolveCascadedPolicy. It applies to a table ONLY when that
   -- table declared no policy of its own; a table that declared one takes ITS policy entire (see the
   -- decision point below). Defaults are the NEVER default of the domain object, so a caller that passes
   -- nothing -- every pre-existing caller, and every package with no RebuildPolicy anywhere -- can never
   -- elect a rebuild.
   p_RebuildPolicyMode TEXT = 'NEVER',
   p_RebuildPolicyThreshold INT = NULL,
   p_RebuildPolicyOnOrderMismatch BOOLEAN = FALSE)
  LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT = '';
  protect_notice TEXT;
  partitioned_tables TEXT;
  rebuild_target RECORD;
BEGIN
    -- OldName rename parity: a table renamed via OldName still has its PRE-rename name in
    -- temp_product_ownership (owned on the prior deploy). It is NOT removed from the product — the
    -- physical table now lives under the new name (MissingTableAndColumnQuench already did the
    -- ALTER ... RENAME) — so drop the stale old-name row from the ownership working set before ANY
    -- removed-table pass below. Otherwise the drop pass routes the already-renamed old name through
    -- CustomTableDrop (recyclebin targets) or DROP TABLE and errors / emits false drop audits.
    -- The persistent ProductOwnership row is reconciled old->new later by FixupTableOwnership
    -- (its catalog-existence prune drops the old name; the new name is inserted).
    DELETE FROM temp_product_ownership tp
      WHERE tp."IndexName" IS NULL
        AND EXISTS (SELECT 1
                      FROM temp_tables t2
                      WHERE t2."Schema" = tp."Schema"
                        AND NULLIF(t2."OldName", '') = tp."TableName");

    -- OldName constraint-rename parity: index/constraint ownership rows (IndexName NOT NULL) still
    -- carry the PRE-rename table name after MissingTableAndColumnQuench renamed the table. Unlike SQL
    -- Server (object_id-keyed extended properties that survive a rename), these rows are table-name
    -- scoped, so the removed-from-product index/constraint drop pass below -- keyed on the NEW table
    -- name -- never matches a carried-over old-named PK/unique/index and never drops it. When the
    -- package also renames the constraint (new PK name on the renamed table) the add pass then issues
    -- a second ADD PRIMARY KEY -> 42P16. Migrate the index/constraint ownership rows old->new so the
    -- drop pass recognises the old-named object as removed and drops it before the add pass. Must be
    -- UPDATE not DELETE: SchemaSmith only drops indexes it owns, so the row must survive pointing at
    -- the renamed table. FKs are unaffected (their drop pass is structural/by-absence, not ownership).
    UPDATE temp_product_ownership tp
       SET "TableName" = t2."Name"
      FROM temp_tables t2
     WHERE tp."IndexName" IS NOT NULL
       AND t2."Schema" = tp."Schema"
       AND NULLIF(t2."OldName", '') = tp."TableName";

    -- No-drop protection tier (#270): when protected mode is active the caller forces
    -- p_DropTablesRemovedFromProduct to FALSE so the table-drop pass below is skipped. Record the
    -- tables that WOULD have been dropped by absence (owned, absent from the package, not sticky
    -- PreventDrop) to the ChangeAudit seam as 'dropSuppressed' so the run can surface a manifest. Same
    -- by-absence predicate as the drop pass; audit rows only, so this runs regardless of p_WhatIf.
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture tables suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'table', tp."Schema" || '.' || tp."TableName", 'dropSuppressed'
          FROM temp_product_ownership tp
          WHERE tp."IndexName" IS NULL
            AND NOT COALESCE(tp."PreventDrop", FALSE)
            AND NOT EXISTS (SELECT 1
                              FROM temp_tables t
                              WHERE tp."Schema" = t."Schema"
                                AND tp."TableName" = t."Name")
            AND NOT EXISTS (SELECT 1
                              FROM pg_matviews mv
                              WHERE mv.schemaname = tp."Schema"
                                AND mv.matviewname = tp."TableName");
    END IF;
    IF p_DropTablesRemovedFromProduct THEN
      -- Data-loss guard: a partitioned table spreads data across multiple physical partitions
      -- that DROP TABLE destroys outright. SchemaSmith has no partitioning support -- partitioning
      -- only happens by hand, typically once a table has grown -- so an ordinary product-owned
      -- table can be partitioned after deployment and later look like an ordinary drop-by-absence
      -- candidate. Fail closed before any DDL below, in both live and WhatIf mode, mirroring the
      -- version-gated 'fail' branches elsewhere in this proc. relispartition also catches a table
      -- ATTACHed as a child partition of another parent -- the realistic "converted in place" path,
      -- since PostgreSQL cannot ALTER an existing plain table into a partitioned parent; relkind =
      -- 'p' catches the table itself being a partitioned parent.
      SELECT STRING_AGG(tp."Schema" || '.' || tp."TableName", ', ')
        INTO partitioned_tables
        FROM temp_product_ownership tp
        JOIN pg_class c     ON c.relname = tp."TableName"
        JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = tp."Schema"
        WHERE tp."IndexName" IS NULL
          AND NOT COALESCE(tp."PreventDrop", FALSE)
          AND NOT EXISTS (SELECT 1 FROM temp_tables t WHERE tp."Schema" = t."Schema" AND tp."TableName" = t."Name")
          AND NOT EXISTS (SELECT 1 FROM pg_matviews mv WHERE mv.schemaname = tp."Schema" AND mv.matviewname = tp."TableName")
          AND (c.relkind = 'p' OR c.relispartition);
      IF partitioned_tables IS NOT NULL THEN
        RAISE EXCEPTION 'Partitioned table(s) removed from the product but not dropped (data-loss guard): %. SchemaSmith cannot verify that data spread across partitions can be safely destroyed by DROP TABLE. Drop the table manually after confirming the data is no longer needed, or mark it PreventDrop to keep it in the product permanently.',
          partitioned_tables;
      END IF;

      RAISE NOTICE 'Drop inbound foreign keys referencing tables removed from the product';
      -- Drop any foreign key that REFERENCES a table about to be removed (from any table), so the
      -- table drop below does not fail on a still-present inbound dependency. Same removed-table
      -- predicate as the drop pass; catalog-driven so it is not limited to product-declared parents.
      SELECT STRING_AGG('ALTER TABLE "' || pn.nspname || '"."' || pc.relname || '" DROP CONSTRAINT IF EXISTS "' || con.conname || '";' || CHR(10) ||
                        'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ''' || pn.nspname || '.' || pc.relname || '.' || con.conname || ''', ''dropped'');', CHR(10))
        INTO sql_script
        FROM temp_product_ownership tp
        JOIN pg_class fc       ON fc.relname = tp."TableName"
        JOIN pg_namespace fn   ON fn.oid = fc.relnamespace AND fn.nspname = tp."Schema"
        JOIN pg_constraint con ON con.contype = 'f' AND con.confrelid = fc.oid
        JOIN pg_class pc       ON pc.oid = con.conrelid
        JOIN pg_namespace pn   ON pn.oid = pc.relnamespace
        WHERE tp."IndexName" IS NULL
          AND NOT COALESCE(tp."PreventDrop", FALSE)  -- a protected table survives, so keep its inbound FKs (#270)
          AND NOT EXISTS (SELECT 1
                            FROM temp_tables t
                            WHERE tp."Schema" = t."Schema"
                              AND tp."TableName" = t."Name")
          AND NOT EXISTS (SELECT 1
                            FROM pg_matviews mv
                            WHERE mv.schemaname = tp."Schema"
                              AND mv.matviewname = tp."TableName");
      CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

      -- #363: WhatIf twin of the embedded inbound-FK 'dropped' audit above; same source/predicate.
      IF p_WhatIf THEN
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'foreignKey', pn.nspname || '.' || pc.relname || '.' || con.conname, 'wouldDrop'
            FROM temp_product_ownership tp
            JOIN pg_class fc       ON fc.relname = tp."TableName"
            JOIN pg_namespace fn   ON fn.oid = fc.relnamespace AND fn.nspname = tp."Schema"
            JOIN pg_constraint con ON con.contype = 'f' AND con.confrelid = fc.oid
            JOIN pg_class pc       ON pc.oid = con.conrelid
            JOIN pg_namespace pn   ON pn.oid = pc.relnamespace
            WHERE tp."IndexName" IS NULL
              AND NOT COALESCE(tp."PreventDrop", FALSE)
              AND NOT EXISTS (SELECT 1 FROM temp_tables t WHERE tp."Schema" = t."Schema" AND tp."TableName" = t."Name")
              AND NOT EXISTS (SELECT 1 FROM pg_matviews mv WHERE mv.schemaname = tp."Schema" AND mv.matviewname = tp."TableName");
      END IF;

      RAISE NOTICE 'Drop tables removed from the product';
      -- temp_product_ownership is template-and-schema-scoped (see ValidateTableOwnership),
      -- so this pass only considers tables owned by the current (template, schema)
      -- iteration. No additional restriction needed here; the predicate already excludes
      -- other templates' tables AND other tenants' tables of the same template.
      SELECT STRING_AGG('RAISE NOTICE ''  Table ' || tp."Schema" || '.' || tp."TableName" || ' no longer in product'';' || CHR(10) ||
                        CASE WHEN EXISTS (SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_namespace n ON p.pronamespace = n.oid WHERE p.proname = 'CustomTableDrop' AND n.nspname = 'SchemaSmith' )
                             THEN 'CALL "SchemaSmith"."CustomTableDrop"(''' || tp."Schema" || ''', ''' || tp."TableName" || ''');'
                             ELSE 'DROP TABLE IF EXISTS "' || tp."Schema" || '"."' || tp."TableName" || '";'
                             END || CHR(10) ||
                        'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''table'', ''' || tp."Schema" || '.' || tp."TableName" || ''', ''dropped'');', CHR(10))
        INTO sql_script
        FROM temp_product_ownership tp
        WHERE tp."IndexName" IS NULL
          AND NOT COALESCE(tp."PreventDrop", FALSE)
          AND NOT EXISTS (SELECT 1
                            FROM temp_tables t
                            WHERE tp."Schema" = t."Schema"
                              AND tp."TableName" = t."Name")
          AND NOT EXISTS (SELECT 1
                            FROM pg_matviews mv
                            WHERE mv.schemaname = tp."Schema"
                              AND mv.matviewname = tp."TableName");
      CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

      -- #363: WhatIf twin of the embedded table 'dropped' audit above; same source/predicate.
      IF p_WhatIf THEN
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'table', tp."Schema" || '.' || tp."TableName", 'wouldDrop'
            FROM temp_product_ownership tp
            WHERE tp."IndexName" IS NULL
              AND NOT COALESCE(tp."PreventDrop", FALSE)
              AND NOT EXISTS (SELECT 1 FROM temp_tables t WHERE tp."Schema" = t."Schema" AND tp."TableName" = t."Name")
              AND NOT EXISTS (SELECT 1 FROM pg_matviews mv WHERE mv.schemaname = tp."Schema" AND mv.matviewname = tp."TableName");
      END IF;
    END IF;

    -- Report protected-but-absent tables as skipped regardless of the global drop toggle (#270).
    RAISE NOTICE 'Report tables removed from product but protected by PreventDrop';
    SELECT STRING_AGG('RAISE NOTICE ''  Table ' || tp."Schema" || '.' || tp."TableName" ||
                      ' removed from product but PreventDrop is set — skipping drop (protected)'';', CHR(10))
      INTO protect_notice
      FROM temp_product_ownership tp
      WHERE tp."IndexName" IS NULL
        AND COALESCE(tp."PreventDrop", FALSE)
        AND NOT EXISTS (SELECT 1 FROM temp_tables t
                          WHERE tp."Schema" = t."Schema" AND tp."TableName" = t."Name");
    CALL "SchemaSmith"."ExecuteOrDebug"(protect_notice, p_WhatIf);

    RAISE NOTICE 'Collect Existing Column Definitions';
    DROP TABLE IF EXISTS temp_existing_columns;
    CREATE TEMPORARY TABLE temp_existing_columns AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             c.column_name AS "ColumnName",
             -- An array column reports udt_name '_text' while the package declares 'text[]', so the composed
             -- form was never equal to the declared one and the column was re-modified on every deploy --
             -- for ANY PostgreSQL array column, not one type. format_type renders the canonical declared
             -- element spelling this codebase uses everywhere else (udt_name), with the element typmod taken from
             -- format_type, which is the only place it survives: information_schema reports NULL length for
             -- an array column, which is why the composed form had no (20) to begin with.
             CASE WHEN c.data_type = 'ARRAY' THEN REGEXP_REPLACE(c.udt_name, '^_', '') || COALESCE(SUBSTRING(format_type(a.atttypid, a.atttypmod) FROM '\(.*\)'), '') || '[]' ELSE
             CASE WHEN c.domain_name IS NOT NULL
                  THEN CASE WHEN c.domain_schema != 'pg_catalog' THEN '"' || c.domain_schema || '".' ELSE '' END || '"' || c.domain_name || '"'
                  ELSE CASE WHEN c.udt_schema != 'pg_catalog' THEN c.udt_schema || '.' ELSE '' END || REGEXP_REPLACE(c.udt_name, 'bpchar', 'CHAR', 'i')
                  END ||
             "SchemaSmith"."ColumnTypeArguments"(c.domain_name, c.udt_name, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision) END AS "DataType",
             CAST(CASE WHEN c.is_nullable = 'YES' THEN TRUE ELSE FALSE END AS BOOLEAN) AS "Nullable",
             c.column_default AS "Default",
             c.collation_name AS "Collation",
             CASE WHEN a.attidentity = 'd'
                  THEN 'GENERATED BY DEFAULT AS IDENTITY'
                  WHEN a.attidentity = 'a'
                  -- Identity KIND only. SchemaSmith does not manage the identity sequence's
                  -- START/INCREMENT declaratively (the package format can't express them), so
                  -- capturing them here only produced spurious "modified" diffs — an identity
                  -- column then built an empty ALTER clause that stranded a sibling clause's
                  -- trailing comma, failing the Alter phase with 42601 on PG < 17.
                  THEN 'GENERATED ALWAYS AS IDENTITY'
                  ELSE c.is_generated END AS "Generated",
             c.generation_expression AS "GenerationExpression",
             CASE WHEN a.attgenerated = '' OR a.attgenerated = 's' THEN FALSE ELSE TRUE END AS "Virtual",
             CASE a.attstorage
                  WHEN 'p' THEN 'PLAIN'
                  WHEN 'm' THEN 'MAIN'
                  WHEN 'e' THEN 'EXTERNAL'
                  WHEN 'x' THEN 'EXTENDED'
                  ELSE 'DEFAULT' END AS "Storage",
             "SchemaSmith"."ColumnCompression"(a.attrelid, a.attnum) AS "Compression",
             '' AS "OldName"
        FROM temp_tables t
        JOIN information_schema.columns c ON c.table_schema = t."Schema"
                                         AND c.table_name = t."Name"
        JOIN pg_attribute a ON a.attrelid = ('"' || c.table_schema || '"' ||  '.' || '"' ||  c.table_name || '"')::regclass
                           AND a.attname = c.column_name
                           AND NOT a.attisdropped
        WHERE t."Schema" = c.table_schema
          AND t."Name" = c.table_name;

    ------------------------------------------------------------------------------------------------
    -- THE REBUILD DECISION POINT.
    --
    -- WHY HERE. temp_existing_columns -- the only live-column snapshot this procedure takes, and the
    -- thing every column pass below compares against -- was just built one statement above, so column
    -- detection is complete. And nothing has yet touched a table that is STILL DECLARED: the three
    -- ExecuteOrDebug calls above this line drop tables that left the product entirely (and their
    -- inbound keys). The first one that dismantles a dependent object OF a declared table is the
    -- foreign-key drop further down, and a rebuild drops all of those wholesale anyway; a rebuild
    -- inheriting a half-dismantled table would have RebuildTable's pre-rebuild refusals reasoning
    -- about a live state that no longer matches the declared file. Renames land earlier still, in
    -- MissingTableAndColumnQuench, which matters: RebuildTable refuses outright while a table or
    -- column rename is pending, because the copy matches columns by their CURRENT name.
    --
    -- Placing the decision even earlier than the remaining "Collect Existing ..." snapshots is
    -- deliberate on this engine: those snapshots are taken from the live catalog, so taking them AFTER
    -- the rebuild means the drop passes see the post-rebuild reality (indexes, constraints and keys
    -- already gone with the old table) instead of emitting drops for objects that no longer exist.
    --
    -- OPT-IN BY CONSTRUCTION. The election below is built by a WHERE that only an explicit
    -- ALWAYS/THRESHOLD, or an explicit OnOrderMismatch, can satisfy. A package with no RebuildPolicy
    -- anywhere resolves to the domain object's NEVER default with OnOrderMismatch false at every level,
    -- matches nothing, and the loop body never runs: no rebuild,
    -- no statement, nothing added to the run. A rebuild moves user data, so a table that did not ask
    -- for one must never get one, and that has to be structurally true rather than true because the
    -- conditions happen not to match.
    --
    -- WHOLE-OBJECT RESOLUTION. "RebuildPolicySpecified" picks WHICH policy applies -- the table's own,
    -- or the resolved upper-tier one passed in -- and then every field comes from that ONE policy.
    -- Never a per-field COALESCE: a table declaring only { "Mode": "ALWAYS" } must not inherit a
    -- product's Threshold. Mirrors ProductQuench.ResolveCascadedPolicy.
    ------------------------------------------------------------------------------------------------

    ------------------------------------------------------------------------------------------------
    -- COLUMN-ORDER DRIFT, the input to the OnOrderMismatch trigger.
    --
    -- Reordering existing columns is impossible in place on every engine SchemaSmith supports, so a
    -- rebuild is the only mechanism that can converge a table whose columns are in the wrong order.
    -- This pass names the tables where that is true.
    --
    -- DECLARED ORDER IS "_RowId", because that is what RebuildTable orders the shadow's CREATE by.
    -- Detection and repair MUST read the declared order off the same column: if this pass elected on
    -- one definition of "declared order" and the rebuild produced another, the table would be
    -- re-elected on every subsequent deploy and rebuilt forever -- which on this feature means copying
    -- every row of the table, every deploy.
    --
    -- THE COMPARISON IS RELATIVE, NEVER ABSOLUTE. temp_declared_column_order pairs each column's
    -- declared position with its live ordinal_position, and the mismatch test below looks only for an
    -- INVERSION -- two columns whose declared order disagrees with their live order. Comparing the two
    -- positions for EQUALITY instead would be wrong: PostgreSQL derives ordinal_position from attnum,
    -- which keeps the gap left by every column ever dropped, so a table that is correctly ordered but
    -- has ever lost a column would show declared 2 against live 3 and be rebuilt on every deploy --
    -- the infinite-rebuild trap. An inversion test never reads a position's value, only two positions'
    -- order, so a uniform shift by any number of gaps is invisible to it.
    --
    -- THE COMPARED SET IS THE INTERSECTION, which the join produces by construction.
    --   * Declared AND live -- compared. This is the real question.
    --   * Live but NOT declared -- excluded, having no declared position to compare. These are the
    --     columns this run drops by absence (a table RETAINING one cannot reach the election at all:
    --     the suppressed-drop guard in the election's WHERE excludes it), so they will not exist after
    --     the run and must not drag a correctly-ordered table into a rebuild. Including them would
    --     re-elect any table with a retained column forever -- the same loop from the other direction.
    --   * Declared but NOT live -- cannot occur here: MissingTableAndColumnQuench added it earlier in
    --     the same run, so by this line it is live and the join finds it. It needs no special case.
    --     Note this makes a NEW column in a mid-file position a genuine mismatch, correctly: the ADD
    --     appended it to the end, and only a rebuild can move it.
    ------------------------------------------------------------------------------------------------
    RAISE NOTICE 'Detect declared-vs-deployed column order drift';
    DROP TABLE IF EXISTS temp_declared_column_order;
    CREATE TEMPORARY TABLE temp_declared_column_order AS
      SELECT c."TableSchema", c."TableName", c."_RowId" AS "DeclaredPos", ic.ordinal_position AS "LivePos"
        FROM temp_columns c
        JOIN information_schema.columns ic ON ic.table_schema = c."TableSchema"
                                          AND ic.table_name = c."TableName"
                                          AND ic.column_name = c."Name";

    DROP TABLE IF EXISTS temp_rebuild_order_mismatch;
    CREATE TEMPORARY TABLE temp_rebuild_order_mismatch AS
      SELECT DISTINCT a."TableSchema", a."TableName"
        FROM temp_declared_column_order a
        JOIN temp_declared_column_order b ON b."TableSchema" = a."TableSchema"
                                         AND b."TableName" = a."TableName"
       WHERE a."DeclaredPos" < b."DeclaredPos"
         AND a."LivePos" > b."LivePos";

    RAISE NOTICE 'Elect tables for rebuild';
    -- Materialized rather than iterated straight out of the SELECT, because the loop body DELETEs from
    -- temp_existing_columns and that table is read by the election query itself.
    DROP TABLE IF EXISTS temp_rebuild_election;
    CREATE TEMPORARY TABLE temp_rebuild_election AS
      SELECT p."Schema", p."Name"
        FROM (SELECT t."Schema", t."Name",
                     -- The winning policy, taken WHOLE from one level.
                     UPPER(TRIM(COALESCE(CASE WHEN COALESCE(t."RebuildPolicySpecified", FALSE) THEN t."RebuildPolicyMode" ELSE p_RebuildPolicyMode END, 'NEVER'))) AS "Mode",
                     CASE WHEN COALESCE(t."RebuildPolicySpecified", FALSE) THEN t."RebuildPolicyThreshold" ELSE p_RebuildPolicyThreshold END AS "Threshold",
                     -- OnOrderMismatch COMPOSES with Mode rather than replacing it -- it is one more OR
                     -- arm on the WHERE below, not a fourth Mode value. { "Mode": "THRESHOLD",
                     -- "Threshold": 3, "OnOrderMismatch": true } therefore reads "rebuild once three
                     -- modifications pile up OR once the column order has drifted", and pairing it with
                     -- the NEVER default asks for a rebuild on order drift and nothing else -- the case
                     -- the trigger mainly exists for.
                     COALESCE(CASE WHEN COALESCE(t."RebuildPolicySpecified", FALSE) THEN t."RebuildPolicyOnOrderMismatch" ELSE p_RebuildPolicyOnOrderMismatch END, FALSE) AS "OnOrderMismatch",
                     EXISTS (SELECT 1 FROM temp_rebuild_order_mismatch m
                               WHERE m."TableSchema" = t."Schema" AND m."TableName" = t."Name") AS "OrderMismatch",
                     -- THE THRESHOLD COUNT: column-MODIFICATION passes only, which is what a rebuild
                     -- actually eliminates. The predicate is the one the "Alter Modified Columns" pass
                     -- uses to decide a column needs an ALTER -- MUST stay in lockstep with it (and with
                     -- the two version-gated drop-and-re-add passes, whose columns this deliberately
                     -- counts too: the version tail that carves them OUT of the ALTER pass is omitted
                     -- here, so the count is the same on every server version). Columns that exist only
                     -- in the package (adds) and columns that exist only live (by-absence drops) are not
                     -- counted -- an add is cheap and already applied by MissingTableAndColumnQuench, a
                     -- drop is metadata-only, and index/constraint/statistics churn is not something a
                     -- rebuild saves either. Counting work a rebuild does not save would fire rebuilds
                     -- that cost data movement and buy nothing.
                     (SELECT COUNT(*)
                        FROM temp_columns c
                        JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema"
                                                     AND ec."TableName" = c."TableName"
                                                     AND ec."ColumnName" = c."Name"
                        WHERE c."TableSchema" = t."Schema" AND c."TableName" = t."Name"
                          AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
                            OR c."Nullable" != ec."Nullable"
                            OR COALESCE("SchemaSmith"."StripTypeCast"(c."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(ec."Default"), '')
                            OR COALESCE(c."Collation", '') != COALESCE(ec."Collation", '')
                            OR COALESCE(REGEXP_REPLACE(c."Generated", '\s*\(.*$', ''), 'NEVER') != COALESCE(REGEXP_REPLACE(ec."Generated", '\s*\(.*$', ''), 'NEVER')
                            OR COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
                            OR (COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != COALESCE(ec."Storage", ''))
                            OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", '')))) AS "ModificationPasses",
                     -- ALWAYS fires on ANY detected column change, which includes a column present live
                     -- but absent from the package -- a rebuild delivers that removal as part of building
                     -- the replacement from the declared definition.
                     (EXISTS (SELECT 1
                                FROM temp_existing_columns ec
                                WHERE ec."TableSchema" = t."Schema" AND ec."TableName" = t."Name"
                                  AND NOT EXISTS (SELECT 1 FROM temp_columns c
                                                    WHERE c."TableSchema" = ec."TableSchema"
                                                      AND c."TableName" = ec."TableName"
                                                      AND c."Name" = ec."ColumnName"))) AS "AnyColumnDrop"
                FROM temp_tables t
                -- A rebuild is a by-absence destroyer: the old table goes whole and only the DECLARED
                -- definition comes back, so anything this run deliberately declined to drop by absence
                -- would go anyway. p_CaptureWouldDrop is set exactly when the environment is in protected
                -- mode (ProductQuench sets CaptureWouldDrop = _protectedMode), which promises to destroy
                -- nothing by absence at all. The second arm is the same promise scoped to one table's
                -- columns: a live column the package no longer declares, whose drop THIS run is
                -- suppressing. Either one outranks the policy -- declining to rebuild costs an in-place
                -- ALTER, rebuilding anyway costs the user the data that protection existed to keep.
                WHERE NOT p_CaptureWouldDrop
                  AND NOT (NOT (p_DropColumnsRemovedFromProduct AND COALESCE(t."DropColumnsRemovedFromProduct", TRUE))
                           AND EXISTS (SELECT 1
                                         FROM temp_existing_columns ec
                                         WHERE ec."TableSchema" = t."Schema" AND ec."TableName" = t."Name"
                                           AND NOT EXISTS (SELECT 1 FROM temp_columns c
                                                             WHERE c."TableSchema" = ec."TableSchema"
                                                               AND c."TableName" = ec."TableName"
                                                               AND c."Name" = ec."ColumnName")))
                  AND EXISTS (SELECT 1 FROM information_schema.tables it
                                WHERE it.table_schema = t."Schema" AND it.table_name = t."Name"
                                  AND it.table_type = 'BASE TABLE')) p
       WHERE (p."Mode" = 'ALWAYS' AND (p."ModificationPasses" > 0 OR p."AnyColumnDrop"))
          OR (p."Mode" = 'THRESHOLD' AND p."Threshold" IS NOT NULL AND p."ModificationPasses" >= p."Threshold")
          -- Deliberately NOT conjoined with any Mode or with a detected column change: drifted column
          -- order is a standing reason to rebuild on its own, and on a table whose columns are merely in
          -- the wrong order there is no column CHANGE to detect -- requiring one would make the trigger
          -- unreachable in exactly the case it was added for.
          OR (p."OnOrderMismatch" AND p."OrderMismatch");

    FOR rebuild_target IN SELECT "Schema", "Name" FROM temp_rebuild_election LOOP
      -- p_WhatIf goes straight through: RebuildTable prints its whole sequence and records 'wouldRebuild'
      -- without executing anything, and it applies its refusals in BOTH modes. So a policy that elects a
      -- rebuild on a BLOCKED table surfaces RebuildTable's refusal as an exception and the quench fails --
      -- deliberately. It does NOT quietly fall back to altering in place: that would let a package ask for
      -- a rebuild and silently get something else, and the states that block a rebuild are exactly the
      -- ones where that difference matters.
      CALL "SchemaSmith"."RebuildTable"(rebuild_target."Schema", rebuild_target."Name", p_WhatIf);
    END LOOP;

    -- BYPASS THE IN-PLACE COLUMN PHASES. Every column pass below joins temp_existing_columns, so removing
    -- a rebuilt table's rows from it is the WHOLE bypass, in one place, instead of a "was it rebuilt?"
    -- test threaded through eight queries where one missed site leaves the table both rebuilt AND altered.
    -- It is also simply true: the replacement was built to the declared definition, so there is no longer
    -- any live column state that differs from it. The one column pass that does not read this snapshot --
    -- "Add New Physical Columns Switched From Generated" -- tests information_schema directly and finds
    -- every declared column already present, so it falls out for the same reason rather than needing its
    -- own guard.
    DELETE FROM temp_existing_columns ec
      WHERE EXISTS (SELECT 1 FROM temp_rebuild_election r
                      WHERE r."Schema" = ec."TableSchema" AND r."Name" = ec."TableName");
    DROP TABLE IF EXISTS temp_rebuild_election;

    RAISE NOTICE 'Collect Existing Index Definitions';
    CALL "SchemaSmith"."BuildExistingIndexesSnapshot"();

    RAISE NOTICE 'Collect Existing Foreign Key Definitions';
    DROP TABLE IF EXISTS temp_existing_foreignkeys;
    CREATE TEMPORARY TABLE temp_existing_foreignkeys AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             con.conname AS "KeyName",
             (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                FROM pg_attribute a
                CROSS JOIN LATERAL UNNEST(con.conkey) WITH ORDINALITY AS u(element, idx)
                WHERE a.attrelid = con.conrelid
                  AND a.attnum = element) AS "Columns",
             fnsp.nspname AS "RelatedTableSchema",
             frel.relname AS "RelatedTable",
             (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                FROM pg_attribute a
                CROSS JOIN LATERAL UNNEST(con.confkey) WITH ORDINALITY AS u(element, idx)
                WHERE a.attrelid = frel.oid
                  AND a.attnum = element) AS "RelatedColumns",
             CASE con.confdeltype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' WHEN 'd' THEN 'SET DEFAULT' END AS "DeleteAction",
             CASE con.confupdtype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' WHEN 'd' THEN 'SET DEFAULT' END AS "UpdateAction"
        FROM temp_tables t
        JOIN pg_catalog.pg_constraint con ON con.conrelid = to_regclass('"' || t."Schema" || '"' ||  '.' || '"' ||  t."Name" || '"')
        JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
        JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
        WHERE con.contype = 'f';

    RAISE NOTICE 'Collect Existing Check Constraint Definitions';
    DROP TABLE IF EXISTS temp_existing_checks;
    CREATE TEMPORARY TABLE temp_existing_checks AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             con.conname AS "CheckName",
             -- SAME RULE EXTRACTION USES (GenerateTableJson's check-constraint read), and it has to be:
             -- when the two disagree, a constraint round-trips into a package that the comparison then
             -- reports as changed on every deploy. This was a regex requiring DOUBLE parens, but
             -- pg_get_constraintdef renders SINGLE parens for a bare boolean column or a top-level
             -- function call -- so CHECK (flag) and CHECK (starts_with(tag, 'a')) never had the prefix
             -- stripped and could never compare equal, while CHECK ((length(tag) > 0)) did.
             -- StripParenWrapping counts balanced parens instead of pattern-matching a fixed depth,
             -- so it is right for both shapes. SUBSTRING(... FROM 6) drops the leading "CHECK ".
             "SchemaSmith"."StripParenWrapping"(SUBSTRING(pg_catalog.PG_GET_CONSTRAINTDEF(con.oid) FROM 6)) AS "Expression"
        FROM temp_tables t
        JOIN pg_catalog.pg_constraint con ON con.conrelid = to_regclass('"' || t."Schema" || '"' ||  '.' || '"' ||  t."Name" || '"')
        WHERE con.contype = 'c';

    RAISE NOTICE 'Collect Existing Exclude Constraint Definitions';
    DROP TABLE IF EXISTS temp_existing_excludes;
    CREATE TEMPORARY TABLE temp_existing_excludes AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             i.relname AS "ExcludeName",
             (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid AND i.relkind = 'i') AS "AccessMethod",
             (SELECT STRING_AGG("SchemaSmith"."QuoteIndexColumnList"(PG_GET_INDEXDEF(idx.indexrelid, idx::int4, true)) ||
                                CASE WHEN (idx.indoption[idx-1] & 1) = 1 THEN ' DESC' || CASE WHEN (idx.indoption[idx-1] & 2) = 2 THEN '' ELSE ' NULLS LAST' END
                                     ELSE CASE WHEN (idx.indoption[idx-1] & 2) = 2 THEN ' NULLS FIRST' ELSE '' END
                                     END || ' WITH ' || COALESCE(nsp.nspname || '.' || o.oprname, o.oprname), ',' ORDER BY idx)
                FROM UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                LEFT JOIN LATERAL (SELECT (con.conexclop::oid[])[idx] AS opr_oid) co ON TRUE
                LEFT JOIN pg_operator o ON o.oid = co.opr_oid
                LEFT JOIN pg_namespace nsp ON nsp.oid = o.oprnamespace AND nsp.nspname != 'pg_catalog') AS "ExcludeColumns",
             COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '') AS "FilterExpression",
             COALESCE(con.condeferrable, FALSE) AS "Deferrable",
             COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred"
        FROM temp_tables t
        JOIN pg_index idx ON idx.indrelid = to_regclass('"' || t."Schema" || '"."' || t."Name" || '"')
        JOIN pg_class i ON i.oid = idx.indexrelid
        JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype = 'x';

    RAISE NOTICE 'Collect Existing Statistics Definitions';
    DROP TABLE IF EXISTS temp_existing_statistics;
    CREATE TEMPORARY TABLE temp_existing_statistics AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             se.stxname AS "StatisticsName",
             COALESCE((SELECT STRING_AGG(CASE k WHEN 'd' THEN 'NDISTINCT' WHEN 'f' THEN 'DEPENDENCIES' WHEN 'm' THEN 'MCV' WHEN 'e' THEN 'EXPRESSIONS' ELSE k::text END, ',')
                       FROM UNNEST(se.stxkind) AS k), '') AS "Kind",
             COALESCE(ARRAY_TO_STRING(ARRAY_CAT(COALESCE((SELECT ARRAY_AGG(a.attname::text)
                                                            FROM UNNEST(se.stxkeys) WITH ORDINALITY AS t(attnum, ord)
                                                            JOIN pg_attribute a ON a.attrelid = se.stxrelid AND a.attnum = t.attnum
                                                            WHERE a.attnum > 0),
                                                         ARRAY[]::text[]),
                                                "SchemaSmith"."StatisticsExpressionColumns"(t."Schema", se.stxname)), ','), '') AS "StatisticsColumns"
      FROM temp_tables t
      JOIN  pg_statistic_ext se ON se.stxrelid = to_regclass('"' || t."Schema" || '"."' || t."Name" || '"');

    -- No-drop protection tier (#270): the FK drop pass below still runs in protected mode but its
    -- by-absence branch is gated by p_DropForeignKeysRemovedFromProduct (forced FALSE), so only
    -- modified (same-name) FKs are dropped. Record the FKs that WOULD be dropped by absence — name
    -- gone from the product, per-table flag still honored — to the ChangeAudit seam as 'dropSuppressed'.
    -- Mirrors the drop pass's removed-from-package branch (NOT EXISTS same-name + per-table flag).
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture foreign keys suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'foreignKey', ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName", 'dropSuppressed'
          FROM temp_existing_foreignkeys ek
          JOIN temp_tables tt ON tt."Schema" = ek."TableSchema"
                             AND tt."Name" = ek."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_fks fk
                              WHERE ek."TableSchema" = fk."TableSchema"
                                AND ek."TableName" = fk."TableName"
                                AND ek."KeyName" = fk."Name"
                                AND ek."Columns" = fk."Columns"
                                AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                                AND ek."RelatedTable" = fk."RelatedTable"
                                AND ek."RelatedColumns" = fk."RelatedColumns"
                                AND ek."DeleteAction" = fk."DeleteAction"
                                AND ek."UpdateAction" = fk."UpdateAction")
            AND NOT EXISTS (SELECT 1
                              FROM temp_fks fk2
                              WHERE ek."TableSchema" = fk2."TableSchema"
                                AND ek."TableName" = fk2."TableName"
                                AND ek."KeyName" = fk2."Name")
            AND COALESCE(tt."DropForeignKeysRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Foreign Keys';
    SELECT STRING_AGG('RAISE NOTICE ''  Foreign Key ' || ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName" || ' no longer in product'';' || CHR(10) ||
                      'ALTER TABLE "' || ek."TableSchema" || '"."' || ek."TableName" || '" DROP CONSTRAINT IF EXISTS "' || ek."KeyName" || '" CASCADE;' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ''' || ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName" || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_existing_foreignkeys ek
      JOIN temp_tables tt ON tt."Schema" = ek."TableSchema"
                         AND tt."Name" = ek."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_fks fk
                          WHERE ek."TableSchema" = fk."TableSchema"
                            AND ek."TableName" = fk."TableName"
                            AND ek."KeyName" = fk."Name"
                            AND ek."Columns" = fk."Columns"
                            AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                            AND ek."RelatedTable" = fk."RelatedTable"
                            AND ek."RelatedColumns" = fk."RelatedColumns"
                            AND ek."DeleteAction" = fk."DeleteAction"
                            AND ek."UpdateAction" = fk."UpdateAction")
        -- Split modified vs removed: a same-named FK whose definition changed is dropped
        -- unconditionally (the create-missing pass recreates it); a FK whose name is gone
        -- from the product is a by-absence removal, gated by the cascade flag.
        AND (EXISTS (SELECT 1
                       FROM temp_fks fk2
                       WHERE ek."TableSchema" = fk2."TableSchema"
                         AND ek."TableName" = fk2."TableName"
                         AND ek."KeyName" = fk2."Name")
             OR (p_DropForeignKeysRemovedFromProduct AND COALESCE(tt."DropForeignKeysRemovedFromProduct", TRUE)));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- #363: WhatIf twin of the embedded 'foreignKey'/'dropped' audit above; same source/predicate.
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'foreignKey', ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName", 'wouldDrop'
          FROM temp_existing_foreignkeys ek
          JOIN temp_tables tt ON tt."Schema" = ek."TableSchema"
                             AND tt."Name" = ek."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_fks fk
                              WHERE ek."TableSchema" = fk."TableSchema"
                                AND ek."TableName" = fk."TableName"
                                AND ek."KeyName" = fk."Name"
                                AND ek."Columns" = fk."Columns"
                                AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                                AND ek."RelatedTable" = fk."RelatedTable"
                                AND ek."RelatedColumns" = fk."RelatedColumns"
                                AND ek."DeleteAction" = fk."DeleteAction"
                                AND ek."UpdateAction" = fk."UpdateAction")
            AND (EXISTS (SELECT 1
                           FROM temp_fks fk2
                           WHERE ek."TableSchema" = fk2."TableSchema"
                             AND ek."TableName" = fk2."TableName"
                             AND ek."KeyName" = fk2."Name")
                 OR (p_DropForeignKeysRemovedFromProduct AND COALESCE(tt."DropForeignKeysRemovedFromProduct", TRUE)));
    END IF;

    -- No-drop protection tier (#270): record check constraints that WOULD be dropped by absence —
    -- name gone from the product, per-table flag still honored — as 'dropSuppressed'. Mirrors the drop
    -- pass's removed-from-package branch, keeping the column-level-check (CK_<table>_<column>)
    -- exclusion so a column-owned check is never mis-attributed to the table-level pass.
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture check constraints suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'constraint', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."CheckName", 'dropSuppressed'
          FROM temp_existing_checks ec
          JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                             AND tt."Name" = ec."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_checks c
                              WHERE ec."TableSchema" = c."TableSchema"
                                AND ec."TableName" = c."TableName"
                                AND ec."CheckName" = c."Name"
                                AND ec."Expression" = c."Expression")
            AND NOT EXISTS (SELECT 1
                              FROM temp_columns col
                              WHERE col."TableSchema" = ec."TableSchema"
                                AND col."TableName" = ec."TableName"
                                AND NULLIF(col."CheckExpression", '') IS NOT NULL
                                AND ec."CheckName" = 'CK_' || col."TableName" || '_' || col."Name")
            AND NOT EXISTS (SELECT 1
                              FROM temp_checks c2
                              WHERE ec."TableSchema" = c2."TableSchema"
                                AND ec."TableName" = c2."TableName"
                                AND ec."CheckName" = c2."Name")
            AND COALESCE(tt."DropCheckConstraintsRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Check Constraints';
    SELECT STRING_AGG('RAISE NOTICE ''  Check Constraint ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."CheckName" || ' modified or no longer in product'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP CONSTRAINT IF EXISTS "' || ec."CheckName" || '" CASCADE;' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''constraint'', ''' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."CheckName" || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_existing_checks ec
      JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                         AND tt."Name" = ec."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_checks c
                          WHERE ec."TableSchema" = c."TableSchema"
                            AND ec."TableName" = c."TableName"
                            AND ec."CheckName" = c."Name"
                            AND ec."Expression" = c."Expression")
        -- Column-level checks (CK_<table>_<column>) are owned by the column pass below, not the
        -- table-level CheckConstraints array; excluding them here prevents a phantom drop on every run.
        AND NOT EXISTS (SELECT 1
                          FROM temp_columns col
                          WHERE col."TableSchema" = ec."TableSchema"
                            AND col."TableName" = ec."TableName"
                            AND NULLIF(col."CheckExpression", '') IS NOT NULL
                            AND ec."CheckName" = 'CK_' || col."TableName" || '_' || col."Name")
        -- Split modified vs removed: a same-named check whose expression changed is dropped
        -- unconditionally (the create pass re-adds it); a check whose name is gone from the
        -- product is a by-absence removal, gated by the cascade flag.
        AND (EXISTS (SELECT 1
                       FROM temp_checks c2
                       WHERE ec."TableSchema" = c2."TableSchema"
                         AND ec."TableName" = c2."TableName"
                         AND ec."CheckName" = c2."Name")
             OR (p_DropCheckConstraintsRemovedFromProduct AND COALESCE(tt."DropCheckConstraintsRemovedFromProduct", TRUE)));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- #363: WhatIf twin of the embedded 'constraint'/'dropped' (check) audit above; same predicate.
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'constraint', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."CheckName", 'wouldDrop'
          FROM temp_existing_checks ec
          JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                             AND tt."Name" = ec."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_checks c
                              WHERE ec."TableSchema" = c."TableSchema"
                                AND ec."TableName" = c."TableName"
                                AND ec."CheckName" = c."Name"
                                AND ec."Expression" = c."Expression")
            AND NOT EXISTS (SELECT 1
                              FROM temp_columns col
                              WHERE col."TableSchema" = ec."TableSchema"
                                AND col."TableName" = ec."TableName"
                                AND NULLIF(col."CheckExpression", '') IS NOT NULL
                                AND ec."CheckName" = 'CK_' || col."TableName" || '_' || col."Name")
            AND (EXISTS (SELECT 1
                           FROM temp_checks c2
                           WHERE ec."TableSchema" = c2."TableSchema"
                             AND ec."TableName" = c2."TableName"
                             AND ec."CheckName" = c2."Name")
                 OR (p_DropCheckConstraintsRemovedFromProduct AND COALESCE(tt."DropCheckConstraintsRemovedFromProduct", TRUE)));
    END IF;

    RAISE NOTICE 'Drop Modified Column Check Constraints';
    -- A column-check (CK_<table>_<column>) whose live, normalized definition differs from the desired
    -- CheckExpression is dropped here so the create pass re-adds it with the new expression. Reuses the
    -- SAME pg_get_constraintdef normalization (temp_existing_checks."Expression") as the table-level
    -- compare above, so an unchanged expression compares equal and is left untouched (idempotent).
    SELECT STRING_AGG('RAISE NOTICE ''  Column Check Constraint ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."CheckName" || ' modified'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP CONSTRAINT IF EXISTS "' || ec."CheckName" || '" CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_checks ec
      JOIN temp_columns col ON col."TableSchema" = ec."TableSchema"
                           AND col."TableName" = ec."TableName"
                           AND NULLIF(col."CheckExpression", '') IS NOT NULL
                           AND ec."CheckName" = 'CK_' || col."TableName" || '_' || col."Name"
      WHERE ec."Expression" != col."CheckExpression";
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- No-drop protection tier (#270): record statistics objects that WOULD be dropped by absence —
    -- name gone from the product, per-table flag still honored — as 'dropSuppressed'. Mirrors the drop
    -- pass's removed-from-package branch. Statistics are schema-scoped, so the object name follows the
    -- pass's own notice form (schema.statisticsName); the drop pass emits no 'dropped' audit row.
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture statistics suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'statistic', es."TableSchema" || '.' || es."StatisticsName", 'dropSuppressed'
          FROM temp_existing_statistics es
          JOIN temp_tables tt ON tt."Schema" = es."TableSchema"
                             AND tt."Name" = es."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_statistics ts
                              WHERE es."TableSchema" = ts."TableSchema"
                                AND es."TableName" = ts."TableName"
                                AND es."StatisticsName" = ts."Name"
                                AND es."Kind" = COALESCE(NULLIF(ts."Kind", ''), 'NDISTINCT,DEPENDENCIES,MCV')
                                AND es."StatisticsColumns" = ts."StatisticsColumns")
            AND NOT EXISTS (SELECT 1
                              FROM temp_statistics ts2
                              WHERE es."TableSchema" = ts2."TableSchema"
                                AND es."TableName" = ts2."TableName"
                                AND es."StatisticsName" = ts2."Name")
            AND COALESCE(tt."DropStatisticsRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Statistics';
    SELECT STRING_AGG('RAISE NOTICE ''  Statistics ' || es."TableSchema" || '.' || es."StatisticsName" || ' modified or no longer in product'';' || CHR(10) ||
                      'DROP STATISTICS IF EXISTS "' || es."TableSchema" || '"."' || es."StatisticsName" || '" CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_statistics es
      JOIN temp_tables tt ON tt."Schema" = es."TableSchema"
                         AND tt."Name" = es."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_statistics ts
                          WHERE es."TableSchema" = ts."TableSchema"
                            AND es."TableName" = ts."TableName"
                            AND es."StatisticsName" = ts."Name"
                            AND es."Kind" = COALESCE(NULLIF(ts."Kind", ''), 'NDISTINCT,DEPENDENCIES,MCV')
                            AND es."StatisticsColumns" = ts."StatisticsColumns")
        -- Split modified vs removed: a same-named statistics object whose definition changed is
        -- dropped unconditionally (the create pass re-adds it); one whose name is gone from the
        -- product is a by-absence removal, gated by the cascade flag + per-table tightening.
        AND (EXISTS (SELECT 1
                       FROM temp_statistics ts2
                       WHERE es."TableSchema" = ts2."TableSchema"
                         AND es."TableName" = ts2."TableName"
                         AND es."StatisticsName" = ts2."Name")
             OR (p_DropStatisticsRemovedFromProduct AND COALESCE(tt."DropStatisticsRemovedFromProduct", TRUE)));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- No-drop protection tier (#270): record exclude constraints that WOULD be dropped by absence —
    -- name gone from the product, per-table flag still honored — as 'dropSuppressed'. Mirrors the drop
    -- pass's removed-from-package branch. Object name follows the pass's own notice form
    -- (schema.table.excludeName); the drop pass emits no 'dropped' audit row, ObjectType 'constraint'.
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture exclude constraints suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'constraint', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ExcludeName", 'dropSuppressed'
          FROM temp_existing_excludes ec
          JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                             AND tt."Name" = ec."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_excludes c
                              WHERE ec."TableSchema" = c."TableSchema"
                                AND ec."TableName" = c."TableName"
                                AND ec."ExcludeName" = c."Name"
                                AND (ec."AccessMethod" = c."AccessMethod" OR c."AccessMethod" = '')
                                AND ec."ExcludeColumns" = (SELECT STRING_AGG("SchemaSmith"."QuoteIndexColumnList"((celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                                                             FROM JSON_ARRAY_ELEMENTS(c."ExcludeColumns"::JSON) AS celem)
                                AND ec."FilterExpression" = c."FilterExpression"
                                AND ec."Deferrable" = c."Deferrable"
                                AND ec."InitiallyDeferred" = c."InitiallyDeferred")
            AND NOT EXISTS (SELECT 1
                              FROM temp_excludes c2
                              WHERE ec."TableSchema" = c2."TableSchema"
                                AND ec."TableName" = c2."TableName"
                                AND ec."ExcludeName" = c2."Name")
            AND COALESCE(tt."DropExcludeConstraintsRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Exclude Constraints';
    SELECT STRING_AGG('RAISE NOTICE ''  Exclude Constraint ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ExcludeName" || ' modified or no longer in product'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP CONSTRAINT IF EXISTS "' || ec."ExcludeName" || '" CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_excludes ec
      JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                         AND tt."Name" = ec."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_excludes c
                          WHERE ec."TableSchema" = c."TableSchema"
                            AND ec."TableName" = c."TableName"
                            AND ec."ExcludeName" = c."Name"
                            AND (ec."AccessMethod" = c."AccessMethod" OR c."AccessMethod" = '')
                            AND ec."ExcludeColumns" = (SELECT STRING_AGG("SchemaSmith"."QuoteIndexColumnList"((celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                                                         FROM JSON_ARRAY_ELEMENTS(c."ExcludeColumns"::JSON) AS celem)
                            AND ec."FilterExpression" = c."FilterExpression"
                            AND ec."Deferrable" = c."Deferrable"
                            AND ec."InitiallyDeferred" = c."InitiallyDeferred")
        -- Split modified vs removed: a same-named exclude constraint whose definition changed is
        -- dropped unconditionally (the create pass re-adds it); one whose name is gone from the
        -- product is a by-absence removal, gated by the cascade flag + per-table tightening.
        AND (EXISTS (SELECT 1
                       FROM temp_excludes c2
                       WHERE ec."TableSchema" = c2."TableSchema"
                         AND ec."TableName" = c2."TableName"
                         AND ec."ExcludeName" = c2."Name")
             OR (p_DropExcludeConstraintsRemovedFromProduct AND COALESCE(tt."DropExcludeConstraintsRemovedFromProduct", TRUE)));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Handle Renamed Indexes And Unique Constraints';
    SELECT STRING_AGG('RAISE NOTICE ''  Renaming ' || CASE WHEN ei."PrimaryKey" OR ei."UniqueConstraint" THEN 'Constraint' ELSE 'Index' END || ' ' || ei."TableSchema" || '.' || ei."TableName" || '.' || ei."IndexName" || ' to ' || i."Name" || ''';' || CHR(10) ||
                      CASE WHEN NOT (ei."PrimaryKey" OR ei."UniqueConstraint")
                           THEN 'ALTER INDEX IF EXISTS "' || ei."TableSchema" || '"."' || ei."IndexName" || '" RENAME TO "' || i."Name"  || '";'
                           ELSE 'ALTER TABLE "' || ei."TableSchema" || '"."' || ei."TableName" || '" RENAME CONSTRAINT "' || ei."IndexName" || '" TO "' || i."Name" || '";' END, CHR(10))
      INTO sql_script
      FROM temp_existing_indexes ei
      JOIN temp_indexes i ON i."TableSchema" = ei."TableSchema"
                         AND i."TableName" = ei."TableName"
                         AND i."Name" != ei."IndexName"
                         AND i."IndexColumns" = ei."IndexColumns"
                         AND COALESCE(i."IncludeColumns", '') = COALESCE(ei."IncludeColumns", '')
                         -- #285: a PRIMARY KEY is unique in the catalog whether or not the package
                         -- says so, so a naturally-authored PK (PrimaryKey: true, no Unique) failed
                         -- this join and a RENAME fell through to drop+recreate. Same disjunction the
                         -- modified-index detection already uses.
                         AND (COALESCE(i."Unique", FALSE) OR COALESCE(i."PrimaryKey", FALSE)
                              OR COALESCE(i."UniqueConstraint", FALSE)) = ei."Unique"
                         AND COALESCE(i."UniqueConstraint", FALSE) = ei."UniqueConstraint"
                         AND COALESCE(i."PrimaryKey", FALSE) = ei."PrimaryKey"
                         AND COALESCE(i."FilterExpression", '') = COALESCE(ei."FilterExpression", '')
                         AND COALESCE(i."AccessMethod", 'btree') = COALESCE(ei."AccessMethod", 'btree')
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
                            OR COALESCE(i."AccessMethod", 'btree') != COALESCE(ei."AccessMethod", 'btree')
                            OR COALESCE(i."NullsNotDistinct", false) != COALESCE(ei."NullsNotDistinct", false)
                            -- A storage-parameter change rebuilds the index (hnsw m and ivfflat lists cannot
                            -- be ALTERed in place), so it drops here and the missing-index pass recreates it.
                            OR COALESCE(i."StorageParameters", '') != COALESCE(ei."StorageParameters", '')))
           OR (p_DropIndexesRemovedFromProduct -- Index Removed from Product Definition (gated)
               AND COALESCE((SELECT tt."DropIndexesRemovedFromProduct" FROM temp_tables tt WHERE tt."Schema" = ei."TableSchema" AND tt."Name" = ei."TableName"), TRUE)
               AND EXISTS (SELECT 1
                        FROM temp_product_ownership tp
                        WHERE tp."IndexName" = ei."IndexName"
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
                               FROM temp_product_ownership tp
                               WHERE tp."IndexName" = ei."IndexName"
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
                           ELSE 'DROP INDEX IF EXISTS "' || ti."TableSchema" || '"."' || ti."IndexName" || '";' END || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || CASE WHEN "IsConstraint" THEN 'constraint' ELSE 'index' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_indexes_to_drop ti;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- #363: WhatIf twin of the embedded 'index'/'constraint' 'dropped' audit above; same source.
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), CASE WHEN "IsConstraint" THEN 'constraint' ELSE 'index' END,
               ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName", 'wouldDrop'
          FROM temp_indexes_to_drop ti;
    END IF;

    -- No-drop protection tier (#270): the column drop pass below is gated inline by
    -- p_DropColumnsRemovedFromProduct (forced FALSE in protected mode), so it drops nothing. Record
    -- the columns that WOULD be dropped by absence — gone from the product, per-table flag still
    -- honored — as 'dropSuppressed'. This is a straight by-absence pass (no modified branch).
    IF p_CaptureWouldDrop THEN
      RAISE NOTICE 'Capture columns suppressed by PreventDrop (would drop by absence)';
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'column', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName", 'dropSuppressed'
          FROM temp_existing_columns ec
          JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                             AND tt."Name" = ec."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_columns c
                              WHERE ec."TableSchema" = c."TableSchema"
                                AND ec."TableName" = c."TableName"
                                AND ec."ColumnName" = c."Name")
            AND COALESCE(tt."DropColumnsRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Columns No Longer Part of The Product Definition';
    SELECT STRING_AGG('RAISE NOTICE ''  Column ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ' no longer in product'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP COLUMN IF EXISTS "' || ec."ColumnName" || '" CASCADE;' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''column'', ''' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_existing_columns ec
      JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                         AND tt."Name" = ec."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_columns c
                          WHERE ec."TableSchema" = c."TableSchema"
                            AND ec."TableName" = c."TableName"
                            AND ec."ColumnName" = c."Name")
        AND p_DropColumnsRemovedFromProduct
        AND COALESCE(tt."DropColumnsRemovedFromProduct", TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- #363: WhatIf twin of the embedded 'column'/'dropped' audit above; same source/predicate.
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'column', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName", 'wouldDrop'
          FROM temp_existing_columns ec
          JOIN temp_tables tt ON tt."Schema" = ec."TableSchema"
                             AND tt."Name" = ec."TableName"
          WHERE NOT EXISTS (SELECT 1
                              FROM temp_columns c
                              WHERE ec."TableSchema" = c."TableSchema"
                                AND ec."TableName" = c."TableName"
                                AND ec."ColumnName" = c."Name")
            AND p_DropColumnsRemovedFromProduct
            AND COALESCE(tt."DropColumnsRemovedFromProduct", TRUE);
    END IF;

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
        -- Positive gate, not a deny-list: an extension AM (e.g. pgvector's hnsw/ivfflat) can't be
        -- enumerated in advance, so allow-listing the AMs verified to accept fillfactor fails safe
        -- (skip the ALTER) instead of failing loud (PostgreSQL's "unrecognized parameter" error).
        AND COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash');
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Drop Generated Columns Referencing Columns That Are Changing Data Type';
    SELECT STRING_AGG('RAISE NOTICE ''  Column ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ' affected by type change'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP COLUMN IF EXISTS "' || ec."ColumnName" || '" CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_columns ec
      WHERE ec."Generated" = 'ALWAYS'
        AND EXISTS (SELECT 1
                      FROM temp_columns c
                      JOIN temp_existing_columns ec2 ON ec2."TableSchema" = c."TableSchema"
                                                    AND ec2."TableName" = c."TableName"
                                                    AND ec2."ColumnName" = c."Name"                                                    
                      WHERE c."TableSchema" = ec."TableSchema"
                        AND c."TableName" = ec."TableName"
                        AND ((c."Name" = ec."ColumnName" AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                          OR (c."Name" = ec."ColumnName" AND ec."Virtual" AND c."GenerationExpression" = '')
                          OR (c."Name" != ec."ColumnName" AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec2."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') OR COALESCE(ec2."Collation", '') != COALESCE(c."Collation", '')) AND ec."GenerationExpression" LIKE '%' || c."Name" || '%')))
         AND EXISTS (SELECT 1
                       FROM information_schema.columns ic
                       WHERE ic.table_schema = ec."TableSchema"
                         AND ic.table_name = ec."TableName"
                         AND ic.column_name = ec."ColumnName");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Drop Columns Changing to Generated';
    SELECT STRING_AGG('RAISE NOTICE ''  Column ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ' now generated'';' || CHR(10) ||
                      'ALTER TABLE "' || ec."TableSchema" || '"."' || ec."TableName" || '" DROP COLUMN IF EXISTS "' || ec."ColumnName" || '" CASCADE;', CHR(10))
    INTO sql_script
    FROM temp_existing_columns ec
    JOIN temp_columns c ON c."TableSchema" = ec."TableSchema"
                        AND c."TableName" = ec."TableName"
                        AND c."Name" = ec."ColumnName"
                        AND c."Generated" = 'ALWAYS'
    WHERE ec."Generated" = 'NEVER'
      AND EXISTS (SELECT 1
                  FROM information_schema.columns ic
                  WHERE ic.table_schema = ec."TableSchema"
                    AND ic.table_name = ec."TableName"
                    AND ic.column_name = ec."ColumnName");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    DELETE FROM temp_existing_columns ec
      WHERE NOT EXISTS (SELECT 1
                          FROM information_schema.columns ic
                          WHERE ic.table_schema = ec."TableSchema"
                            AND ic.table_name = ec."TableName"
                            AND ic.column_name = ec."ColumnName");


    RAISE NOTICE 'Drop Views and Rules Dependent on Modified Columns';
    SELECT STRING_AGG(CASE WHEN dep_view.relname IS NOT NULL
                           THEN 'RAISE NOTICE ''  Dropping View ' || ref_obj.relnamespace::regnamespace || '.' || dep_view.relname || ''';' || CHR(10) ||
                                'DROP VIEW IF EXISTS "' || ref_obj.relnamespace::regnamespace || '"."' || dep_view.relname || '";'
                           ELSE CASE WHEN dep_rule.rulename != '_RETURN'
                                     THEN 'RAISE NOTICE ''  Dropping Rule ' || rule_class.relnamespace::regnamespace || '.' || dep_rule.rulename || ''';' || CHR(10) ||
                                          'DROP RULE IF EXISTS "' || rule_class.relnamespace::regnamespace || '"."' || dep_rule.rulename || '";'
                                     END
                           END, CHR(10))
      INTO sql_script
      FROM temp_existing_columns ec
      JOIN pg_class AS ref_obj ON ref_obj.relnamespace::regnamespace = ('"' || ec."TableSchema" || '"')::regnamespace
                              AND ref_obj.relname = ec."TableName"
      JOIN pg_depend AS d ON ref_obj.oid = d.refobjid
      JOIN pg_attribute AS ref_attr ON ref_attr.attrelid = d.refobjid AND ref_attr.attnum = d.refobjsubid
                                   AND ref_attr.attname = ec."ColumnName"
      JOIN pg_rewrite AS dep_rule ON dep_rule.oid = d.objid
      JOIN pg_class rule_class ON rule_class.oid = dep_rule.ev_class
      LEFT JOIN pg_class AS dep_view ON dep_view.oid = dep_rule.ev_class
      WHERE EXISTS (SELECT 1 -- need to determine if any changes other than type have the need to drop dependent views/rules
                      FROM temp_columns c
                      WHERE c."TableSchema" = ec."TableSchema"
                        AND c."TableName" = ec."TableName"
                        AND c."Name" = ec."ColumnName"
                        AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Add New Physical Columns Switched From Generated';
    SELECT STRING_AGG('RAISE NOTICE ''  Add new physical columns to ' || tt."Schema" || '.' || tt."Name" || ' (' ||
                      (SELECT STRING_AGG(tc."Name" || CASE WHEN COALESCE(tc."VariantName", '') <> '' THEN ' (variant: ' || REPLACE(tc."VariantName", '''', '''''') || ')' ELSE '' END, ', ')
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
                              CASE WHEN COALESCE(tc."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%' AND COALESCE(tc."Default", '') != '' THEN ' DEFAULT ' || tc."Default" ELSE '' END, ', ')
                         FROM temp_columns tc
                         WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                           AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                           AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name")) || ';', CHR(10))
      INTO sql_script
      FROM temp_tables tt
      WHERE EXISTS(SELECT * FROM information_schema.tables t WHERE t.table_schema = tt."Schema" AND t.table_name = tt."Name")
        AND EXISTS (SELECT 1
                      FROM temp_columns tc
                      WHERE tc."TableSchema" = tt."Schema" AND tc."TableName" = tt."Name"
                        AND (COALESCE(tc."Generated", 'NEVER') = 'NEVER' OR COALESCE(tc."Generated", '') LIKE 'GENERATED%IDENTITY%')
                        AND NOT EXISTS (SELECT 1 FROM information_schema.columns ic WHERE ic.table_name = tc."TableName" AND ic.table_schema = tc."TableSchema" AND ic.column_name = tc."Name"));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Recreate Generated Columns With Changed Expression (PG < 17)';
    -- Sole handler for a generated-expression change on a generated column (status unchanged) on PG < 17.
    -- The re-add carries the FULL target definition (DataType, Collation, GENERATED ALWAYS AS (...) STORED,
    -- nullability) and applies non-default Storage/Compression on the freshly re-added column, so the column
    -- is fully converged after this pass — type/collation/nullability/storage/compression changes ride along
    -- here even when they changed alongside the expression. The "Alter Modified Columns" pass below excludes
    -- EXACTLY this column set via a matching NOT (...) guard.
    -- MUST match the "Alter Modified Columns" NOT (...) predicate below (Task A1 #296) — keep in lockstep.
    IF "SchemaSmith"."ServerVersionNum"() < 17 THEN
      SELECT STRING_AGG('RAISE NOTICE ''  Recreating generated column ' || c."TableSchema" || '.' || c."TableName" || '.' || c."Name" || ' (expression changed)'';' || CHR(10) ||
                        'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" DROP COLUMN IF EXISTS "' || c."Name" || '" CASCADE;' || CHR(10) ||
                        'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" ADD "' || c."Name" || '" ' || c."DataType" ||
                        CASE WHEN COALESCE(c."Collation", '') != '' THEN ' COLLATE "' || c."Collation" || '"' ELSE '' END ||
                        ' GENERATED ALWAYS AS (' || c."GenerationExpression" || ') STORED' ||
                        CASE WHEN c."Nullable" THEN '' ELSE ' NOT NULL' END || ';' ||
                        -- Re-added column is at default storage/compression; carry over any non-default target so
                        -- a combined expression+storage/compression change fully converges in this single pass.
                        CASE WHEN COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != 'DEFAULT'
                             THEN CHR(10) || 'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" ALTER COLUMN "' || c."Name" || '" SET STORAGE ' || c."Storage" || ';'
                             ELSE '' END ||
                        CASE WHEN COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != 'DEFAULT' AND "SchemaSmith"."ServerVersionNum"() >= 14
                             THEN CHR(10) || 'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" ALTER COLUMN "' || c."Name" || '" SET COMPRESSION ' || c."Compression" || ';'
                             ELSE '' END, CHR(10))
        INTO sql_script
        FROM temp_columns c
        JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema"
                                     AND ec."TableName" = c."TableName"
                                     AND ec."ColumnName" = c."Name"
        WHERE COALESCE(c."Generated", 'NEVER') != 'NEVER'
          AND COALESCE(c."GenerationExpression", '') != ''
          AND COALESCE(c."Generated", 'NEVER') = COALESCE(ec."Generated", 'NEVER')  -- generated status NOT changing — pure expression edit
          AND COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
          AND EXISTS (SELECT 1
                        FROM information_schema.columns ic
                        WHERE ic.table_schema = c."TableSchema"
                          AND ic.table_name = c."TableName"
                          AND ic.column_name = c."Name");
      CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    END IF;

    -- Un-generate a column (target is no longer GENERATED) on PostgreSQL < 13, which lacks
    -- ALTER COLUMN ... DROP EXPRESSION. Drop and re-add the column as a plain column (decision: Paul,
    -- 2026-07-30 — achieve the un-generation on old PG; the previously-computed values are NOT preserved,
    -- unlike PG13+ in-place DROP EXPRESSION). Runs before the "Alter Modified Columns" pass, which excludes
    -- these columns (the re-add carries the full target definition — type/collation/nullability/default).
    IF "SchemaSmith"."ServerVersionNum"() < 13 THEN
      SELECT STRING_AGG('RAISE NOTICE ''  Un-generating column ' || c."TableSchema" || '.' || c."TableName" || '.' || c."Name" || ' (drop+re-add as plain; PG < 13 has no DROP EXPRESSION)'';' || CHR(10) ||
                        'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" DROP COLUMN IF EXISTS "' || c."Name" || '" CASCADE;' || CHR(10) ||
                        'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" ADD "' || c."Name" || '" ' || c."DataType" ||
                        CASE WHEN COALESCE(c."Collation", '') != '' THEN ' COLLATE "' || c."Collation" || '"' ELSE '' END ||
                        CASE WHEN c."Nullable" THEN '' ELSE ' NOT NULL' END ||
                        CASE WHEN COALESCE(c."Default", '') != '' THEN ' DEFAULT ' || c."Default" ELSE '' END || ';' ||
                        CASE WHEN COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != 'DEFAULT'
                             THEN CHR(10) || 'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '" ALTER COLUMN "' || c."Name" || '" SET STORAGE ' || c."Storage" || ';'
                             ELSE '' END, CHR(10))
        INTO sql_script
        FROM temp_columns c
        JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema"
                                     AND ec."TableName" = c."TableName"
                                     AND ec."ColumnName" = c."Name"
        WHERE (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')  -- target no longer generated
          AND COALESCE(ec."GenerationExpression", '') != ''                                                -- but it currently is a generated column
          AND EXISTS (SELECT 1
                        FROM information_schema.columns ic
                        WHERE ic.table_schema = c."TableSchema"
                          AND ic.table_name = c."TableName"
                          AND ic.column_name = c."Name");
      CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    END IF;

    -- Unsupported-feature policy: per-column compression (SET COMPRESSION) requires PostgreSQL 14. The emit
    -- below is gated off under 14; 'fail' aborts, 'warn' (default) records a downgrade manifest per column
    -- that declared a non-default compression. Same routing spine as NULLS NOT DISTINCT / expression stats.
    IF "SchemaSmith"."ServerVersionNum"() < 14 THEN
      IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
         AND EXISTS (SELECT 1 FROM temp_columns WHERE COALESCE("Compression", '') NOT IN ('', 'DEFAULT')) THEN
        RAISE EXCEPTION 'Per-column compression requires PostgreSQL 14 (detected major %); column(s): %',
          "SchemaSmith"."ServerVersionNum"(),
          (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
             FROM temp_columns WHERE COALESCE("Compression", '') NOT IN ('', 'DEFAULT'));
      ELSE
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'per-column compression (PG14)',
                 "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
            FROM temp_columns WHERE COALESCE("Compression", '') NOT IN ('', 'DEFAULT');
      END IF;
    END IF;

    RAISE NOTICE 'Alter Modified Columns';
    SELECT STRING_AGG("code", CHR(10))
      INTO sql_script
      FROM (
    SELECT 'RAISE NOTICE ''Modified columns found in ' || c."TableSchema" || '.' || c."TableName" || '(' || 
           (SELECT STRING_AGG(ic."Name", ', ')
              FROM temp_columns ic
              JOIN temp_existing_columns iec ON iec."TableSchema" = ic."TableSchema"
                                            AND iec."TableName" = ic."TableName"
                                            AND iec."ColumnName" = ic."Name"
              WHERE EXISTS (SELECT 1
                              FROM information_schema.columns ic2
                              WHERE ic2.table_schema = c."TableSchema"
                                AND ic2.table_name = c."TableName"
                                AND ic2.column_name = ic."Name")
                AND ic."TableSchema" = c."TableSchema"
                AND ic."TableName" = c."TableName"
                AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ic."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(iec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
                  OR ic."Nullable" != iec."Nullable"
                  OR COALESCE("SchemaSmith"."StripTypeCast"(ic."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(iec."Default"), '')
                  OR COALESCE(ic."Collation", '') != COALESCE(iec."Collation", '')
                  OR COALESCE(REGEXP_REPLACE(ic."Generated", '\s*\(.*$', ''), 'NEVER') != COALESCE(REGEXP_REPLACE(iec."Generated", '\s*\(.*$', ''), 'NEVER')
                  OR COALESCE(ic."GenerationExpression", '') != COALESCE(iec."GenerationExpression", '')
                  OR (COALESCE(ic."Storage", '') != '' AND COALESCE(ic."Storage", '') != COALESCE(iec."Storage", ''))
                  OR (COALESCE(ic."Compression", '') != '' AND COALESCE(ic."Compression", '') != COALESCE(iec."Compression", '')))) || ')'';' || CHR(10) ||
           'ALTER TABLE "' || c."TableSchema" || '"."' || c."TableName" || '"' || CHR(10) ||
           STRING_AGG(NULLIF(RTRIM(TRIM(TRAILING ',' FROM
                      CASE WHEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') OR COALESCE(c."Collation", '') != COALESCE(ec."Collation", '')
                           THEN ' ALTER COLUMN "' || c."Name" || '" SET DATA TYPE ' || c."DataType" ||
                                                    CASE WHEN COALESCE(c."Collation", '') != '' THEN ' COLLATE "' || c."Collation" || '"' ELSE '' END || ','
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
                           THEN CASE WHEN (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
                                     THEN CASE WHEN "SchemaSmith"."ServerVersionNum"() >= 13
                                               THEN ' ALTER COLUMN "' || c."Name" || '" DROP EXPRESSION,'
                                               ELSE '' END  -- PG < 13: handled by the un-generate drop-and-re-add pass above
                                     WHEN "SchemaSmith"."ServerVersionNum"() >= 17
                                     THEN ' ALTER COLUMN "' || c."Name" || '" SET EXPRESSION AS (' || COALESCE(c."GenerationExpression", '') || '),'
                                     ELSE '' END  -- PG < 17: expression change is handled by the drop-and-re-add pass below
                           ELSE '' END ||
                      CASE WHEN COALESCE(ec."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%'
                                AND COALESCE(c."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%'
                           THEN ' ALTER COLUMN "' || c."Name" || '" DROP IDENTITY IF EXISTS,'
                           ELSE '' END ||
                      CASE WHEN c."Nullable" != ec."Nullable"
                           THEN CASE WHEN c."Nullable"
                                     THEN ' ALTER COLUMN "' || c."Name" || '" DROP NOT NULL,'
                                     ELSE ' ALTER COLUMN "' || c."Name" || '" SET NOT NULL,' END
                           ELSE '' END ||
                      CASE WHEN COALESCE("SchemaSmith"."StripTypeCast"(c."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(ec."Default"), '')
                           THEN CASE WHEN COALESCE(c."Default", '') = ''
                                     THEN ' ALTER COLUMN "' || c."Name" || '" DROP DEFAULT,'
                                     ELSE ' ALTER COLUMN "' || c."Name" || '" SET DEFAULT ' || c."Default" || ',' END
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."Storage", '') != COALESCE(ec."Storage", '') AND COALESCE(c."Storage", '') != ''
                           THEN ' ALTER COLUMN "' || c."Name" || '" SET STORAGE ' || COALESCE(c."Storage", '') || ','
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."Compression", '') != COALESCE(ec."Compression", '') AND COALESCE(c."Compression", '') != '' AND "SchemaSmith"."ServerVersionNum"() >= 14
                           THEN ' ALTER COLUMN "' || c."Name" || '" SET COMPRESSION ' || COALESCE(c."Compression", '') || ','
                           ELSE '' END)), ''), ',' || CHR(10)) || ';' AS "code"
      FROM temp_columns c
      JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema"
                                   AND ec."TableName" = c."TableName"
                                   AND ec."ColumnName" = c."Name"
      WHERE EXISTS (SELECT 1
                      FROM information_schema.columns ic
                      WHERE ic.table_schema = c."TableSchema"
                        AND ic.table_name = c."TableName"
                        AND ic.column_name = c."Name")
        AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
          OR c."Nullable" != ec."Nullable"
          OR COALESCE("SchemaSmith"."StripTypeCast"(c."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(ec."Default"), '')
          OR COALESCE(c."Collation", '') != COALESCE(ec."Collation", '')
          -- Identity KIND only, both sides. The catalog records the kind and nothing else (see the live
          -- rendering above), while a package may legitimately declare
          -- GENERATED ALWAYS AS IDENTITY(START WITH 1 INCREMENT BY 1) -- and the create path still emits
          -- those options. Comparing the declared string verbatim re-modified every identity column on
          -- every deploy. SchemaSmith does not manage the sequence options declaratively, so they must not
          -- take part in the comparison either.
          OR COALESCE(REGEXP_REPLACE(c."Generated", '\s*\(.*$', ''), 'NEVER') != COALESCE(REGEXP_REPLACE(ec."Generated", '\s*\(.*$', ''), 'NEVER')
          OR COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
          OR (COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != COALESCE(ec."Storage", ''))
          OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", '')))
        AND NOT (-- PG < 17 generated-expression change on a generated column (status unchanged): handled SOLELY by the
                 -- drop-and-re-add pass above, which re-adds the FULL target definition. Exclude such columns from the
                 -- ALTER pass entirely — regardless of whether type/collation/nullability ALSO changed (the re-add carries
                 -- them). This guard must partition the column set IDENTICALLY to that pass: same predicate, no extra
                 -- "all other attributes equal" tail (a mixed change still belongs solely to drop-and-re-add).
                 -- MUST match the "Recreate Generated Columns With Changed Expression (PG < 17)" predicate above (Task A1 #296) — keep in lockstep.
                 "SchemaSmith"."ServerVersionNum"() < 17
                 AND COALESCE(c."Generated", 'NEVER') != 'NEVER'
                 AND COALESCE(c."GenerationExpression", '') != ''
                 AND COALESCE(c."Generated", 'NEVER') = COALESCE(ec."Generated", 'NEVER')
                 AND COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", ''))
        AND NOT (-- PG < 13 un-generate (target no longer GENERATED, currently generated): handled SOLELY by the
                 -- "Un-generating column" drop-and-re-add pass above (which re-adds the full plain-column
                 -- definition). MUST match that pass's predicate — keep in lockstep.
                 "SchemaSmith"."ServerVersionNum"() < 13
                 AND (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
                 AND COALESCE(ec."GenerationExpression", '') != '')
       GROUP BY c."TableSchema", c."TableName") x;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- Object-change audit (#243 E5): one row per column altered above. Reuses the identical
    -- modified-column predicate; temp_existing_columns is a stable snapshot, so it stays valid
    -- after the ALTER ran (per-column, not folded per-table like the ALTER).
    -- #363: runs regardless of p_WhatIf; the action carries the mode (wouldModify under WhatIf).
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'column', c."TableSchema" || '.' || c."TableName" || '.' || c."Name", CASE WHEN p_WhatIf THEN 'wouldModify' ELSE 'modified' END
          FROM temp_columns c
          JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema"
                                       AND ec."TableName" = c."TableName"
                                       AND ec."ColumnName" = c."Name"
          WHERE EXISTS (SELECT 1
                          FROM information_schema.columns ic
                          WHERE ic.table_schema = c."TableSchema"
                            AND ic.table_name = c."TableName"
                            AND ic.column_name = c."Name")
            AND (REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
              OR c."Nullable" != ec."Nullable"
              OR COALESCE("SchemaSmith"."StripTypeCast"(c."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(ec."Default"), '')
              OR COALESCE(c."Collation", '') != COALESCE(ec."Collation", '')
              OR COALESCE(REGEXP_REPLACE(c."Generated", '\s*\(.*$', ''), 'NEVER') != COALESCE(REGEXP_REPLACE(ec."Generated", '\s*\(.*$', ''), 'NEVER')
              OR COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
              OR (COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != COALESCE(ec."Storage", ''))
              OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", '')))
            AND NOT ("SchemaSmith"."ServerVersionNum"() < 17
                     AND COALESCE(c."Generated", 'NEVER') != 'NEVER'
                     AND COALESCE(c."GenerationExpression", '') != ''
                     AND COALESCE(c."Generated", 'NEVER') = COALESCE(ec."Generated", 'NEVER')
                     AND COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", ''));

    -- Unsupported-feature policy: a table-level access method (ALTER TABLE ... SET ACCESS METHOD) requires
    -- PostgreSQL 15. The emit above is gated off under 15 (and the fixup WHERE ignores the AccessMethod
    -- difference there so it doesn't churn); 'fail' aborts, 'warn' (default) records a downgrade manifest per
    -- table that declared a non-default access method. Same routing spine as per-column compression.
    IF "SchemaSmith"."ServerVersionNum"() < 15 THEN
      IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
         AND EXISTS (SELECT 1 FROM temp_tables WHERE COALESCE("AccessMethod", '') NOT IN ('', 'heap')) THEN
        RAISE EXCEPTION 'Table access method (SET ACCESS METHOD) requires PostgreSQL 15 (detected major %); table(s): %',
          "SchemaSmith"."ServerVersionNum"(),
          (SELECT STRING_AGG("Schema" || '.' || "Name", ', ')
             FROM temp_tables WHERE COALESCE("AccessMethod", '') NOT IN ('', 'heap'));
      ELSE
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'table access method (PG15)',
                 "Schema" || '.' || "Name", 'downgraded'
            FROM temp_tables WHERE COALESCE("AccessMethod", '') NOT IN ('', 'heap');
      END IF;
    END IF;

    -- Placement drift. Same posture as SQL Server's FileGroup, and for the same reasons:
    --   * An UNSET Tablespace means "SchemaSmith does not manage placement here", NOT a declaration of
    --     the database default. Reading unset as "the default" would fail every object a DBA placed
    --     elsewhere on its SECOND deploy, in packages that never mentioned placement at all.
    --   * A DECLARED tablespace that differs from where the object already lives is a MOVE.
    --     ALTER TABLE ... SET TABLESPACE rewrites the whole table under an ACCESS EXCLUSIVE lock, and
    --     ALTER INDEX ... SET TABLESPACE rebuilds the index, so this errors naming both rather than
    --     silently moving data. Clearing a declared value back to unset is a no-op, which is the right
    --     side to err on: SchemaSmith never moves objects between tablespaces either way.
    DECLARE
      v_move RECORD;
    BEGIN
      FOR v_move IN
        SELECT t."Schema" AS sch, t."Name" AS obj, 'table' AS kind,
               t."Tablespace" AS declared, COALESCE(ts.spcname, '') AS live
          FROM temp_tables t
          JOIN pg_class c ON c.relname = t."Name"
                         AND c.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
                         AND c.relkind = 'r'
          LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace
         WHERE COALESCE(t."Tablespace", '') <> ''
           AND t."Tablespace" IS DISTINCT FROM COALESCE(ts.spcname, '')
        UNION ALL
        SELECT ti."TableSchema", ti."Name", 'index',
               ti."Tablespace", COALESCE(ts.spcname, '')
          FROM temp_indexes ti
          JOIN pg_class c ON c.relname = ti."Name"
                         AND c.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                         AND c.relkind = 'i'
          LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace
         WHERE COALESCE(ti."Tablespace", '') <> ''
           AND ti."Tablespace" IS DISTINCT FROM COALESCE(ts.spcname, '')
      LOOP
        RAISE EXCEPTION '% %.% declares tablespace %, but is currently deployed on %. SchemaSmith does not move an existing object to a different tablespace (that is a rewrite) -- migrate it manually, or correct the declared tablespace to match.',
          v_move.kind, v_move.sch, v_move.obj, v_move.declared,
          CASE WHEN v_move.live = '' THEN 'the database default' ELSE v_move.live END;
      END LOOP;
    END;

    RAISE NOTICE 'Fixup Table Attributes';
    SELECT STRING_AGG('RAISE NOTICE ''  Fixing up attributes for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                      'ALTER TABLE ' || '"' || t."Schema" || '"."' || t."Name" || '" ' ||
                      TRIM(TRAILING ',' FROM
                           CASE WHEN t."PersistenceType" != '' AND LOWER(t."PersistenceType") != CASE rls.relpersistence WHEN 'p' THEN 'logged' WHEN 'u' THEN 'unlogged' END
                                THEN 'SET ' || UPPER(t."PersistenceType") || ','
                                ELSE '' END ||
                           CASE WHEN t."AccessMethod" != '' AND t."AccessMethod" != am.amname AND "SchemaSmith"."ServerVersionNum"() >= 15
                                THEN ' SET ACCESS METHOD ' || t."AccessMethod" || ','
                                ELSE '' END ||
                           CASE WHEN t."RowLevelSecurity" != rls.relrowsecurity
                                THEN ' ' || CASE WHEN t."RowLevelSecurity" THEN 'ENABLE' ELSE 'DISABLE' END || ' ROW LEVEL SECURITY,'
                                ELSE '' END ||
                           CASE WHEN t."ForceRowLevelSecurity" != rls.relforcerowsecurity
                                THEN ' ' || CASE WHEN t."ForceRowLevelSecurity" THEN '' ELSE 'NO ' END || 'FORCE ROW LEVEL SECURITY,'
                                ELSE '' END ||
                           CASE WHEN t."UpdateFillFactor" AND t."FillFactor" != COALESCE((SELECT SPLIT_PART(opt, '=', 2) FROM UNNEST(rls.reloptions) AS opts(opt) WHERE opt LIKE 'fillfactor=%'), '100')::INT2
                                THEN ' SET (FILLFACTOR = ' || t."FillFactor" || '),'
                                ELSE '' END), ';' || CHR(10)) || ';'
      INTO sql_script
      FROM temp_tables t
      JOIN pg_class rls ON rls.relname = t."Name"
                       AND rls.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
                       AND rls.relkind = 'r'
      JOIN pg_am am ON rls.relam = am.oid
      WHERE (t."PersistenceType" != '' AND LOWER(t."PersistenceType") != CASE rls.relpersistence WHEN 'p' THEN 'logged' WHEN 'u' THEN 'unlogged' END)
         OR (t."AccessMethod" != '' AND t."AccessMethod" != am.amname AND "SchemaSmith"."ServerVersionNum"() >= 15)
         OR t."RowLevelSecurity" != rls.relrowsecurity
         OR t."ForceRowLevelSecurity" != rls.relforcerowsecurity
         OR (t."UpdateFillFactor" AND t."FillFactor" != COALESCE((SELECT SPLIT_PART(opt, '=', 2) FROM UNNEST(rls.reloptions) AS opts(opt) WHERE opt LIKE 'fillfactor=%'), '100')::INT2);
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
END $$;