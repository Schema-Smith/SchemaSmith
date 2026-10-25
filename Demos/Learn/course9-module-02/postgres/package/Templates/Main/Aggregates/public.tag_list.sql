-- Joins text values into one comma-separated list.
CREATE OR REPLACE FUNCTION public.tag_list_step(acc text, val text) RETURNS text
LANGUAGE sql IMMUTABLE AS $$ SELECT CASE WHEN acc IS NULL OR acc = '' THEN val ELSE acc || ',' || val END $$;

CREATE OR REPLACE AGGREGATE public.tag_list(text) (SFUNC = public.tag_list_step, STYPE = text);
