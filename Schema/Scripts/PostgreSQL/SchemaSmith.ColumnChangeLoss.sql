-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- H3. What ALTER COLUMN ... SET DATA TYPE (no USING, which is how a deploy changes a type) does to the values already in the
-- column: 'none' (every value survives as it was), 'fails' (a value that does not fit, or a pair with no assignment cast,
-- stops the statement, so nothing is lost), or 'loses' (values are rounded or cut without an error). Probed on 17 with data
-- that does not fit (H3 P2): a scale or fractional-second cut rounds, a numeric or float into an integer rounds, a timestamp
-- into a date drops the time, json into jsonb keeps one of each duplicate key -- all silently; a length cut, an integer
-- narrowing, a precision overflow and a text-to-number change raise an error. A pair this does not know is 'loses'.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ColumnChangeLoss"(p_From TEXT, p_To TEXT)
  RETURNS TEXT
  LANGUAGE plpgsql IMMUTABLE
AS $$
DECLARE
  f TEXT; t TEXT; fb TEXT; tb TEXT; fa TEXT; ta TEXT;
  f1 INT; f2 INT; t1 INT; t2 INT;
  fr INT; tr INT; fdig INT; tdig INT;
  numeric_types TEXT[] := ARRAY['int2', 'int4', 'int8', 'numeric', 'float4', 'float8'];
  text_types TEXT[] := ARRAY['varchar', 'char', 'text'];
  time_types TEXT[] := ARRAY['date', 'timestamp', 'timestamptz', 'time', 'timetz'];
BEGIN
  -- One spelling per type: lower case, the SQL-standard multi-word names folded, a COLLATE dropped.
  f := LOWER(TRIM(COALESCE(p_From, '')));
  t := LOWER(TRIM(COALESCE(p_To, '')));
  f := REGEXP_REPLACE(f, '\s+collate\s+.*$', '');
  t := REGEXP_REPLACE(t, '\s+collate\s+.*$', '');
  f := REGEXP_REPLACE(f, '^pg_catalog\.', '');
  t := REGEXP_REPLACE(t, '^pg_catalog\.', '');
  f := REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(f,
         '^character varying', 'varchar'), '^character', 'char'), '^double precision', 'float8'),
         '^timestamp(\(\d+\))? with time zone', 'timestamptz\1'), '^timestamp(\(\d+\))? without time zone', 'timestamp\1'),
         '^time(\(\d+\))? with(out)? time zone', 'time\1');
  t := REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(REGEXP_REPLACE(t,
         '^character varying', 'varchar'), '^character', 'char'), '^double precision', 'float8'),
         '^timestamp(\(\d+\))? with time zone', 'timestamptz\1'), '^timestamp(\(\d+\))? without time zone', 'timestamp\1'),
         '^time(\(\d+\))? with(out)? time zone', 'time\1');
  IF f = t THEN RETURN 'none'; END IF;
  -- An interval with a field restriction or a precision truncates; to a plain interval nothing changes.
  IF f LIKE 'interval%' AND t LIKE 'interval%' THEN RETURN CASE WHEN t = 'interval' THEN 'none' ELSE 'loses' END; END IF;

  -- A schema-qualified name (a domain, an enum) is kept whole: cut at the dot, two types in one schema compared equal.
  fb := SUBSTRING(f FROM '^[a-z0-9_".]+');
  tb := SUBSTRING(t FROM '^[a-z0-9_".]+');
  fa := SUBSTRING(f FROM '\(([^)]*)\)');
  ta := SUBSTRING(t FROM '\(([^)]*)\)');
  fb := CASE fb WHEN 'integer' THEN 'int4' WHEN 'int' THEN 'int4' WHEN 'smallint' THEN 'int2' WHEN 'bigint' THEN 'int8'
                WHEN 'real' THEN 'float4' WHEN 'float' THEN 'float8' WHEN 'decimal' THEN 'numeric' WHEN 'bool' THEN 'boolean'
                WHEN 'bpchar' THEN 'char' ELSE fb END;
  tb := CASE tb WHEN 'integer' THEN 'int4' WHEN 'int' THEN 'int4' WHEN 'smallint' THEN 'int2' WHEN 'bigint' THEN 'int8'
                WHEN 'real' THEN 'float4' WHEN 'float' THEN 'float8' WHEN 'decimal' THEN 'numeric' WHEN 'bool' THEN 'boolean'
                WHEN 'bpchar' THEN 'char' ELSE tb END;
  -- PostGIS refuses a row whose subtype or SRID does not match the new modifier.
  IF fb = tb AND fb IN ('geometry', 'geography') THEN RETURN 'fails'; END IF;
  -- Only numeric arguments are lengths, precisions and scales; geometry(point, 4326) has a word in them.
  f1 := SUBSTRING(TRIM(SPLIT_PART(COALESCE(fa, ''), ',', 1)) FROM '^\d+$')::INT;
  f2 := SUBSTRING(TRIM(SPLIT_PART(COALESCE(fa, ''), ',', 2)) FROM '^\d+$')::INT;
  t1 := SUBSTRING(TRIM(SPLIT_PART(COALESCE(ta, ''), ',', 1)) FROM '^\d+$')::INT;
  t2 := SUBSTRING(TRIM(SPLIT_PART(COALESCE(ta, ''), ',', 2)) FROM '^\d+$')::INT;
  IF fb = 'char' THEN f1 := COALESCE(f1, 1); END IF;
  IF tb = 'char' THEN t1 := COALESCE(t1, 1); END IF;
  IF fb IN ('timestamp', 'timestamptz', 'time', 'timetz') THEN f1 := COALESCE(f1, 6); END IF;
  IF tb IN ('timestamp', 'timestamptz', 'time', 'timetz') THEN t1 := COALESCE(t1, 6); END IF;
  IF fb = 'numeric' AND f1 IS NOT NULL THEN f2 := COALESCE(f2, 0); END IF;
  IF tb = 'numeric' AND t1 IS NOT NULL THEN t2 := COALESCE(t2, 0); END IF;
  IF fb = tb AND f1 IS NOT DISTINCT FROM t1 AND f2 IS NOT DISTINCT FROM t2 THEN RETURN 'none'; END IF;

  fr := CASE fb WHEN 'int2' THEN 1 WHEN 'int4' THEN 2 WHEN 'int8' THEN 3 END;
  tr := CASE tb WHEN 'int2' THEN 1 WHEN 'int4' THEN 2 WHEN 'int8' THEN 3 END;
  fdig := CASE fb WHEN 'int2' THEN 5 WHEN 'int4' THEN 10 WHEN 'int8' THEN 19 END;
  tdig := CASE tb WHEN 'int2' THEN 5 WHEN 'int4' THEN 10 WHEN 'int8' THEN 19 END;

  -- Integers.
  IF fr IS NOT NULL AND tr IS NOT NULL THEN RETURN CASE WHEN tr >= fr THEN 'none' ELSE 'fails' END; END IF;
  IF fr IS NOT NULL AND tb = 'numeric' THEN RETURN CASE WHEN t1 IS NULL OR t1 - t2 >= fdig THEN 'none' ELSE 'fails' END; END IF;
  IF fr IS NOT NULL AND tb = 'float8' THEN RETURN CASE WHEN fb = 'int8' THEN 'loses' ELSE 'none' END; END IF;
  IF fr IS NOT NULL AND tb = 'float4' THEN RETURN CASE WHEN fb = 'int2' THEN 'none' ELSE 'loses' END; END IF;

  -- Numeric. An unconstrained numeric into a constrained one can be cut to its scale.
  IF fb = 'numeric' AND tb = 'numeric' THEN
    IF t1 IS NULL THEN RETURN 'none'; END IF;
    IF f1 IS NULL THEN RETURN 'loses'; END IF;
    RETURN CASE WHEN t2 < f2 THEN 'loses' WHEN t1 - t2 < f1 - f2 THEN 'fails' ELSE 'none' END;
  END IF;
  IF fb = 'numeric' AND tr IS NOT NULL THEN
    RETURN CASE WHEN f1 IS NULL OR f2 > 0 THEN 'loses' WHEN f1 <= tdig THEN 'none' ELSE 'fails' END;
  END IF;
  IF fb = 'numeric' AND tb = 'float8' THEN RETURN CASE WHEN f1 IS NOT NULL AND f1 <= 15 THEN 'none' ELSE 'loses' END; END IF;
  IF fb = 'numeric' AND tb = 'float4' THEN RETURN CASE WHEN f1 IS NOT NULL AND f1 <= 6 THEN 'none' ELSE 'loses' END; END IF;

  -- Floating point: into anything exact it rounds; float8 into float4 loses digits.
  IF fb = 'float4' AND tb = 'float8' THEN RETURN 'none'; END IF;
  IF fb IN ('float4', 'float8') AND (tb = ANY(numeric_types)) THEN RETURN 'loses'; END IF;

  -- Strings. A shorter length fails on a value that does not fit; text into anything but text has no assignment cast.
  IF fb = ANY(text_types) AND tb = ANY(text_types) THEN
    RETURN CASE WHEN tb = 'text' OR (t1 IS NULL AND tb = 'varchar') THEN 'none'
                WHEN fb = 'text' OR (f1 IS NULL AND fb = 'varchar') OR t1 < f1 THEN 'fails'
                ELSE 'none' END;
  END IF;
  IF fb = ANY(text_types) THEN RETURN 'fails'; END IF;
  -- Anything into text keeps its text form; into a varchar or char too short for it, it fails.
  IF tb = 'text' OR (tb = 'varchar' AND t1 IS NULL) THEN RETURN CASE WHEN fb = 'bytea' THEN 'loses' ELSE 'none' END; END IF;
  IF tb IN ('varchar', 'char') AND (fr IS NOT NULL OR fb IN ('numeric', 'uuid', 'date', 'timestamp', 'timestamptz', 'boolean')) THEN RETURN 'fails'; END IF;

  -- Dates and times.
  IF fb = 'date' AND tb IN ('timestamp', 'timestamptz') THEN RETURN 'none'; END IF;
  IF fb IN ('timestamp', 'timestamptz') AND tb = 'date' THEN RETURN 'loses'; END IF;
  IF fb = 'timestamp' AND tb = 'timestamptz' THEN RETURN CASE WHEN t1 >= f1 THEN 'none' ELSE 'loses' END; END IF;
  IF fb = 'timestamptz' AND tb = 'timestamp' THEN RETURN 'loses'; END IF;
  IF fb = tb AND fb IN ('timestamp', 'timestamptz', 'time', 'timetz') THEN RETURN CASE WHEN t1 < f1 THEN 'loses' ELSE 'none' END; END IF;
  IF fb IN ('timestamp', 'timestamptz') AND tb IN ('time', 'timetz') THEN RETURN 'loses'; END IF;

  IF fb = 'jsonb' AND tb = 'json' THEN RETURN 'none'; END IF;
  -- Pairs with no assignment cast fail; the ones known here.
  IF (fb = 'boolean' AND tr IS NOT NULL) OR (fr IS NOT NULL AND tb = 'boolean') THEN RETURN 'fails'; END IF;

  RETURN 'loses';
END;
$$;
