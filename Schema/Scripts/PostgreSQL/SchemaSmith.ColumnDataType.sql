-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A column's DataType as SchemaSmith writes it, from one information_schema.columns row and its pg_attribute type.
-- Extraction (GenerateTableJson) and the drift compare (ModifiedTableQuench) both call this, so the two cannot render
-- a type differently: a difference is either a type that is lost on extraction or an ALTER repeated on every deploy.
-- The spelling is information_schema's udt_name (varchar, int4, timestamp), with arguments from ColumnTypeArguments;
-- the arms before the last are the types that spelling cannot carry:
--   - an array keeps its element schema (x.e[]) and typmod (numeric(10,2)[]);
--   - a type outside pg_catalog keeps its typmod (geometry(Point,4326), vector(1536)), which only format_type has;
--   - interval keeps its precision and fields (interval(3), interval year to month), likewise;
--   - "char", the one-byte internal type, is not char, which means character(1);
--   - a negative numeric scale (PostgreSQL 15+) is read signed: information_schema reports it as an unsigned
--     11-bit value, so numeric(4,-2) came back as numeric(4,2046), which no server accepts.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ColumnDataType"(
  p_DataType TEXT,
  p_UdtSchema TEXT,
  p_UdtName TEXT,
  p_DomainSchema TEXT,
  p_DomainName TEXT,
  p_CharacterMaxLength INT,
  p_NumericPrecision INT,
  p_NumericScale INT,
  p_DatetimePrecision INT,
  p_TypeId OID,
  p_TypeMod INT
) RETURNS TEXT
  LANGUAGE sql STABLE
AS $$
  SELECT CASE
           WHEN p_DataType = 'ARRAY'
           THEN CASE WHEN p_UdtSchema != 'pg_catalog'
                     THEN QUOTE_IDENT(p_UdtSchema) || '.' || QUOTE_IDENT(REGEXP_REPLACE(p_UdtName, '^_', ''))
                     ELSE REGEXP_REPLACE(p_UdtName, '^_', '') END
                || COALESCE(SUBSTRING(format_type(p_TypeId, p_TypeMod) FROM '\(.*\)'), '') || '[]'
           WHEN p_DomainName IS NOT NULL
           THEN CASE WHEN p_DomainSchema != 'pg_catalog' THEN '"' || p_DomainSchema || '".' ELSE '' END || '"' || p_DomainName || '"'
           WHEN p_UdtSchema != 'pg_catalog'
           THEN QUOTE_IDENT(p_UdtSchema) || '.' || QUOTE_IDENT(p_UdtName)
                || COALESCE(SUBSTRING(format_type(p_TypeId, p_TypeMod) FROM '\(.*\)'), '')
           WHEN p_UdtName = 'interval' THEN format_type(p_TypeId, p_TypeMod)
           WHEN p_UdtName = 'char' THEN '"char"'
           ELSE REGEXP_REPLACE(p_UdtName, 'bpchar', 'CHAR', 'i')
                || "SchemaSmith"."ColumnTypeArguments"(NULL, p_UdtName, p_CharacterMaxLength, p_NumericPrecision,
                     CASE WHEN p_NumericScale > 1000 THEN p_NumericScale - 2048 ELSE p_NumericScale END,
                     p_DatetimePrecision)
         END
$$;

-- A column's Collation as SchemaSmith writes it. One outside pg_catalog is schema-qualified and written already
-- delimited ("x"."c"), because a bare name may itself contain a dot (en_US.utf8) and so cannot be split on one.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ColumnCollation"(p_CollationSchema TEXT, p_CollationName TEXT)
  RETURNS TEXT
  LANGUAGE sql IMMUTABLE
AS $$
  SELECT CASE WHEN p_CollationSchema IS NULL OR p_CollationSchema = 'pg_catalog' THEN p_CollationName
              ELSE "SchemaSmith"."QuoteName"(p_CollationSchema) || '.' || "SchemaSmith"."QuoteName"(p_CollationName) END
$$;

-- The COLLATE clause for a declared Collation: empty for none, the text as written when it is already delimited
-- (the qualified form above), and otherwise the name delimited whole, as it always was.
CREATE OR REPLACE FUNCTION "SchemaSmith"."CollateClause"(p_Collation TEXT)
  RETURNS TEXT
  LANGUAGE sql IMMUTABLE
AS $$
  SELECT CASE WHEN COALESCE(p_Collation, '') = '' THEN ''
              WHEN LEFT(p_Collation, 1) = '"' THEN ' COLLATE ' || p_Collation
              ELSE ' COLLATE ' || "SchemaSmith"."QuoteName"(p_Collation) END
$$;
