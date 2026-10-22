-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- FOR XML PATH twin of SchemaSmith.GenerateIndexedViewJson.sql for the compare/extraction side below the
-- FOR JSON binary floor (SQL Server pre-2016). Returns the SAME object shape as XML text: the single
-- <IndexedView> object plus its <Indexes> array carrying json:Array="true" (WITH XMLNAMESPACES) so a
-- one-index view does not collapse to an object when C# (ModelXmlSerializer.FromIngestXml) converts it back
-- to JSON for DeserializeObject<SqlServerIndexedView>. bit values are emitted as 'true'/'false' text so
-- Newtonsoft coerces them into the typed model. The definition-extraction logic is identical to the JSON twin.
IF OBJECT_ID('[SchemaSmith].[GenerateIndexedViewXml]') IS NOT NULL DROP FUNCTION [SchemaSmith].[GenerateIndexedViewXml]
GO
CREATE FUNCTION [SchemaSmith].[GenerateIndexedViewXml](@p_Schema SYSNAME, @p_ViewName SYSNAME)
RETURNS NVARCHAR(MAX)
AS
BEGIN
    DECLARE @result NVARCHAR(MAX);
    DECLARE @rawDef NVARCHAR(MAX);
    DECLARE @definition NVARCHAR(MAX);
    DECLARE @objectId INT;

    SELECT @objectId = v.object_id,
           @rawDef = m.definition
      FROM sys.views v
     INNER JOIN sys.sql_modules m ON v.object_id = m.object_id
     INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
     WHERE s.name = @p_Schema AND v.name = @p_ViewName
       AND OBJECTPROPERTY(v.object_id, 'IsIndexed') = 1;

    IF @objectId IS NULL RETURN NULL;

    -- Extract SELECT query from definition
    -- Pattern: CREATE VIEW ... WITH SCHEMABINDING AS <select>
    DECLARE @bindingPos INT = CHARINDEX('SCHEMABINDING', UPPER(@rawDef));
    IF @bindingPos > 0
    BEGIN
        DECLARE @afterBinding NVARCHAR(MAX) = SUBSTRING(@rawDef, @bindingPos + 13, LEN(@rawDef));
        -- Find first AS keyword after SCHEMABINDING (word boundary: space before, space/newline after)
        DECLARE @asPos INT = PATINDEX('%[^A-Za-z_]AS[^A-Za-z_]%', @afterBinding);
        IF @asPos > 0
            SET @definition = LTRIM(RTRIM(SUBSTRING(@afterBinding, @asPos + 3, LEN(@afterBinding))));
        ELSE
            SET @definition = @rawDef;
    END
    ELSE
        SET @definition = @rawDef;

    ;WITH XMLNAMESPACES ('http://james.newtonking.com/projects/json' AS json)
    SELECT @result = CAST((
        SELECT
            [SchemaSmith].[fn_SafeBracketWrap](s.name) AS [Schema],
            [SchemaSmith].[fn_SafeBracketWrap](v.name) AS [Name],
            @definition AS [Definition],
            '' AS [ShouldApplyExpression],
            (SELECT CONVERT(NVARCHAR(MAX), ep.[value]) FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.major_id = v.object_id AND ep.minor_id = 0 AND ep.[name] = 'MS_Description') AS [Description],
            (
                SELECT
                    'true' AS [@json:Array],
                    [SchemaSmith].[fn_SafeBracketWrap](i.name) AS [Name],
                    CASE WHEN i.is_unique = 1 THEN 'true' ELSE 'false' END AS [Unique],
                    CASE WHEN i.type = 1 THEN 'true' ELSE 'false' END AS [Clustered],
                    CASE WHEN i.type IN (5, 6) THEN 'true' ELSE 'false' END AS [ColumnStore],
                    STUFF((SELECT ', ' + [SchemaSmith].[fn_SafeBracketWrap](c.name) +
                                        CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                             FROM sys.index_columns ic
                            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                            WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                            ORDER BY ic.key_ordinal FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS [IndexColumns],
                    STUFF((SELECT ', ' + [SchemaSmith].[fn_SafeBracketWrap](c2.name)
                             FROM sys.index_columns ic2
                            INNER JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
                            WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 1
                            ORDER BY ic2.index_column_id FOR XML PATH(''), TYPE).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS [IncludeColumns],
                    CASE
                        WHEN p.data_compression_desc IS NOT NULL AND p.data_compression_desc != 'NONE'
                        THEN p.data_compression_desc
                        ELSE NULL
                    END AS [CompressionType],
                    CASE WHEN i.fill_factor > 0 THEN i.fill_factor ELSE NULL END AS [FillFactor],
                    CASE WHEN i.is_padded = 1 THEN 'true' ELSE 'false' END AS [PadIndex]
                  FROM sys.indexes i
                  LEFT JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id AND p.partition_number = 1
                 WHERE i.object_id = v.object_id AND i.type > 0
                 ORDER BY CASE WHEN i.type = 1 THEN 0 ELSE 1 END, i.name
                   FOR XML PATH('Indexes'), TYPE
            )
          FROM sys.views v
         INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
         WHERE v.object_id = @objectId
           FOR XML PATH('IndexedView')
    ) AS NVARCHAR(MAX));

    RETURN @result;
END
