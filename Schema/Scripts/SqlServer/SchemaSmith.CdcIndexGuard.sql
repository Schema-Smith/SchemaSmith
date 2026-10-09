-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Refuses, before any index is renamed or dropped, a deploy that would drop or rename an index a CDC capture instance
-- identifies rows by. SQL Server itself refuses the drop (22979, 22983), but only when the statement runs, part-way
-- through the index work. It allows the rename, yet the instance keeps the old name, and the renamed index can then
-- be dropped with nothing to stop it. Every instance counts, not only the newest: a rotation keeps the old one for
-- draining, and it still names its index.
--
-- Reads ModifiedTableQuench's index work tables, so it is a procedure for the same compile-cost reason as
-- ValidateDeclaredTableAttributes, and ModifiedTableQuench calls it only when a table in the run is tracked by CDC.
IF OBJECT_ID('SchemaSmith.CdcIndexGuard', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.CdcIndexGuard
GO
CREATE PROCEDURE SchemaSmith.CdcIndexGuard
    @DropIndexesRemovedFromProduct BIT,
    @DropUnknownIndexes BIT
AS
BEGIN
  SET NOCOUNT ON
  DECLARE @v_Blocked NVARCHAR(MAX) =
    STUFF((SELECT ', ' + x.[Schema] + '.' + x.[TableName] + '.' + QUOTENAME(x.IndexName) + ' (' + x.[Action] + ', named by ' + ct.capture_instance + ')'
             FROM (SELECT [Schema] = r.[Schema] COLLATE DATABASE_DEFAULT, [TableName] = r.[TableName] COLLATE DATABASE_DEFAULT,
                          IndexName = r.[OldName] COLLATE DATABASE_DEFAULT, [Action] = 'renamed'
                     FROM #IndexRenames r WITH (NOLOCK)
                   UNION
                   SELECT ir.[Schema], ir.[TableName], SchemaSmith.fn_StripBracketWrapping(ir.[IndexName]), 'dropped'
                     FROM #IndexesRemovedFromProduct ir WITH (NOLOCK)
                    WHERE @DropIndexesRemovedFromProduct = 1
                      AND ISNULL((SELECT t.[DropIndexesRemovedFromProduct] FROM #Tables t WITH (NOLOCK) WHERE t.[Schema] = ir.[Schema] AND t.[Name] = ir.[TableName]), 1) = 1
                      -- Its old name is gone from the package because it is being renamed, which is reported as that.
                      AND NOT EXISTS (SELECT 1 FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ir.[Schema] AND rn.[TableName] = ir.[TableName] AND rn.[OldName] = SchemaSmith.fn_StripBracketWrapping(ir.[IndexName]))
                   UNION
                   SELECT c.[Schema], c.[TableName], SchemaSmith.fn_StripBracketWrapping(c.[IndexName]), 'dropped'
                     FROM #IndexesToDropForColumnChanges c WITH (NOLOCK)
                   UNION
                   SELECT c.[Schema], c.[TableName], SchemaSmith.fn_StripBracketWrapping(c.[IndexName]), 'dropped'
                     FROM #IndexChanges c WITH (NOLOCK)
                   UNION
                   SELECT ei.[xSchema], ei.[xTableName], ei.[xIndexName], 'dropped'
                     FROM #ExistingIndexes ei WITH (NOLOCK)
                    WHERE @DropUnknownIndexes = 1
                      AND NOT EXISTS (SELECT 1 FROM #Indexes i WITH (NOLOCK) WHERE i.[Schema] = ei.[xSchema] AND i.[TableName] = ei.[xTableName] AND SchemaSmith.fn_StripBracketWrapping(i.[IndexName]) = ei.[xIndexName])
                      AND NOT EXISTS (SELECT 1 FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = ei.[xSchema] AND rn.[TableName] = ei.[xTableName] AND rn.[OldName] = ei.[xIndexName])
                   UNION
                   -- A declared clustered index that does not exist yet displaces the existing one, whatever its name --
                   -- unless the existing one is being renamed to it.
                   SELECT d.[Schema], d.[TableName], si.[name], 'dropped'
                     FROM (SELECT DISTINCT i.[Schema], i.[TableName] FROM #Indexes i WITH (NOLOCK)
                            WHERE i.[Clustered] = 1
                              AND NOT EXISTS (SELECT 1 FROM sys.indexes x WHERE x.[object_id] = OBJECT_ID(i.[Schema] + '.' + i.[TableName]) AND x.[name] = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))
                              AND NOT EXISTS (SELECT 1 FROM #IndexRenames rn WITH (NOLOCK) WHERE rn.[Schema] = i.[Schema] AND rn.[TableName] = i.[TableName] AND SchemaSmith.fn_StripBracketWrapping(rn.[NewName]) = SchemaSmith.fn_StripBracketWrapping(i.[IndexName]))) d
                     JOIN sys.indexes si ON si.[object_id] = OBJECT_ID(d.[Schema] + '.' + d.[TableName]) AND si.[type] = 1) x
             JOIN cdc.change_tables ct WITH (NOLOCK) ON ct.source_object_id = OBJECT_ID(x.[Schema] + '.' + x.[TableName]) AND ct.index_name = x.IndexName
             ORDER BY x.[Schema], x.[TableName], x.IndexName, ct.capture_instance
             FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '')
  IF @v_Blocked IS NOT NULL
    RAISERROR('CDC identifies rows by an index this deploy would drop or rename: %s. SQL Server will not drop an index a capture instance uses, and a rename leaves the instance naming an index that is gone. Nothing has been dropped or renamed. To change the index, set CdcIndexName to another unique index and deploy, which rotates the table to a capture instance on it; then drain each listed instance, drop it (EXEC sys.sp_cdc_disable_table @source_schema = N''<schema>'', @source_name = N''<table>'', @capture_instance = N''<name>''), and re-run. If it is the table''s only instance, the re-run enables CDC again after the index change.', 16, 1, @v_Blocked)
END
