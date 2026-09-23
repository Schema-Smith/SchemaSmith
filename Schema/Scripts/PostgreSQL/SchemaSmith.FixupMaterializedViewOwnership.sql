-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Same template_name scoping as FixupTableOwnership -- see that file for the rationale.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."FixupMaterializedViewOwnership"
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
BEGIN
  -- WhatIf is read-only: ownership bookkeeping is a real mutation, so skip it entirely (#303).
  IF p_WhatIf THEN RETURN; END IF;
  RAISE NOTICE 'Add missing Product ownership to materialized views';
  INSERT INTO "SchemaSmith"."ProductOwnership"
    ("Schema", "TableName", "IndexName", "ProductName", template_name)
    SELECT t."Schema", t."Name", NULL, p_ProductName, p_TemplateName
      FROM temp_materialized_views t
      WHERE NOT EXISTS (SELECT 1 FROM "SchemaSmith"."ProductOwnership" po
                          WHERE po."Schema" = t."Schema"
                            AND po."TableName" = t."Name"
                            AND po."IndexName" IS NULL
                            AND po.template_name = p_TemplateName);

  -- Per-iteration scope (see FixupTableOwnership for rationale).
  RAISE NOTICE 'Remove Product Ownership for Obsolete Materialized Views';
  DELETE FROM "SchemaSmith"."ProductOwnership" po
    WHERE "ProductName" = p_ProductName
      AND "IndexName" IS NULL
      AND template_name = p_TemplateName
      AND (p_SchemaName = '' OR po."Schema" = p_SchemaName)
      AND NOT EXISTS (SELECT 1
                        FROM temp_materialized_views t
                        WHERE t."Schema" = po."Schema"
                          AND t."Name" = po."TableName")
      AND NOT EXISTS (SELECT 1
                        FROM temp_tables t
                        WHERE t."Schema" = po."Schema"
                          AND t."Name" = po."TableName");
END $$;
