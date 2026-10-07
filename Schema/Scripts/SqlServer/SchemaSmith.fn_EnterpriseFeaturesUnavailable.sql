-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_EnterpriseFeaturesUnavailable') IS NOT NULL DROP FUNCTION SchemaSmith.fn_EnterpriseFeaturesUnavailable
GO
CREATE FUNCTION SchemaSmith.fn_EnterpriseFeaturesUnavailable()
  RETURNS BIT
AS
BEGIN
  -- 1 when the server cannot use data compression or columnstore indexes because of its edition. Before SQL Server
  -- 2016 SP1 (13.0.4001) both were Enterprise-only: Standard, Web and Express refuse them with 7738 and 35315
  -- (measured on Express 2008 R2, 2012 and 2014). Enterprise, Developer and Evaluation report EngineEdition 3; Azure
  -- SQL Database and Managed Instance report 5 and 8 and have both.
  --
  -- Test affordance: the CONTEXT_INFO override fn_ServerMajorVersion reads ('SSOV' + major), extended with 'SSED' and
  -- a 1-byte flag in bytes 9-13, simulates an edition without them on a Developer container.
  DECLARE @v_Override VARBINARY(128) = CONTEXT_INFO()
  IF @v_Override IS NOT NULL AND SUBSTRING(@v_Override, 1, 4) = 0x53534F56 AND SUBSTRING(@v_Override, 9, 4) = 0x53534544
    RETURN CONVERT(BIT, SUBSTRING(@v_Override, 13, 1))

  IF CONVERT(INT, SERVERPROPERTY('EngineEdition')) NOT IN (1, 2, 4)
    RETURN 0

  DECLARE @v_Major INT = SchemaSmith.fn_ServerMajorVersion()
  IF @v_Major < 13 RETURN 1
  IF @v_Major > 13 RETURN 0
  RETURN CASE WHEN CONVERT(INT, PARSENAME(CONVERT(NVARCHAR(128), SERVERPROPERTY('ProductVersion')), 2)) < 4001 THEN 1 ELSE 0 END
END
