-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE FUNCTION "SchemaSmith"."GenerateEnumTypeJSON"(p_Schema varchar(200), p_Name varchar(200))
  RETURNS text
  LANGUAGE plpgsql
AS $function$
DECLARE result_string TEXT;
BEGIN
  -- Extracts one enum type as the declarative package form.
  --
  -- ORDER IS THE WHOLE POINT. PostgreSQL sorts and compares enum values by enumsortorder, not
  -- alphabetically, so the ordering below is not presentation -- an extraction that emitted the values in
  -- any other order would produce a package that redeploys a DIFFERENT type from the one it read, and
  -- nothing would report it.
  SELECT "SchemaSmith"."FormatJson"(ROW_TO_JSON(t))
    INTO result_string
    FROM (SELECT n.nspname AS "Schema",
                 ty.typname AS "Name",
                 (SELECT JSON_AGG(e.enumlabel ORDER BY e.enumsortorder)
                    FROM pg_enum e
                   WHERE e.enumtypid = ty.oid) AS "Values",
                 OBJ_DESCRIPTION(ty.oid, 'pg_type') AS "Description"
            FROM pg_type ty
            JOIN pg_namespace n ON n.oid = ty.typnamespace
           WHERE ty.typtype = 'e'
             AND n.nspname = p_Schema
             AND ty.typname = p_Name) t;

  RETURN result_string;
END $function$;
