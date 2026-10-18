-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Delimits one raw (unquoted) identifier for emitted SQL: always quoted, with an embedded double quote doubled.
-- Use it for every identifier new PostgreSQL emission writes. quote_ident is not a substitute: it leaves a name bare
-- when the name does not need quoting, which changes the text of every statement SchemaSmith emits, while this keeps
-- it byte-identical for any name without a quote in it. NULL in, NULL out, like every other concatenation.
CREATE OR REPLACE FUNCTION "SchemaSmith"."QuoteName"(p_Name TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
    STRICT
AS $$
  SELECT '"' || REPLACE(p_Name, '"', '""') || '"'
$$;

-- A name as extraction writes it inside a comma-separated list (IndexColumns, a key's Columns, StatisticsColumns): bare,
-- unless it holds a double quote or a comma, which a list cannot carry bare, and then quoted with its quotes doubled.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ListName"(p_Name TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
    STRICT
AS $$
  SELECT CASE WHEN p_Name LIKE '%"%' OR p_Name LIKE '%,%' THEN '"' || REPLACE(p_Name, '"', '""') || '"' ELSE p_Name END
$$;
