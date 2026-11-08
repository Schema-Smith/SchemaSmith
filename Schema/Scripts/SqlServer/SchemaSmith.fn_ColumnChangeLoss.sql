-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.fn_ColumnChangeLoss', 'FN') IS NOT NULL DROP FUNCTION SchemaSmith.fn_ColumnChangeLoss
GO
CREATE FUNCTION SchemaSmith.fn_ColumnChangeLoss(@From NVARCHAR(400), @To NVARCHAR(400))
RETURNS VARCHAR(5)
AS
BEGIN
  -- H3. What ALTER COLUMN from one type to another does to the values already in the column: 'none' (every value survives
  -- as it was), 'fails' (a value that does not fit stops the statement, so nothing is lost), or 'loses' (values are rounded,
  -- cut or substituted without an error). Probed on 2022 with data that does not fit (H3 P2): a scale or fractional-second
  -- cut rounds, a decimal or float into an integer truncates, NVARCHAR into VARCHAR turns unrepresentable characters into
  -- '?', an integer into a VARCHAR too short for it becomes '*', and a date-time into a DATE drops the time -- all silently;
  -- a length cut, an integer narrowing and a text-to-number conversion of bad data raise an error. A pair this does not
  -- know is 'loses': a change missed is worse than one listed, and naming it in AcceptDestructive makes a false one cheap.
  DECLARE @f NVARCHAR(400) = UPPER(LTRIM(RTRIM(ISNULL(@From, '')))),
          @t NVARCHAR(400) = UPPER(LTRIM(RTRIM(ISNULL(@To, ''))))
  DECLARE @fb NVARCHAR(128) = LEFT(@f, PATINDEX('%[( ]%', @f + ' ') - 1),
          @tb NVARCHAR(128) = LEFT(@t, PATINDEX('%[( ]%', @t + ' ') - 1)
  DECLARE @fa NVARCHAR(100) = CASE WHEN SUBSTRING(@f, LEN(@fb) + 1, 1) = '(' THEN SUBSTRING(@f, LEN(@fb) + 2, CHARINDEX(')', @f) - LEN(@fb) - 2) ELSE '' END,
          @ta NVARCHAR(100) = CASE WHEN SUBSTRING(@t, LEN(@tb) + 1, 1) = '(' THEN SUBSTRING(@t, LEN(@tb) + 2, CHARINDEX(')', @t) - LEN(@tb) - 2) ELSE '' END
  DECLARE @f1 INT, @f2 INT, @t1 INT, @t2 INT
  -- The first and second arguments; MAX is the largest length there is.
  SET @f1 = CASE WHEN @fa = '' THEN NULL WHEN LTRIM(RTRIM(LEFT(@fa + ',', CHARINDEX(',', @fa + ',') - 1))) = 'MAX' THEN 2147483647 ELSE CASE WHEN LTRIM(RTRIM(LEFT(@fa + ',', CHARINDEX(',', @fa + ',') - 1))) <> '' AND LTRIM(RTRIM(LEFT(@fa + ',', CHARINDEX(',', @fa + ',') - 1))) NOT LIKE '%[^0-9]%' THEN CONVERT(INT, LTRIM(RTRIM(LEFT(@fa + ',', CHARINDEX(',', @fa + ',') - 1)))) END END
  SET @f2 = CASE WHEN CHARINDEX(',', @fa) > 0 THEN CASE WHEN LTRIM(RTRIM(SUBSTRING(@fa, CHARINDEX(',', @fa) + 1, 100))) <> '' AND LTRIM(RTRIM(SUBSTRING(@fa, CHARINDEX(',', @fa) + 1, 100))) NOT LIKE '%[^0-9]%' THEN CONVERT(INT, LTRIM(RTRIM(SUBSTRING(@fa, CHARINDEX(',', @fa) + 1, 100)))) END END
  SET @t1 = CASE WHEN @ta = '' THEN NULL WHEN LTRIM(RTRIM(LEFT(@ta + ',', CHARINDEX(',', @ta + ',') - 1))) = 'MAX' THEN 2147483647 ELSE CASE WHEN LTRIM(RTRIM(LEFT(@ta + ',', CHARINDEX(',', @ta + ',') - 1))) <> '' AND LTRIM(RTRIM(LEFT(@ta + ',', CHARINDEX(',', @ta + ',') - 1))) NOT LIKE '%[^0-9]%' THEN CONVERT(INT, LTRIM(RTRIM(LEFT(@ta + ',', CHARINDEX(',', @ta + ',') - 1)))) END END
  SET @t2 = CASE WHEN CHARINDEX(',', @ta) > 0 THEN CASE WHEN LTRIM(RTRIM(SUBSTRING(@ta, CHARINDEX(',', @ta) + 1, 100))) <> '' AND LTRIM(RTRIM(SUBSTRING(@ta, CHARINDEX(',', @ta) + 1, 100))) NOT LIKE '%[^0-9]%' THEN CONVERT(INT, LTRIM(RTRIM(SUBSTRING(@ta, CHARINDEX(',', @ta) + 1, 100)))) END END

  -- One spelling per type, and each type's default arguments.
  SET @fb = CASE @fb WHEN 'NUMERIC' THEN 'DECIMAL' WHEN 'DEC' THEN 'DECIMAL' WHEN 'INTEGER' THEN 'INT' WHEN 'CHARACTER' THEN 'CHAR'
                     WHEN 'FLOAT' THEN CASE WHEN ISNULL(@f1, 53) <= 24 THEN 'REAL' ELSE 'FLOAT' END ELSE @fb END
  SET @tb = CASE @tb WHEN 'NUMERIC' THEN 'DECIMAL' WHEN 'DEC' THEN 'DECIMAL' WHEN 'INTEGER' THEN 'INT' WHEN 'CHARACTER' THEN 'CHAR'
                     WHEN 'FLOAT' THEN CASE WHEN ISNULL(@t1, 53) <= 24 THEN 'REAL' ELSE 'FLOAT' END ELSE @tb END
  IF @fb = 'DECIMAL' SELECT @f1 = ISNULL(@f1, 18), @f2 = ISNULL(@f2, 0)
  IF @tb = 'DECIMAL' SELECT @t1 = ISNULL(@t1, 18), @t2 = ISNULL(@t2, 0)
  IF @fb IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET') SET @f1 = ISNULL(@f1, 7)
  IF @tb IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET') SET @t1 = ISNULL(@t1, 7)
  IF @fb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR', 'BINARY', 'VARBINARY') SET @f1 = ISNULL(@f1, 1)
  IF @tb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR', 'BINARY', 'VARBINARY') SET @t1 = ISNULL(@t1, 1)
  IF @fb IN ('TEXT') SELECT @fb = 'VARCHAR', @f1 = 2147483647
  IF @fb IN ('NTEXT') SELECT @fb = 'NVARCHAR', @f1 = 2147483647
  IF @fb = 'IMAGE' SELECT @fb = 'VARBINARY', @f1 = 2147483647
  IF @tb IN ('TEXT') SELECT @tb = 'VARCHAR', @t1 = 2147483647
  IF @tb IN ('NTEXT') SELECT @tb = 'NVARCHAR', @t1 = 2147483647
  IF @tb = 'IMAGE' SELECT @tb = 'VARBINARY', @t1 = 2147483647
  IF @fb = 'SYSNAME' SELECT @fb = 'NVARCHAR', @f1 = 128
  IF @tb = 'SYSNAME' SELECT @tb = 'NVARCHAR', @t1 = 128
  IF @fb = 'MONEY' SELECT @fb = 'DECIMAL', @f1 = 19, @f2 = 4
  IF @fb = 'SMALLMONEY' SELECT @fb = 'DECIMAL', @f1 = 10, @f2 = 4
  IF @tb = 'MONEY' SELECT @tb = 'DECIMAL', @t1 = 19, @t2 = 4
  IF @tb = 'SMALLMONEY' SELECT @tb = 'DECIMAL', @t1 = 10, @t2 = 4

  IF @fb = @tb AND ISNULL(@f1, -1) = ISNULL(@t1, -1) AND ISNULL(@f2, -1) = ISNULL(@t2, -1) RETURN 'none'

  -- Digits an integer type can hold, for a decimal or a string it moves into.
  DECLARE @fDigits INT = CASE @fb WHEN 'BIT' THEN 1 WHEN 'TINYINT' THEN 3 WHEN 'SMALLINT' THEN 5 WHEN 'INT' THEN 10 WHEN 'BIGINT' THEN 19 END,
          @tDigits INT = CASE @tb WHEN 'BIT' THEN 1 WHEN 'TINYINT' THEN 3 WHEN 'SMALLINT' THEN 5 WHEN 'INT' THEN 10 WHEN 'BIGINT' THEN 19 END
  DECLARE @fRank INT = CASE @fb WHEN 'BIT' THEN 0 WHEN 'TINYINT' THEN 1 WHEN 'SMALLINT' THEN 2 WHEN 'INT' THEN 3 WHEN 'BIGINT' THEN 4 END,
          @tRank INT = CASE @tb WHEN 'BIT' THEN 0 WHEN 'TINYINT' THEN 1 WHEN 'SMALLINT' THEN 2 WHEN 'INT' THEN 3 WHEN 'BIGINT' THEN 4 END

  -- Integers. Into BIT every non-zero value becomes 1; a narrower integer overflows on a value that does not fit.
  IF @fRank IS NOT NULL AND @tRank IS NOT NULL
    RETURN CASE WHEN @tb = 'BIT' THEN 'loses' WHEN @tRank >= @fRank THEN 'none' WHEN @fb = 'BIT' THEN 'none' ELSE 'fails' END
  IF @fRank IS NOT NULL AND @tb = 'DECIMAL' RETURN CASE WHEN @t1 - @t2 >= @fDigits THEN 'none' ELSE 'fails' END
  IF @fRank IS NOT NULL AND @tb = 'FLOAT' RETURN CASE WHEN @fb = 'BIGINT' THEN 'loses' ELSE 'none' END
  IF @fRank IS NOT NULL AND @tb = 'REAL' RETURN CASE WHEN @fb IN ('BIT', 'TINYINT', 'SMALLINT') THEN 'none' ELSE 'loses' END
  -- A number into a string too short for it becomes '*' (integers) or fails (decimals): both read as loss, the safe side.
  IF @fRank IS NOT NULL AND @tb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR') RETURN CASE WHEN @t1 >= @fDigits + 1 THEN 'none' ELSE 'loses' END

  -- Decimals. A smaller scale rounds; fewer whole digits overflows.
  IF @fb = 'DECIMAL' AND @tb = 'DECIMAL'
    RETURN CASE WHEN @t2 < @f2 THEN 'loses' WHEN @t1 - @t2 < @f1 - @f2 THEN 'fails' ELSE 'none' END
  IF @fb = 'DECIMAL' AND @tRank IS NOT NULL
    RETURN CASE WHEN @tb = 'BIT' OR @f2 > 0 THEN 'loses' WHEN @f1 <= @tDigits THEN 'none' ELSE 'fails' END
  IF @fb = 'DECIMAL' AND @tb = 'FLOAT' RETURN CASE WHEN @f1 <= 15 THEN 'none' ELSE 'loses' END
  IF @fb = 'DECIMAL' AND @tb = 'REAL' RETURN CASE WHEN @f1 <= 6 THEN 'none' ELSE 'loses' END
  IF @fb = 'DECIMAL' AND @tb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR') RETURN CASE WHEN @t1 >= @f1 + 2 THEN 'none' ELSE 'fails' END

  -- Floating point: into anything exact it rounds; FLOAT into REAL loses digits.
  IF @fb = 'REAL' AND @tb = 'FLOAT' RETURN 'none'
  IF @fb IN ('REAL', 'FLOAT') RETURN 'loses'

  -- Strings. Into a non-Unicode type, a character the code page lacks becomes '?'. A shorter length fails on a value that
  -- does not fit. CHAR pads, which is not a loss.
  IF @fb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR') AND @tb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR')
    RETURN CASE WHEN @fb IN ('NCHAR', 'NVARCHAR') AND @tb IN ('CHAR', 'VARCHAR') THEN 'loses'
                WHEN @t1 < @f1 THEN 'fails' ELSE 'none' END
  -- Text into a number or a date fails on a value that does not convert. One that does can still lose part of itself without
  -- an error: '1.555' into DECIMAL(10,2) rounds, a time of day into DATE is dropped, fractional seconds beyond the target's
  -- round. Only an integer, a UNIQUEIDENTIFIER, and the 7-digit DATETIME2 / DATETIMEOFFSET take what converts whole.
  IF @fb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR')
     AND (@tRank IS NOT NULL OR @tb IN ('DECIMAL', 'DATE', 'DATETIME', 'DATETIME2', 'SMALLDATETIME', 'DATETIMEOFFSET', 'TIME', 'UNIQUEIDENTIFIER'))
    RETURN CASE WHEN @tb = 'BIT' THEN 'loses'
                WHEN @tRank IS NOT NULL OR @tb = 'UNIQUEIDENTIFIER' THEN 'fails'
                WHEN @tb IN ('DATETIME2', 'DATETIMEOFFSET') AND @t1 >= 7 THEN 'fails'
                ELSE 'loses' END
  IF @fb IN ('BINARY', 'VARBINARY') AND @tb IN ('BINARY', 'VARBINARY') RETURN CASE WHEN @t1 < @f1 THEN 'fails' ELSE 'none' END
  IF @fb = 'UNIQUEIDENTIFIER' AND @tb IN ('CHAR', 'VARCHAR', 'NCHAR', 'NVARCHAR') RETURN CASE WHEN @t1 >= 36 THEN 'none' ELSE 'fails' END

  -- Dates and times. Fewer fractional digits round; a DATE drops the time, a TIME the date, a type without an offset the
  -- offset. DATETIME keeps 3 digits in steps of 1/300 s, SMALLDATETIME none.
  IF @fb = 'DATE' AND @tb IN ('DATETIME', 'DATETIME2', 'SMALLDATETIME', 'DATETIMEOFFSET') RETURN 'none'
  IF @tb = 'DATE' AND @fb IN ('DATETIME', 'DATETIME2', 'SMALLDATETIME', 'DATETIMEOFFSET') RETURN 'loses'
  IF @fb = 'DATETIMEOFFSET' AND @tb <> 'DATETIMEOFFSET' RETURN 'loses'
  IF @fb = @tb AND @fb IN ('DATETIME2', 'TIME', 'DATETIMEOFFSET') RETURN CASE WHEN @t1 < @f1 THEN 'loses' ELSE 'none' END
  IF @fb = 'DATETIME' AND @tb = 'DATETIME2' RETURN CASE WHEN @t1 >= 3 THEN 'none' ELSE 'loses' END
  IF @fb = 'DATETIME' AND @tb = 'DATETIMEOFFSET' RETURN CASE WHEN @t1 >= 3 THEN 'none' ELSE 'loses' END
  IF @fb = 'SMALLDATETIME' AND @tb IN ('DATETIME', 'DATETIME2', 'DATETIMEOFFSET') RETURN 'none'
  IF @fb = 'DATETIME2' AND @tb = 'DATETIMEOFFSET' RETURN CASE WHEN @t1 >= @f1 THEN 'none' ELSE 'loses' END

  RETURN 'loses'
END
