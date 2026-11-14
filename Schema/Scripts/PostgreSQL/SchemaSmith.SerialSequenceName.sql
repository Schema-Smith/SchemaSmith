-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The sequence name PostgreSQL gives a serial column: the one the column already owns, or the one CREATE TABLE ...
-- SERIAL would pick -- makeObjectName (table_column_seq within 63 bytes, the longer of the two parts trimmed first,
-- a multibyte name clipped at a character) and ChooseRelationName's suffix when the name is taken (seq1, seq2, ...).
-- So a column converted to serial gets the sequence a fresh serial gets, and extraction and redeploy agree.
CREATE OR REPLACE FUNCTION "SchemaSmith"."SerialSequenceName"(p_schema TEXT, p_table TEXT, p_column TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
AS $$
DECLARE
  v_owned REGCLASS;
  v_label TEXT;
  v_name TEXT;
  v_avail INT;
  v_bytes1 INT;
  v_bytes2 INT;
  v_part1 TEXT;
  v_part2 TEXT;
  v_pass INT := 0;
BEGIN
  IF TO_REGCLASS(QUOTE_IDENT(p_schema) || '.' || QUOTE_IDENT(p_table)) IS NOT NULL
     AND EXISTS (SELECT 1 FROM pg_catalog.pg_attribute
                  WHERE attrelid = TO_REGCLASS(QUOTE_IDENT(p_schema) || '.' || QUOTE_IDENT(p_table)) AND attname = p_column AND NOT attisdropped) THEN
    v_owned := pg_get_serial_sequence(QUOTE_IDENT(p_schema) || '.' || QUOTE_IDENT(p_table), p_column)::REGCLASS;
    IF v_owned IS NOT NULL THEN
      RETURN (SELECT relname FROM pg_catalog.pg_class WHERE oid = v_owned);
    END IF;
  END IF;

  LOOP
    v_label := 'seq' || CASE WHEN v_pass = 0 THEN '' ELSE v_pass::TEXT END;
    v_avail := 63 - 2 - OCTET_LENGTH(v_label);
    v_bytes1 := OCTET_LENGTH(p_table);
    v_bytes2 := OCTET_LENGTH(p_column);
    WHILE v_bytes1 + v_bytes2 > v_avail LOOP
      IF v_bytes1 > v_bytes2 THEN v_bytes1 := v_bytes1 - 1; ELSE v_bytes2 := v_bytes2 - 1; END IF;
    END LOOP;
    -- The longest run of whole characters within each byte budget (pg_mbcliplen).
    v_part1 := p_table;
    WHILE OCTET_LENGTH(v_part1) > v_bytes1 LOOP v_part1 := LEFT(v_part1, -1); END LOOP;
    v_part2 := p_column;
    WHILE OCTET_LENGTH(v_part2) > v_bytes2 LOOP v_part2 := LEFT(v_part2, -1); END LOOP;
    v_name := v_part1 || '_' || v_part2 || '_' || v_label;
    EXIT WHEN NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class
                           WHERE relname = v_name AND relnamespace = TO_REGNAMESPACE(QUOTE_IDENT(p_schema)));
    v_pass := v_pass + 1;
  END LOOP;
  RETURN v_name;
END;
$$;
