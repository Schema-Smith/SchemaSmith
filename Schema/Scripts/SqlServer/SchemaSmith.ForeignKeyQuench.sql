-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.ForeignKeyQuench', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ForeignKeyQuench
GO
CREATE PROCEDURE SchemaSmith.ForeignKeyQuench
    @ProductName NVARCHAR(50),
    @WhatIf BIT = 0
AS
BEGIN TRY
  DECLARE @v_SQL NVARCHAR(MAX) = ''
  SET NOCOUNT ON

  RAISERROR('Add Missing Foreign Keys', 10, 100) WITH NOWAIT
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Adding foreign key ' + f.[Schema] + '.' + f.[TableName] + '.' + f.[KeyName] + CASE WHEN RTRIM(ISNULL(f.[VariantName], '')) <> '' THEN ' (variant: ' + REPLACE(RTRIM(f.[VariantName]), '''', '''''') + ')' ELSE '' END + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                  'ALTER TABLE ' + f.[Schema] + '.' + f.[TableName] + ' ADD CONSTRAINT ' + f.[KeyName] + ' FOREIGN KEY ' +
                                  '(' + f.[Columns] + ') REFERENCES ' + [RelatedTableSchema] + '.' + f.[RelatedTable] + ' (' + [RelatedColumns] + ')' +
                                  ' ON DELETE ' + [DeleteAction] +
                                  ' ON UPDATE ' + [UpdateAction] + ';' + CHAR(13) + CHAR(10) +
                                  'INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (@@SPID, ''foreignKey'', ''' + f.[Schema] + '.' + f.[TableName] + '.' + f.[KeyName] + ''', ''created'');' AS NVARCHAR(MAX))
                           FROM #ForeignKeys f WITH (NOLOCK)
                           WHERE NOT EXISTS (SELECT *
                                               FROM sys.foreign_keys sf
                                               WHERE sf.[parent_object_id] = OBJECT_ID(f.[Schema] + '.' + f.[TableName])
                                                 AND sf.[name] = SchemaSmith.fn_StripBracketWrapping(f.[KeyName]))
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)

  -- #363: WhatIf twin of the embedded 'foreignKey'/'created' audit above; same predicate.
  IF @WhatIf = 1
    INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
      SELECT @@SPID, 'foreignKey', f.[Schema] + '.' + f.[TableName] + '.' + f.[KeyName], 'wouldCreate'
        FROM #ForeignKeys f WITH (NOLOCK)
        WHERE NOT EXISTS (SELECT *
                            FROM sys.foreign_keys sf
                            WHERE sf.[parent_object_id] = OBJECT_ID(f.[Schema] + '.' + f.[TableName])
                              AND sf.[name] = SchemaSmith.fn_StripBracketWrapping(f.[KeyName]))

  SET NOCOUNT OFF
END TRY
BEGIN CATCH
  DECLARE @v_RethrowMsg NVARCHAR(4000) = ERROR_MESSAGE();
  RAISERROR(@v_RethrowMsg, 16, 1);
END CATCH
