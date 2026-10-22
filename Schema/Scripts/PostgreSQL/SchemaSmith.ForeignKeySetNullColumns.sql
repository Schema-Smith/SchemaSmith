-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ForeignKeySetNullColumns"(p_conoid OID) RETURNS TEXT
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_result TEXT;
BEGIN
  -- pg_constraint.confdelsetcols is a PostgreSQL 15 column: the subset of a foreign key's columns an
  -- ON DELETE SET NULL (col, ...) sets, in the order declared, or empty for a plain SET NULL. Read via EXECUTE so
  -- the column is referenced only on a server that has it -- a static reference is a plan-time 42703 on an older
  -- one. Keyed on the REAL server version, as IndexNullsNotDistinct is: a physical column-existence question.
  -- Returned as the package writes it: the column names, comma-separated, no spaces; '' when there is none.
  IF p_conoid IS NULL THEN
    RETURN '';
  END IF;
  IF (current_setting('server_version_num')::int / 10000) >= 15 THEN
    EXECUTE 'SELECT STRING_AGG(a.attname, '','' ORDER BY u.idx)
               FROM pg_constraint c
               CROSS JOIN LATERAL UNNEST(c.confdelsetcols) WITH ORDINALITY AS u(element, idx)
               JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = u.element
              WHERE c.oid = $1' INTO v_result USING p_conoid;
    RETURN COALESCE(v_result, '');
  END IF;
  RETURN '';
END $$;
