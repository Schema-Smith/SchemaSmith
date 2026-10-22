-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.LiftLedgerColumns', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.LiftLedgerColumns
GO
-- A ledger table's declared generated columns (GeneratedAlways TransactionId/SequenceNumber Start/End) are recorded in the
-- caller's #LedgerColumns. On a table this run creates they stay in #Columns too, since CREATE TABLE is the only place they
-- can be declared; on a table that exists they come off it, so no column pass adds or alters them, and LedgerQuench
-- converges them by role instead. Both parse tiers call this after #Tables is filled.
CREATE PROCEDURE SchemaSmith.LiftLedgerColumns
AS
BEGIN
  SET NOCOUNT ON
  INSERT INTO #LedgerColumns ([Schema], [TableName], [RoleType], [ColumnName], [Hidden])
    SELECT c.[Schema], c.[TableName],
           CASE UPPER(c.[GeneratedAlways]) WHEN 'TRANSACTIONIDSTART' THEN 7 WHEN 'TRANSACTIONIDEND' THEN 8
                                           WHEN 'SEQUENCENUMBERSTART' THEN 9 ELSE 10 END,
           c.[ColumnName], ISNULL(c.[Hidden], 0)
      FROM #Columns c WITH (NOLOCK)
     WHERE UPPER(c.[GeneratedAlways]) IN ('TRANSACTIONIDSTART', 'TRANSACTIONIDEND', 'SEQUENCENUMBERSTART', 'SEQUENCENUMBEREND')
  DELETE c
    FROM #Columns c
    JOIN #Tables t ON t.[Schema] = c.[Schema] AND t.[Name] = c.[TableName]
   WHERE t.NewTable = 0
     AND UPPER(c.[GeneratedAlways]) IN ('TRANSACTIONIDSTART', 'TRANSACTIONIDEND', 'SEQUENCENUMBERSTART', 'SEQUENCENUMBEREND')
END
