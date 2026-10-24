-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Refuses a run that declares a table another product owns. Called before MissingTableAndColumnQuench, which renames
-- and adds columns, so a refusal leaves the owner's table as it was. A table's owner is its ProductName extended
-- property, or the ProductOwnership row a memory-optimized table carries in its place.
IF OBJECT_ID('SchemaSmith.ValidateTableOwnership', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.ValidateTableOwnership
GO
CREATE PROCEDURE SchemaSmith.ValidateTableOwnership
  @ProductName NVARCHAR(50),
  @WhatIf BIT = 0
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_SQL NVARCHAR(MAX) = ''

  RAISERROR('Validate Table Ownership', 10, 100) WITH NOWAIT
  IF OBJECT_ID('tempdb..#ForeignOwners') IS NOT NULL DROP TABLE #ForeignOwners
  SELECT t.[Schema], tgt.[Name], o.[Owner]
    INTO #ForeignOwners
    FROM #Tables t WITH (NOLOCK)
    -- A table declared under a new name is still the old one until the rename, which runs only when the old name exists
    -- and the new one does not.
    CROSS APPLY (SELECT [Name] = CASE WHEN OBJECT_ID(t.[Schema] + '.' + t.[Name]) IS NULL AND ISNULL(t.[OldName], '') <> ''
                                           AND OBJECT_ID(t.[Schema] + '.' + t.[OldName]) IS NOT NULL
                                      THEN t.[OldName] ELSE t.[Name] END) tgt
    CROSS APPLY (SELECT CONVERT(NVARCHAR(50), ep.[value]) COLLATE DATABASE_DEFAULT AS [Owner]
                   FROM sys.extended_properties ep
                   WHERE ep.class = 1
                     AND ep.minor_id = 0
                     AND ep.major_id = OBJECT_ID(t.[Schema] + '.' + tgt.[Name])
                     AND ep.[name] = 'ProductName'
                 UNION ALL
                 SELECT po.[ProductName] COLLATE DATABASE_DEFAULT
                   FROM SchemaSmith.ProductOwnership po
                   WHERE po.[IndexName] = ''
                     AND po.[Schema] COLLATE DATABASE_DEFAULT = t.[Schema]
                     AND QUOTENAME(po.[TableName]) COLLATE DATABASE_DEFAULT = tgt.[Name]) o
    WHERE o.[Owner] <> @ProductName

  IF NOT EXISTS (SELECT * FROM #ForeignOwners) RETURN

  SELECT @v_SQL = STUFF((SELECT CHAR(13) + CHAR(10) + CAST('RAISERROR(''  Table ' + REPLACE(f.[Schema] + '.' + f.[Name], '''', '''''') + ' owned by different product. [' + REPLACE(f.[Owner], '''', '''''') + ']'', 10, 100) WITH NOWAIT;' AS NVARCHAR(MAX))
                           FROM #ForeignOwners f
                           FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL);

  -- RAISERROR, not THROW: this runs on SQL Server 2008 R2.
  RAISERROR('One or more tables in this quench are already owned by another product', 16, 1)
  RETURN
END
