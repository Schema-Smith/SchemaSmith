-- The state function the tag_list aggregate folds each value through.
CREATE OR REPLACE FUNCTION public.tag_list_step(acc text, val text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$ SELECT CASE WHEN acc IS NULL OR acc = '' THEN val ELSE acc || ',' || val END $$;
