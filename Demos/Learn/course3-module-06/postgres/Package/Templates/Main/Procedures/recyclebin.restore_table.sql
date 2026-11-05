-- Moves a recycled table back to its original schema/name and clears its registry entry.
-- Called by SchemaSmith.CustomTableRestore; can also be called directly to recover a
-- specific recycled copy by name. Structure (indexes, constraints, foreign keys) is NOT
-- restored here — the recycled copy is a bare data table; a subsequent quench rebuilds
-- its structure from the package definition.
CREATE OR REPLACE PROCEDURE recyclebin.restore_table
  (p_recycled_name TEXT)
  LANGUAGE plpgsql
AS $$
DECLARE
  v_orig_schema TEXT;
  v_orig_name   TEXT;
BEGIN
  SELECT original_schema, original_name
    INTO v_orig_schema, v_orig_name
    FROM recyclebin.registry
    WHERE recycled_name = p_recycled_name;

  IF v_orig_name IS NULL THEN
    RAISE NOTICE 'No registry entry found for recycled table %.', p_recycled_name;
    RETURN;
  END IF;

  -- Recycled table gone (manually dropped or already restored)? Clear the stale entry.
  IF NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE n.nspname = 'recyclebin' AND c.relname = p_recycled_name) THEN
    RAISE NOTICE 'recyclebin.% no longer exists. Removing registry entry.', p_recycled_name;
    DELETE FROM recyclebin.registry WHERE recycled_name = p_recycled_name;
    RETURN;
  END IF;

  -- The original schema must exist before restoring into it
  IF NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = v_orig_schema) THEN
    RAISE EXCEPTION 'Original schema % does not exist. Create it before restoring.', v_orig_schema;
  END IF;

  -- Refuse to clobber an existing table at the target
  IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = v_orig_schema AND c.relname = v_orig_name) THEN
    RAISE EXCEPTION 'A table %.% already exists. Rename or drop it before restoring.', v_orig_schema, v_orig_name;
  END IF;

  -- Rename back to the original name (if different), then move to the original schema
  IF p_recycled_name <> v_orig_name THEN
    EXECUTE 'ALTER TABLE recyclebin."' || p_recycled_name || '" RENAME TO "' || v_orig_name || '"';
  END IF;
  EXECUTE 'ALTER TABLE recyclebin."' || v_orig_name || '" SET SCHEMA "' || v_orig_schema || '"';

  DELETE FROM recyclebin.registry WHERE recycled_name = p_recycled_name;

  RAISE NOTICE 'recyclebin.% has been restored as %.%.', p_recycled_name, v_orig_schema, v_orig_name;
END;
$$;
