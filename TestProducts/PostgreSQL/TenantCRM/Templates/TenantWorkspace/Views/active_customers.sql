CREATE OR REPLACE VIEW "{{SchemaName}}".active_customers
AS
SELECT c.customer_id,
       c.customer_name,
       c.email,
       c.country_code,
       ctry.country_name,
       c.created_at,
       c.last_modified_at
  FROM "{{SchemaName}}".customers c
  LEFT JOIN public.countries ctry ON ctry.code = c.country_code
 WHERE c.last_modified_at >= (now() - INTERVAL '30 days');
