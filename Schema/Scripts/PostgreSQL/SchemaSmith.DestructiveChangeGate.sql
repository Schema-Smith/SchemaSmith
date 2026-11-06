-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- H3. What this step removes -- tables and columns dropped because the package no longer declares them -- with each table's
-- rows from the catalog (reltuples: never COUNT(*), which would scan exactly the big tables this is for), recorded in the
-- change audit. Under 'fail' the step is refused, unless the change is named in p_Accept: ';schema.table;' accepts every
-- change to the table, ';schema.table.column;' one column, lower case and without quotes, as SchemaQuench renders the list.
-- ModifiedTableQuench is one transaction, so the refusal rolls back the table drops that ran before it -- and the audit rows
-- with them, which is why the refused changes travel in the exception's HINT as JSON (Npgsql redacts DETAIL unless the connection asks for it) for SchemaQuench to report.
-- Reads the caller's temp_product_ownership, temp_tables, temp_existing_columns and temp_columns.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."DestructiveChangeGate"(
  p_Policy TEXT,
  p_Accept TEXT,
  p_WhatIf BOOLEAN,
  p_DropTables BOOLEAN,
  p_DropColumns BOOLEAN)
LANGUAGE plpgsql
AS $$
DECLARE
  v_accept TEXT := LOWER(COALESCE(p_Accept, ''));
  v_list TEXT;
  v_detail TEXT;
BEGIN
  DROP TABLE IF EXISTS temp_destructive;
  CREATE TEMP TABLE temp_destructive ("Kind" TEXT, "TableName" TEXT, "ObjectName" TEXT, "Detail" TEXT, "EstimatedRows" BIGINT, "Accepted" BOOLEAN, "Disposition" TEXT);

  IF p_DropTables THEN
    INSERT INTO temp_destructive ("Kind", "TableName", "ObjectName")
      SELECT 'table drop', tp."Schema" || '.' || tp."TableName", tp."Schema" || '.' || tp."TableName"
        FROM temp_product_ownership tp
        WHERE tp."IndexName" IS NULL
          AND NOT COALESCE(tp."PreventDrop", FALSE)
          AND NOT EXISTS (SELECT 1 FROM temp_tables t WHERE tp."Schema" = t."Schema" AND tp."TableName" = t."Name")
          AND NOT EXISTS (SELECT 1 FROM pg_matviews mv WHERE mv.schemaname = tp."Schema" AND mv.matviewname = tp."TableName");
  END IF;

  IF p_DropColumns THEN
    INSERT INTO temp_destructive ("Kind", "TableName", "ObjectName")
      SELECT 'column drop', ec."TableSchema" || '.' || ec."TableName", ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName"
        FROM temp_existing_columns ec
        JOIN temp_tables tt ON tt."Schema" = ec."TableSchema" AND tt."Name" = ec."TableName"
        WHERE NOT EXISTS (SELECT 1 FROM temp_columns c
                           WHERE ec."TableSchema" = c."TableSchema" AND ec."TableName" = c."TableName" AND ec."ColumnName" = c."Name")
          AND COALESCE(tt."DropColumnsRemovedFromProduct", TRUE);
  END IF;

  -- A column whose type change rounds or cuts values without an error (ColumnChangeLoss), and a plain column the deploy
  -- drops and adds again as generated -- or, below 13, a generated one added again plain and empty -- whose stored values
  -- are replaced. Before a rebuilt table's columns leave temp_existing_columns, so a rebuild's are judged too.
  INSERT INTO temp_destructive ("Kind", "TableName", "ObjectName", "Detail")
    SELECT CASE WHEN replaced THEN 'column replaced' ELSE 'type change' END,
           ec."TableSchema" || '.' || ec."TableName", ec."TableSchema" || '.' || ec."TableName" || '.' || ec."ColumnName",
           LEFT(ec."DataType" || ' -> ' || CASE WHEN replaced AND COALESCE(c."GenerationExpression", '') <> '' THEN 'generated' ELSE c."DataType" END, 400)
      FROM temp_existing_columns ec
      JOIN temp_columns c ON c."TableSchema" = ec."TableSchema" AND c."TableName" = ec."TableName" AND c."Name" = ec."ColumnName"
      JOIN temp_tables tt ON tt."Schema" = ec."TableSchema" AND tt."Name" = ec."TableName"
      CROSS JOIN LATERAL (SELECT (COALESCE(c."GenerationExpression", '') <> '' AND COALESCE(ec."GenerationExpression", '') = '')
                                 OR (COALESCE(c."GenerationExpression", '') = '' AND COALESCE(ec."GenerationExpression", '') <> ''
                                     AND "SchemaSmith"."ServerVersionNum"() < 13) AS replaced) r
      WHERE replaced
         OR (COALESCE(ec."GenerationExpression", '') = '' AND COALESCE(c."GenerationExpression", '') = ''
             AND "SchemaSmith"."ColumnChangeLoss"(ec."DataType", c."DataType") = 'loses');

  IF NOT EXISTS (SELECT 1 FROM temp_destructive) THEN
    RETURN;
  END IF;

  -- reltuples is -1 on 14+ for a table never analyzed, and 0 with pages on 12-13: unknown, not zero. A partitioned parent
  -- holds no rows of its own, so its partitions' are summed.
  UPDATE temp_destructive d
     SET "EstimatedRows" = (
       SELECT CASE WHEN COUNT(*) FILTER (WHERE c.reltuples < 0 OR (c.reltuples = 0 AND c.relpages > 0)) > 0 THEN NULL
                   ELSE SUM(c.reltuples)::BIGINT END
         FROM pg_class c
        WHERE c.relkind IN ('r', 'p')
          AND (c.oid = to_regclass("SchemaSmith"."QuoteName"(split_part(d."TableName", '.', 1)) || '.' || "SchemaSmith"."QuoteName"(split_part(d."TableName", '.', 2)))
               OR c.oid IN (SELECT inhrelid FROM pg_inherits
                             WHERE inhparent = to_regclass("SchemaSmith"."QuoteName"(split_part(d."TableName", '.', 1)) || '.' || "SchemaSmith"."QuoteName"(split_part(d."TableName", '.', 2)))))
          AND c.relkind = 'r');

  UPDATE temp_destructive
     SET "Accepted" = POSITION(';' || LOWER("ObjectName") || ';' IN v_accept) > 0 OR POSITION(';' || LOWER("TableName") || ';' IN v_accept) > 0;
  UPDATE temp_destructive
     SET "Disposition" = CASE WHEN "Accepted" THEN 'accepted'
                              WHEN p_Policy = 'fail' THEN CASE WHEN p_WhatIf THEN 'wouldRefuse' ELSE 'refused' END
                              WHEN p_Policy = 'warn' THEN 'warned'
                              ELSE 'allowed' END;

  IF EXISTS (SELECT 1 FROM temp_destructive WHERE "Disposition" = 'refused') THEN
    SELECT STRING_AGG(CASE "Kind" WHEN 'table drop' THEN 'table ' || "ObjectName" || ' dropped' WHEN 'column drop' THEN 'column ' || "ObjectName" || ' dropped'
                                  ELSE "Kind" || ' ' || "ObjectName" || ' ' || COALESCE("Detail", '') END
                      || COALESCE(' (about ' || "EstimatedRows" || ' rows)', ''), '; ' ORDER BY "ObjectName") FILTER (WHERE "Disposition" = 'refused'),
           'SchemaSmith.Destructive:' || json_agg(json_build_object('kind', "Kind", 'name', "ObjectName", 'detail', "Detail", 'rows', "EstimatedRows", 'mode', "Disposition"))::TEXT
      INTO v_list, v_detail
      FROM temp_destructive;
    RAISE EXCEPTION 'Destructive change refused (DestructiveChangePolicy is fail): %. Nothing in this step was dropped or altered. Name each one in AcceptDestructive to proceed, restore it to the package, or keep it with DropColumnsRemovedFromProduct or PreventDrop.', v_list
      USING HINT = v_detail;
  END IF;

  INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType", "Mode", "Detail", "EstimatedRows")
    SELECT pg_backend_pid(), "Kind", "ObjectName", 'destructive', "Disposition", "Detail", "EstimatedRows" FROM temp_destructive;
END;
$$;
