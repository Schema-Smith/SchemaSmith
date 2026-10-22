-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Single source of "does this DataType carry a parenthesized argument, and what is it", shared by
-- extraction (SchemaSmith.GenerateTableJson.sql / .GenerateTableXml.sql) and drift comparison
-- (SchemaSmith.ModifiedTableQuench.sql) so the two can never render a type differently and loop an
-- ALTER forever (the TIME(3)/DATETIMEOFFSET(3) regression: both sites hand-carried a near-identical
-- CASE, and TIME/DATETIMEOFFSET were simply missing from one arm).
--
-- Every VALUE inside the parens is read from the catalog (never a literal). The one thing this
-- function still hand-lists is WHICH types accept parenthesized DDL syntax at all -- SQL Server has
-- no catalog-queryable fact for that (it's parser grammar, not metadata), so it cannot be derived.
-- Types that share a source column share one arm: TIME/DATETIME2/DATETIMEOFFSET are all read from
-- DATETIME_PRECISION, so a future fractional-seconds-precision type only needs its name added to
-- that one IN-list, not a new arm. Precision is always emitted for this family, matching DATETIME2's
-- pre-existing behavior and the canonicalization ParseTableJsonIntoTempTables.sql already applies to
-- a bare-declared TIME/DATETIME2/DATETIMEOFFSET (it appends the default "(7)" to the JSON-declared
-- side specifically so it matches what this function renders for the live column) -- suppressing the
-- parens here for a default-precision column would silently break that half of the round trip.
IF OBJECT_ID('SchemaSmith.fn_ColumnTypeArguments') IS NOT NULL DROP FUNCTION SchemaSmith.fn_ColumnTypeArguments
GO
CREATE FUNCTION SchemaSmith.fn_ColumnTypeArguments
(
  @p_UserType NVARCHAR(128),
  @p_CharacterMaxLength INT,
  @p_NumericPrecision INT,
  @p_NumericScale INT,
  @p_DateTimePrecision INT,
  @p_XmlSchemaCollection NVARCHAR(400),
  @p_IsRowGuidCol BIT
)
RETURNS NVARCHAR(450)
AS
BEGIN
  -- UPPER because sys.types names are lower case and a case-sensitive database compares them as written. The names
  -- are exact: an alias type (dbo.ShortChar) carries its own width, and SQL Server refuses one stated with it.
  DECLARE @type NVARCHAR(128) = UPPER(@p_UserType)
  RETURN CASE WHEN @type IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR', 'BINARY', 'VARBINARY')
              THEN '(' + CASE WHEN @p_CharacterMaxLength = -1 THEN 'MAX' ELSE CONVERT(NVARCHAR(20), @p_CharacterMaxLength) END + ')'
              WHEN @type IN ('NUMERIC', 'DECIMAL')
              THEN  '(' + CONVERT(NVARCHAR(20), @p_NumericPrecision) + ', ' + CONVERT(NVARCHAR(20), @p_NumericScale) + ')'
              WHEN @type IN ('TIME', 'DATETIME2', 'DATETIMEOFFSET')
              THEN  '(' + CONVERT(NVARCHAR(20), @p_DateTimePrecision) + ')'
              WHEN @type = 'XML' AND @p_XmlSchemaCollection IS NOT NULL
              THEN  '(' + @p_XmlSchemaCollection + ')'
              -- VECTOR(n[, float16]) (2025): the catalog stores the byte length, 8 + n * 4 for float32 and 8 + n * 2
              -- for float16, and marks float16 with scale 1. INFORMATION_SCHEMA reports no scale for the type, so the
              -- callers pass sys.columns.scale in its place.
              WHEN @type = 'VECTOR'
              THEN  '(' + CONVERT(NVARCHAR(20), (@p_CharacterMaxLength - 8) / CASE WHEN @p_NumericScale = 1 THEN 2 ELSE 4 END) + CASE WHEN @p_NumericScale = 1 THEN ', float16' ELSE '' END + ')'
              WHEN @type = 'UNIQUEIDENTIFIER' AND @p_IsRowGuidCol = 1
              THEN  ' ROWGUIDCOL'
              ELSE '' END
END

