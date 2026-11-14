-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_PartitionCompression') IS NOT NULL DROP FUNCTION SchemaSmith.fn_PartitionCompression
GO
CREATE FUNCTION SchemaSmith.fn_PartitionCompression(@ObjectId INT, @IndexId INT)
  RETURNS NVARCHAR(60)
AS
BEGIN
  -- An index's compression, read once per index rather than once per partition: the single value its partitions
  -- share, or MIXED when they differ -- the word SchemaTongs writes for that, which a deploy leaves alone.
  RETURN (SELECT CASE WHEN MIN(p.data_compression_desc) = MAX(p.data_compression_desc) THEN MIN(p.data_compression_desc)
                      WHEN COUNT(*) > 0 THEN N'MIXED' END
            FROM sys.partitions p
           WHERE p.[object_id] = @ObjectId AND p.index_id = @IndexId)
END
