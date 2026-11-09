-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table definition lives in the sibling Kindling_DeploymentHistory.json resource: one row per deployment of a
-- template to this database (or one schema of it), written by SchemaQuench unless DeploymentHistory is off.
-- ForgeKindler substitutes the JSON body into this script's TableDef token before execution.

EXEC SchemaSmith.BootstrapTableQuench @TableDefinitions = '{{TableDef}}'
