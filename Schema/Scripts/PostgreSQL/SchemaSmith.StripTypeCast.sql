-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Strip trailing PostgreSQL type-cast suffix(es) from a stored/declared expression so column
-- defaults compare canonically. PostgreSQL stores a varchar/char default as "'value'::character
-- varying" (an explicit cast the catalog reports verbatim), while a hand-authored package declares
-- the bare "'value'". Without this, the two never compare equal and ModifiedTableQuench classes the
-- column as modified on every quench. Applied to BOTH sides of the default comparison so a
-- hand-authored ("'value'") and a SchemaTongs-extracted ("'value'::character varying") package both
-- converge to a no-op against the same catalog value. Only a TRAILING cast is removed (anchored at
-- end), so an embedded cast inside a function/expression default — e.g. nextval('seq'::regclass) —
-- is left intact. The type may be schema-qualified: a default on a column typed by an enum or domain in a schema
-- that is not on the search path is stored as 'value'::tenant.type, which must strip like the unqualified form.
CREATE OR REPLACE FUNCTION "SchemaSmith"."StripTypeCast"(p_text TEXT) RETURNS TEXT
    LANGUAGE sql IMMUTABLE
AS $$
  SELECT CASE
           WHEN p_text IS NULL THEN NULL
           ELSE regexp_replace(p_text, '(::(?:(?:"[^"]+"|[A-Za-z_][A-Za-z0-9_]*)\.)?(?:"[^"]+"|[A-Za-z_][A-Za-z0-9_ ]*)(?:\([0-9, ]+\))?(?:\[\])*)+$', '')
         END;
$$;
