-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_FormatJson') IS NOT NULL DROP FUNCTION SchemaSmith.fn_FormatJson
GO
CREATE FUNCTION SchemaSmith.fn_FormatJson(@Json NVARCHAR(MAX), @Level INT) 
  RETURNS @r_Result TABLE ([LineNo] INT IDENTITY(1,1), [Line] NVARCHAR(MAX))
AS 
BEGIN
  DECLARE @RowCount AS INT, 
          @CurrentRow AS INT = 1,
          @Indent NVARCHAR(200) = SPACE(@Level * 2),
          @key AS NVARCHAR(MAX), 
          @value AS NVARCHAR(MAX),
          @type AS INT

  DECLARE @JsonTbl AS TABLE (Id INT IDENTITY(1,1), [key] NVARCHAR(MAX), [value] NVARCHAR(MAX), [type] INT)
  INSERT INTO @JsonTbl ([key], [value], [type])
    SELECT [key], [value], [type]
      FROM OPENJSON(@Json)
  SET @RowCount = @@ROWCOUNT

  IF @Level = 1 INSERT @r_Result ([Line]) VALUES('{')

  WHILE @CurrentRow <= @RowCount
  BEGIN
    SELECT @key = [key], @value = [value], @type = [type] 
      FROM @JsonTbl 
      WHERE Id = @CurrentRow
    IF @type = 1 -- String property
      INSERT @r_Result ([Line]) VALUES(@Indent + '"' + @key + '": "' + @value + '"' + CASE WHEN @CurrentRow = @RowCount THEN '' ELSE ',' END)
    ELSE IF @type IN (2, 3) -- Numeric or boolean property
      INSERT @r_Result ([Line]) VALUES(@Indent + '"' + @key + '": ' + @value + CASE WHEN @CurrentRow = @RowCount THEN '' ELSE ',' END)
    ELSE IF @type = 4 -- Array
    BEGIN
      INSERT @r_Result ([Line]) VALUES(@Indent + '"' + @key + '": [')
      INSERT @r_Result ([Line]) SELECT [Line] FROM SchemaSmith.fn_FormatJson(@value, @Level + 1) ORDER BY [LineNo]
      INSERT @r_Result ([Line]) VALUES(@Indent + ']' + CASE WHEN @CurrentRow = @RowCount THEN '' ELSE ',' END)
    END
    ELSE IF @type = 5 -- Embedded object
    BEGIN      
      INSERT @r_Result ([Line]) VALUES(@Indent + CASE WHEN @Level = 1 OR ISNUMERIC(@key) = 0 THEN '"' + @key + '": ' ELSE '' END + '{')
      INSERT @r_Result ([Line]) SELECT [Line] FROM SchemaSmith.fn_FormatJson(@value, @Level + 1) ORDER BY [LineNo]
      INSERT @r_Result ([Line]) VALUES(@Indent + '}' + CASE WHEN @CurrentRow = @RowCount THEN '' ELSE ',' END)
    END

    SET @CurrentRow += 1
  END
  IF @Level = 1 INSERT @r_Result ([Line]) VALUES('}')

  RETURN
END