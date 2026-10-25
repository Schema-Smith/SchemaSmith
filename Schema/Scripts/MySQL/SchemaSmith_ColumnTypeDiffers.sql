-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ColumnTypeDiffers//

-- 1 when a declared column type (the parse's CompareDataType) is not the live COLUMN_TYPE, by the comparison STEP 3
-- of ModifiedTableQuench makes before it emits a MODIFY COLUMN. STEP 2.9 asks it first, to take down the foreign keys
-- on and pointing at such a column: the engines refuse most type changes to a column a foreign key uses ("Cannot change
-- column ...: used in a foreign key constraint"), and SchemaSmith_TypeChangeKeepsForeignKey names the widenings they
-- make in place. A number, not a string, so no caller's collation meets it.
CREATE FUNCTION SchemaSmith_ColumnTypeDiffers(
    p_LiveType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_DeclaredType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    IF p_LiveType IS NULL OR p_DeclaredType IS NULL THEN
        RETURN 0;
    END IF;
    IF UPPER(p_DeclaredType) LIKE 'ENUM%' OR UPPER(p_DeclaredType) LIKE 'SET%'
       OR UPPER(p_LiveType) LIKE 'ENUM%' OR UPPER(p_LiveType) LIKE 'SET%' THEN
        RETURN IF(CAST(SchemaSmith_UpperDataType(p_LiveType) AS BINARY) != CAST(SchemaSmith_UpperDataType(p_DeclaredType) AS BINARY), 1, 0);
    END IF;
    RETURN IF(SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(p_LiveType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
              != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(p_DeclaredType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')), 1, 0);
END//

DELIMITER ;
