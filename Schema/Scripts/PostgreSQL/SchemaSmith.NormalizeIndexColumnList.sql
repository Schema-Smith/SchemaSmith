-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A declared index key list in the canonical form the live side renders (IndexKeyForms): plain identifiers unquoted,
-- each key's COLLATE, operator class and parameters in the engine's spelling, and only the order modifiers the
-- catalog records. Also used for INCLUDE lists, which are plain names.
CREATE OR REPLACE FUNCTION "SchemaSmith"."NormalizeIndexColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE plpgsql
    IMMUTABLE
AS $$
BEGIN
    RETURN COALESCE((SELECT STRING_AGG("SchemaSmith"."IndexKeyCanonical"(item, FALSE), ',' ORDER BY ord)
                       FROM UNNEST("SchemaSmith"."SplitTopLevelList"(p_List)) WITH ORDINALITY AS u(item, ord)
                      WHERE TRIM(item) <> ''), '');
END $$;

-- The same, with the live table in hand: a column key's default operator class and own collation are dropped, as the
-- engine never records them, so a package that spells them out compares equal to the live index.
CREATE OR REPLACE FUNCTION "SchemaSmith"."NormalizeIndexColumnList"(p_schema TEXT, p_table TEXT, p_access_method TEXT, p_List TEXT)
    RETURNS TEXT
    LANGUAGE plpgsql
    STABLE
AS $$
BEGIN
    RETURN COALESCE((SELECT STRING_AGG("SchemaSmith"."IndexKeyDeclared"(p_schema, p_table, p_access_method, item), ',' ORDER BY ord)
                       FROM UNNEST("SchemaSmith"."SplitTopLevelList"(p_List)) WITH ORDINALITY AS u(item, ord)
                      WHERE TRIM(item) <> ''), '');
END $$;
