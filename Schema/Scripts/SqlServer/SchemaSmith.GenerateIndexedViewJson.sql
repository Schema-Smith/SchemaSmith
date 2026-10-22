-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[GenerateIndexedViewJson]') IS NOT NULL DROP FUNCTION [SchemaSmith].[GenerateIndexedViewJson]
GO
CREATE FUNCTION [SchemaSmith].[GenerateIndexedViewJson](@p_Schema SYSNAME, @p_ViewName SYSNAME)
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

    SELECT @result = (
        SELECT
            [SchemaSmith].[fn_SafeBracketWrap](s.name) AS [Schema],
            [SchemaSmith].[fn_SafeBracketWrap](v.name) AS [Name],
            @definition AS [Definition],
            '' AS [ShouldApplyExpression],
            (SELECT CONVERT(NVARCHAR(MAX), ep.[value]) FROM sys.extended_properties ep WHERE ep.class = 1 AND ep.major_id = v.object_id AND ep.minor_id = 0 AND ep.[name] = 'MS_Description') AS [Description],
            (
                SELECT
                    [SchemaSmith].[fn_SafeBracketWrap](i.name) AS [Name],
                    CAST(CASE WHEN i.is_unique = 1 THEN 1 ELSE 0 END AS BIT) AS [Unique],
                    CAST(CASE WHEN i.type = 1 THEN 1 ELSE 0 END AS BIT) AS [Clustered],
                    CAST(CASE WHEN i.type IN (5, 6) THEN 1 ELSE 0 END AS BIT) AS [ColumnStore],
                    STRING_AGG(
                        CASE WHEN ic.is_included_column = 0
                            THEN [SchemaSmith].[fn_SafeBracketWrap](c.name) +
                                CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                            ELSE NULL
                        END,
                        ', '
                    ) WITHIN GROUP (ORDER BY ic.key_ordinal) AS [IndexColumns],
                    (
                        SELECT STRING_AGG([SchemaSmith].[fn_SafeBracketWrap](c2.name), ', ')
                        WITHIN GROUP (ORDER BY ic2.index_column_id)
                          FROM sys.index_columns ic2
                         INNER JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
                         WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id AND ic2.is_included_column = 1
                    ) AS [IncludeColumns],
                    CASE
                        WHEN p.data_compression_desc IS NOT NULL AND p.data_compression_desc != 'NONE'
                        THEN p.data_compression_desc
                        ELSE NULL
                    END AS [CompressionType],
                    CASE WHEN i.fill_factor > 0 THEN i.fill_factor ELSE NULL END AS [FillFactor],
                    CONVERT(BIT, i.is_padded) AS [PadIndex],
                    (SELECT CONVERT(NVARCHAR(MAX), ep.[value]) FROM sys.extended_properties ep WHERE ep.class = 7 AND ep.major_id = i.object_id AND ep.minor_id = i.index_id AND ep.[name] = 'MS_Description') AS [Description]
                  FROM sys.indexes i
                 INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                 INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                  LEFT JOIN sys.partitions p ON i.object_id = p.object_id AND i.index_id = p.index_id AND p.partition_number = 1
                 WHERE i.object_id = v.object_id AND i.type > 0
                 GROUP BY i.name, i.is_unique, i.type, i.fill_factor, i.is_padded, p.data_compression_desc, i.object_id, i.index_id
                 ORDER BY CASE WHEN i.type = 1 THEN 0 ELSE 1 END, i.name
                   FOR JSON PATH
            ) AS [Indexes]
          FROM sys.views v
         INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
         WHERE v.object_id = @objectId
           FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    RETURN @result;
END
