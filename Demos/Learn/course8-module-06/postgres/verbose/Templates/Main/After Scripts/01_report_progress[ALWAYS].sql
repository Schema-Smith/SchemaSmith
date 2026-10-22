-- VerboseLogging demo: a user RAISE NOTICE. Suppressed by default on PostgreSQL;
-- surfaced when VerboseLogging is on.
DO $$ BEGIN RAISE NOTICE 'Course 8 M6: VerboseLogging demo notice from a user script.'; END $$;
