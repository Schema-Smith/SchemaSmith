-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table definition lives in the sibling Kindling_RebuildSetAsideForeignKeys.json resource.
-- The foreign keys a table rebuild took off the tables that reference it, recorded inside the rebuild's transaction
-- and cleared when ForeignKeyQuench puts each one back -- so a run that stops in between is repaired by the next.

EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = '{{TableDef}}'
