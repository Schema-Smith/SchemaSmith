-- Joins text values into one comma-separated list.
CREATE OR REPLACE AGGREGATE public.tag_list(text) (SFUNC = public.tag_list_step, STYPE = text);
