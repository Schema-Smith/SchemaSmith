-- LEGACY variant — folder-gated to run ONLY where the database compatibility
-- level is < 130 (see Template.json -> Shred/Legacy). Below compat 130 OPENJSON
-- parse-errors, so the reader shreds the XML twin token (TableXml) with XQuery
-- (.nodes()/.value()) instead — the same model, encoded as ingest XML.
--
-- The TableXml token resolves to a <Tables><Table>...</Table></Tables> document, one
-- <Table> per table in this template, each column a <Columns> element. This
-- mirrors exactly how SchemaSmith's own built-in ingest shreds the payload below
-- compat 130 (ParseTableXmlIntoTempTables.sql) — the reader's hand-written shred
-- and the engine's are the same shape.
--
-- THE FOOTGUN THIS VARIANT TEACHES: in XML every scalar is text, so the boolean
-- Nullable flag arrives as the literal 'true'/'false'. A direct CAST('true' AS
-- BIT) errors ("Msg 245 ... conversion ... to data type bit"). Convert it through
-- an explicit CASE instead. In the JSON variant this was a typed boolean and
-- needed none of this. Both encodings leave Nullable out for a NOT NULL column,
-- so a missing element counts as 'false'.

DECLARE @model xml = N'{{TableXml}}';

DELETE FROM dbo.TableCatalog;

INSERT INTO dbo.TableCatalog ([TableName], [ColumnName], [IsNullable], [Encoding])
SELECT t.n.value('(Name/text())[1]', 'nvarchar(128)'),
       c.col.value('(Name/text())[1]', 'nvarchar(128)'),
       CONVERT(BIT, CASE LOWER(ISNULL(c.col.value('(Nullable/text())[1]', 'varchar(8)'), 'false'))
                      WHEN 'true'  THEN 1
                      WHEN 'false' THEN 0
                    END),
       N'XML (.nodes/.value)'
FROM @model.nodes('/Tables/Table') AS t(n)
CROSS APPLY t.n.nodes('Columns') AS c(col);
