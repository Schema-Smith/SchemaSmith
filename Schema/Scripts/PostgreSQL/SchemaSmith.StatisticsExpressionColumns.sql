-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE FUNCTION "SchemaSmith"."StatisticsExpressionColumns"(p_schema TEXT, p_stxname TEXT) RETURNS TEXT[]
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_result TEXT[];
BEGIN
  -- Expression-based extended statistics are PostgreSQL 14+. Below 14 none can exist, so the result is an empty
  -- array. The expressions are read from the statistics OBJECT (pg_get_statisticsobjdef_expressions, in
  -- declared order) rather than pg_stats_ext_exprs: that view is over statistics DATA, filtered by the caller's
  -- column privileges, carries a row per inheritance flavour once analyzed, and has no defined order. Read
  -- through EXECUTE so the 14+ function is referenced only at runtime on a server that has it. Keyed on the
  -- REAL server version (a physical existence question), not the override-aware ServerVersionNum().
  IF (current_setting('server_version_num')::int / 10000) >= 14 THEN
    EXECUTE 'SELECT pg_get_statisticsobjdef_expressions(se.oid)
               FROM pg_statistic_ext se
               JOIN pg_namespace n ON n.oid = se.stxnamespace
              WHERE n.nspname = $1 AND se.stxname = $2'
      INTO v_result USING p_schema, p_stxname;
  END IF;
  RETURN COALESCE(v_result, ARRAY[]::text[]);
END $$;
