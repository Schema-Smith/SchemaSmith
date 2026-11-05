-- Routed to by SchemaSmith.MissingTableAndColumnQuench before it creates a table that is
-- in the product but missing from the target (the engine calls
-- CALL "SchemaSmith"."CustomTableRestore"(schema, table) only when this proc exists).
-- If a recycled copy of the table exists, restore it (preserving its data) rather than
-- letting the quench create an empty one; otherwise do nothing and let creation proceed.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."CustomTableRestore"
  (p_schema_name TEXT,
   p_table_name  TEXT)
  LANGUAGE plpgsql
AS $$
DECLARE
  v_recycled TEXT;
BEGIN
  IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
               WHERE n.nspname = 'recyclebin' AND c.relname = 'registry') THEN
    -- Most recently recycled copy matching the original schema and table name
    SELECT recycled_name INTO v_recycled
      FROM recyclebin.registry
      WHERE original_schema = p_schema_name AND original_name = p_table_name
      ORDER BY recycled_date DESC
      LIMIT 1;
  END IF;

  IF v_recycled IS NULL THEN
    RAISE NOTICE 'No recycled copies found for %.%.', p_schema_name, p_table_name;
    RETURN;
  END IF;

  CALL recyclebin.restore_table(v_recycled);
END;
$$;
