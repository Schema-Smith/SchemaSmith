-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_OnlineAlterColumnForm') IS NOT NULL DROP FUNCTION SchemaSmith.fn_OnlineAlterColumnForm
GO
CREATE FUNCTION SchemaSmith.fn_OnlineAlterColumnForm(@Mode VARCHAR(7), @ObjectId INT, @ColumnName NVARCHAR(500), @NewDataType NVARCHAR(MAX),
                                                     @Encrypted BIT)
RETURNS TABLE
AS
-- How ALTER COLUMN runs under OnlineApply: [Form] NULL under OFF, 'online' (WITH (ONLINE = ON), 2016+), or
-- 'unavailable' with [Reason]. The refusals are the engine's own, probed on 2022: a text, ntext, image, CLR or
-- FILESTREAM column on either side of the change and an XML column bound to a schema collection (11427, 11428), a
-- system-versioned table (13764), change tracking or merge replication (11429) and a columnstore index on the table
-- (35352). An Always Encrypted column is left offline too. The temporal and memory-optimized tests go through
-- OBJECTPROPERTY because their catalog columns do not exist below 2016 and 2014, and naming one would fail this CREATE.
RETURN
  SELECT [Form] = CASE WHEN @Mode = 'OFF' THEN NULL WHEN a.[Reason] IS NOT NULL THEN 'unavailable' ELSE 'online' END,
         [Reason] = CASE WHEN @Mode = 'OFF' THEN NULL ELSE a.[Reason] END
    FROM (SELECT [Reason] =
            CASE WHEN SchemaSmith.fn_OnlineOperationsAvailable() = 0
                 THEN 'online column changes need Enterprise or Developer edition, or Azure SQL, on SQL Server 2016 or later'
                 WHEN SchemaSmith.fn_ServerMajorVersion() < 13 THEN 'online column changes need SQL Server 2016 or later'
                 WHEN ISNULL(OBJECTPROPERTY(@ObjectId, 'TableTemporalType'), 0) <> 0 THEN 'the table is system-versioned'
                 WHEN ISNULL(OBJECTPROPERTY(@ObjectId, 'TableIsMemoryOptimized'), 0) = 1 THEN 'the table is memory-optimized'
                 WHEN EXISTS (SELECT 1 FROM sys.change_tracking_tables ct WHERE ct.[object_id] = @ObjectId)
                      OR EXISTS (SELECT 1 FROM sys.tables st WHERE st.[object_id] = @ObjectId AND st.is_merge_published = 1)
                 THEN 'the table has change tracking or merge replication'
                 WHEN EXISTS (SELECT 1 FROM sys.indexes ci WHERE ci.[object_id] = @ObjectId AND ci.[type] IN (5, 6))
                 THEN 'the table has a columnstore index'
                 WHEN @Encrypted = 1 THEN 'the column is encrypted'
                 WHEN EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                               WHERE c.[object_id] = @ObjectId AND QUOTENAME(c.[name]) = @ColumnName
                                 AND (c.system_type_id IN (34, 35, 99, 240) OR c.is_filestream = 1 OR ty.is_assembly_type = 1 OR c.xml_collection_id <> 0))
                      OR UPPER(LTRIM(@NewDataType)) LIKE 'TEXT%' OR UPPER(LTRIM(@NewDataType)) LIKE 'NTEXT%' OR UPPER(LTRIM(@NewDataType)) LIKE 'IMAGE%'
                      OR UPPER(LTRIM(@NewDataType)) LIKE 'GEOMETRY%' OR UPPER(LTRIM(@NewDataType)) LIKE 'GEOGRAPHY%' OR UPPER(LTRIM(@NewDataType)) LIKE 'HIERARCHYID%'
                      OR UPPER(LTRIM(@NewDataType)) LIKE 'XML%(%' OR UPPER(@NewDataType) LIKE '%FILESTREAM%'
                 THEN 'the column is (or becomes) text, ntext, image, a CLR type, FILESTREAM or schema-bound XML'
                 END) a
