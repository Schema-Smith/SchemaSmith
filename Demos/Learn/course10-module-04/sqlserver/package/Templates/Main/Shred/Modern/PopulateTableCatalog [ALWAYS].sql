-- MODERN variant — folder-gated to run ONLY where the database compatibility
-- level is >= 130 (see Template.json -> Shred/Modern). At compat 130+, OPENJSON
-- parses, so the reader shreds the JSON model-payload token (TableSchema).
--
-- The TableSchema token resolves to the full model as a JSON array — one object
-- per table in this template. In JSON the Nullable flag arrives as a real boolean,
-- so OPENJSON ... WITH ([IsNullable] BIT '$.Nullable') reads it straight into a BIT
-- with no conversion. Contrast the Legacy variant, where the same flag arrives as
-- the text 'true'/'false' and needs a CASE. The payload leaves Nullable out for a
-- column that is NOT NULL, so a missing flag reads as NULL: ISNULL makes it 0.
--
-- Paired with Shred/Legacy/PopulateTableCatalog.sql (gated on compatibility level
-- < 130). Exactly one of the two folders applies per database — the engine picks
-- the encoding for its own built-in ingest invisibly; THIS choice, in the
-- reader's own SQL, is the one the reader makes.
--
-- (The token name above is written WITHOUT its double braces on purpose: SchemaSmith
-- expands tokens everywhere, including inside -- comments, and the multi-line JSON
-- model would break out of the comment onto later lines as live SQL. The one real
-- token use is the string literal below.)

DECLARE @model NVARCHAR(MAX) = N'{{TableSchema}}';

DELETE FROM dbo.TableCatalog;

INSERT INTO dbo.TableCatalog ([TableName], [ColumnName], [IsNullable], [Encoding])
SELECT tbl.[Name], col.[ColumnName], ISNULL(col.[IsNullable], 0), N'JSON (OPENJSON)'
FROM OPENJSON(@model)
       WITH ([Name]    NVARCHAR(128) '$.Name',
             [Columns] NVARCHAR(MAX) '$.Columns' AS JSON) AS tbl
CROSS APPLY OPENJSON(tbl.[Columns])
       WITH ([ColumnName] NVARCHAR(128) '$.Name',
             [IsNullable] BIT           '$.Nullable') AS col;
