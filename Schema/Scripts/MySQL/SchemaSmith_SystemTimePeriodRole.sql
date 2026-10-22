-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_SystemTimePeriodRole//

-- 1 (ROW START) or 2 (ROW END) for a system-versioned table's explicitly declared period column, 0 for any other column.
-- A number, so a caller compares it without meeting the connection's collation.
-- GENERATION_EXPRESSION carries the literal ROW START / ROW END for those (see SchemaSmith_IsSystemTimePeriodColumn
-- for why that column, not IS_SYSTEM_TIME_PERIOD_*); MySQL has the column too and never these values, so one body
-- serves both engines. The implicit pair is absent from INFORMATION_SCHEMA.COLUMNS altogether.
CREATE FUNCTION SchemaSmith_SystemTimePeriodRole(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_ColumnKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_NameKeyCI(p_Column);
    RETURN COALESCE((
        SELECT CASE c.GENERATION_EXPRESSION WHEN 'ROW START' THEN 1 WHEN 'ROW END' THEN 2 ELSE 0 END
        FROM INFORMATION_SCHEMA.COLUMNS c
        WHERE c.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(c.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(c.TABLE_NAME) = v_TableKey
          AND SchemaSmith_NameKeyCI(c.COLUMN_NAME) = v_ColumnKey
        LIMIT 1), 0);
END //

DELIMITER ;
