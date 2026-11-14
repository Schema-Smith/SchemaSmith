-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A declared type with its modifier, as format_type renders it: the type resolved by to_regtype and the modifier by the
-- type's own input function (pg_type.typmodin), so an extension writes it in its own case and fills in its own
-- defaults -- PostGIS reads geometry(point, 4326) as geometry(Point,4326) and geography(Point) as
-- geography(Point,4326). NULL when the type does not resolve, takes no modifier, or rejects the one declared.
-- PostgreSQL 17 has to_regtypemod for this; typmodin answers the same question on every supported version.
CREATE OR REPLACE FUNCTION "SchemaSmith"."DeclaredTypeCanonical"(p_type TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_oid OID := TO_REGTYPE(p_type);
  v_args TEXT := SUBSTRING(p_type FROM '\((.*)\)\s*$');
  v_in REGPROC;
  v_typmod INT;
BEGIN
  IF v_oid IS NULL THEN RETURN NULL; END IF;
  IF v_args IS NULL THEN RETURN FORMAT_TYPE(v_oid, NULL); END IF;
  SELECT typmodin INTO v_in FROM pg_catalog.pg_type WHERE oid = v_oid;
  IF v_in IS NULL OR v_in = 0 THEN RETURN NULL; END IF;
  EXECUTE FORMAT('SELECT %s(%L::cstring[])', v_in,
                 '{' || (SELECT STRING_AGG('"' || REPLACE(TRIM(a), '"', '\"') || '"', ',') FROM REGEXP_SPLIT_TO_TABLE(v_args, ',') a) || '}')
    INTO v_typmod;
  RETURN FORMAT_TYPE(v_oid, v_typmod);
EXCEPTION WHEN OTHERS THEN
  RETURN NULL;
END;
$$;
