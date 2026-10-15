-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ConstraintWithoutOverlaps"(p_conoid OID) RETURNS BOOLEAN
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_result BOOLEAN;
BEGIN
  -- pg_constraint.conperiod is a PostgreSQL 18 column: a primary key or unique constraint declared WITHOUT
  -- OVERLAPS. Read via EXECUTE so the column is referenced only on a server that has it -- a static reference is
  -- a plan-time 42703 on an older one, even from SchemaTongs extraction. Keyed on the REAL server version, as
  -- IndexNullsNotDistinct is: a physical column-existence question, not the override-aware ServerVersionNum().
  -- NULL (an index with no constraint) is FALSE.
  IF p_conoid IS NULL THEN
    RETURN FALSE;
  END IF;
  IF (current_setting('server_version_num')::int / 10000) >= 18 THEN
    EXECUTE 'SELECT conperiod FROM pg_constraint WHERE oid = $1' INTO v_result USING p_conoid;
    RETURN COALESCE(v_result, FALSE);
  END IF;
  RETURN FALSE;
END $$;
