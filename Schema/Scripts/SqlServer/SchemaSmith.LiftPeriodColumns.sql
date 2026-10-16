-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.LiftPeriodColumns', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.LiftPeriodColumns
GO
-- A temporal table's declared period columns (GeneratedAlways RowStart / RowEnd) move from #Columns onto their #Tables
-- row. They are not ordinary columns: SQL Server creates them only together with PERIOD FOR SYSTEM_TIME and cannot
-- alter them once the table is versioned, so the create, alter and drop passes must never see them.
-- SchemaSmith.PeriodColumnQuench reads them from #Tables. Called by both parse tiers after the ShouldApply filter.
CREATE PROCEDURE SchemaSmith.LiftPeriodColumns
AS
BEGIN
  SET NOCOUNT ON
  -- UPPER: a case-sensitive database compares the role as written, and the package may spell it in any case.
  UPDATE t
     SET [PeriodStart] = s.[ColumnName], [PeriodStartType] = s.[DataType], [PeriodStartHidden] = ISNULL(s.[Hidden], 0)
    FROM #Tables t
    JOIN #Columns s ON s.[Schema] = t.[Schema] AND s.[TableName] = t.[Name] AND UPPER(s.[GeneratedAlways]) = 'ROWSTART'
  UPDATE t
     SET [PeriodEnd] = s.[ColumnName], [PeriodEndType] = s.[DataType], [PeriodEndHidden] = ISNULL(s.[Hidden], 0)
    FROM #Tables t
    JOIN #Columns s ON s.[Schema] = t.[Schema] AND s.[TableName] = t.[Name] AND UPPER(s.[GeneratedAlways]) = 'ROWEND'
  DELETE FROM #Columns WHERE UPPER([GeneratedAlways]) IN ('ROWSTART', 'ROWEND')
  SET NOCOUNT OFF
END
