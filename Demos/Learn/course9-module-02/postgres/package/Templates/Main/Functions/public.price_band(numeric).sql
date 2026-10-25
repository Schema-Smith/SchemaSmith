CREATE OR REPLACE FUNCTION public.price_band(price numeric) RETURNS text
LANGUAGE sql IMMUTABLE AS $$ SELECT CASE WHEN price < 10 THEN 'low' WHEN price < 100 THEN 'mid' ELSE 'high' END $$;
