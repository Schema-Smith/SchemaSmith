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
