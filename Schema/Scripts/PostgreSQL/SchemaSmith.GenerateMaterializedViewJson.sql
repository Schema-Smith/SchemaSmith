-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE FUNCTION "SchemaSmith"."GenerateMaterializedViewJson"(p_Schema varchar(200), p_MatView varchar(200))
  RETURNS text
  LANGUAGE plpgsql
AS $function$
DECLARE result_string TEXT;
BEGIN
SELECT "SchemaSmith"."FormatJson"(ROW_TO_JSON(mv))
  INTO result_string
  FROM (SELECT mv.schemaname AS "Schema",
               mv.matviewname AS "Name",
               mv.definition AS "Definition",
               c.relispopulated AS "WithData",
               ts.spcname AS "Tablespace",
               am.amname AS "AccessMethod",
               '' AS "ShouldApplyExpression",
               OBJ_DESCRIPTION(c.oid, 'pg_class') AS "Description",
               (SELECT COALESCE(JSON_AGG(ROW_TO_JSON(sub)), '[]'::json)
                  FROM (SELECT i.relname AS "Name",
                               idx.indisunique AS "Unique",
                               idx.indisclustered AS "Clustered",
                               -- The key list as the engine writes it (IndexKeyForms), as GenerateTableJson.
                               "SchemaSmith"."IndexKeyList"(idx.indexrelid) AS "IndexColumns",
                               -- Storage parameters and placement, as GenerateTableJson: fillfactor is FillFactor's,
                               -- an index with no other option yields NULL here, and reltablespace 0 is the database
                               -- default, which is not a declared placement.
                               (SELECT JSON_OBJECT_AGG(SPLIT_PART(opt, '=', 1), SPLIT_PART(opt, '=', 2))
                                  FROM UNNEST(i.reloptions) AS o(opt)
                                 WHERE opt NOT LIKE 'fillfactor=%') AS "StorageParameters",
                               (SELECT its.spcname FROM pg_tablespace its WHERE its.oid = i.reltablespace AND i.reltablespace <> 0) AS "Tablespace",
                               (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
                                  FROM pg_attribute a
                                  CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                  WHERE a.attrelid = idx.indrelid
                                    AND idx > idx.indnkeyatts
                                    AND a.attnum = element) AS "IncludeColumns",
                               (SELECT iam.amname FROM pg_am iam WHERE i.relam = iam.oid AND i.relkind = 'i') AS "AccessMethod",
                               "SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)) AS "FilterExpression",
                               '' AS "ShouldApplyExpression",
                               -- Only a fill factor someone set is declared, as GenerateTableJson.
                               (REGEXP_MATCH(ARRAY_TO_STRING(i.reloptions, ','), 'fillfactor=(\d+)'))[1]::int AS "FillFactor",
                               "SchemaSmith"."IndexNullsNotDistinct"(idx.indexrelid) AS "NullsNotDistinct"
                          FROM pg_index idx
                          JOIN pg_class i ON i.oid = idx.indexrelid
                          WHERE idx.indrelid = c.oid
                          ORDER BY i.relname) sub) AS "Indexes"
          FROM pg_matviews mv
          JOIN pg_class c ON c.relname = mv.matviewname
                          AND c.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = mv.schemaname)
                          AND c.relkind = 'm'
          JOIN pg_am am ON c.relam = am.oid
          LEFT JOIN pg_tablespace ts ON c.reltablespace = ts.oid
          WHERE mv.schemaname NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
            AND mv.schemaname NOT LIKE 'pg_temp_%'
            AND mv.schemaname NOT LIKE 'pg_toast_temp_%'
            AND p_Schema = mv.schemaname
            AND p_MatView = mv.matviewname) mv;

RETURN result_string;
END $function$
