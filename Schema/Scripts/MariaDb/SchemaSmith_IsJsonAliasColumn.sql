-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- A column is MariaDB's rendering of JSON when it is longtext in utf8mb4_bin and carries the column-level json_valid
-- check the engine adds from 10.4.3 (SchemaSmith_JsonAliasCheck). The check is found by its name, the column's, which
-- is MariaDB's rule for a column-level check on every version (SchemaSmith_ColumnLevelCheck); a table-level check with
-- the same name and clause also matches, and the deploy's level-aware repair (10.5+) takes it from there. Below
-- 10.4.3 there is no check to recognise the column by, so it is the longtext it reports.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_IsJsonAliasColumn//
CREATE FUNCTION SchemaSmith_IsJsonAliasColumn(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_Expected TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_IsShape INT DEFAULT 0;
    SET v_Expected = SchemaSmith_JsonAliasCheck(p_Column);
    IF v_Expected IS NULL THEN
        RETURN 0;
    END IF;
    SELECT COUNT(*) INTO v_IsShape
      FROM INFORMATION_SCHEMA.COLUMNS c
     WHERE c.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(c.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(p_Schema)
       AND SchemaSmith_IdentifierKey(c.TABLE_NAME) = SchemaSmith_IdentifierKey(p_Table)
       AND SchemaSmith_NameKeyCI(c.COLUMN_NAME) = SchemaSmith_NameKeyCI(p_Column)
       AND c.COLUMN_TYPE = 'longtext'
       AND c.COLLATION_NAME = 'utf8mb4_bin';
    IF v_IsShape = 0 THEN
        RETURN 0;
    END IF;
    RETURN IF(CAST(COALESCE(SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_Schema, p_Table, p_Column)), '') AS BINARY)
              = CAST(SchemaSmith_NormalizeCheckExpression(v_Expected) AS BINARY), 1, 0);
END //
DELIMITER ;
