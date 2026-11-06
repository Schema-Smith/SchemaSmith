-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The list splitter for column and index lists, in place of STRING_SPLIT (which needs compatibility level 130 and splits
-- on every delimiter). A delimiter inside a bracketed name is part of the name: [a,b],[c] is two columns, and ]] inside
-- the brackets is a ], not their end. Returns [Ordinal] (1-based input position) so a caller can rebuild a list in its
-- order, and [value], STRING_SPLIT's column name. A NULL list is one NULL item and an empty one is one empty item, as
-- the inline splitter this replaces returned them. It walks the list a character at a time: the lists are a table's
-- column lists, short, and a set-based split cannot tell a delimiter inside a name from one between names.
IF OBJECT_ID('SchemaSmith.fn_SplitList') IS NOT NULL DROP FUNCTION SchemaSmith.fn_SplitList
GO
CREATE FUNCTION SchemaSmith.fn_SplitList(@List NVARCHAR(MAX), @Delimiter NCHAR(1))
RETURNS @Items TABLE ([Ordinal] INT NOT NULL, [value] NVARCHAR(MAX) NULL)
AS
BEGIN
  IF @List IS NULL
  BEGIN
    INSERT @Items ([Ordinal], [value]) VALUES (1, NULL)
    RETURN
  END

  -- DATALENGTH, not LEN: LEN ignores trailing spaces, which would drop them from the last item.
  DECLARE @v_Length INT = DATALENGTH(@List) / 2, @v_Pos INT = 1, @v_Start INT = 1, @v_Ordinal INT = 0,
          @v_InBracket BIT = 0, @v_Char NCHAR(1)
  WHILE @v_Pos <= @v_Length
  BEGIN
    SET @v_Char = SUBSTRING(@List, @v_Pos, 1)
    IF @v_InBracket = 1
    BEGIN
      IF @v_Char = N']'
      BEGIN
        IF SUBSTRING(@List, @v_Pos + 1, 1) = N']' SET @v_Pos = @v_Pos + 1
        ELSE SET @v_InBracket = 0
      END
    END
    ELSE IF @v_Char = N'['
      SET @v_InBracket = 1
    ELSE IF @v_Char = @Delimiter
    BEGIN
      SET @v_Ordinal = @v_Ordinal + 1
      INSERT @Items ([Ordinal], [value]) VALUES (@v_Ordinal, SUBSTRING(@List, @v_Start, @v_Pos - @v_Start))
      SET @v_Start = @v_Pos + 1
    END
    SET @v_Pos = @v_Pos + 1
  END
  INSERT @Items ([Ordinal], [value]) VALUES (@v_Ordinal + 1, SUBSTRING(@List, @v_Start, @v_Length - @v_Start + 1))
  RETURN
END
GO
