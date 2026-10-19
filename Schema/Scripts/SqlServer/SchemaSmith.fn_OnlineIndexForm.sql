-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_OnlineIndexForm') IS NOT NULL DROP FUNCTION SchemaSmith.fn_OnlineIndexForm
GO
CREATE FUNCTION SchemaSmith.fn_OnlineIndexForm(@Mode VARCHAR(7), @ObjectId INT, @IndexColumns NVARCHAR(MAX), @ColumnStore BIT,
                                               @IsConstraint BIT, @MaxDuration INT)
RETURNS TABLE
AS
-- How an index (or a PRIMARY KEY / UNIQUE constraint) is built under OnlineApply. [Form] is NULL under OFF (the plain
-- statement, recorded as before), 'online', 'resumable', or 'unavailable' with [Reason] saying why; [Options] is what
-- goes in the WITH list. RESUMABLE is used only when OnlineApplyMaxDuration is set, from 2019 for CREATE INDEX, and not
-- where the engine refuses it: a computed or timestamp key column (10644, 10639; a nonclustered index carries the
-- clustered key, so that key is checked too) or a columnstore index on the table (35397). Otherwise the build is
-- ONLINE without it.
RETURN
  SELECT [Form] = CASE WHEN @Mode = 'OFF' THEN NULL
                       WHEN a.[Reason] IS NOT NULL THEN 'unavailable'
                       WHEN r.[Resumable] = 1 THEN 'resumable'
                       ELSE 'online' END,
         [Options] = CASE WHEN @Mode = 'OFF' OR a.[Reason] IS NOT NULL THEN ''
                          WHEN r.[Resumable] = 1 THEN 'ONLINE = ON, RESUMABLE = ON, MAX_DURATION = ' + CONVERT(VARCHAR(10), @MaxDuration) + ' MINUTES'
                          ELSE 'ONLINE = ON' END,
         [Reason] = CASE WHEN @Mode = 'OFF' THEN NULL ELSE a.[Reason] END
    FROM (SELECT [Reason] = CASE WHEN SchemaSmith.fn_OnlineOperationsAvailable() = 0
                                 THEN 'online index operations need Enterprise or Developer edition, or Azure SQL, on SQL Server 2012 or later'
                                 WHEN @ColumnStore = 1 THEN 'a columnstore index is built offline'
                                 END) a
    CROSS APPLY (SELECT [Resumable] = CASE WHEN @MaxDuration IS NOT NULL AND @IsConstraint = 0 AND SchemaSmith.fn_ServerMajorVersion() >= 15
                                            AND NOT EXISTS (SELECT 1 FROM sys.columns c
                                                             WHERE c.[object_id] = @ObjectId AND (c.is_computed = 1 OR c.system_type_id = 189)
                                                               AND (CHARINDEX(QUOTENAME(c.[name]), @IndexColumns) > 0
                                                                    OR EXISTS (SELECT 1 FROM sys.index_columns ic
                                                                                JOIN sys.indexes ci ON ci.[object_id] = ic.[object_id] AND ci.index_id = ic.index_id
                                                                               WHERE ci.[object_id] = @ObjectId AND ci.[type] = 1
                                                                                 AND ic.column_id = c.column_id AND ic.key_ordinal > 0)))
                                            AND NOT EXISTS (SELECT 1 FROM sys.indexes ci WHERE ci.[object_id] = @ObjectId AND ci.[type] IN (5, 6))
                                       THEN 1 ELSE 0 END) r
