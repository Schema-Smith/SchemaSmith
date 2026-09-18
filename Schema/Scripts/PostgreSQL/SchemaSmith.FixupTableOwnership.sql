-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Ownership is scoped per template throughout: reads, INSERT-existence checks, and the DELETE
-- all match on `template_name = p_TemplateName`, so a multi-template product never sees or
-- deletes another template's ownership rows during the per-template prune pass.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."FixupTableOwnership"
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
  RAISE NOTICE 'Add missing Product ownership to tables';
  -- One-owner-per-object (#270): the unique key is (Schema, TableName, IndexName) NULLS NOT DISTINCT,
  -- so ANY existing owner row for this physical table suppresses the insert regardless of template.
  -- A second template declaring the same table in one database silently coalesces onto the existing
  -- owner row (parity with MySQL INSERT IGNORE on uk_object and SQL Server's single ProductName EP)
  -- rather than hitting a raw unique violation.
  INSERT INTO "SchemaSmith"."ProductOwnership"
    ("Schema", "TableName", "IndexName", "ProductName", template_name, "PreventDrop")
    SELECT t."Schema", t."Name", NULL, p_ProductName, p_TemplateName, COALESCE(t."PreventDrop", FALSE)
      FROM temp_tables t
      WHERE NOT EXISTS (SELECT 1 FROM "SchemaSmith"."ProductOwnership" po
                          WHERE po."Schema" = t."Schema"
                            AND po."TableName" = t."Name"
                            AND po."IndexName" IS NULL);

  RAISE NOTICE 'Refresh PreventDrop marker for tables present in the product';
  UPDATE "SchemaSmith"."ProductOwnership" po
     SET "PreventDrop" = COALESCE(t."PreventDrop", FALSE)
    FROM temp_tables t
   WHERE po."Schema" = t."Schema"
     AND po."TableName" = t."Name"
     AND po."IndexName" IS NULL
     AND po."ProductName" = p_ProductName
     AND po.template_name = p_TemplateName
     AND po."PreventDrop" <> COALESCE(t."PreventDrop", FALSE);

  -- Per-iteration scope: p_SchemaName non-empty restricts the prune to rows in the
  -- iteration's schema so a schema-template iteration cannot delete ownership rows
  -- for other tenants of the same template.
  -- D5: prune ownership by catalog existence, not package presence, so sticky PreventDrop survives absence (#270).
  RAISE NOTICE 'Remove Product Ownership for tables no longer present in the catalog';
  DELETE FROM "SchemaSmith"."ProductOwnership" po
    WHERE "ProductName" = p_ProductName
      AND "IndexName" IS NULL
      AND template_name = p_TemplateName
      AND (p_SchemaName = '' OR po."Schema" = p_SchemaName)
      AND NOT EXISTS (SELECT 1
                        FROM pg_catalog.pg_class c
                        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                        WHERE n.nspname = po."Schema"
                          AND c.relname = po."TableName"
                          AND c.relkind IN ('r', 'p'))
      AND NOT EXISTS (SELECT 1
                        FROM pg_matviews mv
                        WHERE mv.schemaname = po."Schema"
                          AND mv.matviewname = po."TableName");
END $$;
