-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The fill factor an index gets when none is declared, per access method (the engine's own defaults), so an index
-- declared without one compares against what the engine actually uses rather than the btree 90. NULL for an access
-- method that has no fill factor (gin, brin, and extension methods such as hnsw / ivfflat), where the option is never
-- emitted or compared.
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexFillFactorDefault"(p_access_method TEXT) RETURNS INT2
    LANGUAGE sql IMMUTABLE
AS $$
  SELECT CASE p_access_method
           WHEN 'btree' THEN 90
           WHEN 'hash' THEN 75
           WHEN 'gist' THEN 90
           WHEN 'spgist' THEN 80
         END::INT2
$$;
