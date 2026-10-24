-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.StageOnlineForms', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.StageOnlineForms
GO
CREATE PROCEDURE SchemaSmith.StageOnlineForms
  @OnlineApplyMaxDuration INT = NULL
AS
BEGIN
  -- Fills the caller's #OnlineForms with how each declared index on a table in the caller's #OnlineTables would be built
  -- under OnlineApply. The caller resolves each table's mode, OFF for a table this deploy creates (it is empty). XML,
  -- spatial and JSON indexes have no online build.
  SET NOCOUNT ON
  DELETE FROM #OnlineForms
  -- A table with no row reads as OFF everywhere #OnlineForms is joined, so a deploy with every table OFF (the default) stages
  -- nothing -- and never compiles the statement below, which recompiles on every call because the caller re-creates both tables.
  IF NOT EXISTS (SELECT 1 FROM #OnlineTables WITH (NOLOCK) WHERE [Mode] <> 'OFF') RETURN
  INSERT INTO #OnlineForms ([Kind], [Schema], [TableName], [IndexName], [Mode], [Form], [Options], [Reason])
    SELECT 'index', i.[Schema], i.[TableName], SchemaSmith.fn_StripBracketWrapping(i.[IndexName]), ot.[Mode], f.[Form], f.[Options], f.[Reason]
      FROM #Indexes i WITH (NOLOCK)
      JOIN #OnlineTables ot WITH (NOLOCK) ON ot.[Schema] = i.[Schema] AND ot.[Name] = i.[TableName]
      CROSS APPLY SchemaSmith.fn_OnlineIndexForm(ot.[Mode], OBJECT_ID(i.[Schema] + '.' + i.[TableName]), i.[IndexColumns], i.[ColumnStore],
                                                 CASE WHEN i.[PrimaryKey] = 1 OR i.[UniqueConstraint] = 1 THEN 1 ELSE 0 END, @OnlineApplyMaxDuration) f
    UNION ALL
    SELECT k.[Kind], k.[Schema], k.[TableName], SchemaSmith.fn_StripBracketWrapping(k.[IndexName]), ot.[Mode],
           CASE WHEN ot.[Mode] = 'OFF' THEN NULL ELSE 'unavailable' END, '', CASE WHEN ot.[Mode] = 'OFF' THEN NULL ELSE k.[Reason] END
      FROM (SELECT [Kind] = 'xmlIndex', [Schema], [TableName], [IndexName], [Reason] = 'an XML index is built offline' FROM #XmlIndexes WITH (NOLOCK)
            UNION ALL
            SELECT 'spatialIndex', [Schema], [TableName], [IndexName], 'a spatial index is built offline' FROM #SpatialIndexes WITH (NOLOCK)
            UNION ALL
            SELECT 'jsonIndex', [Schema], [TableName], [IndexName], 'a JSON index is built offline' FROM #JsonIndexes WITH (NOLOCK)) k
      JOIN #OnlineTables ot WITH (NOLOCK) ON ot.[Schema] = k.[Schema] AND ot.[Name] = k.[TableName]
END
