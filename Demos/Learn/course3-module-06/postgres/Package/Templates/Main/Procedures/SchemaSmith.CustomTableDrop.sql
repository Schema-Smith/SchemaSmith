-- Routed to by SchemaSmith.ModifiedTableQuench when a table is removed from the
-- product (the engine calls CALL "SchemaSmith"."CustomTableDrop"(schema, table) only
-- when this proc exists). Instead of dropping the table outright, soft-drop it: strip
-- its constraints/indexes, move the bare data table into the recyclebin schema, and
-- register it so it can be restored until its retention window expires.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."CustomTableDrop"
  (p_schema_name     TEXT,
   p_table_name      TEXT,
   p_retention_days  INT DEFAULT 90)
  LANGUAGE plpgsql
AS $$
DECLARE
  v_oid       OID;
  v_recycled  TEXT;
  v_modifier  INT;
  v_sql       TEXT;
BEGIN
  -- Validate the table exists
  SELECT c.oid INTO v_oid
    FROM pg_class c
    JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = p_schema_name AND c.relname = p_table_name AND c.relkind = 'r';
  IF v_oid IS NULL THEN
    RAISE EXCEPTION 'Table %.% does not exist.', p_schema_name, p_table_name;
  END IF;

  -- Defensive: the Schemas folder also creates this, but never assume it ran first
  CREATE SCHEMA IF NOT EXISTS recyclebin;

  -- Generate a unique recycled name: <schema>_<table>, suffixed _N on collision
  v_recycled := p_schema_name || '_' || p_table_name;
  IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'recyclebin' AND c.relname = v_recycled) THEN
    v_modifier := 1;
    WHILE EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'recyclebin' AND c.relname = v_recycled || '_' || v_modifier) LOOP
      v_modifier := v_modifier + 1;
    END LOOP;
    v_recycled := v_recycled || '_' || v_modifier;
  END IF;

  -- 1. Drop inbound foreign keys that reference this table. With the inbound-FK drop
  --    ordering fix the engine already clears these before calling the hook, so this is
  --    belt-and-suspenders that keeps the proc self-sufficient if called directly.
  SELECT STRING_AGG('ALTER TABLE "' || pn.nspname || '"."' || pc.relname
                    || '" DROP CONSTRAINT IF EXISTS "' || con.conname || '";', CHR(10))
    INTO v_sql
    FROM pg_constraint con
    JOIN pg_class pc     ON pc.oid = con.conrelid
    JOIN pg_namespace pn ON pn.oid = pc.relnamespace
    WHERE con.contype = 'f' AND con.confrelid = v_oid AND con.conrelid <> v_oid;
  IF v_sql IS NOT NULL THEN EXECUTE v_sql; END IF;

  -- 2. Drop this table's own constraints (foreign key, check, primary/unique). Dropping
  --    a primary/unique constraint also drops its backing index. SET SCHEMA below would
  --    carry these along, so stripping them avoids name collisions across recycled copies
  --    and leaves a bare data table that a later quench rebuilds from the package.
  SELECT STRING_AGG('ALTER TABLE "' || p_schema_name || '"."' || p_table_name
                    || '" DROP CONSTRAINT IF EXISTS "' || con.conname || '";', CHR(10))
    INTO v_sql
    FROM pg_constraint con
    WHERE con.conrelid = v_oid;
  IF v_sql IS NOT NULL THEN EXECUTE v_sql; END IF;

  -- 3. Drop any remaining (non-constraint-backed) indexes
  SELECT STRING_AGG('DROP INDEX IF EXISTS "' || n.nspname || '"."' || ic.relname || '";', CHR(10))
    INTO v_sql
    FROM pg_index i
    JOIN pg_class ic    ON ic.oid = i.indexrelid
    JOIN pg_namespace n ON n.oid = ic.relnamespace
    WHERE i.indrelid = v_oid;
  IF v_sql IS NOT NULL THEN EXECUTE v_sql; END IF;

  -- 4. Move the now-bare table into the recyclebin schema and rename it
  EXECUTE 'ALTER TABLE "' || p_schema_name || '"."' || p_table_name || '" SET SCHEMA recyclebin';
  IF v_recycled <> p_table_name THEN
    EXECUTE 'ALTER TABLE recyclebin."' || p_table_name || '" RENAME TO "' || v_recycled || '"';
  END IF;

  -- 5. Register the recycled copy
  INSERT INTO recyclebin.registry (original_schema, original_name, recycled_name, recycled_date, retention_days, expiration_date)
    VALUES (p_schema_name, p_table_name, v_recycled, now(), p_retention_days,
            now() + (p_retention_days * INTERVAL '1 day'));

  RAISE NOTICE 'Table %.% recycled as recyclebin.% with a retention of % days.',
    p_schema_name, p_table_name, v_recycled, p_retention_days;
END;
$$;
