-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

CREATE OR REPLACE FUNCTION "SchemaSmith"."ConstraintEnforced"(p_conoid OID) RETURNS BOOLEAN
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_result BOOLEAN;
BEGIN
  -- pg_constraint.conenforced is a PostgreSQL 18 column; below 18 every constraint is enforced. It is read via
  -- EXECUTE so the column is referenced only at runtime on a server that has it -- a static reference is a
  -- plan-time 42703 on an older server. Keyed on the REAL server version (a physical column-existence question),
  -- not the override-aware ServerVersionNum(), like IndexNullsNotDistinct.
  IF (current_setting('server_version_num')::int / 10000) >= 18 THEN
    EXECUTE 'SELECT conenforced FROM pg_constraint WHERE oid = $1' INTO v_result USING p_conoid;
    RETURN COALESCE(v_result, TRUE);
  END IF;
  RETURN TRUE;
END $$;
