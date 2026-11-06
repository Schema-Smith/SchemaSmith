-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

IF OBJECT_ID('SchemaSmith.DestructiveChangeGate', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.DestructiveChangeGate
GO
CREATE PROCEDURE SchemaSmith.DestructiveChangeGate
  @Policy VARCHAR(5) = 'allow',
  @Accept NVARCHAR(MAX) = NULL,
  @WhatIf BIT = 0,
  @IncludeColumns BIT = 1
AS
BEGIN
  SET NOCOUNT ON
  -- H3. What this step removes -- tables and columns dropped because the package no longer declares them -- with each
  -- table's row count from the catalog (never COUNT(*), which would scan exactly the big tables this is for), recorded in
  -- the change audit. Under 'fail' the step is refused before anything in it is dropped, unless the change is named in
  -- @Accept: ';schema.table;' accepts every change to the table, ';schema.table.column;' one column, both lower case and
  -- without delimiters, as SchemaQuench renders the list. Reads the caller's #TablesRemovedFromProduct and, with
  -- @IncludeColumns, #ColumnChanges, #Tables and #SuppressedColumnDrops.
  DECLARE @v_Accept NVARCHAR(MAX) = LOWER(ISNULL(@Accept, '')),
          @v_List NVARCHAR(MAX)
  DECLARE @v_Changes TABLE (Kind VARCHAR(16), TableName NVARCHAR(300), ObjectName NVARCHAR(512), EstimatedRows BIGINT NULL,
                            Accepted BIT NOT NULL DEFAULT 0, Disposition VARCHAR(16) NULL)

  -- A partitioned table removed from the product is refused by name by DropTablesRemovedFromProduct, the guard that answers
  -- for it; the gate stands aside so that is the message the deploy stops on.
  IF EXISTS (SELECT 1 FROM #TablesRemovedFromProduct t
              WHERE (SELECT COUNT(*) FROM sys.partitions p
                      WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.TableName)) AND p.index_id < 2) > 1)
    RETURN

  INSERT @v_Changes (Kind, TableName, ObjectName, EstimatedRows)
    SELECT 'table drop', SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + '.' + t.TableName, SchemaSmith.fn_StripBracketWrapping(t.[Schema]) + '.' + t.TableName,
           (SELECT SUM(p.[rows]) FROM sys.partitions p WHERE p.[object_id] = OBJECT_ID(t.[Schema] + '.' + QUOTENAME(t.TableName)) AND p.index_id < 2)
      FROM #TablesRemovedFromProduct t

  IF @IncludeColumns = 1
    INSERT @v_Changes (Kind, TableName, ObjectName, EstimatedRows)
      SELECT 'column drop', SchemaSmith.fn_StripBracketWrapping(cc.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[TableName]),
             SchemaSmith.fn_StripBracketWrapping(cc.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[TableName]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[ColumnName]),
             (SELECT SUM(p.[rows]) FROM sys.partitions p WHERE p.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) AND p.index_id < 2)
        FROM #ColumnChanges cc
        JOIN #Tables t ON t.[Schema] = cc.[Schema] AND t.[Name] = cc.[TableName]
        WHERE cc.DropOnly = 1 AND t.NewTable = 0
          AND NOT EXISTS (SELECT 1 FROM #SuppressedColumnDrops s
                           WHERE s.[Schema] = cc.[Schema] AND s.[TableName] = cc.[TableName] AND s.[ColumnName] = cc.[ColumnName])

  IF NOT EXISTS (SELECT 1 FROM @v_Changes) RETURN

  UPDATE @v_Changes
     SET Accepted = CASE WHEN CHARINDEX(';' + LOWER(ObjectName) + ';', @v_Accept) > 0
                           OR CHARINDEX(';' + LOWER(TableName) + ';', @v_Accept) > 0 THEN 1 ELSE 0 END
  UPDATE @v_Changes
     SET Disposition = CASE WHEN Accepted = 1 THEN 'accepted'
                            WHEN @Policy = 'fail' THEN CASE WHEN @WhatIf = 1 THEN 'wouldRefuse' ELSE 'refused' END
                            WHEN @Policy = 'warn' THEN 'warned'
                            ELSE 'allowed' END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode, EstimatedRows)
    SELECT @@SPID, Kind, ObjectName, 'destructive', Disposition, EstimatedRows FROM @v_Changes

  IF EXISTS (SELECT 1 FROM @v_Changes WHERE Disposition = 'refused')
  BEGIN
    SET @v_List = STUFF((SELECT '; ' + CASE Kind WHEN 'table drop' THEN 'table ' ELSE 'column ' END + ObjectName + ' dropped'
                                + CASE WHEN EstimatedRows IS NULL THEN '' ELSE ' (about ' + CAST(EstimatedRows AS NVARCHAR(20)) + ' rows)' END
                           FROM @v_Changes WHERE Disposition = 'refused' ORDER BY ObjectName
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF LEN(@v_List) > 1400 SET @v_List = LEFT(@v_List, 1400) + '...'
    RAISERROR('Destructive change refused (DestructiveChangePolicy is fail): %s. Nothing in this step was dropped or altered. Name each one in AcceptDestructive to proceed, restore it to the package, or keep it with DropColumnsRemovedFromProduct or PreventDrop.', 16, 1, @v_List)
  END
END
