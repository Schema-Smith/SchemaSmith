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
   p_RebuildPolicyOnOrderMismatch BOOLEAN = FALSE,
   -- OnlineApply (OFF, PREFER, REQUIRE), a table's own value over it: an index dropped because the package no longer
   -- declares it goes CONCURRENTLY after this procedure (DeferOnlineWork), and REQUIRE refuses a build that cannot.
   p_OnlineApply TEXT = 'OFF')
  LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
  protect_notice TEXT;
  partitioned_tables TEXT;
  rebuild_target RECORD;
  undeclared_dependents TEXT;
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
      SELECT STRING_AGG('ALTER TABLE ' || "SchemaSmith"."QuoteName"(pn.nspname) || '.' || "SchemaSmith"."QuoteName"(pc.relname) || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(con.conname) || ';' || CHR(10) ||
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
                             ELSE 'DROP TABLE IF EXISTS ' || "SchemaSmith"."QuoteName"(tp."Schema") || '.' || "SchemaSmith"."QuoteName"(tp."TableName") || ';'
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
             -- The same function renders the type extraction writes, so the two cannot disagree.
             "SchemaSmith"."ColumnDataType"(c.data_type, c.udt_schema, c.udt_name, c.domain_schema, c.domain_name, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision, a.atttypid, a.atttypmod) AS "DataType",
             CAST(CASE WHEN c.is_nullable = 'YES' THEN TRUE ELSE FALSE END AS BOOLEAN) AS "Nullable",
             c.column_default AS "Default",
             "SchemaSmith"."ColumnCollation"(c.collation_schema, c.collation_name) AS "Collation",
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
        JOIN pg_attribute a ON a.attrelid = ("SchemaSmith"."QuoteName"(c.table_schema) ||  '.' || "SchemaSmith"."QuoteName"(c.table_name))::regclass
                           AND a.attname = c.column_name
                           AND NOT a.attisdropped
        WHERE t."Schema" = c.table_schema
          AND t."Name" = c.table_name;

    -- A package from an earlier release can spell a column's type or collation the way extraction used to: an
    -- extension type without its modifier (public.geometry), an array of a public type unqualified (e[]), a collation
    -- by its bare name. Each names what the live column already has, so it takes the live spelling instead of reading
    -- as a change that would strip the modifier. Built-in types are left alone: varchar over a live varchar(20) is a
    -- real change.
    UPDATE temp_columns tc
       SET "DataType" = ec."DataType"
      FROM temp_existing_columns ec
      JOIN pg_attribute a ON a.attrelid = TO_REGCLASS(QUOTE_IDENT(ec."TableSchema") || '.' || QUOTE_IDENT(ec."TableName"))
                         AND a.attname = ec."ColumnName" AND NOT a.attisdropped
      JOIN pg_type lt ON lt.oid = a.atttypid
     WHERE tc."TableSchema" = ec."TableSchema" AND tc."TableName" = ec."TableName" AND tc."Name" = ec."ColumnName"
       AND tc."DataType" IS DISTINCT FROM ec."DataType"
       AND POSITION('(' IN tc."DataType") = 0
       AND lt.typnamespace <> 'pg_catalog'::REGNAMESPACE
       AND TO_REGTYPE(tc."DataType") = a.atttypid;

    UPDATE temp_columns tc
       SET "Collation" = ec."Collation"
      FROM temp_existing_columns ec
     WHERE tc."TableSchema" = ec."TableSchema" AND tc."TableName" = ec."TableName" AND tc."Name" = ec."ColumnName"
       AND COALESCE(tc."Collation", '') <> '' AND tc."Collation" IS DISTINCT FROM ec."Collation"
       AND POSITION('"."' IN tc."Collation") = 0
       AND ec."Collation" LIKE '%.' || "SchemaSmith"."QuoteName"(BTRIM(tc."Collation", '"'));

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
    -- #242. A generated column's expression is compared in a dozen places downstream (rebuild election, alter
    -- detection, SET EXPRESSION emit, drop/re-add). Patching each one is how a fix looks done and still emits
    -- DDL -- so this neutralises the difference ONCE, at the source: where the mapping says the declared
    -- expression is what produced the live one, the live snapshot adopts the authored text and every later
    -- comparison compares equal. Only the SNAPSHOT is touched; the authored value the ALTER/CREATE paths emit
    -- is untouched, and the recorder reads the catalog directly, so the mapping still stores the real pair.
    UPDATE temp_existing_columns ec
       SET "GenerationExpression" = c."GenerationExpression"
      FROM temp_columns c
     WHERE c."TableSchema" = ec."TableSchema"
       AND c."TableName" = ec."TableName"
       AND c."Name" = ec."ColumnName"
       AND COALESCE(c."GenerationExpression", '') != ''
       AND COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
       AND "SchemaSmith"."ExpressionMapUnchanged"(c."TableSchema", c."TableName", 'COLUMN', c."Name",
             'generated', c."GenerationExpression", COALESCE(ec."GenerationExpression", ''));

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
                            OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", ''))
                  -- Declared identity sequence options that differ from the live sequence (only the ones declared).
                  OR (COALESCE(c."Generated", '') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", '') LIKE 'GENERATED%IDENTITY%'
                      AND "SchemaSmith"."IdentityOptionChanges"(c."Generated", pg_get_serial_sequence(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"), c."Name")::regclass) <> ''))) AS "ModificationPasses",
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

    -- #242. A partial index's predicate is compared in more than one place (the drop/modify election and the
    -- rename join), and PostgreSQL never renders it the way it was authored. Neutralise the difference ONCE, in
    -- the live snapshot: where the mapping vouches that the declared predicate produced the live one, the
    -- snapshot adopts the declared text and every later comparison compares equal. The snapshot only -- the
    -- CREATE path emits the declared text, and the recorder reads the catalog directly.
    UPDATE temp_existing_indexes ei
       SET "FilterExpression" = i."FilterExpression"
      FROM temp_indexes i
     WHERE i."TableSchema" = ei."TableSchema"
       AND i."TableName" = ei."TableName"
       AND i."Name" = ei."IndexName"
       AND COALESCE(i."FilterExpression", '') != ''
       AND COALESCE(i."FilterExpression", '') != COALESCE(ei."FilterExpression", '')
       AND "SchemaSmith"."ExpressionMapUnchanged"(i."TableSchema", i."TableName", 'INDEX', i."Name", 'filter',
             i."FilterExpression", COALESCE(ei."FilterExpression", ''));

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
             -- The subset a SET NULL applies to (15+; empty below, where the declared value is folded to empty too).
             "SchemaSmith"."ForeignKeySetNullColumns"(con.oid) AS "SetNullColumns",
             CASE con.confupdtype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' WHEN 'd' THEN 'SET DEFAULT' END AS "UpdateAction",
             con.condeferrable AS "Deferrable",
             con.condeferred AS "InitiallyDeferred",
             CASE con.confmatchtype WHEN 'f' THEN 'FULL' WHEN 'p' THEN 'PARTIAL' ELSE 'SIMPLE' END AS "MatchType"
        FROM temp_tables t
        JOIN pg_catalog.pg_constraint con ON con.conrelid = to_regclass("SchemaSmith"."QuoteName"(t."Schema") ||  '.' || "SchemaSmith"."QuoteName"(t."Name"))
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
             "SchemaSmith"."CheckPredicate"(con.oid) AS "Expression",
             COALESCE(con.connoinherit, FALSE) AS "NoInherit",
             "SchemaSmith"."ConstraintEnforced"(con.oid) AS "Enforced"
        FROM temp_tables t
        JOIN pg_catalog.pg_constraint con ON con.conrelid = to_regclass("SchemaSmith"."QuoteName"(t."Schema") ||  '.' || "SchemaSmith"."QuoteName"(t."Name"))
        WHERE con.contype = 'c';

    RAISE NOTICE 'Collect Existing Exclude Constraint Definitions';
    DROP TABLE IF EXISTS temp_existing_excludes;
    CREATE TEMPORARY TABLE temp_existing_excludes AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             i.relname AS "ExcludeName",
             (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid AND i.relkind = 'i') AS "AccessMethod",
             -- Each key in the engine's canonical form (operator class, COLLATE, parameters; IndexKeyForms), paired
             -- with its exclusion operator by position; the declared side is normalised to the same form below.
             (SELECT STRING_AGG(k.item || ' WITH ' || COALESCE(nsp.nspname || '.' || o.oprname, o.oprname), ',' ORDER BY k.idx)
                FROM UNNEST("SchemaSmith"."IndexKeyItems"(idx.indexrelid)) WITH ORDINALITY AS k(item, idx)
                LEFT JOIN LATERAL (SELECT (con.conexclop::oid[])[k.idx] AS opr_oid) co ON TRUE
                LEFT JOIN pg_operator o ON o.oid = co.opr_oid
                LEFT JOIN pg_namespace nsp ON nsp.oid = o.oprnamespace AND nsp.nspname != 'pg_catalog') AS "ExcludeColumns",
             COALESCE("SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)), '') AS "FilterExpression",
             COALESCE(con.condeferrable, FALSE) AS "Deferrable",
             COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred",
             -- The backing index's extras: covering columns and every storage parameter (fillfactor included).
             COALESCE((SELECT STRING_AGG(a.attname, ',' ORDER BY u.idx)
                         FROM pg_attribute a
                         CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                        WHERE a.attrelid = idx.indrelid AND u.idx > idx.indnkeyatts AND a.attnum = u.element), '') AS "IncludeColumns",
             COALESCE((SELECT STRING_AGG(opt, ',' ORDER BY opt) FROM UNNEST(i.reloptions) AS o(opt)), '') AS "StorageParameters"
        FROM temp_tables t
        JOIN pg_index idx ON idx.indrelid = to_regclass("SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name"))
        JOIN pg_class i ON i.oid = idx.indexrelid
        JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype = 'x';

    RAISE NOTICE 'Collect Existing Statistics Definitions';
    DROP TABLE IF EXISTS temp_existing_statistics;
    CREATE TEMPORARY TABLE temp_existing_statistics AS
      SELECT t."Schema" AS "TableSchema",
             t."Name" AS "TableName",
             se.stxname AS "StatisticsName",
             (SELECT sn.nspname FROM pg_namespace sn WHERE sn.oid = se.stxnamespace) AS "StatisticsSchema",
             -- Both definitions in the normalised forms of SchemaSmith.StatisticsDefinitionForms; the declared side
             -- is put through the same functions wherever it is compared.
             "SchemaSmith"."NormalizeStatisticsKind"((SELECT STRING_AGG(CASE k WHEN 'd' THEN 'NDISTINCT' WHEN 'f' THEN 'DEPENDENCIES' WHEN 'm' THEN 'MCV' ELSE NULL END, ',')
                                                       FROM UNNEST(se.stxkind) AS k)) AS "Kind",
             "SchemaSmith"."StatisticsLiveColumns"(se.oid) AS "StatisticsColumns"
      FROM temp_tables t
      JOIN  pg_statistic_ext se ON se.stxrelid = to_regclass("SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name"));

    -- #242, the same move as index predicates: where the mapping vouches that the declared column list produced
    -- the live one (an expression PostgreSQL rewrote), the snapshot adopts the declared list's normalised form,
    -- so every comparison below compares equal.
    UPDATE temp_existing_statistics es
       SET "StatisticsColumns" = "SchemaSmith"."NormalizeStatisticsColumnList"(ts."StatisticsColumns")
      FROM temp_statistics ts
     WHERE ts."TableSchema" = es."TableSchema"
       AND ts."TableName" = es."TableName"
       AND ts."Name" = es."StatisticsName"
       AND ts."StatisticsColumns" LIKE '%(%'
       AND "SchemaSmith"."NormalizeStatisticsColumnList"(ts."StatisticsColumns") != es."StatisticsColumns"
       AND "SchemaSmith"."ExpressionMapUnchanged"(ts."TableSchema", ts."TableName", 'STATISTIC', ts."Name", 'columns',
             ts."StatisticsColumns", es."StatisticsColumns");

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
                                AND ek."Columns" = "SchemaSmith"."BareColumnList"(fk."Columns")
                                AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                                AND ek."RelatedTable" = fk."RelatedTable"
                                AND ek."RelatedColumns" = "SchemaSmith"."BareColumnList"(fk."RelatedColumns")
                                AND ek."DeleteAction" = fk."DeleteAction"
                                AND ek."UpdateAction" = fk."UpdateAction"
                                AND COALESCE(ek."SetNullColumns", '') = COALESCE("SchemaSmith"."BareColumnList"(fk."SetNullColumns"), '')
                                AND ek."MatchType" = COALESCE(NULLIF(UPPER(fk."MatchType"), ''), 'SIMPLE'))
            AND NOT EXISTS (SELECT 1
                              FROM temp_fks fk2
                              WHERE ek."TableSchema" = fk2."TableSchema"
                                AND ek."TableName" = fk2."TableName"
                                AND ek."KeyName" = fk2."Name")
            AND COALESCE(tt."DropForeignKeysRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Foreign Keys';
    SELECT STRING_AGG('RAISE NOTICE ''  Foreign Key ' || ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName" || ' no longer in product'';' || CHR(10) ||
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ek."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ek."TableName") || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(ek."KeyName") || ' CASCADE;' || CHR(10) ||
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
                            AND ek."Columns" = "SchemaSmith"."BareColumnList"(fk."Columns")
                            AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                            AND ek."RelatedTable" = fk."RelatedTable"
                            AND ek."RelatedColumns" = "SchemaSmith"."BareColumnList"(fk."RelatedColumns")
                            AND ek."DeleteAction" = fk."DeleteAction"
                            AND ek."UpdateAction" = fk."UpdateAction"
                                AND COALESCE(ek."SetNullColumns", '') = COALESCE("SchemaSmith"."BareColumnList"(fk."SetNullColumns"), '')
                                AND ek."MatchType" = COALESCE(NULLIF(UPPER(fk."MatchType"), ''), 'SIMPLE'))
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
                                AND ek."Columns" = "SchemaSmith"."BareColumnList"(fk."Columns")
                                AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                                AND ek."RelatedTable" = fk."RelatedTable"
                                AND ek."RelatedColumns" = "SchemaSmith"."BareColumnList"(fk."RelatedColumns")
                                AND ek."DeleteAction" = fk."DeleteAction"
                                AND ek."UpdateAction" = fk."UpdateAction"
                                AND COALESCE(ek."SetNullColumns", '') = COALESCE("SchemaSmith"."BareColumnList"(fk."SetNullColumns"), '')
                                AND ek."MatchType" = COALESCE(NULLIF(UPPER(fk."MatchType"), ''), 'SIMPLE'))
            AND (EXISTS (SELECT 1
                           FROM temp_fks fk2
                           WHERE ek."TableSchema" = fk2."TableSchema"
                             AND ek."TableName" = fk2."TableName"
                             AND ek."KeyName" = fk2."Name")
                 OR (p_DropForeignKeysRemovedFromProduct AND COALESCE(tt."DropForeignKeysRemovedFromProduct", TRUE)));
    END IF;

    -- A changed DEFERRABLE / INITIALLY DEFERRED is applied in place: ALTER CONSTRAINT changes it without re-validating
    -- the rows, which a drop and re-create would. Only keys the pass above left standing, so every other attribute agrees.
    RAISE NOTICE 'Alter Foreign Key Deferral';
    SELECT STRING_AGG('ALTER TABLE ' || "SchemaSmith"."QuoteName"(ek."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ek."TableName") || ' ALTER CONSTRAINT ' || "SchemaSmith"."QuoteName"(ek."KeyName") ||
                      CASE WHEN fk."Deferrable" THEN ' DEFERRABLE' || CASE WHEN fk."InitiallyDeferred" THEN ' INITIALLY DEFERRED' ELSE ' INITIALLY IMMEDIATE' END
                           ELSE ' NOT DEFERRABLE' END || ';' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ''' || ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName" || ''', ''modified'');', CHR(10))
      INTO sql_script
      FROM temp_existing_foreignkeys ek
      JOIN temp_fks fk ON ek."TableSchema" = fk."TableSchema"
                      AND ek."TableName" = fk."TableName"
                      AND ek."KeyName" = fk."Name"
                      AND ek."Columns" = "SchemaSmith"."BareColumnList"(fk."Columns")
                      AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                      AND ek."RelatedTable" = fk."RelatedTable"
                      AND ek."RelatedColumns" = "SchemaSmith"."BareColumnList"(fk."RelatedColumns")
                      AND ek."DeleteAction" = fk."DeleteAction"
                      AND ek."UpdateAction" = fk."UpdateAction"
                      AND COALESCE(ek."SetNullColumns", '') = COALESCE("SchemaSmith"."BareColumnList"(fk."SetNullColumns"), '')
                      AND ek."MatchType" = COALESCE(NULLIF(UPPER(fk."MatchType"), ''), 'SIMPLE')
     WHERE ek."Deferrable" <> fk."Deferrable"
        OR (fk."Deferrable" AND ek."InitiallyDeferred" <> fk."InitiallyDeferred");
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'foreignKey', ek."TableSchema" || '.' || ek."TableName" || '.' || ek."KeyName", 'wouldModify'
          FROM temp_existing_foreignkeys ek
          JOIN temp_fks fk ON ek."TableSchema" = fk."TableSchema"
                          AND ek."TableName" = fk."TableName"
                          AND ek."KeyName" = fk."Name"
                          AND ek."Columns" = "SchemaSmith"."BareColumnList"(fk."Columns")
                          AND ek."RelatedTableSchema" = fk."RelatedTableSchema"
                          AND ek."RelatedTable" = fk."RelatedTable"
                          AND ek."RelatedColumns" = "SchemaSmith"."BareColumnList"(fk."RelatedColumns")
                          AND ek."DeleteAction" = fk."DeleteAction"
                          AND ek."UpdateAction" = fk."UpdateAction"
                          AND COALESCE(ek."SetNullColumns", '') = COALESCE("SchemaSmith"."BareColumnList"(fk."SetNullColumns"), '')
                          AND ek."MatchType" = COALESCE(NULLIF(UPPER(fk."MatchType"), ''), 'SIMPLE')
         WHERE ek."Deferrable" <> fk."Deferrable"
            OR (fk."Deferrable" AND ek."InitiallyDeferred" <> fk."InitiallyDeferred");
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
                                AND (ec."Expression" = c."Expression"
                                     -- #242: PostgreSQL rewrites what it stores, so ask what was applied before calling the text a change.
                                     OR "SchemaSmith"."ExpressionMapUnchanged"(c."TableSchema", c."TableName", 'CHECK', c."Name",
                                          'expression', c."Expression", ec."Expression")))
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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."CheckName") || ' CASCADE;' || CHR(10) ||
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
                            -- NO INHERIT and, on a check, NOT ENFORCED are create-time state (PostgreSQL cannot alter a check's
                            -- enforceability): a change is a drop and re-create. Below 18 a check is always enforced, so a
                            -- declared false is the degrade, not a difference.
                            AND ec."NoInherit" = c."NoInherit"
                            AND (c."EnforcedDeclared" IS NULL OR ec."Enforced" = (c."Enforced" OR "SchemaSmith"."ServerVersionNum"() < 18))
                            AND (ec."Expression" = c."Expression"
                                 -- #242: PostgreSQL rewrites what it stores, so ask what was applied before calling the text a change.
                                 OR "SchemaSmith"."ExpressionMapUnchanged"(c."TableSchema", c."TableName", 'CHECK', c."Name",
                                      'expression', c."Expression", ec."Expression")))
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
                                AND (ec."Expression" = c."Expression"
                                     -- #242: PostgreSQL rewrites what it stores, so ask what was applied before calling the text a change.
                                     OR "SchemaSmith"."ExpressionMapUnchanged"(c."TableSchema", c."TableName", 'CHECK', c."Name",
                                          'expression', c."Expression", ec."Expression")))
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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."CheckName") || ' CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_checks ec
      JOIN temp_columns col ON col."TableSchema" = ec."TableSchema"
                           AND col."TableName" = ec."TableName"
                           AND NULLIF(col."CheckExpression", '') IS NOT NULL
                           AND ec."CheckName" = 'CK_' || col."TableName" || '_' || col."Name"
      WHERE ec."Expression" != col."CheckExpression"
        -- #242: the texts differ, but they always differ once the engine has rewritten the expression
        -- (starts_with(tag, 'a') comes back as starts_with(tag, 'a'::text)). Ask what was actually applied
        -- before calling it a change.
        AND NOT "SchemaSmith"."ExpressionMapUnchanged"(ec."TableSchema", ec."TableName", 'CHECK', ec."CheckName",
              'expression', col."CheckExpression", ec."Expression");
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
                                AND es."Kind" = "SchemaSmith"."NormalizeStatisticsKind"(ts."Kind")
                                AND es."StatisticsColumns" = "SchemaSmith"."NormalizeStatisticsColumnList"(ts."StatisticsColumns"))
            AND NOT EXISTS (SELECT 1
                              FROM temp_statistics ts2
                              WHERE es."TableSchema" = ts2."TableSchema"
                                AND es."TableName" = ts2."TableName"
                                AND es."StatisticsName" = ts2."Name")
            AND COALESCE(tt."DropStatisticsRemovedFromProduct", TRUE);
    END IF;
    RAISE NOTICE 'Drop Modified or Removed Statistics';
    SELECT STRING_AGG('RAISE NOTICE ''  Statistics ' || es."TableSchema" || '.' || es."StatisticsName" || ' modified or no longer in product'';' || CHR(10) ||
                      'DROP STATISTICS IF EXISTS ' || "SchemaSmith"."QuoteName"(es."StatisticsSchema") || '.' || "SchemaSmith"."QuoteName"(es."StatisticsName") || ' CASCADE;', CHR(10))
      INTO sql_script
      FROM temp_existing_statistics es
      JOIN temp_tables tt ON tt."Schema" = es."TableSchema"
                         AND tt."Name" = es."TableName"
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_statistics ts
                          WHERE es."TableSchema" = ts."TableSchema"
                            AND es."TableName" = ts."TableName"
                            AND es."StatisticsName" = ts."Name"
                            AND es."Kind" = "SchemaSmith"."NormalizeStatisticsKind"(ts."Kind")
                            AND es."StatisticsColumns" = "SchemaSmith"."NormalizeStatisticsColumnList"(ts."StatisticsColumns"))
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
                                AND ec."ExcludeColumns" = (SELECT STRING_AGG("SchemaSmith"."IndexKeyDeclared"(c."TableSchema", c."TableName", c."AccessMethod", (celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                                                             FROM JSON_ARRAY_ELEMENTS(c."ExcludeColumns"::JSON) AS celem)
                                AND ec."IncludeColumns" = "SchemaSmith"."NormalizeIndexColumnList"(c."IncludeColumns")
                                AND ec."StorageParameters" = c."StorageParameters"
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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."ExcludeName") || ' CASCADE;', CHR(10))
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
                            AND ec."ExcludeColumns" = (SELECT STRING_AGG("SchemaSmith"."IndexKeyDeclared"(c."TableSchema", c."TableName", c."AccessMethod", (celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT, ',')
                                                         FROM JSON_ARRAY_ELEMENTS(c."ExcludeColumns"::JSON) AS celem)
                            AND ec."IncludeColumns" = "SchemaSmith"."NormalizeIndexColumnList"(c."IncludeColumns")
                            AND ec."StorageParameters" = c."StorageParameters"
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
    -- The rename pairs are materialised, not just rendered to SQL: the drop election below starts from the
    -- PRE-rename snapshot, where a renamed index still carries its old name and is absent from the package --
    -- so without excluding these it was elected as unknown (or removed) and the deploy logged dropping it
    -- right after renaming it.
    DROP TABLE IF EXISTS temp_index_renames;
    CREATE TEMPORARY TABLE temp_index_renames AS
      SELECT ei."TableSchema", ei."TableName", ei."IndexName" AS "OldName", i."Name" AS "NewName",
             ei."PrimaryKey", ei."UniqueConstraint"
      FROM temp_existing_indexes ei
      JOIN temp_indexes i ON i."TableSchema" = ei."TableSchema"
                         AND i."TableName" = ei."TableName"
                         AND i."Name" != ei."IndexName"
                         -- The declared lists are compared through NormalizeIndexColumnList: the snapshot reads
                         -- columns back unquoted with only non-default sort options, so "status" or status ASC
                         -- never matched and the index was rebuilt on every deploy.
                         AND "SchemaSmith"."NormalizeIndexColumnList"(i."TableSchema", i."TableName", i."AccessMethod", i."IndexColumns") = ei."IndexColumns"
                         AND "SchemaSmith"."NormalizeIndexColumnList"(i."IncludeColumns") = COALESCE(ei."IncludeColumns", '')
                         -- #285: a PRIMARY KEY is unique in the catalog whether or not the package
                         -- says so, so a naturally-authored PK (PrimaryKey: true, no Unique) failed
                         -- this join and a RENAME fell through to drop+recreate. Same disjunction the
                         -- modified-index detection already uses.
                         AND (COALESCE(i."Unique", FALSE) OR COALESCE(i."PrimaryKey", FALSE)
                              OR COALESCE(i."UniqueConstraint", FALSE)) = ei."Unique"
                         AND COALESCE(i."UniqueConstraint", FALSE) = ei."UniqueConstraint"
                         AND COALESCE(i."PrimaryKey", FALSE) = ei."PrimaryKey"
                         -- #242: a rename pairs the declared index with a live one under its OLD name, so the
                         -- snapshot substitution above (which matches by name) cannot reach it. Ask the mapping,
                         -- keyed on the old name, whether the declared predicate produced the live one.
                         AND (COALESCE(i."FilterExpression", '') = COALESCE(ei."FilterExpression", '')
                              OR "SchemaSmith"."ExpressionMapUnchanged"(ei."TableSchema", ei."TableName", 'INDEX',
                                   ei."IndexName", 'filter', i."FilterExpression", COALESCE(ei."FilterExpression", '')))
                         AND COALESCE(i."AccessMethod", 'btree') = COALESCE(ei."AccessMethod", 'btree')
                         AND COALESCE(i."NullsNotDistinct", false) = COALESCE(ei."NullsNotDistinct", false)
                         AND COALESCE(i."WithoutOverlaps", false) = COALESCE(ei."WithoutOverlaps", false)
                         AND COALESCE(i."Deferrable", false) = COALESCE(ei."Deferrable", false)
                         AND COALESCE(i."InitiallyDeferred", false) = COALESCE(ei."InitiallyDeferred", false)
                         AND COALESCE(i."StorageParameters", '') = COALESCE(ei."StorageParameters", '')
      WHERE NOT EXISTS (SELECT 1
                          FROM temp_indexes i
                          WHERE i."TableSchema" = ei."TableSchema"
                            AND i."TableName" = ei."TableName"
                            AND i."Name" = ei."IndexName");
    SELECT STRING_AGG('RAISE NOTICE ''  Renaming ' || CASE WHEN rn."PrimaryKey" OR rn."UniqueConstraint" THEN 'Constraint' ELSE 'Index' END || ' ' || rn."TableSchema" || '.' || rn."TableName" || '.' || rn."OldName" || ' to ' || rn."NewName" || ''';' || CHR(10) ||
                      CASE WHEN NOT (rn."PrimaryKey" OR rn."UniqueConstraint")
                           THEN 'ALTER INDEX IF EXISTS ' || "SchemaSmith"."QuoteName"(rn."TableSchema") || '.' || "SchemaSmith"."QuoteName"(rn."OldName") || ' RENAME TO ' || "SchemaSmith"."QuoteName"(rn."NewName") || ';'
                           ELSE 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(rn."TableSchema") || '.' || "SchemaSmith"."QuoteName"(rn."TableName") || ' RENAME CONSTRAINT ' || "SchemaSmith"."QuoteName"(rn."OldName") || ' TO ' || "SchemaSmith"."QuoteName"(rn."NewName") || ';' END, CHR(10))
      INTO sql_script
      FROM temp_index_renames rn;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Identify Unknown, Removed, and Modified Indexes to Drop';
    DROP TABLE IF EXISTS temp_indexes_to_drop;
    CREATE TEMPORARY TABLE temp_indexes_to_drop AS
      SELECT ei."TableSchema",
             ei."TableName",
             ei."IndexName",
             ei."PrimaryKey" OR ei."UniqueConstraint" AS "IsConstraint",
             FALSE AS "Online"
        FROM temp_existing_indexes ei
        WHERE NOT EXISTS (SELECT 1 FROM temp_index_renames rn
                          WHERE rn."TableSchema" = ei."TableSchema" AND rn."TableName" = ei."TableName"
                            AND rn."OldName" = ei."IndexName")
          AND ((p_DropUnknownIndexes 
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
                          AND ("SchemaSmith"."NormalizeIndexColumnList"(i."TableSchema", i."TableName", i."AccessMethod", i."IndexColumns") != ei."IndexColumns"
                            OR "SchemaSmith"."NormalizeIndexColumnList"(i."IncludeColumns") != COALESCE(ei."IncludeColumns", '')
                            OR (COALESCE(i."Unique", FALSE) OR COALESCE(i."PrimaryKey", FALSE) OR COALESCE(i."UniqueConstraint", FALSE)) != ei."Unique"
                            OR COALESCE(i."UniqueConstraint", FALSE) != ei."UniqueConstraint"
                            OR COALESCE(i."PrimaryKey", FALSE) != ei."PrimaryKey"
                            OR COALESCE(i."FilterExpression", '') != COALESCE(ei."FilterExpression", '')
                            OR COALESCE(i."AccessMethod", 'btree') != COALESCE(ei."AccessMethod", 'btree')
                            OR COALESCE(i."NullsNotDistinct", false) != COALESCE(ei."NullsNotDistinct", false)
                            OR COALESCE(i."WithoutOverlaps", false) != COALESCE(ei."WithoutOverlaps", false)
                            OR COALESCE(i."Deferrable", false) != COALESCE(ei."Deferrable", false)
                            OR COALESCE(i."InitiallyDeferred", false) != COALESCE(ei."InitiallyDeferred", false)
                            -- A storage-parameter change rebuilds the index (hnsw m and ivfflat lists cannot
                            -- be ALTERed in place), so it drops here and the missing-index pass recreates it.
                            OR COALESCE(i."StorageParameters", '') != COALESCE(ei."StorageParameters", '')
                            -- An invalid index (a failed CONCURRENTLY build) is present by name and used by nothing:
                            -- not the declared index, so it is dropped and the declared one created.
                            OR NOT ei."Valid"))
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
                                              AND i."Name" = ei."IndexName"))));

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
          WHERE NOT EXISTS (SELECT 1 FROM temp_index_renames rn
                          WHERE rn."TableSchema" = ei."TableSchema" AND rn."TableName" = ei."TableName"
                            AND rn."OldName" = ei."IndexName")
            AND (NOT EXISTS (SELECT 1 -- Unknown Index (minus the p_DropUnknownIndexes gate)
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
                                     AND i."Name" = ei."IndexName")));
    END IF;

    -- A foreign key this deploy will not re-create references an index it would drop or change. Dropping a constraint
    -- CASCADE took the key with it for good, and a plain unique index a key references cannot be dropped at all, so the
    -- index is kept as it is.
    DROP TABLE IF EXISTS temp_indexes_kept_for_fks;
    CREATE TEMPORARY TABLE temp_indexes_kept_for_fks AS
      SELECT DISTINCT ti."TableSchema", ti."TableName", ti."IndexName",
             fkn.nspname || '.' || fkr.relname || '.' || fkc.conname AS "KeyName"
        FROM temp_indexes_to_drop ti
        JOIN pg_namespace icn ON icn.nspname = ti."TableSchema"
        JOIN pg_class ic ON ic.relnamespace = icn.oid AND ic.relname = ti."IndexName" AND ic.relkind = 'i'
        JOIN pg_constraint fkc ON fkc.contype = 'f' AND fkc.conindid = ic.oid
        JOIN pg_class fkr ON fkr.oid = fkc.conrelid
        JOIN pg_namespace fkn ON fkn.oid = fkr.relnamespace
        WHERE TRUE
          -- A key the package declares is dropped with the constraint and put back by ForeignKeyQuench.
          AND NOT EXISTS (SELECT 1 FROM temp_fks f
                          WHERE f."TableSchema" = fkn.nspname AND f."TableName" = fkr.relname AND f."Name" = fkc.conname);
    SELECT STRING_AGG('RAISE WARNING ''  WARNING: Kept ' || REPLACE(k."TableSchema" || '.' || k."TableName" || '.' || k."IndexName", '''', '''''') ||
                      ' as it is: foreign key ' || REPLACE(k."KeyName", '''', '''''') || ' references it'';', CHR(10))
      INTO sql_script
      FROM temp_indexes_kept_for_fks k;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, FALSE);
    DELETE FROM temp_indexes_to_drop ti
      USING temp_indexes_kept_for_fks k
      WHERE k."TableSchema" = ti."TableSchema" AND k."TableName" = ti."TableName" AND k."IndexName" = ti."IndexName";
    DROP TABLE IF EXISTS temp_indexes_kept_for_fks;

    -- Name the invalid ones: the drop below reads like any other, and the reason is the engine's, not the package's.
    SELECT STRING_AGG('RAISE WARNING ''  WARNING: ' || REPLACE(ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName", '''', '''''') ||
                      ' is marked invalid by the engine (a CREATE INDEX CONCURRENTLY that did not finish); it is dropped and the declared index created'';', CHR(10))
      INTO sql_script
      FROM temp_indexes_to_drop ti
      JOIN temp_existing_indexes ei ON ei."TableSchema" = ti."TableSchema" AND ei."TableName" = ti."TableName" AND ei."IndexName" = ti."IndexName"
      WHERE NOT ei."Valid";
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, FALSE);

    RAISE NOTICE 'Drop Unknown, Removed, and Modified Indexes';
    -- OnlineApply: an index the package no longer declares is dropped CONCURRENTLY after this procedure. A changed one
    -- is dropped here, because the missing-index pass builds it again only once it is gone.
    UPDATE temp_indexes_to_drop ti
       SET "Online" = TRUE
     WHERE NOT ti."IsConstraint"
       AND NOT EXISTS (SELECT 1 FROM temp_indexes i
                        WHERE i."TableSchema" = ti."TableSchema" AND i."TableName" = ti."TableName" AND i."Name" = ti."IndexName")
       AND "SchemaSmith"."OnlineApplyForm"(p_OnlineApply, ti."TableSchema", ti."TableName", 'index') = 'online';
    SELECT STRING_AGG(CASE WHEN ti."Online"
                           THEN 'RAISE NOTICE ''  Dropping Index ' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ' (online, after this pass)'';' || CHR(10) ||
                                CASE WHEN p_WhatIf THEN d."Statement"
                                     ELSE 'CALL "SchemaSmith"."DeferOnlineWork"(' || quote_literal('Dropping Index ' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ' concurrently') || ', ' ||
                                          quote_literal(d."Statement") || ', NULL, ' ||
                                          quote_literal('INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode") VALUES (pg_backend_pid(), ''index'', ''' ||
                                                        ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ''', ''dropped'', ''online'');') || ');' END
                           ELSE 'RAISE NOTICE ''  Dropping ' || CASE WHEN "IsConstraint" THEN 'Constraint' ELSE 'Index' END || ' ' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ''';' || CHR(10) ||
                                CASE WHEN "IsConstraint"
                                     THEN 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."TableName") || ' DROP CONSTRAINT IF EXISTS ' || "SchemaSmith"."QuoteName"(ti."IndexName") || ' CASCADE;'
                                     ELSE 'DROP INDEX IF EXISTS ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."IndexName") || ';' END || CHR(10) ||
                                'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || CASE WHEN "IsConstraint" THEN 'constraint' ELSE 'index' END || ''', ''' || ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName" || ''', ''dropped'');'
                      END, CHR(10))
      INTO sql_script
      FROM temp_indexes_to_drop ti
      CROSS JOIN LATERAL (SELECT 'DROP INDEX CONCURRENTLY IF EXISTS ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."IndexName") || ';' AS "Statement") d;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- #363: WhatIf twin of the embedded 'index'/'constraint' 'dropped' audit above; same source.
    IF p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode")
        SELECT pg_backend_pid(), CASE WHEN "IsConstraint" THEN 'constraint' ELSE 'index' END,
               ti."TableSchema" || '.' || ti."TableName" || '.' || ti."IndexName", 'wouldDrop',
               CASE WHEN ti."Online" THEN 'online' END
          FROM temp_indexes_to_drop ti;
    END IF;

    -- Every drop has run, so a declared index or constraint missing now is one this deploy builds.
    CALL "SchemaSmith"."RefuseUnmetOnlineRequire"(p_OnlineApply, p_WhatIf, FALSE);

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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."ColumnName") || ' CASCADE;' || CHR(10) ||
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
                      'ALTER INDEX ' || "SchemaSmith"."QuoteName"(ti."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ti."Name") || ' SET (fillfactor = ' || ti."FillFactor" || ');', CHR(10))
      INTO sql_script
      FROM temp_indexes ti
      JOIN temp_existing_indexes ei ON ei."TableSchema" = ti."TableSchema"
                                   AND ei."TableName" = ti."TableName"
                                   AND ei."IndexName" = ti."Name"
      JOIN pg_index idx ON idx.indrelid = ("SchemaSmith"."QuoteName"(ti."TableSchema") ||  '.' || "SchemaSmith"."QuoteName"(ti."TableName"))::regclass
      JOIN pg_class i ON i.oid = idx.indexrelid
                     AND i.relname = ti."Name"
      WHERE ti."UpdateFillFactor"
        AND ei."FillFactor" != ti."FillFactor"
        -- Positive gate, not a deny-list: an extension AM (e.g. pgvector's hnsw/ivfflat) can't be
        -- enumerated in advance, so allow-listing the AMs verified to accept fillfactor fails safe
        -- (skip the ALTER) instead of failing loud (PostgreSQL's "unrecognized parameter" error).
        AND COALESCE(ti."AccessMethod", 'btree') IN ('btree', 'gist', 'hash', 'spgist');
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Drop Generated Columns Referencing Columns That Are Changing Data Type';
    SELECT STRING_AGG('RAISE NOTICE ''  Column ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ' affected by type change'';' || CHR(10) ||
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."ColumnName") || ' CASCADE;', CHR(10))
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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."ColumnName") || ' CASCADE;', CHR(10))
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

    -- STORED <-> VIRTUAL has no ALTER, so the column is dropped here and the create pass adds it back as declared. A
    -- generated column holds nothing of its own, so nothing is lost. Only from 18: below it there is no VIRTUAL to keep,
    -- and the create pass skips a declared VIRTUAL there, so a drop would lose the column.
    IF "SchemaSmith"."ServerVersionNum"() >= 18 THEN
      RAISE NOTICE 'Drop Generated Columns Changing Between STORED and VIRTUAL';
      SELECT STRING_AGG('RAISE NOTICE ''  Column ' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ' becomes ' || CASE WHEN c."Virtual" THEN 'VIRTUAL' ELSE 'STORED' END || ''';' || CHR(10) ||
                        'ALTER TABLE ' || "SchemaSmith"."QuoteName"(ec."TableSchema") || '.' || "SchemaSmith"."QuoteName"(ec."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(ec."ColumnName") || ' CASCADE;' || CHR(10) ||
                        'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''column'', ''' || ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName" || ''', ''dropped'');', CHR(10))
        INTO sql_script
        FROM temp_existing_columns ec
        JOIN temp_columns c ON c."TableSchema" = ec."TableSchema"
                           AND c."TableName" = ec."TableName"
                           AND c."Name" = ec."ColumnName"
                           AND c."Generated" = 'ALWAYS' AND COALESCE(c."GenerationExpression", '') <> ''
       WHERE ec."Generated" = 'ALWAYS'
         AND c."Virtual" <> ec."Virtual";
      CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
      IF p_WhatIf THEN
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'column', ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName", 'wouldDrop'
            FROM temp_existing_columns ec
            JOIN temp_columns c ON c."TableSchema" = ec."TableSchema"
                               AND c."TableName" = ec."TableName"
                               AND c."Name" = ec."ColumnName"
                               AND c."Generated" = 'ALWAYS' AND COALESCE(c."GenerationExpression", '') <> ''
           WHERE ec."Generated" = 'ALWAYS'
             AND c."Virtual" <> ec."Virtual";
      END IF;
    END IF;

    DELETE FROM temp_existing_columns ec
      WHERE NOT EXISTS (SELECT 1
                          FROM information_schema.columns ic
                          WHERE ic.table_schema = ec."TableSchema"
                            AND ic.table_name = ec."TableName"
                            AND ic.column_name = ec."ColumnName");


    -- The objects a column type change must take down and put back: views and materialized views (pg_rewrite), SQL-standard
    -- routines (pg_proc, PostgreSQL 14) and row-level security policies (pg_policy) that read a column whose type changes.
    -- Only what the package declares can be put back -- a view or routine by its script, a materialized view or a policy
    -- by its declaration -- so when the deploy says what it declares (temp_declared_modules, staged by SchemaQuench), a
    -- dependent it does not declare stops the deploy by name before anything is dropped, as SQL Server's schema-bound
    -- dependents do. Dropping it would lose it.
    DROP TABLE IF EXISTS temp_type_change_dependents;
    CREATE TEMPORARY TABLE temp_type_change_dependents AS
      SELECT DISTINCT dep.*
        FROM temp_existing_columns ec
        JOIN temp_columns c ON c."TableSchema" = ec."TableSchema" AND c."TableName" = ec."TableName" AND c."Name" = ec."ColumnName"
        JOIN pg_class ref_obj ON ref_obj.oid = to_regclass(QUOTE_IDENT(ec."TableSchema") || '.' || QUOTE_IDENT(ec."TableName"))
        JOIN pg_attribute ref_attr ON ref_attr.attrelid = ref_obj.oid AND ref_attr.attname = ec."ColumnName"
        JOIN pg_depend d ON d.refclassid = 'pg_class'::regclass AND d.refobjid = ref_obj.oid AND d.refobjsubid = ref_attr.attnum
        CROSS JOIN LATERAL (
          SELECT CASE vc.relkind WHEN 'm' THEN 'materialized view' ELSE 'view' END AS "Kind", vn.nspname AS "Schema", vc.relname AS "Name", NULL::TEXT AS "OnTable"
            FROM pg_rewrite r JOIN pg_class vc ON vc.oid = r.ev_class JOIN pg_namespace vn ON vn.oid = vc.relnamespace
           WHERE d.classid = 'pg_rewrite'::regclass AND r.oid = d.objid AND r.rulename = '_RETURN' AND vc.oid <> ref_obj.oid
          UNION ALL
          SELECT CASE p.prokind WHEN 'p' THEN 'procedure' ELSE 'function' END, pn.nspname, p.proname, NULL
            FROM pg_proc p JOIN pg_namespace pn ON pn.oid = p.pronamespace
           WHERE d.classid = 'pg_proc'::regclass AND p.oid = d.objid
          UNION ALL
          SELECT 'policy', ec."TableSchema", pol.polname, ec."TableName"
            FROM pg_policy pol
           WHERE d.classid = 'pg_policy'::regclass AND pol.oid = d.objid) dep
       WHERE REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC');

    IF to_regclass('pg_temp.temp_declared_modules') IS NOT NULL THEN
      SELECT STRING_AGG(dep."Kind" || ' ' || dep."Schema" || '.' || dep."Name", ', ' ORDER BY dep."Kind", dep."Schema", dep."Name")
        INTO undeclared_dependents
        FROM temp_type_change_dependents dep
       WHERE (dep."Kind" <> 'policy'
              AND NOT EXISTS (SELECT 1 FROM temp_declared_modules m WHERE m."Schema" = dep."Schema" AND m."Name" = dep."Name"))
          OR (dep."Kind" = 'policy'
              AND NOT EXISTS (SELECT 1 FROM temp_policies tp WHERE tp."TableSchema" = dep."Schema" AND tp."TableName" = dep."OnTable" AND tp."Name" = dep."Name"));
      IF undeclared_dependents IS NOT NULL THEN
        RAISE EXCEPTION 'A column type change in this package has to drop and re-create what reads that column, and the package does not declare: %. Declare it in the package so it can be put back, or drop it in a Before script. Nothing was changed.', undeclared_dependents;
      END IF;
    END IF;

    -- A DROP takes the object's privileges, owner and comments with it, and the script or declaration that re-creates it
    -- does not carry them. They are captured here and put back by SchemaQuench once the run has re-created the object.
    IF NOT p_WhatIf AND to_regclass('pg_temp.temp_preserved_module_permissions') IS NOT NULL THEN
      INSERT INTO temp_preserved_module_permissions ("ObjectType", "Schema", "Name", "Script")
        SELECT cap."ObjectType", cap."Schema", cap."Name", cap."Script"
          FROM (SELECT CASE dep."Kind" WHEN 'materialized view' THEN 'matview' ELSE dep."Kind" END AS "ObjectType",
                       dep."Schema", dep."Name",
                       "SchemaSmith"."CapturePermissions"(dep."Schema", dep."Name", UPPER(dep."Kind")) AS "Script"
                  FROM (SELECT DISTINCT "Kind", "Schema", "Name" FROM temp_type_change_dependents WHERE "Kind" <> 'policy') dep) cap
         WHERE cap."Script" IS NOT NULL;
    END IF;

    -- Declared policies over a type-changing column: dropped here, created again by MissingIndexesAndConstraintsQuench.
    SELECT STRING_AGG('RAISE NOTICE ''  Dropping Policy ' || REPLACE(dep."Name", '''', '''''') || ' on ' || REPLACE(dep."Schema" || '.' || dep."OnTable", '''', '''''') || ''';' || CHR(10) ||
                      'DROP POLICY IF EXISTS ' || QUOTE_IDENT(dep."Name") || ' ON ' || QUOTE_IDENT(dep."Schema") || '.' || QUOTE_IDENT(dep."OnTable") || ';' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''policy'', ''' ||
                      REPLACE(dep."Schema" || '.' || dep."OnTable" || '.' || dep."Name", '''', '''''') || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_type_change_dependents dep
     WHERE dep."Kind" = 'policy';
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- A trigger whose WHEN clause or column list reads a type-changing column ("used in a trigger definition") and a
    -- publication's row filter or column list over one (PostgreSQL 15, "used by a publication WHERE clause") block the
    -- change too. The catalog describes both in full, so they are scripted here, taken down, and put back right after
    -- the column pass, whether or not the package carries them.
    DROP TABLE IF EXISTS temp_type_change_rebuilds;
    CREATE TEMPORARY TABLE temp_type_change_rebuilds ("Kind" TEXT, "Name" TEXT, "DropSql" TEXT, "CreateSql" TEXT);
    INSERT INTO temp_type_change_rebuilds
      SELECT DISTINCT 'trigger', tn.nspname || '.' || tc.relname || '.' || tg.tgname,
             'DROP TRIGGER ' || QUOTE_IDENT(tg.tgname) || ' ON ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ';',
             pg_get_triggerdef(tg.oid) || ';' ||
             CASE tg.tgenabled WHEN 'D' THEN ' ALTER TABLE ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ' DISABLE TRIGGER ' || QUOTE_IDENT(tg.tgname) || ';'
                               WHEN 'R' THEN ' ALTER TABLE ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ' ENABLE REPLICA TRIGGER ' || QUOTE_IDENT(tg.tgname) || ';'
                               WHEN 'A' THEN ' ALTER TABLE ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ' ENABLE ALWAYS TRIGGER ' || QUOTE_IDENT(tg.tgname) || ';'
                               ELSE '' END ||
             COALESCE(' COMMENT ON TRIGGER ' || QUOTE_IDENT(tg.tgname) || ' ON ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ' IS ' ||
                      QUOTE_LITERAL(obj_description(tg.oid, 'pg_trigger')) || ';', '')
        FROM temp_existing_columns ec
        JOIN temp_columns c ON c."TableSchema" = ec."TableSchema" AND c."TableName" = ec."TableName" AND c."Name" = ec."ColumnName"
        JOIN pg_class tc ON tc.oid = to_regclass(QUOTE_IDENT(ec."TableSchema") || '.' || QUOTE_IDENT(ec."TableName"))
        JOIN pg_namespace tn ON tn.oid = tc.relnamespace
        JOIN pg_attribute a ON a.attrelid = tc.oid AND a.attname = ec."ColumnName"
        JOIN pg_depend d ON d.classid = 'pg_trigger'::regclass AND d.refclassid = 'pg_class'::regclass AND d.refobjid = tc.oid AND d.refobjsubid = a.attnum
        JOIN pg_trigger tg ON tg.oid = d.objid AND NOT tg.tgisinternal
       WHERE REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC');
    -- prqual and prattrs are PostgreSQL 15 columns, so the query is only planned where they exist. The catalog is asked
    -- rather than the version, which a test may pin above the server it runs on.
    IF EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'pg_catalog.pg_publication_rel'::regclass AND attname = 'prqual') THEN
      EXECUTE $q$
        INSERT INTO temp_type_change_rebuilds
          SELECT DISTINCT 'publication', pub.pubname || ' ' || tn.nspname || '.' || tc.relname,
                 'ALTER PUBLICATION ' || QUOTE_IDENT(pub.pubname) || ' DROP TABLE ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) || ';',
                 'ALTER PUBLICATION ' || QUOTE_IDENT(pub.pubname) || ' ADD TABLE ' || QUOTE_IDENT(tn.nspname) || '.' || QUOTE_IDENT(tc.relname) ||
                 COALESCE(' (' || (SELECT STRING_AGG(QUOTE_IDENT(pa.attname), ', ' ORDER BY k.ord)
                                     FROM UNNEST(pr.prattrs::int2[]) WITH ORDINALITY k(attnum, ord)
                                     JOIN pg_attribute pa ON pa.attrelid = tc.oid AND pa.attnum = k.attnum) || ')', '') ||
                 COALESCE(' WHERE (' || pg_get_expr(pr.prqual, pr.prrelid) || ')', '') || ';'
            FROM temp_existing_columns ec
            JOIN temp_columns c ON c."TableSchema" = ec."TableSchema" AND c."TableName" = ec."TableName" AND c."Name" = ec."ColumnName"
            JOIN pg_class tc ON tc.oid = to_regclass(QUOTE_IDENT(ec."TableSchema") || '.' || QUOTE_IDENT(ec."TableName"))
            JOIN pg_namespace tn ON tn.oid = tc.relnamespace
            JOIN pg_attribute a ON a.attrelid = tc.oid AND a.attname = ec."ColumnName"
            JOIN pg_depend d ON d.classid = 'pg_publication_rel'::regclass AND d.refclassid = 'pg_class'::regclass AND d.refobjid = tc.oid AND d.refobjsubid = a.attnum
            JOIN pg_publication_rel pr ON pr.oid = d.objid
            JOIN pg_publication pub ON pub.oid = pr.prpubid
           WHERE REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')
      $q$;
    END IF;
    SELECT STRING_AGG('RAISE NOTICE ''  Dropping ' || INITCAP(r."Kind") || ' ' || REPLACE(r."Name", '''', '''''') || ' - it reads a type-changing column; put back after the column pass'';' || CHR(10) ||
                      r."DropSql" || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || r."Kind" || ''', ''' ||
                      REPLACE(r."Name", '''', '''''') || ''', ''dropped'');', CHR(10) ORDER BY r."Kind", r."Name")
      INTO sql_script
      FROM temp_type_change_rebuilds r;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    RAISE NOTICE 'Drop Views and Rules Dependent on Modified Columns';
    SELECT STRING_AGG(CASE WHEN dep_view.relname IS NOT NULL
                           THEN 'RAISE NOTICE ''  Dropping ' || CASE WHEN dep_view.relkind = 'm' THEN 'Materialized View ' ELSE 'View ' END || view_ns.nspname || '.' || dep_view.relname || ''';' || CHR(10) ||
                                'DROP ' || CASE WHEN dep_view.relkind = 'm' THEN 'MATERIALIZED VIEW' ELSE 'VIEW' END || ' IF EXISTS ' || QUOTE_IDENT(view_ns.nspname) || '.' || QUOTE_IDENT(dep_view.relname) || ';' || CHR(10) ||
                                'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || CASE WHEN dep_view.relkind = 'm' THEN 'materializedView' ELSE 'view' END || ''', ''' ||
                                REPLACE(view_ns.nspname || '.' || dep_view.relname, '''', '''''') || ''', ''dropped'');'
                           ELSE CASE WHEN dep_rule.rulename != '_RETURN'
                                     THEN 'RAISE NOTICE ''  Dropping Rule ' || rule_class.relnamespace::regnamespace || '.' || dep_rule.rulename || ''';' || CHR(10) ||
                                          'DROP RULE IF EXISTS ' || rule_class.relnamespace::regnamespace::text || '.' || "SchemaSmith"."QuoteName"(dep_rule.rulename) || ';'
                                     END
                           END, CHR(10))
      INTO sql_script
      FROM temp_existing_columns ec
      JOIN pg_class AS ref_obj ON ref_obj.relnamespace::regnamespace = ("SchemaSmith"."QuoteName"(ec."TableSchema"))::regnamespace
                              AND ref_obj.relname = ec."TableName"
      JOIN pg_depend AS d ON ref_obj.oid = d.refobjid
      JOIN pg_attribute AS ref_attr ON ref_attr.attrelid = d.refobjid AND ref_attr.attnum = d.refobjsubid
                                   AND ref_attr.attname = ec."ColumnName"
      JOIN pg_rewrite AS dep_rule ON dep_rule.oid = d.objid
      JOIN pg_class rule_class ON rule_class.oid = dep_rule.ev_class
      LEFT JOIN pg_class AS dep_view ON dep_view.oid = dep_rule.ev_class
      LEFT JOIN pg_namespace AS view_ns ON view_ns.oid = dep_view.relnamespace
      WHERE EXISTS (SELECT 1 -- need to determine if any changes other than type have the need to drop dependent views/rules
                      FROM temp_columns c
                      WHERE c."TableSchema" = ec."TableSchema"
                        AND c."TableName" = ec."TableName"
                        AND c."Name" = ec."ColumnName"
                        AND REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'));
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    -- PG-128. A SQL-standard routine body (BEGIN ATOMIC / RETURN, PostgreSQL 14) records a dependency on the columns it
    -- reads, and PostgreSQL refuses to change their type ("cannot alter type of a column used by a function or
    -- procedure"). Dropped here like a dependent view, and put back by the after-tables object pass, which DatabaseQuench
    -- tells from the 'dropped' audit row (a script already applied this run is not run again otherwise). A routine with a dollar-quoted body
    -- records no such dependency, so below 14 this finds nothing.
    RAISE NOTICE 'Drop SQL-Standard Routines Dependent on Modified Columns';
    SELECT STRING_AGG(DISTINCT 'RAISE NOTICE ''  Dropping ' || CASE WHEN p.prokind = 'p' THEN 'Procedure ' ELSE 'Function ' END || REPLACE(p.oid::regprocedure::text, '''', '''''') || ''';' || CHR(10) ||
                      'DROP ' || CASE WHEN p.prokind = 'p' THEN 'PROCEDURE ' ELSE 'FUNCTION ' END || p.oid::regprocedure::text || ';' || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' ||
                      CASE WHEN p.prokind = 'p' THEN 'procedure' ELSE 'function' END || ''', ''' || REPLACE(pn.nspname || '.' || p.proname, '''', '''''') || ''', ''dropped'');', CHR(10))
      INTO sql_script
      FROM temp_existing_columns ec
      JOIN pg_class AS ref_obj ON ref_obj.relnamespace::regnamespace = ("SchemaSmith"."QuoteName"(ec."TableSchema"))::regnamespace
                              AND ref_obj.relname = ec."TableName"
      JOIN pg_attribute AS ref_attr ON ref_attr.attrelid = ref_obj.oid AND ref_attr.attname = ec."ColumnName"
      JOIN pg_depend AS d ON d.refclassid = 'pg_class'::regclass AND d.refobjid = ref_obj.oid AND d.refobjsubid = ref_attr.attnum
                         AND d.classid = 'pg_proc'::regclass
      JOIN pg_proc AS p ON p.oid = d.objid
      JOIN pg_namespace AS pn ON pn.oid = p.pronamespace
      WHERE EXISTS (SELECT 1
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
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(tt."Schema") || '.' || "SchemaSmith"."QuoteName"(tt."Name") || ' ' ||
                      (SELECT STRING_AGG('ADD ' || "SchemaSmith"."QuoteName"(tc."Name") || ' ' || tc."DataType" ||
                              "SchemaSmith"."CollateClause"(tc."Collation") ||
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
                        'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(c."Name") || ' CASCADE;' || CHR(10) ||
                        'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' ADD ' || "SchemaSmith"."QuoteName"(c."Name") || ' ' || c."DataType" ||
                        "SchemaSmith"."CollateClause"(c."Collation") ||
                        ' GENERATED ALWAYS AS (' || c."GenerationExpression" || ') STORED' ||
                        -- NOT NULL only when the package asked for it. An undeclared column's Nullable is the live
                        -- value, and re-emitting a NOT NULL an older version applied would fail the re-add -- after
                        -- the DROP -- whenever the new expression can yield NULL.
                        CASE WHEN c."Nullable" OR NOT c."NullableDeclared" THEN '' ELSE ' NOT NULL' END || ';' ||
                        -- Re-added column is at default storage/compression; carry over any non-default target so
                        -- a combined expression+storage/compression change fully converges in this single pass.
                        CASE WHEN COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != 'DEFAULT'
                             THEN CHR(10) || 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET STORAGE ' || c."Storage" || ';'
                             ELSE '' END ||
                        CASE WHEN COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != 'DEFAULT' AND "SchemaSmith"."ServerVersionNum"() >= 14
                             THEN CHR(10) || 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET COMPRESSION ' || c."Compression" || ';'
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
      -- The lost values go on the record: 'fail' refuses before anything is dropped, 'warn' records a
      -- downgrade row per column, and the drop-and-re-add below runs.
      IF EXISTS (SELECT 1 FROM temp_columns c
                   JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema" AND ec."TableName" = c."TableName" AND ec."ColumnName" = c."Name"
                  WHERE (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
                    AND COALESCE(ec."GenerationExpression", '') != '') THEN
        IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail' THEN
          RAISE EXCEPTION 'Turning a generated column into a plain one keeps its values from PostgreSQL 13 (DROP EXPRESSION); below that the column is dropped and re-added empty (detected major %); column(s): %',
            "SchemaSmith"."ServerVersionNum"(),
            (SELECT STRING_AGG(c."TableSchema" || '.' || c."TableName" || '.' || c."Name", ', ')
               FROM temp_columns c
               JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema" AND ec."TableName" = c."TableName" AND ec."ColumnName" = c."Name"
              WHERE (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
                AND COALESCE(ec."GenerationExpression", '') != '');
        END IF;
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'un-generated column, values not kept (PG13)',
                 c."TableSchema" || '.' || c."TableName" || '.' || c."Name", 'downgraded'
            FROM temp_columns c
            JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema" AND ec."TableName" = c."TableName" AND ec."ColumnName" = c."Name"
           WHERE (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
             AND COALESCE(ec."GenerationExpression", '') != '';
      END IF;

      SELECT STRING_AGG('RAISE NOTICE ''  Un-generating column ' || c."TableSchema" || '.' || c."TableName" || '.' || c."Name" || ' (drop+re-add as plain; PG < 13 has no DROP EXPRESSION)'';' || CHR(10) ||
                        'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' DROP COLUMN IF EXISTS ' || "SchemaSmith"."QuoteName"(c."Name") || ' CASCADE;' || CHR(10) ||
                        'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' ADD ' || "SchemaSmith"."QuoteName"(c."Name") || ' ' || c."DataType" ||
                        "SchemaSmith"."CollateClause"(c."Collation") ||
                        CASE WHEN c."Nullable" THEN '' ELSE ' NOT NULL' END ||
                        CASE WHEN COALESCE(c."Default", '') != '' THEN ' DEFAULT ' || c."Default" ELSE '' END || ';' ||
                        CASE WHEN COALESCE(c."Storage", '') != '' AND COALESCE(c."Storage", '') != 'DEFAULT'
                             THEN CHR(10) || 'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET STORAGE ' || c."Storage" || ';'
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

    -- Which identity columns' declared sequence options differ, decided before the ALTER below changes the sequence: the
    -- audit after it reads this, because by then the live sequence already matches.
    DROP TABLE IF EXISTS temp_identity_option_changes;
    CREATE TEMPORARY TABLE temp_identity_option_changes AS
      SELECT c."TableSchema", c."TableName", c."Name"
        FROM temp_columns c
        JOIN temp_existing_columns ec ON ec."TableSchema" = c."TableSchema" AND ec."TableName" = c."TableName" AND ec."ColumnName" = c."Name"
       WHERE (COALESCE(c."Generated", '') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", '') LIKE 'GENERATED%IDENTITY%'
                      AND "SchemaSmith"."IdentityOptionChanges"(c."Generated", pg_get_serial_sequence(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"), c."Name")::regclass) <> '');

    -- PG-095. A column cannot become nullable while it is in the index the table uses as its replica identity, and the
    -- declared identity is applied later (ReplicaIdentityQuench), so a package that moves the identity off that index and
    -- relaxes the column failed: "column ... is in index used as replica identity". The identity steps aside to DEFAULT
    -- first; ReplicaIdentityQuench then sets the declared one. A package that keeps the index gets the engine's refusal.
    RAISE NOTICE 'Release Replica Identity Index Ahead of a Nullability Change';
    SELECT STRING_AGG(DISTINCT 'ALTER TABLE ' || QUOTE_IDENT(t."Schema") || '.' || QUOTE_IDENT(t."Name") || ' REPLICA IDENTITY DEFAULT;', CHR(10))
      INTO sql_script
      FROM temp_tables t
      JOIN pg_class c ON c.oid = to_regclass(QUOTE_IDENT(t."Schema") || '.' || QUOTE_IDENT(t."Name")) AND c.relreplident = 'i'
      JOIN pg_index ri ON ri.indrelid = c.oid AND ri.indisreplident
      JOIN pg_class ric ON ric.oid = ri.indexrelid
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY (ri.indkey)
      JOIN temp_columns col ON col."TableSchema" = t."Schema" AND col."TableName" = t."Name" AND col."Name" = a.attname AND col."Nullable"
      JOIN temp_existing_columns ec ON ec."TableSchema" = t."Schema" AND ec."TableName" = t."Name" AND ec."ColumnName" = a.attname AND NOT ec."Nullable"
     WHERE COALESCE(t."ReplicaIdentity", '') != ''
       AND NOT (t."ReplicaIdentity" = 'INDEX' AND t."ReplicaIdentityIndex" = ric.relname);
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

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
                  OR (COALESCE(ic."Compression", '') != '' AND COALESCE(ic."Compression", '') != COALESCE(iec."Compression", ''))
                  -- Declared identity sequence options that differ from the live sequence (only the ones declared).
                  OR (COALESCE(ic."Generated", '') LIKE 'GENERATED%IDENTITY%' AND COALESCE(iec."Generated", '') LIKE 'GENERATED%IDENTITY%'
                      AND "SchemaSmith"."IdentityOptionChanges"(ic."Generated", pg_get_serial_sequence(QUOTE_IDENT(ic."TableSchema") || '.' || QUOTE_IDENT(ic."TableName"), ic."Name")::regclass) <> ''))) || ')'';' || CHR(10) ||
           -- A column converging to serial needs its sequence first: created and owned by the column here, made the
           -- default by the ALTER below. IF NOT EXISTS keeps an earlier half-applied run harmless.
           COALESCE((SELECT STRING_AGG('CREATE SEQUENCE IF NOT EXISTS ' || QUOTE_IDENT(sc."TableSchema") || '.' || QUOTE_IDENT(sc."TableName" || '_' || sc."Name" || '_seq') ||
                                       ' OWNED BY ' || QUOTE_IDENT(sc."TableSchema") || '.' || QUOTE_IDENT(sc."TableName") || '.' || QUOTE_IDENT(sc."Name") || ';', CHR(10))
                       FROM temp_columns sc
                       JOIN temp_existing_columns sec ON sec."TableSchema" = sc."TableSchema" AND sec."TableName" = sc."TableName" AND sec."ColumnName" = sc."Name"
                      WHERE sc."TableSchema" = c."TableSchema" AND sc."TableName" = c."TableName" AND sc."IsSerial"
                        AND COALESCE("SchemaSmith"."StripTypeCast"(sc."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(sec."Default"), '')) || CHR(10), '') ||
           'ALTER TABLE ' || "SchemaSmith"."QuoteName"(c."TableSchema") || '.' || "SchemaSmith"."QuoteName"(c."TableName") || CHR(10) ||
           STRING_AGG(NULLIF(RTRIM(TRIM(TRAILING ',' FROM
                      CASE WHEN REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') != REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(ec."DataType"), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC') OR COALESCE(c."Collation", '') != COALESCE(ec."Collation", '')
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET DATA TYPE ' || c."DataType" ||
                                                    "SchemaSmith"."CollateClause"(c."Collation") || ','
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."GenerationExpression", '') != COALESCE(ec."GenerationExpression", '')
                           THEN CASE WHEN (COALESCE(c."Generated", 'NEVER') = 'NEVER' OR COALESCE(c."GenerationExpression", '') = '')
                                     THEN CASE WHEN "SchemaSmith"."ServerVersionNum"() >= 13
                                               THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' DROP EXPRESSION,'
                                               ELSE '' END  -- PG < 13: handled by the un-generate drop-and-re-add pass above
                                     WHEN "SchemaSmith"."ServerVersionNum"() >= 17
                                     THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET EXPRESSION AS (' || COALESCE(c."GenerationExpression", '') || '),'
                                     ELSE '' END  -- PG < 17: expression change is handled by the drop-and-re-add pass below
                           ELSE '' END ||
                      CASE WHEN COALESCE(ec."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%'
                                AND COALESCE(c."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%'
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' DROP IDENTITY IF EXISTS,'
                           ELSE '' END ||
                      CASE WHEN c."Nullable" != ec."Nullable"
                           THEN CASE WHEN c."Nullable"
                                     THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' DROP NOT NULL,'
                                     ELSE ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET NOT NULL,' END
                           ELSE '' END ||
                      CASE WHEN COALESCE("SchemaSmith"."StripTypeCast"(c."Default"), '') != COALESCE("SchemaSmith"."StripTypeCast"(ec."Default"), '')
                           THEN CASE WHEN COALESCE(c."Default", '') = ''
                                     THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' DROP DEFAULT,'
                                     ELSE ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET DEFAULT ' || c."Default" || ',' END
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."Storage", '') != COALESCE(ec."Storage", '') AND COALESCE(c."Storage", '') != ''
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET STORAGE ' || COALESCE(c."Storage", '') || ','
                           ELSE '' END ||
                      CASE WHEN COALESCE(c."Compression", '') != COALESCE(ec."Compression", '') AND COALESCE(c."Compression", '') != '' AND "SchemaSmith"."ServerVersionNum"() >= 14
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET COMPRESSION ' || COALESCE(c."Compression", '') || ','
                           ELSE '' END ||
                      -- Identity kind, in place: a switch between ALWAYS and BY DEFAULT, or a plain column becoming identity
                      -- with the declared sequence options. Last, after the NOT NULL and the dropped default identity needs.
                      CASE WHEN COALESCE(c."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%'
                                AND REGEXP_REPLACE(c."Generated", '\s*\(.*$', '') != ec."Generated"
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' SET GENERATED ' || CASE WHEN UPPER(c."Generated") LIKE 'GENERATED ALWAYS%' THEN 'ALWAYS' ELSE 'BY DEFAULT' END || ','
                           WHEN COALESCE(c."Generated", 'NEVER') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", 'NEVER') = 'NEVER'
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || ' ADD ' || c."Generated" || ','
                           ELSE '' END ||
                      -- The declared sequence options, in place (only those declared, and only where they differ).
                      CASE WHEN (COALESCE(c."Generated", '') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", '') LIKE 'GENERATED%IDENTITY%'
                      AND "SchemaSmith"."IdentityOptionChanges"(c."Generated", pg_get_serial_sequence(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"), c."Name")::regclass) <> '')
                           THEN ' ALTER COLUMN ' || "SchemaSmith"."QuoteName"(c."Name") || "SchemaSmith"."IdentityOptionChanges"(c."Generated", pg_get_serial_sequence(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"), c."Name")::regclass) || ','
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
          OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", ''))
                  -- Declared identity sequence options that differ from the live sequence (only the ones declared).
                  OR (COALESCE(c."Generated", '') LIKE 'GENERATED%IDENTITY%' AND COALESCE(ec."Generated", '') LIKE 'GENERATED%IDENTITY%'
                      AND "SchemaSmith"."IdentityOptionChanges"(c."Generated", pg_get_serial_sequence(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"), c."Name")::regclass) <> ''))
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

    SELECT STRING_AGG('RAISE NOTICE ''  Creating ' || INITCAP(r."Kind") || ' ' || REPLACE(r."Name", '''', '''''') || ' again'';' || CHR(10) ||
                      r."CreateSql" || CHR(10) ||
                      'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''' || r."Kind" || ''', ''' ||
                      REPLACE(r."Name", '''', '''''') || ''', ''created'');', CHR(10) ORDER BY r."Kind", r."Name")
      INTO sql_script
      FROM temp_type_change_rebuilds r;
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
              OR (COALESCE(c."Compression", '') != '' AND COALESCE(c."Compression", '') != COALESCE(ec."Compression", ''))
                  -- Declared identity sequence options that differ from the live sequence (only the ones declared).
                  OR EXISTS (SELECT 1 FROM temp_identity_option_changes io
                         WHERE io."TableSchema" = c."TableSchema" AND io."TableName" = c."TableName" AND io."Name" = c."Name"));
    -- Without the ALTER pass's PG < 17 exclusion: a generated column whose expression changed there is altered by the
    -- drop-and-re-add pass above instead, and it is a column change all the same.

    -- Unsupported-feature policy: CHANGING an existing table's access method (ALTER TABLE ... SET ACCESS METHOD)
    -- requires PostgreSQL 15. The emit above is gated off under 15 (and the fixup WHERE ignores the AccessMethod
    -- difference there so it doesn't churn); 'fail' aborts, 'warn' (default) records a downgrade manifest per table
    -- whose live access method differs from the declared one. A new table already got its method from CREATE TABLE
    -- ... USING (PostgreSQL 12+), so it never reaches this. Same routing spine as per-column compression.
    IF "SchemaSmith"."ServerVersionNum"() < 15 THEN
      IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
         AND EXISTS (SELECT 1 FROM temp_tables t
                       JOIN pg_class c ON c.relname = t."Name"
                       JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = t."Schema"
                       JOIN pg_am am ON am.oid = c.relam
                      WHERE c.relkind IN ('r', 'p') AND COALESCE(t."AccessMethod", '') <> '' AND t."AccessMethod" <> am.amname) THEN
        RAISE EXCEPTION 'Table access method (SET ACCESS METHOD) requires PostgreSQL 15 (detected major %); table(s): %',
          "SchemaSmith"."ServerVersionNum"(),
          (SELECT STRING_AGG(t."Schema" || '.' || t."Name", ', ')
             FROM temp_tables t
             JOIN pg_class c ON c.relname = t."Name"
             JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = t."Schema"
             JOIN pg_am am ON am.oid = c.relam
            WHERE c.relkind IN ('r', 'p') AND COALESCE(t."AccessMethod", '') <> '' AND t."AccessMethod" <> am.amname);
      ELSE
        INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
          SELECT pg_backend_pid(), 'table access method (PG15)',
                 t."Schema" || '.' || t."Name", 'downgraded'
            FROM temp_tables t
            JOIN pg_class c ON c.relname = t."Name"
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = t."Schema"
            JOIN pg_am am ON am.oid = c.relam
           WHERE c.relkind IN ('r', 'p') AND COALESCE(t."AccessMethod", '') <> '' AND t."AccessMethod" <> am.amname;
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
    --   * An object on the database default records tablespace 0 whatever it was created with, so the default's
    --     own name (pg_default, or the database's TABLESPACE) is where it lives, not a move (#545).
    DECLARE
      v_move RECORD;
      v_default_tablespace TEXT := (SELECT ts.spcname FROM pg_database d JOIN pg_tablespace ts ON ts.oid = d.dattablespace
                                     WHERE d.datname = current_database());
    BEGIN
      FOR v_move IN
        SELECT t."Schema" AS sch, t."Name" AS obj, 'table' AS kind,
               t."Tablespace" AS declared, COALESCE(ts.spcname, v_default_tablespace) AS live
          FROM temp_tables t
          JOIN pg_class c ON c.relname = t."Name"
                         AND c.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = t."Schema")
                         AND c.relkind = 'r'
          LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace
         WHERE COALESCE(t."Tablespace", '') <> ''
           AND t."Tablespace" IS DISTINCT FROM COALESCE(ts.spcname, v_default_tablespace)
        UNION ALL
        SELECT ti."TableSchema", ti."Name", 'index',
               ti."Tablespace", COALESCE(ts.spcname, v_default_tablespace)
          FROM temp_indexes ti
          JOIN pg_class c ON c.relname = ti."Name"
                         AND c.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = ti."TableSchema")
                         AND c.relkind = 'i'
          LEFT JOIN pg_tablespace ts ON ts.oid = c.reltablespace
         WHERE COALESCE(ti."Tablespace", '') <> ''
           AND ti."Tablespace" IS DISTINCT FROM COALESCE(ts.spcname, v_default_tablespace)
      LOOP
        RAISE EXCEPTION '% %.% declares tablespace %, but is currently deployed on %. SchemaSmith does not move an existing object to a different tablespace (that is a rewrite) -- migrate it manually, or correct the declared tablespace to match.',
          v_move.kind, v_move.sch, v_move.obj, v_move.declared, v_move.live;
      END LOOP;
    END;

    RAISE NOTICE 'Fixup Table Attributes';
    SELECT STRING_AGG('RAISE NOTICE ''  Fixing up attributes for ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                      'ALTER TABLE ' || "SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name") || ' ' ||
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