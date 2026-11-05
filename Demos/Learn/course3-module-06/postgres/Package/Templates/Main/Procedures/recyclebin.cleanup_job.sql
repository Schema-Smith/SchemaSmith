-- Purges recycled tables whose retention window has expired. Ships as a procedure on all
-- three engines; scheduling is left to the operator (PostgreSQL: pg_cron, e.g.
--   SELECT cron.schedule('recyclebin-cleanup', '0 23 * * *',
--                         $$CALL recyclebin.cleanup_job()$$);
-- once the recyclebin framework is in place). Safe to run by hand or on every quench.
CREATE OR REPLACE PROCEDURE recyclebin.cleanup_job()
  LANGUAGE plpgsql
AS $$
DECLARE
  v_row RECORD;
BEGIN
  -- 1. Adopt untracked tables in the recyclebin schema (present but not in the registry),
  --    so a table moved aside out-of-band still gets a retention window instead of living
  --    forever.
  INSERT INTO recyclebin.registry (original_schema, original_name, recycled_name, recycled_date, retention_days, expiration_date)
    SELECT 'unknown', c.relname, c.relname, now(), 90, now() + (90 * INTERVAL '1 day')
      FROM pg_class c
      JOIN pg_namespace n ON n.oid = c.relnamespace
      WHERE n.nspname = 'recyclebin' AND c.relkind = 'r' AND c.relname <> 'registry'
        AND NOT EXISTS (SELECT 1 FROM recyclebin.registry reg WHERE reg.recycled_name = c.relname);

  -- 2. Drop tables that have exceeded their retention period
  FOR v_row IN
    SELECT recycled_name FROM recyclebin.registry WHERE expiration_date <= now()
  LOOP
    IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'recyclebin' AND c.relname = v_row.recycled_name) THEN
      EXECUTE 'DROP TABLE IF EXISTS recyclebin."' || v_row.recycled_name || '"';
      RAISE NOTICE 'Dropped expired table recyclebin.%', v_row.recycled_name;
    END IF;
    DELETE FROM recyclebin.registry WHERE recycled_name = v_row.recycled_name;
  END LOOP;
END;
$$;
