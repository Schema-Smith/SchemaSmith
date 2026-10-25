-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_FullTextInstalled') IS NOT NULL DROP FUNCTION SchemaSmith.fn_FullTextInstalled
GO
CREATE FUNCTION SchemaSmith.fn_FullTextInstalled()
  RETURNS BIT
AS
BEGIN
  -- 0 when the server has no Full-Text Search, as the stock Linux container images do until mssql-server-fts is added;
  -- SQL Server then refuses every full-text index.
  --
  -- Test affordance: the CONTEXT_INFO override fn_EnterpriseFeaturesUnavailable reads, extended with 'SSFT' and a 1-byte
  -- flag in bytes 14-18, simulates a server without it on one that has it.
  DECLARE @v_Override VARBINARY(128) = CONTEXT_INFO()
  IF @v_Override IS NOT NULL AND SUBSTRING(@v_Override, 1, 4) = 0x53534F56 AND SUBSTRING(@v_Override, 14, 4) = 0x53534654
    RETURN CONVERT(BIT, SUBSTRING(@v_Override, 18, 1))

  RETURN CASE WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 THEN 1 ELSE 0 END
END
