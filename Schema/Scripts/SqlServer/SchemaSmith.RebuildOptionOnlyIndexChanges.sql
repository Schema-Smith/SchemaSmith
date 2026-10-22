-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.RebuildOptionOnlyIndexChanges', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.RebuildOptionOnlyIndexChanges
GO
CREATE PROCEDURE SchemaSmith.RebuildOptionOnlyIndexChanges
  @WhatIf BIT = 0
AS
BEGIN
  -- An index in the caller's #IndexChanges whose only difference is PAD_INDEX, or IGNORE_DUP_KEY on an index that backs
  -- no constraint (a constraint's cannot change in place, 1979), is rebuilt in place and taken out of #IndexChanges, so
  -- it is not dropped and created again: the same end state, the index keeps serving queries, and a clustered index's
  -- nonclustered indexes are not rebuilt twice. A rebuild keeps the compression and the fill factor. The scripts are
  -- compared with both options taken out; DATA_COMPRESSION always leads a rowstore index's WITH list, so each option is
  -- written ", NAME=ON". An index a column change drops anyway is left to that.
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX)
  RAISERROR('Rebuild Indexes Whose Only Change Is An Option', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#IndexOptionRebuilds') IS NOT NULL DROP TABLE #IndexOptionRebuilds
  SELECT ic.[Schema], ic.[TableName], ic.[IndexName], ic.[IsConstraint], ic.[PadIndex], ic.[IgnoreDuplicateKey]
    INTO #IndexOptionRebuilds
    FROM #IndexChanges ic WITH (NOLOCK)
    WHERE ic.[ColumnStore] = 0
      AND REPLACE(REPLACE(ic.[LiveScript], ', IGNORE_DUP_KEY=ON', ''), ', PAD_INDEX=ON', '') = REPLACE(REPLACE(ic.[DeclaredScript], ', IGNORE_DUP_KEY=ON', ''), ', PAD_INDEX=ON', '')
      AND (ic.[IsConstraint] = 0
           OR (CHARINDEX(', IGNORE_DUP_KEY=ON', ic.[LiveScript]) > 0 AND ISNULL(ic.[IgnoreDuplicateKey], 0) = 1)
           OR (CHARINDEX(', IGNORE_DUP_KEY=ON', ic.[LiveScript]) = 0 AND ISNULL(ic.[IgnoreDuplicateKey], 0) = 0))
      AND NOT EXISTS (SELECT 1 FROM #IndexesToDropForColumnChanges dc WITH (NOLOCK)
                       WHERE dc.[Schema] = ic.[Schema] AND dc.[TableName] = ic.[TableName] AND dc.[IndexName] = SchemaSmith.fn_StripBracketWrapping(ic.[IndexName]))
  DELETE ic FROM #IndexChanges ic
   WHERE EXISTS (SELECT 1 FROM #IndexOptionRebuilds r WITH (NOLOCK) WHERE r.[Schema] = ic.[Schema] AND r.[TableName] = ic.[TableName] AND r.[IndexName] = ic.[IndexName])

  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Rebuilding ' + CASE WHEN r.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END + ' ' + r.[Schema] + '.' + r.[TableName] + '.' + r.[IndexName] + ' with its declared options' +
                                  CASE WHEN ol.[Form] IN ('online', 'resumable') THEN ' (online)' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER INDEX ' + r.[IndexName] + ' ON ' + r.[Schema] + '.' + r.[TableName] + ' REBUILD WITH (PAD_INDEX = ' + CASE WHEN r.[PadIndex] = 1 THEN 'ON' ELSE 'OFF' END +
                                  CASE WHEN r.[IsConstraint] = 0 THEN ', IGNORE_DUP_KEY = ' + CASE WHEN r.[IgnoreDuplicateKey] = 1 THEN 'ON' ELSE 'OFF' END ELSE '' END +
                                  CASE WHEN ol.[Form] IN ('online', 'resumable') THEN ', ONLINE = ON' ELSE '' END + ');' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode) VALUES (@@SPID, ''' + CASE WHEN r.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END + ''', ''' +
                                  REPLACE(r.[Schema] + '.' + r.[TableName] + '.' + r.[IndexName], '''', '''''') + ''', ''modified'', ' +
                                  CASE WHEN ol.[Form] IS NULL THEN 'NULL' WHEN ol.[Form] = 'resumable' THEN '''online''' ELSE '''' + ol.[Form] + '''' END + ');' AS NVARCHAR(MAX))
                           FROM #IndexOptionRebuilds r WITH (NOLOCK)
                           LEFT JOIN #OnlineForms ol WITH (NOLOCK) ON ol.[Kind] = 'index' AND ol.[Schema] = r.[Schema] AND ol.[TableName] = r.[TableName]
                                                                  AND ol.[IndexName] = SchemaSmith.fn_StripBracketWrapping(r.[IndexName])
                           ORDER BY r.[Schema], r.[TableName], r.[IndexName]
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode)
      SELECT @@SPID, CASE WHEN r.[IsConstraint] = 1 THEN 'constraint' ELSE 'index' END, r.[Schema] + '.' + r.[TableName] + '.' + r.[IndexName], 'wouldModify',
             CASE WHEN ol.[Form] = 'resumable' THEN 'online' ELSE ol.[Form] END
        FROM #IndexOptionRebuilds r WITH (NOLOCK)
        LEFT JOIN #OnlineForms ol WITH (NOLOCK) ON ol.[Kind] = 'index' AND ol.[Schema] = r.[Schema] AND ol.[TableName] = r.[TableName]
                                               AND ol.[IndexName] = SchemaSmith.fn_StripBracketWrapping(r.[IndexName])
END
