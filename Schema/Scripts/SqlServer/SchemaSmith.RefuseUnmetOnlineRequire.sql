-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.RefuseUnmetOnlineRequire', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.RefuseUnmetOnlineRequire
GO
CREATE PROCEDURE SchemaSmith.RefuseUnmetOnlineRequire
AS
BEGIN
  -- OnlineApply REQUIRE: an index this deploy will build (missing, or in the caller's #IndexesToDrop to be built again)
  -- that cannot be built online stops the deploy here, naming each one, before any index is dropped. Reads the caller's
  -- #OnlineForms.
  SET NOCOUNT ON
  DECLARE @v_Refused NVARCHAR(MAX) =
    STUFF((SELECT '; ' + f.[Schema] + '.' + f.[TableName] + '.' + QUOTENAME(f.[IndexName]) + ' (' + f.[Reason] + ')'
             FROM #OnlineForms f WITH (NOLOCK)
            WHERE f.[Mode] = 'REQUIRE' AND f.[Form] = 'unavailable'
              AND (INDEXPROPERTY(OBJECT_ID(f.[Schema] + '.' + f.[TableName]), f.[IndexName], 'IndexID') IS NULL
                   OR EXISTS (SELECT 1 FROM #IndexesToDrop d WITH (NOLOCK)
                               WHERE d.[Schema] = f.[Schema] AND d.[TableName] = f.[TableName] AND d.[IndexName] = f.[IndexName]))
            ORDER BY f.[Schema], f.[TableName], f.[IndexName]
            FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @v_Refused IS NOT NULL
    RAISERROR('OnlineApply is Require, and these indexes cannot be built online: %s. Set the table''s OnlineApply to Prefer to build them with the plain statement.', 16, 1, @v_Refused)
END
