-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- One row per object script the deploy applied: the hash of the script text it ran and the hash of the engine's
-- own rendering of the module afterwards. A script runs again only when either changed.
EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = '{{TableDef}}'
