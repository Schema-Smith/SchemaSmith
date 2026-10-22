-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A declared index key list in DDL form: a plain identifier quoted, an expression as written, and each key's COLLATE,
-- operator class, parameters and order modifiers carried through in the engine's spelling (IndexKeyForms). NULL for
-- an empty list, which the callers test with NULLIF.
CREATE OR REPLACE FUNCTION "SchemaSmith"."QuoteIndexColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE plpgsql
AS $$
BEGIN
    IF p_List IS NULL OR TRIM(p_List) = '' THEN
        RETURN NULL;
    END IF;
    RETURN (SELECT STRING_AGG("SchemaSmith"."IndexKeyCanonical"(item, TRUE), ',' ORDER BY ord)
              FROM UNNEST("SchemaSmith"."SplitTopLevelList"(p_List)) WITH ORDINALITY AS u(item, ord)
             WHERE TRIM(item) <> '');
END $$;
