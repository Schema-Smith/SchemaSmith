-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
IF OBJECT_ID('SchemaSmith.EdgeConstraintQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.EdgeConstraintQuench
GO
-- Edge constraints (#graph, SQL Server 2019): CONNECTION (node TO node, ...) ON DELETE action on an edge table.
-- Runs after ForeignKeyQuench, once every node table a clause names exists. A constraint is compared by its clause
-- set and action; a change is a drop and re-create (there is no ALTER), a declared one missing is created, and one
-- the package no longer declares is dropped under the table's DropForeignKeysRemovedFromProduct, as a foreign key is.
--
-- sys.edge_constraints and sys.edge_constraint_clauses are 2019+, so the live read is a version-guarded dynamic
-- statement into a temp table: this procedure is kindled on every tier, and a static reference would fail at CREATE
-- time on an older binary. Below 2019 the live set is empty and DegradeUnsupportedFeatures has already emptied the
-- declared one, so nothing happens.
CREATE PROCEDURE SchemaSmith.EdgeConstraintQuench
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  IF OBJECT_ID('tempdb..#EdgeConstraintsCat') IS NOT NULL DROP TABLE #EdgeConstraintsCat
  CREATE TABLE #EdgeConstraintsCat ([Schema] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL, [TableName] NVARCHAR(200) COLLATE DATABASE_DEFAULT NOT NULL,
                                    [ConstraintName] NVARCHAR(500) COLLATE DATABASE_DEFAULT NOT NULL, [DeleteAction] NVARCHAR(20) COLLATE DATABASE_DEFAULT NOT NULL,
                                    [ClauseSet] NVARCHAR(MAX) COLLATE DATABASE_DEFAULT NULL)
  IF SchemaSmith.fn_ServerMajorVersion() >= 15
    EXEC sp_executesql N'
      INSERT INTO #EdgeConstraintsCat ([Schema], [TableName], [ConstraintName], [DeleteAction], [ClauseSet])
      SELECT QUOTENAME(OBJECT_SCHEMA_NAME(ec.parent_object_id)), QUOTENAME(OBJECT_NAME(ec.parent_object_id)), QUOTENAME(ec.[name]),
             CASE WHEN ec.delete_referential_action = 1 THEN ''CASCADE'' ELSE ''NO ACTION'' END,
             -- Ordered aggregation via FOR XML PATH (STRING_AGG ... WITHIN GROUP is a syntax error at compat 100).
             STUFF((SELECT '', '' + x.[Clause]
                      FROM (SELECT [Clause] = QUOTENAME(OBJECT_SCHEMA_NAME(cl.from_object_id)) + ''.'' + QUOTENAME(OBJECT_NAME(cl.from_object_id)) + '' TO '' +
                                              QUOTENAME(OBJECT_SCHEMA_NAME(cl.to_object_id)) + ''.'' + QUOTENAME(OBJECT_NAME(cl.to_object_id))
                              FROM sys.edge_constraint_clauses cl WHERE cl.[object_id] = ec.[object_id]) x
                     ORDER BY x.[Clause]
                       FOR XML PATH(''''), TYPE).value(''.'', ''NVARCHAR(MAX)''), 1, 2, '''')
        FROM sys.edge_constraints ec
        JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = QUOTENAME(OBJECT_SCHEMA_NAME(ec.parent_object_id)) COLLATE DATABASE_DEFAULT AND t.[Name] = QUOTENAME(OBJECT_NAME(ec.parent_object_id)) COLLATE DATABASE_DEFAULT'

  -- Drop: removed from the product (under the table''s flag), or changed in its clauses or action.
  RAISERROR('Drop Edge Constraints No Longer Defined In The Product, Or Changed', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#EdgeConstraintsToDrop') IS NOT NULL DROP TABLE #EdgeConstraintsToDrop
  SELECT c.[Schema], c.[TableName], c.[ConstraintName]
    INTO #EdgeConstraintsToDrop
    FROM #EdgeConstraintsCat c WITH (NOLOCK)
    LEFT JOIN #EdgeConstraints e WITH (NOLOCK) ON e.[Schema] = c.[Schema] AND e.[TableName] = c.[TableName] AND e.[ConstraintName] = c.[ConstraintName]
    JOIN #Tables t WITH (NOLOCK) ON t.[Schema] = c.[Schema] AND t.[Name] = c.[TableName]
   WHERE (e.[ConstraintName] IS NULL AND ISNULL(t.[DropForeignKeysRemovedFromProduct], 1) = 1)
      OR (e.[ConstraintName] IS NOT NULL AND (e.[DeleteAction] <> c.[DeleteAction] OR ISNULL(e.[ClauseSet], '') <> ISNULL(c.[ClauseSet], '')))

  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Dropping edge constraint ' + d.[Schema] + '.' + d.[TableName] + '.' + d.[ConstraintName] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + d.[Schema] + '.' + d.[TableName] + ' DROP CONSTRAINT ' + d.[ConstraintName] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''edgeConstraint'', ''' + d.[Schema] + '.' + d.[TableName] + '.' + d.[ConstraintName] + ''', ''dropped'');' AS NVARCHAR(MAX))
                           FROM #EdgeConstraintsToDrop d WITH (NOLOCK)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'edgeConstraint', d.[Schema] + '.' + d.[TableName] + '.' + d.[ConstraintName], 'wouldDrop'
        FROM #EdgeConstraintsToDrop d WITH (NOLOCK)

  -- Create: declared and not live, or just dropped as changed.
  RAISERROR('Add Missing Edge Constraints', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding edge constraint ' + e.[Schema] + '.' + e.[TableName] + '.' + e.[ConstraintName] + CASE WHEN RTRIM(ISNULL(e.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(e.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + e.[Schema] + '.' + e.[TableName] + ' ADD CONSTRAINT ' + e.[ConstraintName] + ' CONNECTION (' + e.[ClauseSet] + ') ON DELETE ' + e.[DeleteAction] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''edgeConstraint'', ''' + e.[Schema] + '.' + e.[TableName] + '.' + e.[ConstraintName] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM #EdgeConstraints e WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT 1 FROM #EdgeConstraintsCat c WITH (NOLOCK)
                                              WHERE c.[Schema] = e.[Schema] AND c.[TableName] = e.[TableName] AND c.[ConstraintName] = e.[ConstraintName])
                              OR EXISTS (SELECT 1 FROM #EdgeConstraintsToDrop d WITH (NOLOCK)
                                          WHERE d.[Schema] = e.[Schema] AND d.[TableName] = e.[TableName] AND d.[ConstraintName] = e.[ConstraintName])
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'edgeConstraint', e.[Schema] + '.' + e.[TableName] + '.' + e.[ConstraintName], 'wouldCreate'
        FROM #EdgeConstraints e WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT 1 FROM #EdgeConstraintsCat c WITH (NOLOCK)
                           WHERE c.[Schema] = e.[Schema] AND c.[TableName] = e.[TableName] AND c.[ConstraintName] = e.[ConstraintName])
           OR EXISTS (SELECT 1 FROM #EdgeConstraintsToDrop d WITH (NOLOCK)
                       WHERE d.[Schema] = e.[Schema] AND d.[TableName] = e.[TableName] AND d.[ConstraintName] = e.[ConstraintName])

  DROP TABLE #EdgeConstraintsToDrop
  DROP TABLE #EdgeConstraintsCat
  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
GO
