-- Run-once per tenant: backfills customers.country_code for rows that pre-date
-- the country_code column. New tenants have no rows to backfill but the migration
-- still tracks as completed per-tenant, so a future re-quench skips it cleanly.

UPDATE "{{SchemaName}}".customers
   SET country_code = 'US'
 WHERE country_code IS NULL;
