CREATE OR REPLACE FUNCTION public.price_band(price numeric, ceiling numeric) RETURNS text
LANGUAGE sql IMMUTABLE AS $$ SELECT CASE WHEN price < ceiling / 10 THEN 'low' WHEN price < ceiling THEN 'mid' ELSE 'high' END $$;
