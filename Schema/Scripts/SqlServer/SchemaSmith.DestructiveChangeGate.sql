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
  DECLARE @v_Changes TABLE (Kind VARCHAR(16), TableName NVARCHAR(300), ObjectName NVARCHAR(512), Detail NVARCHAR(400) NULL,
                            EstimatedRows BIGINT NULL, Accepted BIT NOT NULL DEFAULT 0, Disposition VARCHAR(16) NULL)

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

  -- A column whose type change rounds, cuts or substitutes values without an error (fn_ColumnChangeLoss), and a plain column
  -- the deploy drops and adds again as computed or as an identity, whose stored values the new ones replace. A computed
  -- column changing is derived data, and a change that fails on data that does not fit loses nothing.
  IF @IncludeColumns = 1
    INSERT @v_Changes (Kind, TableName, ObjectName, Detail, EstimatedRows)
      SELECT CASE WHEN cc.MustDropAndRecreate = 1 THEN 'column replaced' ELSE 'type change' END,
             SchemaSmith.fn_StripBracketWrapping(cc.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[TableName]),
             SchemaSmith.fn_StripBracketWrapping(cc.[Schema]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[TableName]) + '.' + SchemaSmith.fn_StripBracketWrapping(cc.[ColumnName]),
             LEFT(CASE WHEN cc.MustDropAndRecreate = 1
                       THEN lt.LiveType + ' -> ' + CASE WHEN RTRIM(ISNULL(c.[ComputedExpression], '')) <> '' THEN 'computed' ELSE 'IDENTITY' END
                       ELSE lt.LiveType + ' -> ' + LTRIM(RTRIM(c.[DataType])) END, 400),
             (SELECT SUM(p.[rows]) FROM sys.partitions p WHERE p.[object_id] = sc.[object_id] AND p.index_id < 2)
        FROM #ColumnChanges cc
        JOIN #Tables t ON t.[Schema] = cc.[Schema] AND t.[Name] = cc.[TableName]
        JOIN #Columns c ON c.[Schema] = cc.[Schema] AND c.[TableName] = cc.[TableName] AND c.[ColumnName] = cc.[ColumnName]
        JOIN sys.columns sc ON sc.[object_id] = OBJECT_ID(cc.[Schema] + '.' + cc.[TableName]) AND QUOTENAME(sc.[name]) = cc.[ColumnName] COLLATE DATABASE_DEFAULT
        -- TYPE_NAME is catalog metadata: in a contained database it carries the catalog collation, not the database's.
        CROSS APPLY (SELECT LiveType = (UPPER(TYPE_NAME(sc.system_type_id))
                                       + CASE WHEN TYPE_NAME(sc.system_type_id) IN ('varchar', 'char', 'varbinary', 'binary')
                                                   THEN '(' + CASE WHEN sc.max_length = -1 THEN 'MAX' ELSE CAST(sc.max_length AS VARCHAR(10)) END + ')'
                                              WHEN TYPE_NAME(sc.system_type_id) IN ('nvarchar', 'nchar')
                                                   THEN '(' + CASE WHEN sc.max_length = -1 THEN 'MAX' ELSE CAST(sc.max_length / 2 AS VARCHAR(10)) END + ')'
                                              WHEN TYPE_NAME(sc.system_type_id) IN ('decimal', 'numeric')
                                                   THEN '(' + CAST(sc.[precision] AS VARCHAR(10)) + ',' + CAST(sc.scale AS VARCHAR(10)) + ')'
                                              WHEN TYPE_NAME(sc.system_type_id) IN ('datetime2', 'time', 'datetimeoffset')
                                                   THEN '(' + CAST(sc.scale AS VARCHAR(10)) + ')'
                                              WHEN TYPE_NAME(sc.system_type_id) = 'float'
                                                   THEN '(' + CAST(sc.[precision] AS VARCHAR(10)) + ')'
                                              ELSE '' END) COLLATE DATABASE_DEFAULT) lt
        WHERE cc.DropOnly = 0 AND t.NewTable = 0 AND sc.is_computed = 0
          AND ((cc.MustDropAndRecreate = 1
                AND (RTRIM(ISNULL(c.[ComputedExpression], '')) <> '' OR (c.[DataType] LIKE '%IDENTITY%' AND sc.is_identity = 0)))
               OR (cc.MustDropAndRecreate = 0 AND cc.[TypeChanged] = 1
                   AND SchemaSmith.fn_ColumnChangeLoss(lt.LiveType, c.[DataType]) = 'loses'))

  IF NOT EXISTS (SELECT 1 FROM @v_Changes) RETURN

  UPDATE @v_Changes
     SET Accepted = CASE WHEN CHARINDEX(';' + LOWER(ObjectName) + ';', @v_Accept) > 0
                           OR CHARINDEX(';' + LOWER(TableName) + ';', @v_Accept) > 0 THEN 1 ELSE 0 END
  UPDATE @v_Changes
     SET Disposition = CASE WHEN Accepted = 1 THEN 'accepted'
                            WHEN @Policy = 'fail' THEN CASE WHEN @WhatIf = 1 THEN 'wouldRefuse' ELSE 'refused' END
                            WHEN @Policy = 'warn' THEN 'warned'
                            ELSE 'allowed' END

  INSERT INTO SchemaSmith.ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode, Detail, EstimatedRows)
    SELECT @@SPID, Kind, ObjectName, 'destructive', Disposition, Detail, EstimatedRows FROM @v_Changes

  IF EXISTS (SELECT 1 FROM @v_Changes WHERE Disposition = 'refused')
  BEGIN
    SET @v_List = STUFF((SELECT '; ' + CASE Kind WHEN 'table drop' THEN 'table ' + ObjectName + ' dropped'
                                                 WHEN 'column drop' THEN 'column ' + ObjectName + ' dropped'
                                                 ELSE Kind + ' ' + ObjectName + ' ' + ISNULL(Detail, '') END
                                + CASE WHEN EstimatedRows IS NULL THEN '' ELSE ' (about ' + CAST(EstimatedRows AS NVARCHAR(20)) + ' rows)' END
                           FROM @v_Changes WHERE Disposition = 'refused' ORDER BY ObjectName
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
    IF LEN(@v_List) > 1400 SET @v_List = LEFT(@v_List, 1400) + '...'
    RAISERROR('Destructive change refused (DestructiveChangePolicy is fail): %s. Nothing in this step was dropped or altered. Name each one in AcceptDestructive to proceed, restore it to the package, or keep it with DropColumnsRemovedFromProduct or PreventDrop.', 16, 1, @v_List)
  END
END
