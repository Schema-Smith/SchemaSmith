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
  -- LLVM compilation is never worth it for this work, and the planner has no way to know that.
  --
  -- Temp tables built with CREATE TEMPORARY TABLE ... AS are never ANALYZEd, so every query over them
  -- is planned against default row guesses (140, 270, 200). Push those guesses through a correlated
  -- NOT EXISTS carrying JSON_ARRAY_ELEMENTS, STRING_AGG and a user function and the estimate explodes:
  -- the exclude-constraint drop query costed at 2,066,541, clearing jit_above_cost (100,000) and both
  -- jit_inline_above_cost and jit_optimize_above_cost (500,000). PostgreSQL then compiled, inlined and
  -- optimised 54 functions for 797ms -- to run a plan that returned zero rows in 30 MICROseconds.
  -- Every deploy paid that, on every table, whether or not anything had changed.
  --
  -- Nothing SchemaSmith asks of the database is shaped like a query JIT helps: these are metadata
  -- queries over temp tables and catalogs, tens to hundreds of rows, where compilation can never
  -- amortise. Measured on the PostgreSQL integration suite (417 tests, same image, JIT the only
  -- variable): 596s -> 199s.
  --
  -- A function-level SET is saved and restored around the call and covers every nested CALL below,
  -- so the caller's own jit setting is left exactly as it was found.
  SET jit = 'off'
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