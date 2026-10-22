-- Run as an admin in each tenant, after the kindle has created SchemaSmith's run-once record.
-- SELECT and INSERT come with the tenant-wide grant course6-setup made; DELETE is the one to add.
GRANT DELETE ON SchemaSmith_CompletedMigrationScripts TO 'datafix_user'@'%';
