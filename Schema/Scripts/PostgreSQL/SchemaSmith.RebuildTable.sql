-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Replaces one table with a table built to the DECLARED definition, carrying its rows across:
-- refuse-if-blocked, capture the sequence position, drop inbound foreign keys, create a shadow, copy,
-- restore the sequence, swap, drop the old one. Nothing in here decides WHEN a rebuild should happen --
-- the caller decides that and calls this; the procedure is also directly callable, which is what makes it
-- testable before any decision path exists.
--
-- Deliberately NOT this procedure's job: indexes, primary keys, unique/exclude/check constraints, foreign
-- keys, and the table-level attributes (fillfactor, access method, LOGGED/UNLOGGED, row-level security).
-- The old table is dropped whole, which takes all of them with it, and the ordinary quench passes that
-- follow re-add them from the same JSON that produced temp_columns -- ModifiedTableQuench's table-attribute
-- pass converges persistence/access-method/RLS/fillfactor, MissingIndexesAndConstraintsQuench re-adds the
-- indexes and constraints, ForeignKeyQuench re-adds the keys. Re-adding any of it here would duplicate that
-- logic against a second source of truth, so the surface stays small on purpose and the one thing this
-- procedure owns is the DATA.
--
-- Reads the declared definition from temp_columns / temp_tables, so it MUST be called after
-- ParseTableJsonIntoTempTables has run in the caller's session -- the same contract every quench procedure
-- already has. Called with no parse in scope it refuses rather than reading a stale or absent working set.
--
-- ATOMICITY. There is no BEGIN/COMMIT in here and there deliberately cannot be: a plpgsql procedure runs
-- INSIDE the caller's transaction, so it has no transaction of its own to open. What provides the atomicity
-- is (a) PostgreSQL's transactional DDL, and (b) the fact that this procedure never catches an exception.
-- Every statement below -- the inbound-FK drops, the shadow CREATE, the copy, the swap, the DROP -- runs in
-- one transaction, and any failure at any point aborts that transaction whole. A mid-failure therefore
-- leaves the ORIGINAL table untouched under its own name, with all of its rows, its inbound foreign keys
-- still in place, and no shadow and no _SchemaSmithOld left behind. That is why there is no EXCEPTION block
-- here: an EXCEPTION handler opens a subtransaction and lets execution continue past a half-done rebuild,
-- which is precisely the state this procedure must never produce. If a future caller wraps this in an
-- explicit transaction block that also does other work, a failure here takes that work down too -- correct,
-- but called out so nobody discovers it.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."RebuildTable"(p_Schema TEXT, p_Table TEXT, p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  v_SchemaRaw TEXT;
  v_TableRaw TEXT;
  v_Qualified TEXT;
  v_ObjectName TEXT;
  v_Oid OID;
  v_BlockedReason TEXT;
  v_ShadowRaw TEXT;
  v_OldRaw TEXT;
  v_ShadowQualified TEXT;
  v_OldQualified TEXT;
  v_ShadowColumnList TEXT;
  v_CopyColumnList TEXT;
  v_NeedsOverriding BOOLEAN;
  v_HasRows BOOLEAN;
  v_RowsBefore BIGINT;
  v_RowsAfter BIGINT;
  v_Col RECORD;
  v_SeqText TEXT;
  v_SeqName TEXT;
  v_SeqPosition BIGINT;
  v_LockSql TEXT;
  v_DropInboundFkSql TEXT;
  v_CreateShadowSql TEXT;
  v_CopySql TEXT;
  v_SeqRestoreSql TEXT;
  v_SeqRenameSql TEXT;
  v_SwapSql TEXT;
  v_DropOldSql TEXT;
BEGIN
  -- Identifiers are interpolated into dynamic DDL on the one code path in SchemaSmith that destroys data,
  -- so they go through quote_ident() rather than the hand-built '"' || x || '"' form the sibling quench
  -- scripts use. quote_ident is the engine's own escaper and cannot be broken out of by a name containing a
  -- double quote; the hand-built form silently can be, and here the consequence is DDL aimed at the wrong
  -- object. Same reasoning drives quote_literal() for every value embedded in a generated statement.
  v_SchemaRaw := TRIM(COALESCE(p_Schema, ''));
  v_TableRaw := TRIM(COALESCE(p_Table, ''));
  v_Qualified := quote_ident(v_SchemaRaw) || '.' || quote_ident(v_TableRaw);
  -- ChangeAudit's ObjectName shape on PostgreSQL is unquoted schema.name everywhere else, so match it --
  -- an operator filtering the manifest should not have to know which pass wrote the row.
  v_ObjectName := v_SchemaRaw || '.' || v_TableRaw;

  ----------------------------------------------------------------------------------------------------
  -- 1. REFUSE WHEN BLOCKED -- before any DDL, and in WhatIf too.
  --
  -- RebuildBlockedReason names the live state a shadow copy would silently destroy (a logical replication
  -- publication's article, an inheritance or partition edge, a partitioned parent that holds no rows of its
  -- own). A WhatIf preview that hid the refusal would tell the operator a rebuild is available on a table
  -- where it can never be, so the refusal fires in both modes.
  ----------------------------------------------------------------------------------------------------
  v_BlockedReason := "SchemaSmith"."RebuildBlockedReason"(v_SchemaRaw, v_TableRaw);
  IF v_BlockedReason IS NOT NULL THEN
    RAISE EXCEPTION 'Table rebuild refused for %: %. A rebuild replaces the table with a shadow copy, and that state lives outside the schema package -- the copy discards it and no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the blocking state first and re-run.',
      v_ObjectName, v_BlockedReason;
  END IF;

  ----------------------------------------------------------------------------------------------------
  -- 2. CONTRACT AND SAFETY REFUSALS -- all before any DDL, all in both modes.
  ----------------------------------------------------------------------------------------------------

  -- No parsed working set in the session. Reaching the copy without one would build a shadow from nothing.
  -- This check has to come first among the working-set reads: plpgsql plans a statement on its first
  -- execution, so a static reference to a temp table that does not exist would surface the engine's
  -- "relation does not exist" instead of an explanation.
  IF to_regclass('pg_temp.temp_tables') IS NULL OR to_regclass('pg_temp.temp_columns') IS NULL THEN
    RAISE EXCEPTION 'Table rebuild refused for %: SchemaSmith.RebuildTable was called with no parsed table definition in scope. It reads the declared column set from the temp_tables / temp_columns temporary tables that ParseTableJsonIntoTempTables populates, so it must be called from a session where that parse has already run.',
      v_ObjectName;
  END IF;

  -- relkind 'r' only: a partitioned parent ('p') is already refused above, and a view or foreign table is
  -- not something this procedure knows how to replace.
  SELECT c.oid
    INTO v_Oid
    FROM pg_catalog.pg_class c
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = v_SchemaRaw
      AND c.relname = v_TableRaw
      AND c.relkind = 'r';

  IF v_Oid IS NULL THEN
    RAISE EXCEPTION 'Table rebuild refused: % does not exist as an ordinary table. There is nothing to rebuild. If this table is mid-rename, the rename pass has to land before a rebuild can be considered.',
      v_ObjectName;
  END IF;

  IF NOT EXISTS (SELECT 1 FROM temp_tables t WHERE t."Schema" = v_SchemaRaw AND t."Name" = v_TableRaw) THEN
    RAISE EXCEPTION 'Table rebuild refused for %: the parsed working set carries no declaration for this table. Rebuilding to a definition that is not in the package would replace the table with an empty one.',
      v_ObjectName;
  END IF;

  -- An UNAPPLIED TABLE RENAME. The package renames OldName -> Name; if BOTH names resolve to live tables the
  -- rename has not happened (or has been re-declared), and rebuilding the destination would act on the wrong
  -- table while the source still holds rows. Refuse rather than pick one.
  IF EXISTS (SELECT 1
               FROM temp_tables t
               WHERE t."Schema" = v_SchemaRaw
                 AND t."Name" = v_TableRaw
                 AND COALESCE(t."OldName", '') <> ''
                 AND EXISTS (SELECT 1
                               FROM pg_catalog.pg_class oc
                               JOIN pg_catalog.pg_namespace onsp ON onsp.oid = oc.relnamespace
                               WHERE onsp.nspname = v_SchemaRaw
                                 AND oc.relname = t."OldName"
                                 AND oc.relkind IN ('r', 'p'))) THEN
    RAISE EXCEPTION 'Table rebuild refused for %: the package declares an OldName that still resolves to a live table, so a table rename is pending. Let the rename land first -- rebuilding now would copy from the wrong table.',
      v_ObjectName;
  END IF;

  -- An UNAPPLIED COLUMN RENAME. The copy matches columns BY CURRENT NAME. A column declared under its new
  -- name whose data still lives under OldName would match nothing, and the rebuild would drop that column's
  -- data with no error at all. This is the quietest data-loss shape in the whole procedure, so it is refused
  -- outright rather than guessed at.
  IF EXISTS (SELECT 1
               FROM temp_columns c
               WHERE c."TableSchema" = v_SchemaRaw
                 AND c."TableName" = v_TableRaw
                 AND COALESCE(c."OldName", '') <> ''
                 AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute oa
                               WHERE oa.attrelid = v_Oid AND oa.attname = c."OldName"
                                 AND oa.attnum > 0 AND NOT oa.attisdropped)
                 AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute na
                                   WHERE na.attrelid = v_Oid AND na.attname = c."Name"
                                     AND na.attnum > 0 AND NOT na.attisdropped)) THEN
    RAISE EXCEPTION 'Table rebuild refused for %: a declared column carries an OldName that still exists on the live table under that old name, so a column rename is pending. The copy matches columns by their current name and would silently discard that column''s data. Let the rename land first.',
      v_ObjectName;
  END IF;

  ----------------------------------------------------------------------------------------------------
  -- 3. NAMES FOR THE SHADOW AND THE RENAMED-OUT ORIGINAL.
  --
  -- PostgreSQL caps a relation name at 63 BYTES and TRUNCATES silently past it, so a name built from a long
  -- table name would not be the name the engine actually stored -- and every collision check and rename
  -- below would then be aimed at a name that does not exist. Truncate the base first, then refuse outright
  -- if the result still does not fit (a multi-byte name can exceed 63 bytes at 44 characters). Both working
  -- names are refused if already taken: a leftover from a previous run is an operator decision, not
  -- something to overwrite.
  ----------------------------------------------------------------------------------------------------
  v_ShadowRaw := LEFT(v_TableRaw, 44) || '_SchemaSmithRebuild';
  v_OldRaw := LEFT(v_TableRaw, 44) || '_SchemaSmithOld';

  IF OCTET_LENGTH(v_ShadowRaw) > 63 OR OCTET_LENGTH(v_OldRaw) > 63 THEN
    RAISE EXCEPTION 'Table rebuild refused for %: the working names this rebuild needs (% / %) do not fit PostgreSQL''s 63-byte relation-name limit, and a silently truncated name would make the swap and the collision checks target the wrong object.',
      v_ObjectName, v_ShadowRaw, v_OldRaw;
  END IF;

  v_ShadowQualified := quote_ident(v_SchemaRaw) || '.' || quote_ident(v_ShadowRaw);
  v_OldQualified := quote_ident(v_SchemaRaw) || '.' || quote_ident(v_OldRaw);

  IF to_regclass(v_ShadowQualified) IS NOT NULL OR to_regclass(v_OldQualified) IS NOT NULL THEN
    RAISE EXCEPTION 'Table rebuild refused for %: the working names % / % are already in use. That is normally a leftover from an interrupted rebuild -- inspect it and drop it deliberately rather than having this run overwrite it.',
      v_ObjectName, v_ShadowQualified, v_OldQualified;
  END IF;

  ----------------------------------------------------------------------------------------------------
  -- 4. COLUMN LISTS.
  --
  -- The shadow's CREATE takes the WHOLE declared column set, ordered by "_RowId" -- the order the columns
  -- appear in the package file. Generated columns are included: PostgreSQL allows a GENERATED ALWAYS AS
  -- (...) expression to reference a column DECLARED LATER in the same CREATE TABLE, so the declared order
  -- can be honoured literally with no follow-up ALTER.
  --
  -- The COPY moves only the INTERSECTION of declared and live, which is what makes the three cases fall out
  -- without special-casing: a column declared but not live is new (it takes its DEFAULT or NULL and must not
  -- appear in the SELECT), a column live but not declared is being removed (it appears in neither list), and
  -- a column on both sides carries its data.
  --
  -- Columns generated ON THE DECLARED SIDE are excluded from the copy, because the SHADOW derives them and
  -- INSERT cannot target a generated column at all. The exclusion is deliberately declared-side only: the
  -- live table is only ever READ from, and a live generated column the package now declares plain is
  -- perfectly selectable -- carrying its computed values across is the right answer, and excluding it on a
  -- live-side test would lose them silently.
  --
  -- A newly declared NOT NULL column with no DEFAULT on a non-empty table is NOT special-cased: the copy
  -- fails on the null violation, the transaction aborts, and the original table is untouched. Failing loudly
  -- beats inventing a value.
  --
  -- The insert list and the select list are ONE string by construction: same columns, same order, so they
  -- cannot drift apart into a positional mismatch that would write data into the wrong column.
  ----------------------------------------------------------------------------------------------------
  SELECT STRING_AGG(quote_ident(c."Name") || ' ' || c."DataType" ||
           CASE WHEN COALESCE(c."Collation", '') <> '' THEN ' COLLATE ' || quote_ident(c."Collation") ELSE '' END ||
           CASE WHEN UPPER(COALESCE(c."Generated", 'NEVER')) LIKE 'GENERATED%IDENTITY%' THEN ' ' || c."Generated" ELSE '' END ||
           CASE WHEN COALESCE(c."Generated", 'NEVER') = 'ALWAYS' AND COALESCE(c."GenerationExpression", '') <> ''
                THEN ' GENERATED ALWAYS AS (' || c."GenerationExpression" || ') ' || CASE WHEN c."Virtual" THEN 'VIRTUAL' ELSE 'STORED' END
                ELSE '' END ||
           CASE WHEN c."Nullable" THEN '' ELSE ' NOT NULL' END ||
           CASE WHEN UPPER(COALESCE(c."Generated", 'NEVER')) NOT LIKE 'GENERATED%IDENTITY%'
                 AND NOT (COALESCE(c."Generated", 'NEVER') = 'ALWAYS' AND COALESCE(c."GenerationExpression", '') <> '')
                 AND COALESCE(c."Default", '') <> ''
                THEN ' DEFAULT ' || c."Default" ELSE '' END,
         ', ' ORDER BY c."_RowId")
    INTO v_ShadowColumnList
    FROM temp_columns c
    WHERE c."TableSchema" = v_SchemaRaw
      AND c."TableName" = v_TableRaw
      -- VIRTUAL generated columns need PostgreSQL 18. Below it they cannot exist live either, and
      -- MissingIndexesAndConstraintsQuench already routes the declared-but-unsupported column through the
      -- unsupported-feature policy and records the downgrade. Skipping it here keeps the shadow buildable
      -- and leaves that ONE policy/manifest decision with the pass that owns it, rather than emitting a
      -- second downgrade row for the same column from a second place.
      AND NOT (c."Virtual" AND COALESCE(c."Generated", 'NEVER') = 'ALWAYS'
               AND COALESCE(c."GenerationExpression", '') <> ''
               AND "SchemaSmith"."ServerVersionNum"() < 18);

  IF COALESCE(v_ShadowColumnList, '') = '' THEN
    RAISE EXCEPTION 'Table rebuild refused for %: the declared definition produced no columns to build the replacement from.',
      v_ObjectName;
  END IF;

  SELECT STRING_AGG(quote_ident(c."Name"), ', ' ORDER BY c."_RowId")
    INTO v_CopyColumnList
    FROM temp_columns c
    WHERE c."TableSchema" = v_SchemaRaw
      AND c."TableName" = v_TableRaw
      AND NOT (COALESCE(c."Generated", 'NEVER') = 'ALWAYS' AND COALESCE(c."GenerationExpression", '') <> '')
      AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                    WHERE a.attrelid = v_Oid AND a.attname = c."Name"
                      AND a.attnum > 0 AND NOT a.attisdropped);

  -- Nothing to copy AND rows to lose. Every live column is being removed, so the rows would survive only as
  -- empty shells -- and manufacturing those is a guess about intent, not a data-preserving rebuild.
  IF v_CopyColumnList IS NULL THEN
    EXECUTE 'SELECT EXISTS (SELECT 1 FROM ' || v_Qualified || ')' INTO v_HasRows;
    IF v_HasRows THEN
      RAISE EXCEPTION 'Table rebuild refused for %: no declared column also exists on the live table, so there is nothing to copy, and the table is not empty. Rebuilding would destroy every row. Use Before/After migration scripts if the rows are meant to survive a full column replacement.',
        v_ObjectName;
    END IF;
  END IF;

  -- A GENERATED ALWAYS AS IDENTITY column rejects an explicit value unless the INSERT says OVERRIDING SYSTEM
  -- VALUE. Without it the copy fails outright on exactly the tables whose identifiers matter most. Gated on
  -- the column also being live, because a brand-new identity column is not in the copy list at all.
  SELECT EXISTS (SELECT 1
                   FROM temp_columns c
                   WHERE c."TableSchema" = v_SchemaRaw
                     AND c."TableName" = v_TableRaw
                     AND UPPER(COALESCE(c."Generated", '')) LIKE 'GENERATED ALWAYS AS IDENTITY%'
                     AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute a
                                   WHERE a.attrelid = v_Oid AND a.attname = c."Name"
                                     AND a.attnum > 0 AND NOT a.attisdropped))
    INTO v_NeedsOverriding;

  ----------------------------------------------------------------------------------------------------
  -- 5. SEQUENCES -- capture the POSITION and the NAME before anything is created or copied.
  --
  -- POSITION. pg_sequences.last_value is the last value the sequence WROTE, which is not the same as the
  -- largest value the table still holds. With ids 1-3 and id 3 deleted, last_value is 3 while max(id) is 2;
  -- restoring to the copied max makes the next insert re-issue 3, a value already given to a row that
  -- existed, and anything that recorded the old 3 then aliases two different entities. PostgreSQL is
  -- unforgiving about it in a useful way -- the shadow's sequence is NOT advanced by an explicit-value copy,
  -- so the naive path fails immediately with a duplicate key on id 1 rather than quietly re-issuing later --
  -- but it is wrong either way. So: capture first, setval to the capture, never to max(). last_value is NULL
  -- until the sequence has issued something, which distinguishes "never inserted" from "seeded at 1";
  -- forcing a never-used sequence would burn its start value for no reason, so that case is skipped.
  --
  -- NAME -- the PostgreSQL-only trap. An identity/serial column owns a NAMED sequence, and the sequence does
  -- NOT follow a table rename. Rebuild t through t_SchemaSmithRebuild and the surviving table ends up using
  -- t_SchemaSmithRebuild_id_seq. The data is correct, so a single functional test passes; the defect only
  -- shows up in a pg_dump or a puzzled \d, and it compounds across repeated rebuilds. The fix is in the
  -- DROP/RENAME script below and its ORDERING IS LOAD-BEARING: the old table still owns the natural sequence
  -- name until it is dropped, so the DROP must come first or the rename collides.
  --
  -- The names are derived with pg_get_serial_sequence rather than string-built, and resolved at RUN time
  -- inside the emitted statement, so the shadow's ACTUAL sequence is renamed even where PostgreSQL had to
  -- truncate or disambiguate the name it generated. pg_get_serial_sequence is called one column at a time in
  -- this loop rather than joined into the query: it ERRORS on a column that does not exist, and a planner
  -- free to evaluate it before the live-column join would hit exactly that.
  --
  -- KNOWN GAP, deliberate: a declared identity column that is NOT on the live table has no captured natural
  -- name to restore -- PostgreSQL names the shadow's new sequence after the shadow -- so its name is left
  -- alone rather than renamed to a string-built guess on a path that destroys data. It does not compound:
  -- the next rebuild captures that name as the natural one and restores it.
  ----------------------------------------------------------------------------------------------------
  FOR v_Col IN
    SELECT c."Name" AS "ColumnName"
      FROM temp_columns c
      JOIN pg_catalog.pg_attribute a ON a.attrelid = v_Oid AND a.attname = c."Name"
                                    AND a.attnum > 0 AND NOT a.attisdropped
      WHERE c."TableSchema" = v_SchemaRaw
        AND c."TableName" = v_TableRaw
        -- Only a column the SHADOW will build with a sequence of its own is interesting here. A live
        -- sequence whose column the package no longer declares as identity/serial simply dies with the old
        -- table, and there is nothing on the new one to restore it to.
        AND (UPPER(COALESCE(c."Generated", 'NEVER')) LIKE 'GENERATED%IDENTITY%'
             OR UPPER(TRIM(COALESCE(c."DataType", ''))) LIKE 'SERIAL%'
             OR UPPER(TRIM(COALESCE(c."DataType", ''))) IN ('BIGSERIAL', 'SMALLSERIAL'))
      ORDER BY c."_RowId"
  LOOP
    v_SeqText := pg_get_serial_sequence(v_Qualified, v_Col."ColumnName");

    IF v_SeqText IS NULL THEN
      -- The live column carries no sequence but the package now declares one. There is no counter to carry
      -- and no name to put back -- but the copied rows already hold values, and PostgreSQL does NOT advance
      -- a sequence for an explicit-value insert, so the shadow's brand-new sequence would still be sitting
      -- at its start value and the very next insert would collide with a copied row. (SQL Server hides this
      -- case: IDENTITY_INSERT advances the counter to the highest value inserted, so 3a has nothing to do
      -- here.) Seed past the data instead. max() is the right authority in THIS case precisely because no
      -- counter ever existed to be more authoritative than it -- which is not true anywhere else.
      EXECUTE 'SELECT MAX(' || quote_ident(v_Col."ColumnName") || ') FROM ' || v_Qualified INTO v_SeqPosition;
      IF v_SeqPosition IS NOT NULL THEN
        v_SeqRestoreSql := COALESCE(v_SeqRestoreSql || CHR(10), '') ||
          'RAISE NOTICE ' || quote_literal('  Seeding new sequence past the copied data at ' || v_SeqPosition::TEXT || ' for ' || v_ObjectName || '.' || v_Col."ColumnName") || ';' || CHR(10) ||
          'PERFORM setval(pg_get_serial_sequence(' || quote_literal(v_ShadowQualified) || ', ' ||
            quote_literal(v_Col."ColumnName") || '), ' || v_SeqPosition::TEXT || ', TRUE);';
      END IF;
      CONTINUE;
    END IF;

    SELECT s.relname, sq.last_value
      INTO v_SeqName, v_SeqPosition
      FROM pg_catalog.pg_class s
      JOIN pg_catalog.pg_namespace sn ON sn.oid = s.relnamespace
      LEFT JOIN pg_catalog.pg_sequences sq ON sq.schemaname = sn.nspname AND sq.sequencename = s.relname
      WHERE s.oid = v_SeqText::REGCLASS;

    IF v_SeqPosition IS NOT NULL THEN
      v_SeqRestoreSql := COALESCE(v_SeqRestoreSql || CHR(10), '') ||
        'RAISE NOTICE ' || quote_literal('  Restoring sequence position ' || v_SeqPosition::TEXT || ' for ' || v_ObjectName || '.' || v_Col."ColumnName") || ';' || CHR(10) ||
        'PERFORM setval(pg_get_serial_sequence(' || quote_literal(v_ShadowQualified) || ', ' ||
          quote_literal(v_Col."ColumnName") || '), ' || v_SeqPosition::TEXT || ', TRUE);';
    END IF;

    -- Resolved against v_Qualified, which by the time this statement runs names the SWAPPED-IN shadow.
    -- EXECUTE rather than a literal ALTER SEQUENCE because the shadow's sequence name is not knowable until
    -- it exists -- and a WhatIf preview has to be able to show this statement without creating anything.
    v_SeqRenameSql := COALESCE(v_SeqRenameSql || CHR(10), '') ||
      'RAISE NOTICE ' || quote_literal('  Restoring sequence name ' || v_SeqName) || ';' || CHR(10) ||
      'EXECUTE ' || quote_literal('ALTER SEQUENCE ') || ' || pg_get_serial_sequence(' ||
        quote_literal(v_Qualified) || ', ' || quote_literal(v_Col."ColumnName") || ') || ' ||
        quote_literal(' RENAME TO ' || quote_ident(v_SeqName)) || ';';
  END LOOP;

  ----------------------------------------------------------------------------------------------------
  -- 6. BUILD EVERY STATEMENT UP FRONT.
  --
  -- Built before anything executes so WhatIf can print exactly what a real run would do, from exactly the
  -- same source -- a preview assembled by a second code path is a preview of something else. Each script is
  -- valid plpgsql because ExecuteOrDebug runs it inside a DO block (hence PERFORM, not SELECT).
  ----------------------------------------------------------------------------------------------------

  -- ACCESS EXCLUSIVE, held for the life of the transaction: without it a row inserted by another session
  -- AFTER the copy scan and BEFORE the swap is copied nowhere and then dropped with the old table -- a
  -- silent loss with no error on either side.
  v_LockSql := 'LOCK TABLE ' || v_Qualified || ' IN ACCESS EXCLUSIVE MODE;';

  -- Inbound foreign keys: OTHER tables pointing AT this one (a self-reference included -- it is declared in
  -- this table's own JSON and comes back with the rest). These must go before the swap, and the reason is
  -- NOT that they block the DROP.
  --
  -- An inbound foreign key FOLLOWS a table rename on PostgreSQL, so after a swap the child would be
  -- constrained against the table that was moved aside instead of the one that replaced it. The DROP failing
  -- afterwards is merely what makes that visible.
  --
  -- They are NOT re-added here. Each one is defined in its OWNING table's JSON, so that table's foreign-key
  -- quench pass sees it missing and re-creates it from the package. Re-adding them inside this procedure
  -- would mean maintaining FK construction against a second source of truth.
  SELECT STRING_AGG('RAISE NOTICE ' || quote_literal('  Dropping inbound foreign key ' || pn.nspname || '.' || pc.relname || '.' || con.conname) || ';' || CHR(10) ||
                    'ALTER TABLE ' || quote_ident(pn.nspname) || '.' || quote_ident(pc.relname) ||
                      ' DROP CONSTRAINT ' || quote_ident(con.conname) || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''foreignKey'', ' ||
                      quote_literal(pn.nspname || '.' || pc.relname || '.' || con.conname) || ', ''dropped'');', CHR(10))
    INTO v_DropInboundFkSql
    FROM pg_catalog.pg_constraint con
    JOIN pg_catalog.pg_class pc ON pc.oid = con.conrelid
    JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace
    WHERE con.contype = 'f'
      AND con.confrelid = v_Oid;

  v_CreateShadowSql := 'CREATE TABLE ' || v_ShadowQualified || ' (' || v_ShadowColumnList || ');';

  v_CopySql := CASE WHEN v_CopyColumnList IS NULL THEN NULL
                    ELSE 'INSERT INTO ' || v_ShadowQualified || ' (' || v_CopyColumnList || ')' || CHR(10) ||
                         CASE WHEN v_NeedsOverriding THEN '  OVERRIDING SYSTEM VALUE' || CHR(10) ELSE '' END ||
                         '  SELECT ' || v_CopyColumnList || ' FROM ' || v_Qualified || ';'
               END;

  v_SwapSql := 'ALTER TABLE ' || v_Qualified || ' RENAME TO ' || quote_ident(v_OldRaw) || ';' || CHR(10) ||
               'ALTER TABLE ' || v_ShadowQualified || ' RENAME TO ' || quote_ident(v_TableRaw) || ';';

  -- The DROP comes FIRST and the sequence renames follow it -- see section 5 for why that order cannot be
  -- flipped. They are ONE script so nothing can ever run them apart. No CASCADE: a view or other dependent
  -- that would block this DROP is the user's object, and silently destroying it is not this procedure's
  -- call. The DROP fails, the transaction aborts, and the original table is still standing under its own
  -- name with its rows and its inbound keys.
  v_DropOldSql := 'DROP TABLE ' || v_OldQualified || ';' || COALESCE(CHR(10) || v_SeqRenameSql, '');

  ----------------------------------------------------------------------------------------------------
  -- 7. WHATIF -- print, execute nothing.
  ----------------------------------------------------------------------------------------------------
  IF p_WhatIf THEN
    RAISE NOTICE '  Would rebuild table %', v_ObjectName;
    CALL "SchemaSmith"."ExecuteOrDebug"(v_LockSql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_DropInboundFkSql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_CreateShadowSql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_CopySql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_SeqRestoreSql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_SwapSql, TRUE);
    CALL "SchemaSmith"."ExecuteOrDebug"(v_DropOldSql, TRUE);

    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      VALUES (pg_backend_pid(), 'table', v_ObjectName, 'wouldRebuild');

    -- WhatIf twin of the 'foreignKey'/'dropped' rows embedded in the drop batch above (that batch is
    -- printed, not executed, under WhatIf). Same source and same ObjectName shape as ModifiedTableQuench, so
    -- a preview's manifest lists the inbound keys a real run would take out.
    INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
      SELECT pg_backend_pid(), 'foreignKey', pn.nspname || '.' || pc.relname || '.' || con.conname, 'wouldDrop'
        FROM pg_catalog.pg_constraint con
        JOIN pg_catalog.pg_class pc ON pc.oid = con.conrelid
        JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace
        WHERE con.contype = 'f'
          AND con.confrelid = v_Oid;
    RETURN;
  END IF;

  ----------------------------------------------------------------------------------------------------
  -- 8. THE DESTRUCTIVE SEQUENCE -- see the ATOMICITY note at the top of this file for what a failure at any
  -- point below leaves behind. Nothing here is wrapped in an EXCEPTION block, on purpose.
  ----------------------------------------------------------------------------------------------------
  RAISE NOTICE '  Rebuilding table %', v_ObjectName;

  CALL "SchemaSmith"."ExecuteOrDebug"(v_LockSql, FALSE);

  -- Counted under the lock taken above, so the before/after comparison below is a real invariant and not a
  -- race. This is the one operation in SchemaSmith that destroys user data, so it pays for a verification
  -- scan rather than trusting that INSERT ... SELECT moved everything.
  EXECUTE 'SELECT COUNT(*) FROM ' || v_Qualified INTO v_RowsBefore;

  CALL "SchemaSmith"."ExecuteOrDebug"(v_DropInboundFkSql, FALSE);
  CALL "SchemaSmith"."ExecuteOrDebug"(v_CreateShadowSql, FALSE);

  IF v_CopySql IS NOT NULL THEN
    CALL "SchemaSmith"."ExecuteOrDebug"(v_CopySql, FALSE);

    EXECUTE 'SELECT COUNT(*) FROM ' || v_ShadowQualified INTO v_RowsAfter;

    IF COALESCE(v_RowsAfter, -1) <> v_RowsBefore THEN
      RAISE EXCEPTION 'Table rebuild aborted for %: the replacement holds % rows but the original holds %. Nothing has been changed -- the whole rebuild is rolled back.',
        v_ObjectName, COALESCE(v_RowsAfter, -1), v_RowsBefore;
    END IF;
  END IF;

  CALL "SchemaSmith"."ExecuteOrDebug"(v_SeqRestoreSql, FALSE);
  CALL "SchemaSmith"."ExecuteOrDebug"(v_SwapSql, FALSE);
  CALL "SchemaSmith"."ExecuteOrDebug"(v_DropOldSql, FALSE);

  INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
    VALUES (pg_backend_pid(), 'table', v_ObjectName, 'rebuilt');
END $$;
