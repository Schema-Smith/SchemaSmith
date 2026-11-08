-- The "scripted" form: a guarded CREATE in an ordinary migration script.
-- It is the shape almost everyone reaches for first, and it is worse than
-- doing it by hand -- see Step 1 of the lab README.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'email_address') THEN
    CREATE DOMAIN public.email_address AS VARCHAR(256)
      CONSTRAINT email_address_has_at CHECK (VALUE LIKE '%@%');
  END IF;
END $$;
