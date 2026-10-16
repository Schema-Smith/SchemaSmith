-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The one rendering of an index's WITH clause, shared by every create arm (a key constraint's USING INDEX options and
-- a plain index's, in MissingIndexesAndConstraintsQuench and IndexOnlyQuench): ' WITH (fillfactor = n, k=v, ...)' or
-- '' when there is nothing to say. fillfactor goes in only when the package declares one -- an undeclared fill factor
-- is the engine's default, not a value to write -- and only for the access methods that accept it. That is a positive
-- gate, not a deny-list: an extension method (pgvector's hnsw / ivfflat) cannot be enumerated in advance, and a
-- deny-list would default it INTO the clause and fail the CREATE with the engine's own "unrecognized parameter".
-- p_storage_parameters is already canonical key=value,key=value, which is WITH-clause syntax, so it drops straight in.
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexWithClause"(p_fill_factor_declared BOOLEAN, p_fill_factor INT2, p_access_method TEXT, p_storage_parameters TEXT) RETURNS TEXT
    LANGUAGE sql IMMUTABLE
AS $$
  SELECT CASE WHEN opts <> '' THEN ' WITH (' || opts || ')' ELSE '' END
    FROM (SELECT CONCAT_WS(', ',
                   CASE WHEN p_fill_factor_declared AND COALESCE(p_access_method, 'btree') IN ('btree', 'gist', 'hash', 'spgist')
                        THEN 'fillfactor = ' || p_fill_factor END,
                   NULLIF(p_storage_parameters, '')) AS opts) o
$$;
