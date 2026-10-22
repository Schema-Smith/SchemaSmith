-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- One reading of an index key, shared by the declared side (NormalizeIndexColumnList, QuoteIndexColumnList) and the
-- live side (IndexKeyItems, IndexKeyList). PostgreSQL's key grammar is positional:
--   expression [COLLATE collation] [opclass [(name=value, ...)]] [ASC | DESC] [NULLS FIRST | NULLS LAST]
-- so an operator class, a per-key collation and operator-class parameters live IN the key text, and both sides must
-- read them the same way or a declared "name gin_trgm_ops" never equals the live key and the index churns on every
-- deploy. The canonical form is the engine's own (pg_get_indexdef): a plain identifier unquoted, COLLATE "name" only
-- when it differs from the column's, the operator class only when it is not the default for the key's type, the
-- parameters as (name=value) with no quotes or spaces, and only the order modifiers the catalog records (DESC,
-- NULLS FIRST, DESC NULLS LAST).

-- The five parts of one key item: [expression, collation, opclass, params, mods]. Empty strings for what is absent.
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexKeyParts"(p_item TEXT) RETURNS TEXT[]
    LANGUAGE plpgsql IMMUTABLE
AS $$
DECLARE
  s TEXT := TRIM(COALESCE(p_item, ''));
  len INT;
  i INT := 1;
  ch TEXT;
  depth INT := 0;
  in_double BOOLEAN := FALSE;
  in_single BOOLEAN := FALSE;
  expr TEXT;
  rest TEXT;
  token TEXT;
  mods TEXT := '';
  coll TEXT := '';
  opclass TEXT := '';
  params TEXT := '';
  m TEXT[];
BEGIN
  IF s = '' THEN RETURN ARRAY['', '', '', '', '']; END IF;

  -- The expression runs to the first space outside quotes and parentheses: a quoted identifier, a bare one, or an
  -- expression such as lower(name) or (a + b).
  len := CHAR_LENGTH(s);
  WHILE i <= len LOOP
    ch := SUBSTR(s, i, 1);
    IF in_double THEN
      IF ch = '"' THEN in_double := FALSE; END IF;
    ELSIF in_single THEN
      IF ch = '''' THEN in_single := FALSE; END IF;
    ELSIF ch = '"' THEN in_double := TRUE;
    ELSIF ch = '''' THEN in_single := TRUE;
    ELSIF ch = '(' THEN depth := depth + 1;
    ELSIF ch = ')' THEN depth := depth - 1;
    ELSIF ch = ' ' AND depth = 0 THEN EXIT;
    END IF;
    i := i + 1;
  END LOOP;
  expr := SUBSTR(s, 1, i - 1);
  rest := TRIM(SUBSTR(s, i));

  -- Order modifiers come last; peel them off the end in either spelling.
  LOOP
    EXIT WHEN rest = '' OR NOT (rest ~* '(^|\s)(NULLS\s+FIRST|NULLS\s+LAST|ASC|DESC|WITHOUT\s+OVERLAPS)\s*$');
    token := UPPER(REGEXP_REPLACE(TRIM(SUBSTRING(rest FROM '(?i)(NULLS\s+FIRST|NULLS\s+LAST|ASC|DESC|WITHOUT\s+OVERLAPS)\s*$')), '\s+', ' ', 'g'));
    rest := RTRIM(REGEXP_REPLACE(rest, '(?i)\s*(NULLS\s+FIRST|NULLS\s+LAST|ASC|DESC|WITHOUT\s+OVERLAPS)\s*$', ''));
    mods := CASE WHEN mods = '' THEN token ELSE token || ' ' || mods END;
  END LOOP;

  -- Then an optional COLLATE, then an optional operator class with optional parameters.
  m := REGEXP_MATCH(rest, '^COLLATE\s+("(?:[^"]|"")+"|[A-Za-z_][A-Za-z0-9_$]*(?:\."?[A-Za-z_][A-Za-z0-9_$]*"?)?)\s*(.*)$', 'i');
  IF m IS NOT NULL THEN
    coll := m[1];
    rest := TRIM(m[2]);
  END IF;
  m := REGEXP_MATCH(rest, '^([A-Za-z_][A-Za-z0-9_$]*(?:\."?[A-Za-z_][A-Za-z0-9_$]*"?)?)\s*(\(.*\))?\s*$');
  IF m IS NOT NULL THEN
    opclass := m[1];
    params := COALESCE(m[2], '');
    rest := '';
  END IF;
  -- Anything still unread is kept with the expression rather than lost.
  IF rest <> '' THEN expr := expr || ' ' || rest; END IF;

  RETURN ARRAY[expr, coll, opclass, params, mods];
END $$;

-- One key item in its canonical form; p_quote true renders the DDL form (a plain identifier quoted), false the
-- comparison and extraction form (unquoted).
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexKeyCanonical"(p_item TEXT, p_quote BOOLEAN) RETURNS TEXT
    LANGUAGE plpgsql IMMUTABLE
AS $$
DECLARE
  parts TEXT[] := "SchemaSmith"."IndexKeyParts"(p_item);
  expr TEXT := parts[1];
  coll TEXT := parts[2];
  opclass TEXT := parts[3];
  params TEXT := parts[4];
  mods TEXT := parts[5];
  rendered TEXT;
BEGIN
  IF expr = '' THEN RETURN ''; END IF;
  IF expr NOT LIKE '%(%' THEN
    -- A plain identifier, bare or quoted: both sides unwrap it the same way, so a quote inside the name compares equal.
    expr := "SchemaSmith"."BareName"(expr);
    IF p_quote THEN expr := "SchemaSmith"."QuoteName"(expr); END IF;
  END IF;
  rendered := expr;
  IF coll <> '' THEN
    rendered := rendered || ' COLLATE ' || CASE WHEN coll LIKE '%.%' THEN coll ELSE "SchemaSmith"."QuoteName"("SchemaSmith"."BareName"(coll)) END;
  END IF;
  IF opclass <> '' THEN
    rendered := rendered || ' ' || opclass;
    IF params <> '' THEN
      -- (name = 'value', ...) in any spelling becomes (name=value,...), the one form both sides agree on.
      rendered := rendered || ' (' || (SELECT STRING_AGG(TRIM(SPLIT_PART(kv, '=', 1)) || '=' || TRIM(BOTH '''' FROM TRIM(SPLIT_PART(kv, '=', 2))), ',' ORDER BY ord)
                                         FROM UNNEST(STRING_TO_ARRAY(SUBSTR(params, 2, CHAR_LENGTH(params) - 2), ',')) WITH ORDINALITY AS u(kv, ord)) || ')';
    END IF;
  END IF;
  -- Defaults the catalog does not record, so the live side never shows them.
  mods := CASE mods
            WHEN 'ASC' THEN ''
            WHEN 'NULLS LAST' THEN ''
            WHEN 'ASC NULLS LAST' THEN ''
            WHEN 'ASC NULLS FIRST' THEN 'NULLS FIRST'
            WHEN 'DESC NULLS FIRST' THEN 'DESC'
            ELSE mods END;
  IF mods <> '' THEN rendered := rendered || ' ' || mods; END IF;
  RETURN rendered;
END $$;

-- The declared side with the catalog in hand: a column key's operator class is dropped when it is the access
-- method's default for the column's type, and its COLLATE when it is the column's own collation, because the engine
-- records neither and the live rendering shows neither. An expression key is taken as written.
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexKeyDeclared"(p_schema TEXT, p_table TEXT, p_access_method TEXT, p_item TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  parts TEXT[] := "SchemaSmith"."IndexKeyParts"(p_item);
  rel REGCLASS := to_regclass("SchemaSmith"."QuoteName"(p_schema) || '.' || "SchemaSmith"."QuoteName"(p_table));
  col_type OID;
  col_coll TEXT;
  item TEXT := p_item;
  is_default BOOLEAN;
BEGIN
  IF parts[1] = '' OR parts[1] LIKE '%(%' OR rel IS NULL THEN
    RETURN "SchemaSmith"."IndexKeyCanonical"(item, FALSE);
  END IF;
  SELECT a.atttypid, c.collname INTO col_type, col_coll
    FROM pg_attribute a
    LEFT JOIN pg_collation c ON c.oid = a.attcollation
   WHERE a.attrelid = rel AND a.attname = "SchemaSmith"."BareName"(parts[1]);
  IF col_type IS NULL THEN
    RETURN "SchemaSmith"."IndexKeyCanonical"(item, FALSE);
  END IF;
  IF parts[3] <> '' AND parts[4] = '' THEN
    SELECT oc.opcdefault INTO is_default
      FROM pg_opclass oc
      JOIN pg_am am ON am.oid = oc.opcmethod
     WHERE am.amname = COALESCE(NULLIF(p_access_method, ''), 'btree')
       AND oc.opcname = TRIM(BOTH '"' FROM SPLIT_PART(parts[3], '.', CASE WHEN parts[3] LIKE '%.%' THEN 2 ELSE 1 END))
       AND oc.opcintype = col_type;
    IF COALESCE(is_default, FALSE) THEN parts[3] := ''; END IF;
  END IF;
  IF parts[2] <> '' AND "SchemaSmith"."BareName"(parts[2]) = COALESCE(col_coll, '') THEN
    parts[2] := '';
  END IF;
  item := parts[1] || CASE WHEN parts[2] <> '' THEN ' COLLATE ' || parts[2] ELSE '' END
                   || CASE WHEN parts[3] <> '' THEN ' ' || parts[3] ELSE '' END
                   || CASE WHEN parts[4] <> '' THEN ' ' || parts[4] ELSE '' END
                   || CASE WHEN parts[5] <> '' THEN ' ' || parts[5] ELSE '' END;
  RETURN "SchemaSmith"."IndexKeyCanonical"(item, FALSE);
END $$;

-- The live side: a live index's key items in canonical form, read from the engine's own rendering
-- (pg_get_indexdef writes a non-default operator class, a differing COLLATE and the parameters, and nothing else),
-- one array element per key, in key order.
CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexKeyItems"(p_indexrelid OID) RETURNS TEXT[]
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  def TEXT := pg_get_indexdef(p_indexrelid);
  start_at INT;
  i INT;
  len INT;
  ch TEXT;
  depth INT := 0;
  in_double BOOLEAN := FALSE;
  in_single BOOLEAN := FALSE;
  keys TEXT;
  nkeys INT := (SELECT indnkeyatts FROM pg_index WHERE indexrelid = p_indexrelid);
BEGIN
  IF def IS NULL THEN RETURN ARRAY[]::TEXT[]; END IF;
  -- The key list is the parenthesised group after USING <method>; INCLUDE, WITH, WHERE and TABLESPACE follow it.
  start_at := POSITION(' USING ' IN def);
  IF start_at = 0 THEN RETURN ARRAY[]::TEXT[]; END IF;
  i := start_at + 7;
  len := CHAR_LENGTH(def);
  WHILE i <= len AND SUBSTR(def, i, 1) <> '(' LOOP i := i + 1; END LOOP;
  start_at := i + 1;
  i := start_at;
  depth := 1;
  WHILE i <= len LOOP
    ch := SUBSTR(def, i, 1);
    IF in_double THEN
      IF ch = '"' THEN in_double := FALSE; END IF;
    ELSIF in_single THEN
      IF ch = '''' THEN in_single := FALSE; END IF;
    ELSIF ch = '"' THEN in_double := TRUE;
    ELSIF ch = '''' THEN in_single := TRUE;
    ELSIF ch = '(' THEN depth := depth + 1;
    ELSIF ch = ')' THEN
      depth := depth - 1;
      EXIT WHEN depth = 0;
    END IF;
    i := i + 1;
  END LOOP;
  keys := SUBSTR(def, start_at, i - start_at);
  -- Only the key columns: an INCLUDE list is rendered separately by the engine, so the group holds keys alone.
  RETURN (SELECT COALESCE(ARRAY_AGG("SchemaSmith"."IndexKeyCanonical"(item, FALSE) ORDER BY ord), ARRAY[]::TEXT[])
            FROM UNNEST("SchemaSmith"."SplitTopLevelList"(keys)) WITH ORDINALITY AS u(item, ord)
           WHERE ord <= nkeys);
END $$;

CREATE OR REPLACE FUNCTION "SchemaSmith"."IndexKeyList"(p_indexrelid OID) RETURNS TEXT
    LANGUAGE sql STABLE
AS $$
  SELECT ARRAY_TO_STRING("SchemaSmith"."IndexKeyItems"(p_indexrelid), ',')
$$;
