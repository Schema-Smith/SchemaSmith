-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[PrintWithNoWait]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[PrintWithNoWait]
GO
CREATE PROCEDURE [SchemaSmith].[PrintWithNoWait]
  @Message NVARCHAR(MAX)
AS
BEGIN
  SET NOCOUNT ON

  -- Walks the message with an advancing position. It used to consume it instead --
  -- SET @Message = SUBSTRING(@Message, ..., LEN(@Message)) at the end of every iteration -- which
  -- rebuilt the entire remaining NVARCHAR(MAX) once per line, and re-scanned it from the start to find
  -- the next line ending. That is quadratic in the size of the message, and the messages this receives
  -- are generated DDL: on a 1,783-table model, printing one statement's output measured 53,846 ms, with
  -- the string that produced it built in 620 ms. WhatIf spent its entire time in here.
  --
  -- CHARINDEX's third argument does the same search without copying anything, so the message is read
  -- once end to end.
  DECLARE @v_Line NVARCHAR(MAX)
  DECLARE @v_LfPos INT
  DECLARE @v_Pos INT = 1
  DECLARE @v_End INT

  IF @Message IS NULL OR LEN(@Message) = 0
    RETURN

  -- DATALENGTH, not LEN: LEN ignores trailing spaces, which would drop the tail of a line that ends in
  -- them. NVARCHAR is two bytes per character.
  SET @v_End = DATALENGTH(@Message) / 2

  WHILE @v_Pos <= @v_End
  BEGIN
    SET @v_LfPos = CHARINDEX(CHAR(10), @Message, @v_Pos)

    IF @v_LfPos = 0
    BEGIN
      -- No further line ending: whatever remains is the last line.
      SET @v_Line = SUBSTRING(@Message, @v_Pos, @v_End - @v_Pos + 1)
      IF LEN(@v_Line) > 0 RAISERROR(@v_Line, 10, 100) WITH NOWAIT
      BREAK
    END

    -- Both endings are handled by cutting at the line feed and dropping a carriage return if one sits
    -- in front of it, which is what the two separate CHARINDEX scans used to work out.
    SET @v_Line = SUBSTRING(@Message, @v_Pos, @v_LfPos - @v_Pos)
    IF RIGHT(@v_Line, 1) = CHAR(13) SET @v_Line = LEFT(@v_Line, LEN(@v_Line) - 1)

    RAISERROR(@v_Line, 10, 100) WITH NOWAIT
    SET @v_Pos = @v_LfPos + 1
  END
END
