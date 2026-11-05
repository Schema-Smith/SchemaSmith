SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO
-- Legacy variant: identical behaviour to the Procedures-Modern copy, built with
-- FOR XML PATH instead of STRING_AGG, which is SQL Server 2017+.
CREATE OR ALTER PROCEDURE [SchemaSmith].[CustomTableDrop]
  @SchemaName SYSNAME,
  @TableName  SYSNAME,
  @RetentionDays INT = 90
AS
BEGIN
  SET NOCOUNT ON;

  DECLARE @FullName NVARCHAR(512) = QUOTENAME(@SchemaName) + '.' + QUOTENAME(@TableName);
  DECLARE @RecycledName SYSNAME;
  DECLARE @v_SQL NVARCHAR(MAX);
  DECLARE @ObjectId INT;

  -- Validate the table exists
  SET @ObjectId = OBJECT_ID(@FullName);
  IF @ObjectId IS NULL
  BEGIN
    RAISERROR(N'Table %s does not exist.', 16, 1, @FullName);
    RETURN;
  END

  -- Generate unique recycled name
  SET @RecycledName = @SchemaName + N'_' + @TableName;
  IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = N'recyclebin' AND t.name = @RecycledName)
  BEGIN
    DECLARE @Modifier INT = 1;
    WHILE EXISTS (SELECT 1 FROM sys.tables t
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = N'recyclebin'
                      AND t.name = @RecycledName + N'_' + CAST(@Modifier AS NVARCHAR(10)))
    BEGIN
      SET @Modifier = @Modifier + 1;
    END;

    SET @RecycledName = @RecycledName + N'_' + CAST(@Modifier AS NVARCHAR(10));
  END

  -- =========================================================================
  -- 1. Drop foreign keys on other tables that reference this table
  -- =========================================================================
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(fk_tab.schema_id)) + '.' + QUOTENAME(fk_tab.name) + ' DROP CONSTRAINT ' + QUOTENAME(fk.name) + ';'
                           FROM sys.foreign_keys fk
                           JOIN sys.tables fk_tab ON fk.parent_object_id = fk_tab.object_id
                           WHERE fk.referenced_object_id = @ObjectId
                             AND fk.parent_object_id <> @ObjectId   -- exclude self-referencing (handled below)
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
  EXEC(@v_SQL);

  -- =========================================================================
  -- 2. Drop foreign keys on this table (including self-referencing)
  -- =========================================================================
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'ALTER TABLE ' + @FullName + ' DROP CONSTRAINT ' + QUOTENAME(fk.name) + ';'
                           FROM sys.foreign_keys fk
                           WHERE fk.parent_object_id = @ObjectId
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
  EXEC(@v_SQL);

  -- =========================================================================
  -- 3. Drop check constraints and default constraints
  -- =========================================================================
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + 'ALTER TABLE ' + @FullName + ' DROP CONSTRAINT ' + QUOTENAME(x.name) + ';'
                           FROM (SELECT name
                                   FROM sys.check_constraints
                                   WHERE parent_object_id = @ObjectId
                                 UNION ALL
                                 SELECT name
                                   FROM sys.default_constraints
                                   WHERE parent_object_id = @ObjectId) x
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
  EXEC(@v_SQL);

  -- =========================================================================
  -- 4. Drop non-clustered indexes and constraints, then the clustered last
  --    (STRING_AGG's WITHIN GROUP becomes a plain ORDER BY in the subquery)
  -- =========================================================================
  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CASE WHEN pk.[object_id] IS NOT NULL OR uq.[object_id] IS NOT NULL
                                                           THEN 'ALTER TABLE ' + @FullName + ' DROP CONSTRAINT ' + QUOTENAME(i.name) + ';'
                                                           ELSE 'DROP INDEX ' + QUOTENAME(i.name) + N' ON ' + @FullName + ';'
                                                           END
                           FROM sys.indexes i
                           LEFT JOIN sys.key_constraints pk ON pk.parent_object_id = i.[object_id] AND pk.unique_index_id = i.index_id AND pk.[type] = 'PK'
                           LEFT JOIN sys.key_constraints uq ON uq.parent_object_id = i.[object_id] AND uq.unique_index_id = i.index_id AND uq.[type] = 'UQ'
                           WHERE i.[object_id] = @ObjectId
                             AND i.[type] > 0 -- exclude heap
                           ORDER BY CASE WHEN i.[type_desc] = 'CLUSTERED' THEN 1 ELSE 0 END, i.index_id
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '');
  EXEC(@v_SQL);

  -- =========================================================================
  -- 5. Transfer the table to the recyclebin schema and rename
  -- =========================================================================
  SET @v_SQL = N'ALTER SCHEMA [recyclebin] TRANSFER ' + @FullName;
  EXEC sp_executesql @v_SQL;

  -- Rename to the schema_table convention (only if the name differs)
  DECLARE @v_OriginalName NVARCHAR(500) = N'recyclebin.' + @TableName
  IF @TableName <> @RecycledName EXEC sp_rename @v_OriginalName, @RecycledName, N'OBJECT';

  -- =========================================================================
  -- 6. Register in the recyclebin registry
  -- =========================================================================
  INSERT INTO [recyclebin].[Registry] ([OriginalSchema], [OriginalName], [RecycledName], [RecycledDate], [RetentionDays])
    VALUES (@SchemaName, @TableName, @RecycledName, SYSDATETIME(), @RetentionDays);

  PRINT N'Table ' + @FullName + N' has been recycled as [recyclebin].' + QUOTENAME(@RecycledName)
      + N' with a retention of ' + CAST(@RetentionDays AS NVARCHAR(10)) + N' days.';
END
GO
