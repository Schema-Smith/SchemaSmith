-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."TableQuench"
  (p_ProductName VARCHAR(50),
   p_TableDefinitions TEXT,
   p_WhatIf BOOLEAN = FALSE,
   p_DropUnknownIndexes BOOLEAN = FALSE,
   p_DropTablesRemovedFromProduct BOOLEAN = TRUE,
   p_UpdateFillFactor BOOLEAN = TRUE,
   -- The resolved upper-tier RebuildPolicy, forwarded to ModifiedTableQuench (which owns the decision).
   -- Defaults are the domain object's NEVER default, so an existing caller that passes nothing behaves
   -- exactly as before and can never elect a rebuild.
   p_RebuildPolicyMode TEXT = 'NEVER',
   p_RebuildPolicyThreshold INT = NULL,
   p_RebuildPolicyOnOrderMismatch BOOLEAN = FALSE)
  LANGUAGE plpgsql
AS $$
DECLARE
  table_json TEXT = CASE WHEN LEFT(p_TableDefinitions, 1) = '[' THEN p_TableDefinitions ELSE '[' || p_TableDefinitions || ']' END;
  sql_script TEXT = '';
BEGIN
{{ParseJson}}

  CALL "SchemaSmith"."MissingTableAndColumnQuench"(p_WhatIf);
  CALL "SchemaSmith"."ValidateTableOwnership"(p_ProductName, p_WhatIf);
  CALL "SchemaSmith"."ModifiedTableQuench"(p_WhatIf := p_WhatIf, p_DropUnknownIndexes := p_DropUnknownIndexes, p_DropTablesRemovedFromProduct := p_DropTablesRemovedFromProduct,
                                           p_RebuildPolicyMode := p_RebuildPolicyMode, p_RebuildPolicyThreshold := p_RebuildPolicyThreshold, p_RebuildPolicyOnOrderMismatch := p_RebuildPolicyOnOrderMismatch);
  CALL "SchemaSmith"."MissingIndexesAndConstraintsQuench"(p_WhatIf);
  CALL "SchemaSmith"."ReplicaIdentityQuench"(p_WhatIf);
  CALL "SchemaSmith"."ForeignKeyQuench"(p_WhatIf);
  CALL "SchemaSmith"."FixupTableOwnership"(p_ProductName, p_WhatIf);
  CALL "SchemaSmith"."FixupIndexOwnership"(p_ProductName, p_WhatIf);
  -- #242: last, so anything the passes above created is recorded on this run rather than churning once more.
  CALL "SchemaSmith"."ExpressionMapRecord"(p_WhatIf);
END $$;