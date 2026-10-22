-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A name as a package may write it: bare (a"b), or quoted with its inner quotes doubled ("a""b"). Only a matched outer
-- pair is unwrapped, so a quote inside a bare name stays part of it.
CREATE OR REPLACE FUNCTION "SchemaSmith"."BareName"(p_Name TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
AS $$
  SELECT CASE WHEN LENGTH(TRIM(p_Name)) >= 2 AND LEFT(TRIM(p_Name), 1) = '"' AND RIGHT(TRIM(p_Name), 1) = '"'
              THEN REPLACE(SUBSTR(TRIM(p_Name), 2, LENGTH(TRIM(p_Name)) - 2), '""', '"')
              ELSE TRIM(p_Name) END
$$;

-- The same list as the catalog spells it: bare names, comma-separated, no spaces. What a declared key's columns compare as.
CREATE OR REPLACE FUNCTION "SchemaSmith"."BareColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
AS $$
  SELECT STRING_AGG("SchemaSmith"."BareName"(item), ',' ORDER BY ord)
    FROM UNNEST(STRING_TO_ARRAY(p_List, ',')) WITH ORDINALITY AS u(item, ord)
$$;

CREATE OR REPLACE FUNCTION "SchemaSmith"."QuoteColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE plpgsql
AS $$
BEGIN
  RETURN (SELECT STRING_AGG("SchemaSmith"."QuoteName"("SchemaSmith"."BareName"(item)), ',')
            FROM UNNEST(STRING_TO_ARRAY(p_List, ',')) AS item);
END $$