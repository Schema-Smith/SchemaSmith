-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Lightweight bootstrap procedure with ZERO SchemaSmith_* table or proc dependencies.
-- Parses a TableQuench-shaped JSON definition and applies, in order:
--   1. TABLE rename when OldName is set (old table present, new absent) -- BEFORE CREATE TABLE so a
--      renamed table's history is not orphaned under an empty freshly-created new table
--   2. CREATE TABLE IF NOT EXISTS (built from Columns + any PrimaryKey index)
--   3. COLUMN rename when a column's OldName is set (old column present, new absent) -- BEFORE
--      ADD COLUMN so a renamed column's data is not left behind under an empty new column
--   4. ALTER TABLE ADD COLUMN IF NOT EXISTS per missing column
--   5. CREATE INDEX IF NOT EXISTS per missing non-PK index (incl. NullsNotDistinct, version-adaptive)
-- Both a rename's old AND new name already present (table or column) is a hard failure, not a
-- silent skip -- it means an object exists that the model does not expect.
-- Out of scope: column type changes (beyond a same-shape rename), drops, FKs, check constraints,
-- ownership tracking.
-- Idempotent: a second call on the same definition is a no-op.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."BootstrapTableQuench"
  (p_TableDefinitions TEXT)
  LANGUAGE plpgsql
AS $$
DECLARE
    v_def JSONB := p_TableDefinitions::jsonb;
    v_pg15 BOOLEAN := (current_setting('server_version_num')::int / 10000) >= 15;
    v_schema TEXT;
    v_name TEXT;
    v_column_list TEXT := '';
    v_pk_clause TEXT := '';
    v_sql TEXT;
    v_col JSONB;
    v_idx JSONB;
    v_col_name TEXT;
    v_col_type TEXT;
    v_col_nullable BOOLEAN;
    v_col_default TEXT;
    v_idx_name TEXT;
    v_idx_cols TEXT;
    v_old_name TEXT;
    v_col_old_name TEXT;
    v_oid OID;
    v_actual_unique BOOLEAN;
    v_actual_nnd BOOLEAN;
    v_actual_sig TEXT;
    v_expected_sig TEXT;
    v_expected_nnd BOOLEAN;
    v_conname TEXT;
    v_actual_extra BOOLEAN;
    v_pk_name TEXT;
    v_has_dupes BOOLEAN;
    v_group_cols TEXT;
BEGIN
    v_schema := TRIM(BOTH FROM (v_def->>'Schema'));
    v_name := TRIM(BOTH FROM (v_def->>'Name'));

    IF v_schema IS NULL OR v_schema = '' OR v_name IS NULL OR v_name = '' THEN
        RAISE EXCEPTION 'BootstrapTableQuench: JSON must contain non-blank Schema and Name.';
    END IF;

    -- Step 1: TABLE-level declarative rename (OldName), run BEFORE CREATE TABLE IF NOT EXISTS below --
    -- a freshly-created empty table under the new name would otherwise orphan the old table and every
    -- row of its history. #375's blank/whitespace-OldName trap (SchemaSmith_ParseTableJson.sql) applies
    -- here too: NULLIF(TRIM(...), '') normalizes a blank OldName to "no rename", not a bogus empty name.
    v_old_name := NULLIF(TRIM(v_def->>'OldName'), '');
    IF v_old_name IS NOT NULL THEN
        IF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = v_schema AND table_name = v_old_name)
           AND EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = v_schema AND table_name = v_name) THEN
            RAISE EXCEPTION 'BootstrapTableQuench: both "%"."%" (OldName) and "%"."%" already exist; resolve manually before bootstrap can rename.',
                v_schema, v_old_name, v_schema, v_name;
        ELSIF EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = v_schema AND table_name = v_old_name)
          AND NOT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = v_schema AND table_name = v_name) THEN
            EXECUTE 'ALTER TABLE "' || v_schema || '"."' || v_old_name || '" RENAME TO "' || v_name || '"';
        END IF;
    END IF;

    -- Step 2: CREATE TABLE IF NOT EXISTS, built from Columns + optional inline PK.
    -- We always emit CREATE TABLE IF NOT EXISTS — it is a no-op against existing tables.
    FOR v_col IN SELECT * FROM jsonb_array_elements(v_def->'Columns')
    LOOP
        v_col_name := v_col->>'Name';
        v_col_type := v_col->>'DataType';
        v_col_nullable := COALESCE((v_col->>'Nullable')::boolean, false);
        v_col_default := v_col->>'Default';

        IF v_column_list <> '' THEN
            v_column_list := v_column_list || ', ';
        END IF;
        v_column_list := v_column_list || '"' || v_col_name || '" ' || v_col_type ||
                         CASE WHEN v_col_nullable THEN ' NULL' ELSE ' NOT NULL' END ||
                         CASE WHEN COALESCE(TRIM(v_col_default), '') <> '' THEN ' DEFAULT ' || v_col_default ELSE '' END;
    END LOOP;

    -- First PrimaryKey index goes inline at CREATE TABLE time as a PRIMARY KEY constraint.
    SELECT idx INTO v_idx
      FROM jsonb_array_elements(v_def->'Indexes') idx
      WHERE COALESCE((idx->>'PrimaryKey')::boolean, false) = true
      LIMIT 1;

    IF v_idx IS NOT NULL THEN
        v_idx_name := v_idx->>'Name';
        v_idx_cols := v_idx->>'IndexColumns';
        v_pk_clause := ', CONSTRAINT "' || v_idx_name || '" PRIMARY KEY (' || v_idx_cols || ')';
    END IF;

    v_sql := 'CREATE TABLE IF NOT EXISTS "' || v_schema || '"."' || v_name || '" (' || v_column_list || v_pk_clause || ')';
    EXECUTE v_sql;

    -- Step 3: COLUMN-level declarative rename (OldName), run BEFORE Step 4's add-missing-columns so
    -- a renamed column's data is not left behind under an empty freshly-added new column. Works
    -- across the whole PostgreSQL support range -- no version gate needed (unlike MySQL/MariaDB).
    FOR v_col IN SELECT * FROM jsonb_array_elements(v_def->'Columns')
    LOOP
        v_col_name := v_col->>'Name';
        v_col_old_name := NULLIF(TRIM(v_col->>'OldName'), '');
        IF v_col_old_name IS NOT NULL THEN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = v_schema AND table_name = v_name AND column_name = v_col_old_name)
               AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = v_schema AND table_name = v_name AND column_name = v_col_name) THEN
                RAISE EXCEPTION 'BootstrapTableQuench: both "%"."%"."%" (OldName) and "%" already exist; resolve manually before bootstrap can rename.',
                    v_schema, v_name, v_col_old_name, v_col_name;
            ELSIF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = v_schema AND table_name = v_name AND column_name = v_col_old_name)
              AND NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = v_schema AND table_name = v_name AND column_name = v_col_name) THEN
                EXECUTE 'ALTER TABLE "' || v_schema || '"."' || v_name || '" RENAME COLUMN "' || v_col_old_name || '" TO "' || v_col_name || '"';
            END IF;
        END IF;
    END LOOP;

    -- Step 4: ADD each genuinely-missing column, folded into one multi-clause ALTER, declared order
    -- preserved. Missing-ness is checked explicitly against information_schema rather than relying on
    -- ADD COLUMN IF NOT EXISTS: on PostgreSQL 12, ADD COLUMN IF NOT EXISTS "<col>" ... GENERATED AS IDENTITY
    -- leaks an orphan owned sequence even when it skips an already-present column, leaving the column
    -- owning two sequences and failing every later INSERT with "more than one owned sequence found". Since
    -- Step 2 already created every column inline, on a fresh table this ADD set is empty (no leak); on an
    -- existing table it adds only the columns that are truly absent.
    SELECT string_agg(
             'ADD COLUMN "' || (col->>'Name') || '" ' || (col->>'DataType') ||
             CASE WHEN COALESCE((col->>'Nullable')::boolean, false) THEN ' NULL' ELSE ' NOT NULL' END ||
             CASE WHEN COALESCE(TRIM(col->>'Default'), '') <> '' THEN ' DEFAULT ' || (col->>'Default') ELSE '' END,
             ', ' ORDER BY ord)
      INTO v_sql
      FROM jsonb_array_elements(v_def->'Columns') WITH ORDINALITY AS t(col, ord)
      WHERE NOT EXISTS (SELECT 1 FROM information_schema.columns c
                          WHERE c.table_schema = v_schema AND c.table_name = v_name
                            AND c.column_name = (col->>'Name'));
    IF v_sql IS NOT NULL THEN
        EXECUTE 'ALTER TABLE "' || v_schema || '"."' || v_name || '" ' || v_sql;
    END IF;

    -- Step 4.4: a declared PRIMARY KEY whose deployed shape differs is swapped IN PLACE -- dropped and re-added
    -- in one ALTER TABLE, which PostgreSQL applies atomically and which never touches the table's rows. Leaving
    -- the PK alone was a real gap: a PK is exactly the kind of key whose drift matters, and "rebuild the table"
    -- is not an acceptable answer for SchemaSmith's own tables.
    FOR v_idx IN SELECT value FROM jsonb_array_elements(v_def->'Indexes')
                  WHERE COALESCE((value->>'PrimaryKey')::boolean, false) = true
    LOOP
        v_idx_name := v_idx->>'Name';

        SELECT con.conname,
               (SELECT string_agg(a.attname, ',' ORDER BY k.ord)
                  FROM unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord)
                  JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum)
          INTO v_pk_name, v_actual_sig
          FROM pg_constraint con
         WHERE con.conrelid = to_regclass('"' || v_schema || '"."' || v_name || '"')
           AND con.contype = 'p';
        CONTINUE WHEN v_pk_name IS NULL;   -- no PK deployed: Step 2's CREATE TABLE owns that case

        SELECT string_agg(REPLACE(btrim(k.col), '"', ''), ',' ORDER BY k.ord)
          INTO v_expected_sig
          FROM unnest(string_to_array(v_idx->>'IndexColumns', ',')) WITH ORDINALITY AS k(col, ord);

        IF v_pk_name IS DISTINCT FROM v_idx_name OR v_actual_sig IS DISTINCT FROM v_expected_sig THEN
            -- Refuse rather than half-apply when the data cannot satisfy the new key: the ALTER would fail on
            -- its own, but its message names a system-generated index, not the declaration that asked for it.
            -- The DECLARED column list, which carries its own quoting: an unquoted name would be folded to
            -- lower case here and the pre-check would fail with "column does not exist" on a correct table.
            -- The DECLARED column list, which carries its own quoting -- an unquoted name would be folded
            -- to lower case and the pre-check would fail with "column does not exist" on a correct table -- but
            -- with any sort direction removed, which GROUP BY does not accept.
            SELECT string_agg(CASE WHEN lower(right(btrim(x), 5)) = ' desc'
                                        THEN btrim(left(btrim(x), length(btrim(x)) - 5))
                                   WHEN lower(right(btrim(x), 4)) = ' asc'
                                        THEN btrim(left(btrim(x), length(btrim(x)) - 4))
                                   ELSE btrim(x) END, ',')
              INTO v_group_cols
              FROM unnest(string_to_array(v_idx->>'IndexColumns', ',')) AS x;
            EXECUTE 'SELECT EXISTS (SELECT 1 FROM "' || v_schema || '"."' || v_name || '" GROUP BY ' ||
                    v_group_cols || ' HAVING COUNT(*) > 1)' INTO v_has_dupes;
            IF v_has_dupes THEN
                RAISE EXCEPTION 'SchemaSmith bootstrap: cannot rebuild PRIMARY KEY %.% as (%) -- the table holds duplicate rows for that key. The existing key is unchanged; resolve the duplicates and re-run.',
                    v_schema, v_name, v_expected_sig;
            END IF;
            RAISE NOTICE '  Rebuilding PRIMARY KEY %.%: the deployed key does not match its declaration', v_schema, v_name;
            EXECUTE 'ALTER TABLE "' || v_schema || '"."' || v_name || '" DROP CONSTRAINT "' || v_pk_name ||
                    '", ADD CONSTRAINT "' || v_idx_name || '" PRIMARY KEY (' || (v_idx->>'IndexColumns') || ')';
        END IF;
    END LOOP;


    -- Step 4.5: a declared index that EXISTS UNDER THE RIGHT NAME BUT THE WRONG SHAPE is dropped here, so the
    -- create below rebuilds it. Existence-by-name alone was the hole: an index created by an older SchemaSmith
    -- (or by hand) kept whatever shape it had, CREATE INDEX IF NOT EXISTS said "already there", and the
    -- declaration in the JSON quietly did not hold -- which is how ProductOwnership's one-owner invariant could
    -- go missing on a database that had simply never run a since-deleted migration script. Shape is read from
    -- the catalog, not from text, and compared only against what this JSON actually declares: uniqueness, the
    -- NULLS NOT DISTINCT form, and the key signature.
    FOR v_idx IN SELECT value FROM jsonb_array_elements(v_def->'Indexes')
                  WHERE COALESCE((value->>'PrimaryKey')::boolean, false) = false
    LOOP
        v_idx_name := v_idx->>'Name';
        -- Scoped to THIS table: an index name is unique per schema, not per table, so matching on name
        -- alone could compare (and then drop) an index belonging to another table in the same schema.
        SELECT c.oid INTO v_oid
          FROM pg_class c
          JOIN pg_namespace n ON n.oid = c.relnamespace
          JOIN pg_index i ON i.indexrelid = c.oid
         WHERE n.nspname = v_schema AND c.relname = v_idx_name AND c.relkind IN ('i', 'I')
           AND i.indrelid = to_regclass('"' || v_schema || '"."' || v_name || '"');
        CONTINUE WHEN v_oid IS NULL;   -- missing entirely: the create below handles it

        SELECT i.indisunique INTO v_actual_unique FROM pg_index i WHERE i.indexrelid = v_oid;

        -- indnullsnotdistinct is PG15+; naming it statically is a parse error below 15, even unreached.
        IF v_pg15 THEN
            EXECUTE 'SELECT indnullsnotdistinct FROM pg_index WHERE indexrelid = $1' INTO v_actual_nnd USING v_oid;
        ELSE
            v_actual_nnd := false;
        END IF;

        -- One normaliser on both sides: strip spaces, quotes, parentheses and ::text casts, so the engine's
        -- rendering of a key -- COALESCE(("IndexName")::text, ''::text) -- compares equal to the form emitted
        -- below without either side having to guess the other's punctuation.
        -- pg_get_indexdef(oid, colno, true) returns the key EXPRESSION only -- it suppresses the sort
        -- direction -- so DESC is read from indoption's low bit and appended, matching how the declared side
        -- spells it. Without this a declared "col DESC" rebuilt forever, and a deployed DESC where the
        -- declaration says ascending compared equal and was never corrected.
        SELECT string_agg(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            pg_get_indexdef(v_oid, k.ord::int, true), ' ', ''), '"', ''), '::text', ''), '(', ''), ')', '')
                          || CASE WHEN (SELECT i.indoption[k.ord - 1] & 1 FROM pg_index i WHERE i.indexrelid = v_oid) = 1
                                  THEN ' DESC' ELSE '' END,
                          ',' ORDER BY k.ord)
          INTO v_actual_sig
          FROM generate_series(1, (SELECT i.indnkeyatts FROM pg_index i WHERE i.indexrelid = v_oid)) AS k(ord);

        -- A partial index (or one carrying INCLUDE columns) is a shape no bootstrap declaration can express, so
        -- it cannot be what the declaration asks for.
        SELECT i.indpred IS NOT NULL OR i.indnatts > i.indnkeyatts INTO v_actual_extra
          FROM pg_index i WHERE i.indexrelid = v_oid;

        v_expected_nnd := COALESCE((v_idx->>'NullsNotDistinct')::boolean, false) AND v_pg15;
        -- Declared side, normalised the same way, with an explicit ASC meaning what the catalog shows for it.

        SELECT string_agg(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                   CASE WHEN COALESCE((v_idx->>'NullsNotDistinct')::boolean, false) AND NOT v_pg15
                             AND EXISTS (SELECT 1 FROM jsonb_array_elements(v_def->'Columns') c
                                          WHERE '"' || (c.value->>'Name') || '"' = btrim(k.col)
                                            AND COALESCE((c.value->>'Nullable')::boolean, true))
                        THEN 'COALESCE(' || btrim(k.col) || '::text, '''')'
                        -- An explicit ASC is what the catalog renders as nothing, so it is dropped rather than
                        -- compared. Plain string functions, not a regex, so nothing rests on the regex dialect.
                        WHEN lower(right(btrim(k.col), 4)) = ' asc'
                             THEN btrim(left(btrim(k.col), length(btrim(k.col)) - 4))
                        ELSE btrim(k.col) END,
                   ' ', ''), '"', ''), '::text', ''), '(', ''), ')', ''),
                 ',' ORDER BY k.ord)
          INTO v_expected_sig
          FROM unnest(string_to_array(v_idx->>'IndexColumns', ',')) WITH ORDINALITY AS k(col, ord);

        IF v_actual_unique IS DISTINCT FROM COALESCE((v_idx->>'Unique')::boolean, false)
           OR v_actual_nnd IS DISTINCT FROM v_expected_nnd
           OR COALESCE(v_actual_extra, false)
           OR v_actual_sig IS DISTINCT FROM v_expected_sig THEN
            RAISE NOTICE '  Rebuilding %.%: the deployed index does not match its declaration', v_schema, v_idx_name;
            -- An index that BACKS a constraint cannot be dropped as an index -- PostgreSQL refuses with
            -- "cannot drop index ... because constraint ... requires it". A hand-added UNIQUE constraint is
            -- exactly the by-hand case this step exists for, so drop it as the constraint it is and let the
            -- create below rebuild the declared index. (The SQL Server twin has always done this.)
            SELECT con.conname INTO v_conname
              FROM pg_constraint con
             WHERE con.conindid = v_oid AND con.contype IN ('p', 'u', 'x');
            IF v_conname IS NOT NULL THEN
                EXECUTE 'ALTER TABLE "' || v_schema || '"."' || v_name || '" DROP CONSTRAINT "' || v_conname || '"';
            ELSE
                EXECUTE 'DROP INDEX "' || v_schema || '"."' || v_idx_name || '"';
            END IF;
        END IF;
    END LOOP;


    -- Step 5: CREATE INDEX IF NOT EXISTS for non-PK indexes, folded into one batch
    -- (PG supports IF NOT EXISTS natively and runs a multi-statement EXECUTE string). Order preserved.
    --
    -- NullsNotDistinct (#270): a unique index that treats NULLs as EQUAL, so a key with a nullable column can
    -- hold one row per distinct key INCLUDING the NULL one -- which is what makes "one owner per object, ever"
    -- structural for ProductOwnership, where a table is the NULL IndexName row. Two forms, same meaning:
    --   PG15+  NULLS NOT DISTINCT, the engine's own clause.
    --   PG12-14  a functional index over COALESCE(<nullable col>::text, ''), so a table folds to '' and
    --            collides with itself. Non-nullable key columns are indexed as themselves.
    -- The clause is built at runtime, never as static text: NULLS NOT DISTINCT is a syntax error at parse time
    -- on an older server even inside a branch that server never takes.
    SELECT string_agg(
             'CREATE ' ||
             CASE WHEN COALESCE((idx->>'Unique')::boolean, false) THEN 'UNIQUE ' ELSE '' END ||
             'INDEX IF NOT EXISTS "' || (idx->>'Name') || '" ON "' ||
             v_schema || '"."' || v_name || '" (' ||
             CASE WHEN COALESCE((idx->>'NullsNotDistinct')::boolean, false) AND NOT v_pg15
                  THEN (SELECT string_agg(
                               CASE WHEN EXISTS (SELECT 1
                                                   FROM jsonb_array_elements(v_def->'Columns') c
                                                  WHERE '"' || (c.value->>'Name') || '"' = btrim(k.col)
                                                    AND COALESCE((c.value->>'Nullable')::boolean, true))
                                    THEN 'COALESCE(' || btrim(k.col) || '::text, '''')'
                                    ELSE btrim(k.col) END, ', ' ORDER BY k.ord)
                          FROM unnest(string_to_array(idx->>'IndexColumns', ',')) WITH ORDINALITY AS k(col, ord))
                  ELSE (idx->>'IndexColumns') END || ')' ||
             CASE WHEN COALESCE((idx->>'NullsNotDistinct')::boolean, false) AND v_pg15
                  THEN ' NULLS NOT DISTINCT' ELSE '' END,
             '; ' ORDER BY ord)
      INTO v_sql
      FROM jsonb_array_elements(v_def->'Indexes') WITH ORDINALITY AS t(idx, ord)
      WHERE COALESCE((idx->>'PrimaryKey')::boolean, false) = false;
    IF v_sql IS NOT NULL THEN
        EXECUTE v_sql;
    END IF;
END $$;
