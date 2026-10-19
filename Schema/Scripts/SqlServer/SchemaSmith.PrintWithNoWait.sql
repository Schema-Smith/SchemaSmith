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
  DECLARE @v_Last BIT = 0
  DECLARE @v_Audit INT
  DECLARE @v_AuditEnd INT, @v_AuditNull INT

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
      SET @v_Last = 1
    END
    ELSE
    BEGIN
      -- Both endings are handled by cutting at the line feed and dropping a carriage return if one sits
      -- in front of it, which is what the two separate CHARINDEX scans used to work out.
      SET @v_Line = SUBSTRING(@Message, @v_Pos, @v_LfPos - @v_Pos)
      IF RIGHT(@v_Line, 1) = CHAR(13) SET @v_Line = LEFT(@v_Line, LEN(@v_Line) - 1)
    END

    -- A quench script carries the change rows it records, and under WhatIf they are not written, so they are not
    -- shown either: printed, they read as statements the run would execute.
    SET @v_Audit = CHARINDEX('INSERT INTO SchemaSmith.ChangeAudit', @v_Line)
    IF @v_Audit > 0
    BEGIN
      WHILE @v_Audit > 0
      BEGIN
        -- A row ends with a quoted value, or with a NULL Mode.
        SET @v_AuditEnd = CHARINDEX(''');', @v_Line, @v_Audit)
        IF @v_AuditEnd > 0 SET @v_AuditEnd = @v_AuditEnd + 3
        SET @v_AuditNull = CHARINDEX(', NULL);', @v_Line, @v_Audit)
        IF @v_AuditNull > 0 AND (@v_AuditEnd = 0 OR @v_AuditNull + 8 < @v_AuditEnd) SET @v_AuditEnd = @v_AuditNull + 8
        IF @v_AuditEnd = 0 BREAK
        SET @v_Line = STUFF(@v_Line, @v_Audit, @v_AuditEnd - @v_Audit, '')
        SET @v_Audit = CHARINDEX('INSERT INTO SchemaSmith.ChangeAudit', @v_Line)
      END
      IF LTRIM(@v_Line) = '' SET @v_Line = NULL
    END

    -- DATALENGTH for the last line, for the same reason it sizes @v_End above: LEN reports 0 for a line made
    -- entirely of spaces, so a final line of indentation was silently dropped rather than printed.
    IF @v_Line IS NOT NULL AND (@v_Last = 0 OR DATALENGTH(@v_Line) > 0) RAISERROR(@v_Line, 10, 100) WITH NOWAIT
    IF @v_Last = 1 BREAK
    SET @v_Pos = @v_LfPos + 1
  END
END
