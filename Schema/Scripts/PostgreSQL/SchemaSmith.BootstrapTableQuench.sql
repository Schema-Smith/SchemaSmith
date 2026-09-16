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
