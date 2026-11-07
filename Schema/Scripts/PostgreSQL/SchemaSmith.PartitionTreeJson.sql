-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A partitioned table's declared partitioning, as the package writes it: the key split into method and expression, and
-- each partition's name, bound (as \d+ shows it), tablespace and, when it is partitioned again, the next level. NULL for
-- a table that is not partitioned. Under UTC, so a timestamp with time zone bound is written the way the deploy reads it.
-- DEFAULT last at each level, then by name: the order is not load-bearing, and name order is byte-stable.
CREATE OR REPLACE FUNCTION "SchemaSmith"."PartitionTreeJson"(p_Table OID) RETURNS JSON
    LANGUAGE plpgsql STABLE
  SET "TimeZone" = 'UTC'
AS $$
DECLARE
  v_key TEXT;
BEGIN
  IF (SELECT relkind FROM pg_catalog.pg_class WHERE oid = p_Table) IS DISTINCT FROM 'p' THEN RETURN NULL; END IF;
  v_key := pg_catalog.pg_get_partkeydef(p_Table);
  RETURN JSON_BUILD_OBJECT(
    'Method', SPLIT_PART(v_key, ' ', 1),
    'Expression', SUBSTRING(v_key FROM POSITION('(' IN v_key) + 1 FOR LENGTH(v_key) - POSITION('(' IN v_key) - 1),
    'Partitions', COALESCE((SELECT JSON_AGG(JSON_STRIP_NULLS(JSON_BUILD_OBJECT(
                                      'Name', c.relname,
                                      'Bound', pg_catalog.pg_get_expr(c.relpartbound, c.oid),
                                      'Tablespace', ts.spcname,
                                      'Partitioning', "SchemaSmith"."PartitionTreeJson"(c.oid)))
                                    ORDER BY pg_catalog.pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT', c.relname)
                              FROM pg_catalog.pg_inherits ih
                              JOIN pg_catalog.pg_class c ON c.oid = ih.inhrelid AND c.relkind IN ('r', 'p')
                              LEFT JOIN pg_catalog.pg_tablespace ts ON ts.oid = c.reltablespace
                             WHERE ih.inhparent = p_Table), '[]'::JSON));
END $$;
