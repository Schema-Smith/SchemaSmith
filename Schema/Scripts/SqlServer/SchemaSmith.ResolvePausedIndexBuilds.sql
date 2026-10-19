-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ResolvePausedIndexBuilds', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ResolvePausedIndexBuilds
GO
CREATE PROCEDURE SchemaSmith.ResolvePausedIndexBuilds
  @OnlineApplyMaxDuration INT = NULL,
  @WhatIf BIT = 0
AS
BEGIN
  -- A resumable build an earlier deploy left paused (its OnlineApplyMaxDuration ran out, and the engine killed that
  -- session) blocks every other DDL on the table (10637), whatever this deploy's settings. One the package still
  -- declares (the caller's #Indexes) is resumed first, so this deploy finishes it and then compares it like any index;
  -- one it no longer declares is abandoned. Only the caller's #Tables are touched. The catalog view is 2017+.
  SET NOCOUNT ON
  IF SchemaSmith.fn_ServerMajorVersion() < 14 RETURN

  DECLARE @v_SQL NVARCHAR(MAX)
  CREATE TABLE #PausedBuilds ([Schema] NVARCHAR(500) COLLATE DATABASE_DEFAULT, [TableName] NVARCHAR(500) COLLATE DATABASE_DEFAULT,
                              [IndexName] NVARCHAR(500) COLLATE DATABASE_DEFAULT)
  EXEC('INSERT INTO #PausedBuilds SELECT QUOTENAME(OBJECT_SCHEMA_NAME(r.[object_id])), QUOTENAME(OBJECT_NAME(r.[object_id])), r.[name]
          FROM sys.index_resumable_operations r WHERE r.[state] = 1')
  IF NOT EXISTS (SELECT 1 FROM #PausedBuilds p JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = p.[Schema] AND t.[Name] = p.[TableName]) RETURN

  RAISERROR('Resolve Paused Resumable Index Builds', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  ' + CASE WHEN i.[IndexName] IS NULL THEN 'Abandoning' ELSE 'Resuming' END + ' the paused build of ' +
                                  REPLACE(p.[Schema] + '.' + p.[TableName] + '.' + QUOTENAME(p.[IndexName]), '''', '''''') + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER INDEX ' + QUOTENAME(p.[IndexName]) + ' ON ' + p.[Schema] + '.' + p.[TableName] +
                                  CASE WHEN i.[IndexName] IS NULL THEN ' ABORT;'
                                       ELSE ' RESUME' + ISNULL(' WITH (MAX_DURATION = ' + CONVERT(VARCHAR(10), @OnlineApplyMaxDuration) + ' MINUTES)', '') + ';' + CHAR(13) + CHAR(10) +
                                            'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode) VALUES (@@SPID, ''index'', ''' +
                                            REPLACE(p.[Schema] + '.' + p.[TableName] + '.' + QUOTENAME(p.[IndexName]), '''', '''''') + ''', ''created'', ''resumable'');'
                                       END AS NVARCHAR(MAX))
                           FROM #PausedBuilds p
                           JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = p.[Schema] AND t.[Name] = p.[TableName]
                           LEFT JOIN #Indexes i WITH (NOLOCK) ON i.[Schema] = p.[Schema] AND i.[TableName] = p.[TableName]
                                                             AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = p.[IndexName]
                           ORDER BY p.[Schema], p.[TableName], p.[IndexName]
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
END
