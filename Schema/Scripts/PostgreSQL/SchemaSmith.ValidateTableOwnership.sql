-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- temp_product_ownership is populated strictly on template_name = p_TemplateName so a
-- multi-template product's ModifiedTableQuench drop-candidates query only sees rows owned by
-- the CURRENT template. Without this, every template iteration's drop pass would see every
-- other template's owned tables as drop candidates.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."ValidateTableOwnership"
(p_ProductName VARCHAR(50),
 p_WhatIf BOOLEAN = FALSE,
 p_TemplateName VARCHAR(256) = '',
 p_SchemaName VARCHAR(256) = '')
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT = '';
  missing_tablespaces TEXT;
BEGIN
    -- #571: a declared tablespace that does not exist failed mid-deploy with the engine's 42704. Refused here, before any
    -- DDL and on every path that deploys tables, naming what declares it, as SQL Server refuses a missing filegroup.
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

    RAISE NOTICE 'Validate Table Ownership';
    SELECT STRING_AGG('RAISE NOTICE ''  Table ' || tp."Schema" || '.' || tp."TableName" || ' owned by different product. [' || tp."ProductName" || '] <> [' || p_ProductName || ']'';', CHR(10))
      INTO sql_script
      FROM temp_tables t
      JOIN "SchemaSmith"."ProductOwnership" tp ON tp."Schema" = t."Schema"
                                              AND (tp."TableName" = t."Name"
                                                   -- A table declared under a new name is still the old one until the rename, which runs
                                                   -- only when the old name exists and the new one does not.
                                                   OR (COALESCE(t."OldName", '') <> '' AND tp."TableName" = t."OldName"
                                                       AND NOT EXISTS (SELECT 1 FROM information_schema.tables it WHERE it.table_schema = t."Schema" AND it.table_name = t."Name")))
                                              AND tp."IndexName" IS NULL -- table level ownership
      WHERE tp."ProductName" <> p_ProductName;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

    IF EXISTS (SELECT 1
                 FROM temp_tables t
                 JOIN "SchemaSmith"."ProductOwnership" tp ON t."Schema" = tp."Schema" AND tp."IndexName" IS NULL -- table level ownership
                                                  AND (tp."TableName" = t."Name"
                                                   OR (COALESCE(t."OldName", '') <> '' AND tp."TableName" = t."OldName"
                                                       AND NOT EXISTS (SELECT 1 FROM information_schema.tables it WHERE it.table_schema = t."Schema" AND it.table_name = t."Name")))
                 WHERE tp."ProductName" <> p_ProductName) THEN
        RAISE EXCEPTION 'One or more tables in this quench are already owned by another product';
    END IF;

    -- Per-iteration ownership scope. p_SchemaName non-empty => schema-template iteration:
    -- restrict to rows in THIS schema so a TenantBody iteration for tenant_acme cannot
    -- propose drops against tenant_beta tables (both owned by the same template).
    -- p_SchemaName empty => regular template: no per-schema restriction.
    RAISE NOTICE 'Collect Existing Column Definitions';
    DROP TABLE IF EXISTS temp_product_ownership;
    CREATE TEMPORARY TABLE temp_product_ownership AS
      SELECT "Schema", "TableName", "IndexName", "PreventDrop"
        FROM "SchemaSmith"."ProductOwnership"
        WHERE "ProductName" = p_ProductName
          AND template_name = p_TemplateName
          AND (p_SchemaName = '' OR "Schema" = p_SchemaName);

END $$;
