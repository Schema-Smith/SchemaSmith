-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE FUNCTION "SchemaSmith"."GenerateSequenceJSON"(p_Schema varchar(200), p_Name varchar(200))
  RETURNS text
  LANGUAGE plpgsql
AS $function$
DECLARE result_string TEXT;
BEGIN
  -- Extracts one sequence as the declarative package form.
  --
  -- THE CURRENT VALUE IS NOT READ. A sequence's position is data -- which numbers have already been
  -- handed out -- so putting it in the package would mean a later deploy resetting a live sequence and
  -- re-issuing keys that are in use. seqstart is the DECLARED start, which is schema; last_value is not,
  -- and is deliberately absent from this query.
  SELECT "SchemaSmith"."FormatJson"(ROW_TO_JSON(t))
    INTO result_string
    FROM (SELECT n.nspname AS "Schema",
                 c.relname AS "Name",
                 FORMAT_TYPE(q.seqtypid, NULL) AS "DataType",
                 q.seqstart AS "Start",
                 q.seqincrement AS "Increment",
                 q.seqmin AS "MinValue",
                 q.seqmax AS "MaxValue",
                 q.seqcache AS "Cache",
                 q.seqcycle AS "Cycle",
                 -- Only an unlogged sequence says so; a logged one extracts exactly as it always did.
                 CASE WHEN c.relpersistence = 'u' THEN 'Unlogged' END AS "PersistenceType"
            FROM pg_sequence q
            JOIN pg_class c ON c.oid = q.seqrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = p_Schema
             AND c.relname = p_Name) t;

  RETURN result_string;
END $function$;
