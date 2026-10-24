-- Run-once per tenant: backfills [Customers].[CountryCode] for rows that pre-date
-- the CountryCode column. New tenants have no rows to backfill but the migration
-- still tracks as completed per-tenant, so a future re-quench skips it cleanly.

UPDATE [{{SchemaName}}].[Customers]
   SET [CountryCode] = 'US'
 WHERE [CountryCode] IS NULL;
