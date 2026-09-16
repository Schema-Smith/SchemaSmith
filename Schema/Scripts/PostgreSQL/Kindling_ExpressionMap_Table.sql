-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Table definition lives in the sibling Kindling_ExpressionMap.json resource.
-- ForgeKindler substitutes the JSON body into this script's TableDef token before execution.
-- BootstrapTableQuench has zero SchemaSmith_* dependencies, so this script runs early
-- in the kindling pipeline (right after the BootstrapTableQuench proc itself is created).

CALL "SchemaSmith"."BootstrapTableQuench"(p_TableDefinitions := '{{TableDef}}');
