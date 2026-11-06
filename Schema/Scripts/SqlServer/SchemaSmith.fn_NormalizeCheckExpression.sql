-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.fn_NormalizeCheckExpression') IS NOT NULL
  DROP FUNCTION SchemaSmith.fn_NormalizeCheckExpression
GO

-- Folds a check-constraint expression to the form SQL Server itself stores, so a declared expression can be
-- compared with the catalog's canonical rendering of the same thing.
--
-- SQL Server rewrites what you write: [RetentionDays] <= 365 is stored as ([RetentionDays]<=(365)) -- outer
-- parens, parens around the literal, spaces around operators removed. None of that survived a text
-- comparison, so a constraint was dropped and re-created on EVERY deploy. That break exits 0 and logs
-- success, so nothing surfaces it; the user just sees the constraint churn forever.
--
-- Deliberately narrow. It undoes only the three things the engine adds, and never removes a parenthesis that
-- groups an expression -- over-normalizing would make two genuinely different constraints compare equal and
-- suppress a re-create that was needed, which is a worse failure than the churn it fixes.
CREATE FUNCTION SchemaSmith.fn_NormalizeCheckExpression(@p_Expression NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
AS
BEGIN
  IF @p_Expression IS NULL RETURN NULL

  DECLARE @v_Result NVARCHAR(MAX) = SchemaSmith.fn_StripParenWrapping(LTRIM(RTRIM(@p_Expression)))

  -- Both folds below apply outside string literals only. Applied to the whole text, IN ('A, B') read as IN ('A,B') and
  -- '(5)' as '5', so a changed literal compared equal and the constraint was never re-created: a missed change.
  DECLARE @v_Out NVARCHAR(MAX) = N'', @v_Segment NVARCHAR(MAX), @v_Previous NVARCHAR(MAX)
  DECLARE @v_At INT = 1, @v_Len INT = DATALENGTH(@v_Result) / 2, @v_Quote INT, @v_End INT
  WHILE @v_At <= @v_Len
  BEGIN
    SET @v_Quote = CHARINDEX('''', @v_Result, @v_At)
    SET @v_Segment = SUBSTRING(@v_Result, @v_At, CASE WHEN @v_Quote = 0 THEN @v_Len - @v_At + 1 ELSE @v_Quote - @v_At END)

    -- Spaces adjacent to an operator or punctuation only. A space BETWEEN word characters is left alone, so
    -- `IS NOT NULL` keeps its shape.
    SET @v_Previous = N''
    WHILE @v_Segment <> @v_Previous
    BEGIN
      SET @v_Previous = @v_Segment
      SET @v_Segment = REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@v_Segment,
                         ' <', '<'), '< ', '<'), ' >', '>'), '> ', '>'),
                         ' =', '='), '= ', '='), ' (', '('), '( ', '('),
                         ' )', ')'), ') ', ')'), ' ,', ','), ', ', ','),
                         ' +', '+'), '+ ', '+')
    END
    SET @v_Out = @v_Out + @v_Segment
    IF @v_Quote = 0 BREAK

    -- The literal, copied as it is: through its closing quote, a doubled quote being part of it.
    SET @v_End = @v_Quote
    WHILE 1 = 1
    BEGIN
      SET @v_End = CHARINDEX('''', @v_Result, @v_End + 1)
      IF @v_End = 0 OR SUBSTRING(@v_Result, @v_End + 1, 1) <> '''' BREAK
      SET @v_End = @v_End + 1
    END
    IF @v_End = 0
    BEGIN
      SET @v_Out = @v_Out + SUBSTRING(@v_Result, @v_Quote, @v_Len - @v_Quote + 1)
      BREAK
    END
    SET @v_Out = @v_Out + SUBSTRING(@v_Result, @v_Quote, @v_End - @v_Quote + 1)
    SET @v_At = @v_End + 1
  END
  SET @v_Result = @v_Out

  -- Parens the engine puts around a bare numeric literal: (365) -> 365, outside a string literal. Bounded by the
  -- search position, so a malformed expression cannot spin here.
  DECLARE @v_From INT = 1, @v_Pos INT, @v_Close INT, @v_Inner NVARCHAR(MAX)
  WHILE @v_From <= LEN(@v_Result)
  BEGIN
    SET @v_Pos = PATINDEX('%(%[0-9]%)%', SUBSTRING(@v_Result, @v_From, LEN(@v_Result)))
    IF @v_Pos = 0 BREAK
    SET @v_Pos = @v_Pos + @v_From - 1
    SET @v_Close = CHARINDEX(')', @v_Result, @v_Pos)
    IF @v_Close = 0 BREAK
    SET @v_Inner = SUBSTRING(@v_Result, @v_Pos + 1, @v_Close - @v_Pos - 1)
    -- An odd number of quotes before it puts it inside a literal.
    IF (LEN(LEFT(@v_Result, @v_Pos)) - LEN(REPLACE(LEFT(@v_Result, @v_Pos), '''', ''))) % 2 = 0
       AND @v_Inner NOT LIKE '%[^0-9.-]%' AND LEN(@v_Inner) > 0
      SET @v_Result = STUFF(@v_Result, @v_Pos, @v_Close - @v_Pos + 1, @v_Inner)
    ELSE
      SET @v_From = @v_Pos + 1
  END

  RETURN @v_Result
END
GO
