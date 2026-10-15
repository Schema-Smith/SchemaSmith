-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The predicate of a CHECK constraint as a package declares it: pg_get_constraintdef minus its "CHECK " prefix, minus
-- the state suffixes it renders after the predicate (NOT VALID, NO INHERIT, NOT ENFORCED -- attributes, not expression
-- text), minus the parentheses the engine wraps it in. ONE definition on purpose: extraction, the modified-check compare
-- and the expression map all read a live check through this, and when two of them disagree a constraint round-trips
-- into a package that the compare then reports as changed on every deploy.
CREATE OR REPLACE FUNCTION "SchemaSmith"."CheckPredicate"(p_conoid OID) RETURNS TEXT
    LANGUAGE sql STABLE
AS $$
  SELECT "SchemaSmith"."StripParenWrapping"(
           REGEXP_REPLACE(SUBSTRING(pg_catalog.PG_GET_CONSTRAINTDEF(p_conoid) FROM 6), '(\s+(NOT VALID|NO INHERIT|NOT ENFORCED))+\s*$', '', 'i'));
$$;
