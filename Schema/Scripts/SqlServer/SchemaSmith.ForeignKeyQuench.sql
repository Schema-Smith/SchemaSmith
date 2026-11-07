-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ForeignKeyQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ForeignKeyQuench
GO
CREATE PROCEDURE SchemaSmith.ForeignKeyQuench
    @ProductName NVARCHAR(50),
    @WhatIf BIT = 0,
    -- OnlineApply has no lower-lock form for adding a foreign key, so under Prefer or Require it runs as written and says so.
    @OnlineApply VARCHAR(7) = 'OFF'
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  RAISERROR('Add Missing Foreign Keys', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding foreign key ' + REPLACE(REPLACE(f.[Schema], '''', ''''''), '%', '%%') + '.' + REPLACE(REPLACE(f.[TableName], '''', ''''''), '%', '%%') + '.' + REPLACE(REPLACE(f.[KeyName], '''', ''''''), '%', '%%') + REPLACE(CASE WHEN RTRIM(ISNULL(f.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(f.[VariantName]), '''', '''''') + ')' ELSE '' END, '%', '%%') +
                                  REPLACE(REPLACE(CASE WHEN EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = f.[Schema] AND t.[Name] = f.[TableName] AND t.[NewTable] = 0 AND COALESCE(t.[OnlineApply], @OnlineApply) <> 'OFF') THEN ' (not covered by OnlineApply)' ELSE '' END, '''', ''''''), '%', '%%') + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  -- WITH NOCHECK leaves the existing rows unchecked (Validated false); NOT FOR REPLICATION
                                  -- is part of the definition. Enforced is converged afterwards by ConstraintStateQuench. A key a
                                  -- table rebuild set aside keeps the trust and the enabled state it had, where they are not declared.
                                  'ALTER TABLE ' + f.[Schema] + '.' + f.[TableName] + CASE WHEN COALESCE(f.[Validated], sa.[WasTrusted], 1) = 0 THEN ' WITH NOCHECK' ELSE '' END + ' ADD CONSTRAINT ' + f.[KeyName] + ' FOREIGN KEY ' +
                                  '(' + f.[Columns] + ') REFERENCES ' + [RelatedTableSchema] + '.' + f.[RelatedTable] + ' (' + [RelatedColumns] + ')' +
                                  ' ON DELETE ' + [DeleteAction] +
                                  ' ON UPDATE ' + [UpdateAction] +
                                  CASE WHEN ISNULL(f.[NotForReplication], 0) = 1 THEN ' NOT FOR REPLICATION' ELSE '' END + ';' + CHAR(13) + CHAR(10) +
                                  CASE WHEN f.[Enforced] IS NULL AND sa.[WasEnabled] = 0
                                       THEN 'ALTER TABLE ' + f.[Schema] + '.' + f.[TableName] + ' NOCHECK CONSTRAINT ' + f.[KeyName] + ';' + CHAR(13) + CHAR(10) ELSE '' END +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode) VALUES (@@SPID, ''foreignKey'', N''' + REPLACE((f.[Schema] + '.' + f.[TableName] + '.' + f.[KeyName]) COLLATE DATABASE_DEFAULT, '''', '''''') + ''', ''created'', ' +
                                  CASE WHEN EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = f.[Schema] AND t.[Name] = f.[TableName] AND t.[NewTable] = 0 AND COALESCE(t.[OnlineApply], @OnlineApply) <> 'OFF') THEN '''notCovered''' ELSE 'NULL' END + ');' AS NVARCHAR(MAX))
                           FROM #ForeignKeys f WITH (NOLOCK)
                           LEFT JOIN SchemaSmith.RebuildSetAsideForeignKeys sa ON sa.[ChildSchema] = SchemaSmith.fn_StripBracketWrapping(f.[Schema]) COLLATE DATABASE_DEFAULT
                                                                             AND sa.[ChildTable] = SchemaSmith.fn_StripBracketWrapping(f.[TableName]) COLLATE DATABASE_DEFAULT
                                                                             AND sa.[KeyName] = SchemaSmith.fn_StripBracketWrapping(f.[KeyName]) COLLATE DATABASE_DEFAULT
                           WHERE NOT EXISTS (SELECT *
                                               FROM sys.foreign_keys sf
                                               WHERE sf.[parent_object_id] = OBJECT_ID(f.[Schema] + '.' + f.[TableName])
                                                 AND sf.[name] = SchemaSmith.fn_StripBracketWrapping(f.[KeyName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'foreignKey'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode)
      SELECT @@SPID, 'foreignKey', f.[Schema] + '.' + f.[TableName] + '.' + f.[KeyName], 'wouldCreate',
             CASE WHEN EXISTS (SELECT 1 FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = f.[Schema] AND t.[Name] = f.[TableName] AND t.[NewTable] = 0 AND COALESCE(t.[OnlineApply], @OnlineApply) <> 'OFF') THEN 'notCovered' END
        FROM #ForeignKeys f WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT *
                            FROM sys.foreign_keys sf
                            WHERE sf.[parent_object_id] = OBJECT_ID(f.[Schema] + '.' + f.[TableName])
                              AND sf.[name] = SchemaSmith.fn_StripBracketWrapping(f.[KeyName]))

  -- The inbound keys a table rebuild set aside (RebuildTable). A key back on its table -- declared and added above, or
  -- re-created by hand -- is done with; one whose table is gone cannot come back. Every other one is added again as it
  -- was captured: trusted or not, enabled or not. Any run restores any of them, and one that fails stays recorded,
  -- named in the error, for the next run.
  IF EXISTS (SELECT 1 FROM SchemaSmith.RebuildSetAsideForeignKeys)
  BEGIN
    RAISERROR('Restore Foreign Keys Set Aside By A Table Rebuild', 10, 100) WITH NOWAIT
    DECLARE @v_RsSchema NVARCHAR(128), @v_RsTable NVARCHAR(128), @v_RsKey NVARCHAR(128), @v_RsDefinition NVARCHAR(MAX),
            @v_RsTrusted BIT, @v_RsEnabled BIT, @v_RsChild NVARCHAR(300), @v_RsSql NVARCHAR(MAX), @v_RsFailed NVARCHAR(MAX)
    DECLARE restore_cursor CURSOR LOCAL FAST_FORWARD FOR
      SELECT [ChildSchema], [ChildTable], [KeyName], [Definition], [WasTrusted], [WasEnabled]
        FROM SchemaSmith.RebuildSetAsideForeignKeys
       ORDER BY [ChildSchema], [ChildTable], [KeyName]
    OPEN restore_cursor
    FETCH NEXT FROM restore_cursor INTO @v_RsSchema, @v_RsTable, @v_RsKey, @v_RsDefinition, @v_RsTrusted, @v_RsEnabled
    WHILE @@FETCH_STATUS = 0
    BEGIN
      SET @v_RsChild = QUOTENAME(@v_RsSchema) + '.' + QUOTENAME(@v_RsTable)
      IF OBJECT_ID(@v_RsChild, 'U') IS NULL OR EXISTS (SELECT 1 FROM sys.foreign_keys sf WHERE sf.[parent_object_id] = OBJECT_ID(@v_RsChild) AND sf.[name] = @v_RsKey)
      BEGIN
        IF OBJECT_ID(@v_RsChild, 'U') IS NULL
          RAISERROR('  Not restoring foreign key %s on %s: the table no longer exists', 10, 100, @v_RsKey, @v_RsChild) WITH NOWAIT
        IF @WhatIf = 0
          DELETE FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [ChildSchema] = @v_RsSchema AND [ChildTable] = @v_RsTable AND [KeyName] = @v_RsKey
      END
      ELSE
      BEGIN
        SET @v_RsSql = 'ALTER TABLE ' + @v_RsChild + CASE WHEN @v_RsTrusted = 1 THEN ' WITH CHECK' ELSE ' WITH NOCHECK' END +
                       ' ADD CONSTRAINT ' + QUOTENAME(@v_RsKey) + ' ' + @v_RsDefinition + ';' +
                       CASE WHEN @v_RsEnabled = 0 THEN ' ALTER TABLE ' + @v_RsChild + ' NOCHECK CONSTRAINT ' + QUOTENAME(@v_RsKey) + ';' ELSE '' END
        RAISERROR('  Restoring foreign key %s.%s', 10, 100, @v_RsChild, @v_RsKey) WITH NOWAIT
        IF @WhatIf = 1
        BEGIN
          EXEC SchemaSmith.PrintWithNoWait @v_RsSql
          INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            VALUES (@@SPID, 'foreignKey', @v_RsChild + '.' + QUOTENAME(@v_RsKey), 'wouldCreate')
        END
        ELSE
        BEGIN
          BEGIN TRY
            EXEC(@v_RsSql)
            INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
              VALUES (@@SPID, 'foreignKey', @v_RsChild + '.' + QUOTENAME(@v_RsKey), 'created')
            DELETE FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [ChildSchema] = @v_RsSchema AND [ChildTable] = @v_RsTable AND [KeyName] = @v_RsKey
          END TRY
          BEGIN CATCH
            -- The rebuilt table does not have the key this one references yet (1776, often with 1750): the run that set
            -- it aside stopped before its index pass, and this may be another product's deploy. The record waits for the
            -- run that puts that key back.
            IF ERROR_NUMBER() IN (1776, 1750) AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys sf WHERE sf.[parent_object_id] = OBJECT_ID(@v_RsChild) AND sf.[name] = @v_RsKey)
              RAISERROR('  Foreign key %s.%s waits: the table it references has no key for it yet', 10, 100, @v_RsChild, @v_RsKey) WITH NOWAIT
            -- Another deploy in this database can restore the same key, or drop its table, between the look above and the
            -- ADD. Either way there is nothing left to restore, and this deploy does not fail for it.
            ELSE IF OBJECT_ID(@v_RsChild) IS NULL
                 OR EXISTS (SELECT 1 FROM sys.foreign_keys sf WHERE sf.[parent_object_id] = OBJECT_ID(@v_RsChild) AND sf.[name] = @v_RsKey)
              DELETE FROM SchemaSmith.RebuildSetAsideForeignKeys WHERE [ChildSchema] = @v_RsSchema AND [ChildTable] = @v_RsTable AND [KeyName] = @v_RsKey
            ELSE
              SET @v_RsFailed = ISNULL(@v_RsFailed + '; ', '') + @v_RsChild + '.' + QUOTENAME(@v_RsKey) + ' (' + ERROR_MESSAGE() + ')'
          END CATCH
        END
      END
      FETCH NEXT FROM restore_cursor INTO @v_RsSchema, @v_RsTable, @v_RsKey, @v_RsDefinition, @v_RsTrusted, @v_RsEnabled
    END
    CLOSE restore_cursor
    DEALLOCATE restore_cursor
    IF @v_RsFailed IS NOT NULL
      RAISERROR('Foreign keys set aside by a table rebuild could not be restored, and stay recorded for the next run: %s', 16, 1, @v_RsFailed)
  END

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = CASE WHEN ERROR_NUMBER() = 50000 THEN ERROR_MESSAGE() ELSE N'Msg ' + CAST(ERROR_NUMBER() AS NVARCHAR(10)) + N', Line ' + CAST(ERROR_LINE() AS NVARCHAR(10)) + ISNULL(N', Procedure ' + ERROR_PROCEDURE(), N'') + N': ' + ERROR_MESSAGE() END;
  RAISERROR('%s', 16, 1, @v_RethrowMsg);
END CATCH
