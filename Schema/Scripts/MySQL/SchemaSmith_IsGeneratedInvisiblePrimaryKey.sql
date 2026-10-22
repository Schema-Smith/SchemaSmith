-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IsGeneratedInvisiblePrimaryKey//

CREATE FUNCTION SchemaSmith_IsGeneratedInvisiblePrimaryKey(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- Is this the key MySQL 8.0.30+ generates for a table created without one (sql_generate_invisible_primary_key):
    -- my_row_id, invisible, auto-increment, and the whole primary key? The server made it, not the package, so it is
    -- neither extracted nor dropped by absence. Always 0 on MariaDB (override), which has no such key.
    IF p_Column IS NULL OR LOWER(p_Column) <> 'my_row_id' THEN
        RETURN 0;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c
                    WHERE CONVERT(c.TABLE_SCHEMA USING utf8mb4) COLLATE utf8mb4_unicode_ci = p_Schema
                      AND CONVERT(c.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci = p_Table
                      AND CONVERT(c.COLUMN_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci = p_Column
                      AND c.EXTRA LIKE '%auto_increment%' AND c.EXTRA LIKE '%INVISIBLE%') THEN
        RETURN 0;
    END IF;
    RETURN (SELECT CASE WHEN COUNT(*) = 1 AND LOWER(MAX(s.COLUMN_NAME)) = 'my_row_id' THEN 1 ELSE 0 END
              FROM INFORMATION_SCHEMA.STATISTICS s
             WHERE CONVERT(s.TABLE_SCHEMA USING utf8mb4) COLLATE utf8mb4_unicode_ci = p_Schema
               AND CONVERT(s.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci = p_Table
               AND s.INDEX_NAME = 'PRIMARY');
END //

DELIMITER ;
