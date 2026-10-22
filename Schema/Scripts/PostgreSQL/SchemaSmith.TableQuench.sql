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
   p_RebuildPolicyOnOrderMismatch BOOLEAN = FALSE,
   -- Forwarded to every pass that builds an index or constraint. The CONCURRENTLY statements those passes queue run
   -- outside any transaction, so a caller of this procedure runs the queue (temp_deferred_online_work) itself.
   p_OnlineApply TEXT = 'OFF')
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
  missing_tablespaces TEXT;
BEGIN
{{ParseJson}}

  -- #571: a declared tablespace that does not exist failed mid-deploy with the engine's 42704. Refused here, before any
  -- DDL, naming what declares it, as SQL Server refuses a missing partition scheme or filegroup.
  missing_tablespaces := (SELECT STRING_AGG(d.what || ' declares tablespace ' || d.ts, '; ' ORDER BY d.what)
                            FROM (SELECT 'table ' || t."Schema" || '.' || t."Name" AS what, t."Tablespace" AS ts FROM temp_tables t
                                UNION ALL SELECT 'index ' || i."TableSchema" || '.' || i."TableName" || '.' || i."Name", i."Tablespace"
                                  FROM temp_indexes i JOIN temp_tables t ON t."Schema" = i."TableSchema" AND t."Name" = i."TableName"
                                UNION ALL SELECT 'exclude constraint ' || x."TableSchema" || '.' || x."TableName" || '.' || x."Name", x."Tablespace"
                                  FROM temp_excludes x JOIN temp_tables t ON t."Schema" = x."TableSchema" AND t."Name" = x."TableName") d
                           WHERE d.ts <> '' AND NOT EXISTS (SELECT 1 FROM pg_tablespace s WHERE s.spcname IN (d.ts, LOWER(d.ts))));
  IF missing_tablespaces IS NOT NULL THEN
    RAISE EXCEPTION 'Tablespace not found: %. Create it, or remove Tablespace from the declaration.', missing_tablespaces;
  END IF;

  -- Before anything is created or renamed, so a refusal leaves another product's table as it was.
  CALL "SchemaSmith"."ValidateTableOwnership"(p_ProductName, p_WhatIf);
  -- #508: before any table or column is created, so only what already exists is checked on a scratch copy.
  CALL "SchemaSmith"."ExpressionMapAdopt"(p_WhatIf);
  CALL "SchemaSmith"."MissingTableAndColumnQuench"(p_WhatIf);
  CALL "SchemaSmith"."ModifiedTableQuench"(p_WhatIf := p_WhatIf, p_DropUnknownIndexes := p_DropUnknownIndexes, p_DropTablesRemovedFromProduct := p_DropTablesRemovedFromProduct,
                                           p_RebuildPolicyMode := p_RebuildPolicyMode, p_RebuildPolicyThreshold := p_RebuildPolicyThreshold, p_RebuildPolicyOnOrderMismatch := p_RebuildPolicyOnOrderMismatch,
                                           p_OnlineApply := p_OnlineApply);
  CALL "SchemaSmith"."MissingIndexesAndConstraintsQuench"(p_WhatIf, p_OnlineApply);
  -- After the passes above: a description is a property of an object that must already exist.
  CALL "SchemaSmith"."DescriptionQuench"(p_WhatIf);
  CALL "SchemaSmith"."ReplicaIdentityQuench"(p_WhatIf);
  CALL "SchemaSmith"."ForeignKeyQuench"(p_WhatIf, p_OnlineApply);
  -- After the keys exist: enforcement and validation are state on a constraint that must already be there.
  CALL "SchemaSmith"."ConstraintStateQuench"(p_WhatIf, p_OnlineApply);
  CALL "SchemaSmith"."FixupTableOwnership"(p_ProductName, p_WhatIf);
  CALL "SchemaSmith"."FixupIndexOwnership"(p_ProductName, p_WhatIf);
  -- #242: last, so anything the passes above created is recorded on this run rather than churning once more.
  CALL "SchemaSmith"."ExpressionMapRecord"(p_WhatIf);
END $$;