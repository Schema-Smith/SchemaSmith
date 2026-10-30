-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[RecordWouldChanges]', 'P') IS NOT NULL DROP PROCEDURE [SchemaSmith].[RecordWouldChanges]
GO
-- Under WhatIf a quench script is printed, not run, so the change rows it carries are never written. This writes each
-- one with its preview action instead (created -> wouldCreate, modified -> wouldModify, dropped -> wouldDrop), so the
-- preview lists exactly the changes the real run would record, from the same statement. A row that also names its
-- OnlineApply mode carries that mode into the preview.
CREATE PROCEDURE [SchemaSmith].[RecordWouldChanges]
  @Script NVARCHAR(MAX)
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_Marker NVARCHAR(200) = N'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType'
  DECLARE @v_Plain NVARCHAR(200) = N') VALUES (@@SPID, '''
  DECLARE @v_WithMode NVARCHAR(200) = N', Mode) VALUES (@@SPID, '''
  DECLARE @v_Pos INT = CHARINDEX(@v_Marker, ISNULL(@Script, N''))
  DECLARE @v_Start INT, @v_Quote INT, @v_Field INT, @v_HasMode BIT, @v_ModeEnd INT
  DECLARE @v_Type NVARCHAR(4000), @v_Name NVARCHAR(4000), @v_Action NVARCHAR(4000), @v_Value NVARCHAR(4000), @v_Mode NVARCHAR(4000)

  WHILE @v_Pos > 0
  BEGIN
    SET @v_Start = @v_Pos + LEN(@v_Marker)
    SET @v_HasMode = CASE WHEN SUBSTRING(@Script, @v_Start, LEN(@v_WithMode)) = @v_WithMode THEN 1 ELSE 0 END
    IF @v_HasMode = 0 AND SUBSTRING(@Script, @v_Start, LEN(@v_Plain)) <> @v_Plain
    BEGIN
      SET @v_Pos = CHARINDEX(@v_Marker, @Script, @v_Start)
      CONTINUE
    END
    SET @v_Start = @v_Start + CASE WHEN @v_HasMode = 1 THEN LEN(@v_WithMode) ELSE LEN(@v_Plain) END
    SET @v_Mode = NULL
    SET @v_Field = 1
    WHILE @v_Field <= 3
    BEGIN
      -- A literal ends at a quote that is not doubled.
      SET @v_Quote = @v_Start
      WHILE 1 = 1
      BEGIN
        SET @v_Quote = CHARINDEX(N'''', @Script, @v_Quote)
        IF @v_Quote = 0 OR SUBSTRING(@Script, @v_Quote + 1, 1) <> N'''' BREAK
        SET @v_Quote = @v_Quote + 2
      END
      IF @v_Quote = 0 BREAK
      SET @v_Value = REPLACE(SUBSTRING(@Script, @v_Start, @v_Quote - @v_Start), N'''''', N'''')
      IF @v_Field = 1 SET @v_Type = @v_Value
      ELSE IF @v_Field = 2 SET @v_Name = @v_Value
      ELSE
      BEGIN
        SET @v_Action = @v_Value
        -- The mode follows as a literal (', 'notCovered')') or as NULL; a mode never holds a quote.
        IF @v_HasMode = 1 AND SUBSTRING(@Script, @v_Quote + 1, 3) = N', '''
        BEGIN
          SET @v_ModeEnd = CHARINDEX(N'''', @Script, @v_Quote + 4)
          IF @v_ModeEnd > 0 SET @v_Mode = SUBSTRING(@Script, @v_Quote + 4, @v_ModeEnd - @v_Quote - 4)
        END
      END
      -- Past the closing quote, the comma and the next opening quote.
      SET @v_Start = CHARINDEX(N'''', @Script, @v_Quote + 1) + 1
      SET @v_Field = @v_Field + 1
    END

    IF @v_Field > 3 AND @v_Action IN (N'created', N'modified', N'dropped')
      INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode)
        VALUES (@@SPID, @v_Type, @v_Name,
                CASE @v_Action WHEN N'created' THEN N'wouldCreate' WHEN N'modified' THEN N'wouldModify' ELSE N'wouldDrop' END,
                @v_Mode)

    SET @v_Pos = CHARINDEX(@v_Marker, @Script, @v_Pos + LEN(@v_Marker))
  END
END
