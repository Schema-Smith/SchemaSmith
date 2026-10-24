CREATE TABLE IF NOT EXISTS "{{SchemaName}}".setup_marker (ran integer NOT NULL);
INSERT INTO "{{SchemaName}}".setup_marker (ran) VALUES (1);
