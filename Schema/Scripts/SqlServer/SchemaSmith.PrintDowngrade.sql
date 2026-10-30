-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.PrintDowngrade', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.PrintDowngrade
GO
CREATE PROCEDURE SchemaSmith.PrintDowngrade
  @Line NVARCHAR(400),
  @List NVARCHAR(MAX)
AS
BEGIN
  -- A degrade's log line names what it applied to, as the MySQL family's do. A list too long for one message is cut,
  -- and says so; the deployment summary lists every object.
  DECLARE @v_Msg NVARCHAR(2047) = @Line + ': ' + CASE WHEN LEN(ISNULL(@List, '')) > 1500 THEN LEFT(@List, 1500) + ', ... (the summary lists them all)'
                                                      ELSE ISNULL(@List, '') END
  RAISERROR('%s', 10, 100, @v_Msg) WITH NOWAIT
END
