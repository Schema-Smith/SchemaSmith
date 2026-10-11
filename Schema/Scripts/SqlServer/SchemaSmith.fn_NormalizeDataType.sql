-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_NormalizeDataType') IS NOT NULL DROP FUNCTION SchemaSmith.fn_NormalizeDataType
GO
-- Maps a declared data type onto the spelling the catalog reports back, so a package that authors a
-- SYNONYM does not read as a type change on every quench (Rule 20 parity -- PostgreSQL has had this
-- in its parse step for some time; SQL Server had only ROWVERSION, inline, in three separate places).
--
-- ONE PLACE TO ADD THE NEXT SYNONYM. The three scattered REPLACE(..., 'ROWVERSION', 'TIMESTAMP')
-- calls -- two in the JSON parse, one in the XML twin -- are folded in here. Three copies of a
-- mapping is three chances to update two of them.
--
-- MEASURED, NOT ASSUMED. Every row below is what sys.types reports for a column declared with the
-- synonym, probed on SQL Server 2022:
--
--     INTEGER            -> int          DEC                -> decimal
--     CHARACTER VARYING  -> varchar      NATIONAL CHARACTER -> nchar
--     BINARY VARYING     -> varbinary    DOUBLE PRECISION   -> float
--     ROWVERSION         -> timestamp
--
-- TWO THAT THE SURVEY EXPECTED AND THE ENGINE DISAGREED WITH, so they are deliberately absent:
-- NUMERIC stays numeric (it is NOT folded to decimal here, unlike MySQL), and SYSNAME stays sysname
-- -- it is its own type in sys.types, not an alias resolved away. Mapping either would invent a
-- difference rather than remove one.
--
-- Only the leading keyword is rewritten; the parenthesised part is carried through untouched. The
-- longer forms are tested first -- NATIONAL CHARACTER VARYING before NATIONAL CHARACTER before
-- CHARACTER VARYING before CHARACTER -- because a prefix match on the shorter one would otherwise
-- eat the longer.
CREATE FUNCTION SchemaSmith.fn_NormalizeDataType(@p_DataType NVARCHAR(MAX))
  RETURNS NVARCHAR(MAX)
AS
BEGIN
  IF @p_DataType IS NULL RETURN NULL

  DECLARE @trimmed NVARCHAR(MAX) = LTRIM(RTRIM(@p_DataType))
  DECLARE @upper NVARCHAR(MAX) = UPPER(@trimmed)
  DECLARE @t NVARCHAR(MAX)

  SET @t = CASE
    WHEN @upper = 'INTEGER' THEN 'INT'
    WHEN @upper = 'ROWVERSION' THEN 'TIMESTAMP'
    WHEN @upper = 'DOUBLE PRECISION' THEN 'FLOAT'
    WHEN @upper LIKE 'NATIONAL CHARACTER VARYING%'
         THEN 'NVARCHAR' + SUBSTRING(@trimmed, LEN('NATIONAL CHARACTER VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'NATIONAL CHARACTER%'
         THEN 'NCHAR' + SUBSTRING(@trimmed, LEN('NATIONAL CHARACTER') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'NATIONAL CHAR VARYING%'
         THEN 'NVARCHAR' + SUBSTRING(@trimmed, LEN('NATIONAL CHAR VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'NATIONAL CHAR%'
         THEN 'NCHAR' + SUBSTRING(@trimmed, LEN('NATIONAL CHAR') + 1, LEN(@trimmed))
    WHEN @upper = 'NATIONAL TEXT' THEN 'NTEXT'
    WHEN @upper LIKE 'NCHAR VARYING%'
         THEN 'NVARCHAR' + SUBSTRING(@trimmed, LEN('NCHAR VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'CHAR VARYING%'
         THEN 'VARCHAR' + SUBSTRING(@trimmed, LEN('CHAR VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'CHARACTER VARYING%'
         THEN 'VARCHAR' + SUBSTRING(@trimmed, LEN('CHARACTER VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'CHARACTER%'
         THEN 'CHAR' + SUBSTRING(@trimmed, LEN('CHARACTER') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'BINARY VARYING%'
         THEN 'VARBINARY' + SUBSTRING(@trimmed, LEN('BINARY VARYING') + 1, LEN(@trimmed))
    WHEN @upper LIKE 'DEC(%' OR @upper = 'DEC'
         THEN 'DECIMAL' + SUBSTRING(@trimmed, LEN('DEC') + 1, LEN(@trimmed))
    -- ROWVERSION inside a longer string kept its historical REPLACE semantics rather than an exact
    -- match, so that behaviour is preserved for anything not matched above.
    ELSE REPLACE(@p_DataType, 'ROWVERSION', 'TIMESTAMP')
  END

  -- A length, precision or scale left to its default is reported spelled out, so it is spelled out here: the bare
  -- character types are length 1, DECIMAL/NUMERIC are (18,0) and (p) is (p,0), and FLOAT(n) is stored as real up to
  -- n = 24 and as float above. Each is the same column in DDL, so this changes nothing the engine creates. Whatever
  -- follows the type (IDENTITY, ROWGUIDCOL) is carried through.
  DECLARE @name NVARCHAR(128) = LEFT(@t, PATINDEX('%[^A-Za-z]%', @t + ' ') - 1)
  DECLARE @rest NVARCHAR(MAX) = LTRIM(SUBSTRING(@t, LEN(@name) + 1, LEN(@t)))
  DECLARE @args NVARCHAR(MAX) = CASE WHEN LEFT(@rest, 1) = '(' AND CHARINDEX(')', @rest) > 0
                                     THEN LTRIM(RTRIM(SUBSTRING(@rest, 2, CHARINDEX(')', @rest) - 2))) END
  DECLARE @after NVARCHAR(MAX) = CASE WHEN @args IS NULL THEN @rest ELSE SUBSTRING(@rest, CHARINDEX(')', @rest) + 1, LEN(@rest)) END
  SET @after = CASE WHEN @after = '' THEN '' ELSE ' ' + LTRIM(@after) END

  RETURN CASE
    WHEN UPPER(@name) IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR', 'BINARY', 'VARBINARY') AND @args IS NULL
         THEN @name + '(1)' + @after
    WHEN UPPER(@name) IN ('DECIMAL', 'NUMERIC') AND @args IS NULL
         THEN @name + '(18, 0)' + @after
    WHEN UPPER(@name) IN ('DECIMAL', 'NUMERIC') AND @args NOT LIKE '%,%'
         THEN @name + '(' + @args + ', 0)' + @after
    WHEN UPPER(@name) = 'FLOAT' AND @args <> '' AND @args NOT LIKE '%[^0-9]%' AND LEN(@args) <= 2
         THEN CASE WHEN CONVERT(INT, @args) BETWEEN 1 AND 24 THEN 'REAL' + @after
                   WHEN CONVERT(INT, @args) BETWEEN 25 AND 53 THEN 'FLOAT' + @after
                   ELSE @t END
    ELSE @t
  END
END
GO
