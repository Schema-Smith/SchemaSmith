-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Single source of "does this DataType carry a parenthesized argument, and what is it", shared by
-- extraction (SchemaSmith.GenerateTableJson.sql) and drift comparison (SchemaSmith.ModifiedTableQuench.sql)
-- so the two can never render a type differently and loop an ALTER forever. Mirrors the SQL Server
-- twin (SchemaSmith.fn_ColumnTypeArguments.sql): the VALUE inside the parens always comes from the
-- catalog; only WHICH types accept the parenthesized syntax is hand-listed, because PostgreSQL (like
-- SQL Server) exposes no catalog fact for that. Originally only 'timestamp' was covered here, so
-- 'timestamptz'/'time'/'timetz' silently lost their declared precision the same way SQL Server's
-- TIME/DATETIMEOFFSET did -- all four share one arm because they all read datetime_precision.
-- Precision is only emitted when it differs from the family default (6), matching the convention this
-- function already had for 'timestamp' -- a bare "time"/"timestamptz" column round-trips as bare.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ColumnTypeArguments"(
  p_DomainName TEXT,
  p_UdtName TEXT,
  p_CharacterMaxLength INT,
  p_NumericPrecision INT,
  p_NumericScale INT,
  p_DatetimePrecision INT
) RETURNS TEXT
  LANGUAGE sql IMMUTABLE
AS $$
  -- A domain-typed column carries no positional argument of its own -- any precision/scale is implied
  -- by the domain definition, not the column, so every arm below is guarded to skip domain types.
  SELECT CASE WHEN p_DomainName IS NOT NULL THEN ''
              WHEN UPPER(p_UdtName) LIKE '%CHAR'
              THEN CASE WHEN COALESCE(p_CharacterMaxLength, -1) = -1 THEN '' ELSE '(' || p_CharacterMaxLength || ')' END
              WHEN UPPER(p_UdtName) IN ('NUMERIC', 'DECIMAL') AND p_NumericPrecision IS NOT NULL
              THEN '(' || p_NumericPrecision || CASE WHEN COALESCE(p_NumericScale, 0) != 0 THEN ', ' || p_NumericScale ELSE '' END || ')'
              WHEN UPPER(p_UdtName) IN ('TIMESTAMP', 'TIMESTAMPTZ', 'TIME', 'TIMETZ') AND COALESCE(p_DatetimePrecision, 6) != 6
              THEN '(' || p_DatetimePrecision || ')'
              -- BIT and BIT VARYING carry a LENGTH, in character_maximum_length like the char family, and
              -- this function dropped it on the floor: not one of the arms above matches ('BIT' does not
              -- end in CHAR). Two consequences, and the second is the serious one. (1) A column declared
              -- bit(8) compared against a catalog rendering of bare 'bit' and re-altered on every deploy.
              -- (2) EXTRACTION reads this same function, so a bit(8) column extracted as 'bit' and
              -- redeploying that package built bit(1) -- a silent truncation to one bit, not a diff.
              --
              -- The two types need different rules because their defaults differ. Bare 'bit varying' is
              -- UNLIMITED and 'bit varying(1)' holds at most one bit -- genuinely different types -- so any
              -- reported length is emitted. Bare 'bit' IS 'bit(1)' -- the same type spelled two ways, and
              -- the catalog reports 1 for both -- so a length of 1 is emitted as nothing, matching the
              -- convention the datetime arm above already uses for its family default. The authored side
              -- folds 'BIT(1)' to 'BIT' to meet it (ParseTableJsonIntoTempTables), so both spellings
              -- converge on the same rendering from either direction.
              WHEN UPPER(p_UdtName) = 'VARBIT' AND p_CharacterMaxLength IS NOT NULL
              THEN '(' || p_CharacterMaxLength || ')'
              WHEN UPPER(p_UdtName) = 'BIT' AND COALESCE(p_CharacterMaxLength, 1) != 1
              THEN '(' || p_CharacterMaxLength || ')'
              ELSE '' END
$$;
