-- Run as an admin in each tenant, after the kindle has created SchemaSmith's run-once record.
GRANT USAGE ON SCHEMA "SchemaSmith" TO datafix_user;
GRANT SELECT, INSERT, DELETE ON "SchemaSmith"."CompletedMigrationScripts" TO datafix_user;
