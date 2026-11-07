-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_PartitionCompressionDrift') IS NOT NULL DROP FUNCTION SchemaSmith.fn_PartitionCompressionDrift
GO
CREATE FUNCTION SchemaSmith.fn_PartitionCompressionDrift(@ObjectId INT, @IndexId INT, @Desired NVARCHAR(60))
  RETURNS TABLE
AS
  -- How many of an index's (or, for index 0 or 1, a table's) partitions are not compressed @Desired, out of how many,
  -- and which: a compression fixup rebuilds only those, and the whole object in one statement when it is all of them.
  RETURN
    SELECT [Differing] = ISNULL(SUM(CASE WHEN p.data_compression_desc COLLATE DATABASE_DEFAULT <> @Desired THEN 1 ELSE 0 END), 0),
           [Total] = COUNT(*),
           [List] = STUFF((SELECT ', ' + CAST(d.partition_number AS NVARCHAR(10))
                             FROM sys.partitions d
                            WHERE d.[object_id] = @ObjectId AND d.index_id = @IndexId
                              AND d.data_compression_desc COLLATE DATABASE_DEFAULT <> @Desired
                            ORDER BY d.partition_number
                              FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
      FROM sys.partitions p
     WHERE p.[object_id] = @ObjectId AND p.index_id = @IndexId
