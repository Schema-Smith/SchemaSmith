-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
IF OBJECT_ID('SchemaSmith.StageGraphKeyParts', 'P') IS NOT NULL DROP PROCEDURE SchemaSmith.StageGraphKeyParts
GO
-- Fills the caller's #GraphKeyParts ([object_id], [column_id], [graph_type], [PseudoName]) with every graph column
-- in the database. A node or edge table carries system columns whose names end in a per-table GUID; an index a
-- user declares over $from_id or $to_id is stored over two hidden columns each (the *_obj_id and *_id pair), and
-- the pseudo-column name is the only spelling a package can carry and CREATE INDEX accepts. PseudoName is $from_id
-- for the from_id column (graph_type 3) and $to_id for the to_id column (6); NULL for every other graph column,
-- which a key-part renderer then leaves out (the obj_id halves, 4 and 7) or an index filter excludes (graph_id, 1,
-- which only the engine's own GRAPH_UNIQUE_INDEX ever covers).
--
-- sys.columns.graph_type is 2017+, so the read is a version-guarded dynamic statement: this procedure is kindled
-- for the XML tier too, which reaches older binaries where a static reference fails at CREATE time. Empty below
-- 2017, where graph tables cannot exist. The caller creates the table, since a temp table created here would not
-- outlive this procedure.
CREATE PROCEDURE SchemaSmith.StageGraphKeyParts
AS
BEGIN
  SET NOCOUNT ON
  IF SchemaSmith.fn_ServerMajorVersion() >= 14
    EXEC sp_executesql N'
      INSERT INTO #GraphKeyParts ([object_id], [column_id], [graph_type], [PseudoName])
        SELECT c.[object_id], c.column_id, c.graph_type,
               CASE c.graph_type WHEN 3 THEN ''$from_id'' WHEN 6 THEN ''$to_id'' END
          FROM sys.columns c
         WHERE c.graph_type IS NOT NULL'
END
GO
