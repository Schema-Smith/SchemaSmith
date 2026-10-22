-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_OnlineOperationsAvailable') IS NOT NULL DROP FUNCTION SchemaSmith.fn_OnlineOperationsAvailable
GO
CREATE FUNCTION SchemaSmith.fn_OnlineOperationsAvailable()
  RETURNS BIT
AS
BEGIN
  -- 1 when the server can build an index ONLINE: Enterprise, Developer and Evaluation (EngineEdition 3), Azure SQL
  -- Database (5) and Managed Instance (8), from 2012. Standard, Web and Express cannot at any version, 2016 SP1
  -- included. 2008 and 2008 R2 Enterprise can, but refuse it for an index over a LOB column, which is not modelled, so
  -- they take the plain form.
  --
  -- Test affordance: the CONTEXT_INFO edition flag fn_EnterpriseFeaturesUnavailable reads turns this off as well.
  DECLARE @v_Override VARBINARY(128) = CONTEXT_INFO()
  IF @v_Override IS NOT NULL AND SUBSTRING(@v_Override, 1, 4) = 0x53534F56 AND SUBSTRING(@v_Override, 9, 4) = 0x53534544
     AND SUBSTRING(@v_Override, 13, 1) = 0x01
    RETURN 0

  IF CONVERT(INT, SERVERPROPERTY('EngineEdition')) NOT IN (3, 5, 8)
    RETURN 0

  RETURN CASE WHEN SchemaSmith.fn_ServerMajorVersion() >= 11 THEN 1 ELSE 0 END
END
