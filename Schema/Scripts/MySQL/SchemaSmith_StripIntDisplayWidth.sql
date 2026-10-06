-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_StripIntDisplayWidth//

CREATE FUNCTION SchemaSmith_StripIntDisplayWidth(
    p_DataType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- Removes the deprecated integer/YEAR display width — INT(10) -> INT, BIGINT(20) UNSIGNED ->
    -- BIGINT UNSIGNED, YEAR(4) -> YEAR — so a column's type is not falsely reported as "modified".
    --
    -- WHY: MySQL 8.0.19+ dropped the display width from INFORMATION_SCHEMA.COLUMN_TYPE for integer
    -- types (and YEAR); MariaDB still reports it (`int(10) unsigned`, `year(4)`). A product authored
    -- against the MySQL 8.0 form (`INT UNSIGNED`, `YEAR`) therefore never matches the MariaDB
    -- read-back, so every such column phantom-drops/recreates forever. The width is cosmetic — both
    -- engines ignore it for storage — so stripping it on both sides of the comparison cannot hide a
    -- real change (same soundness as the existing DECIMAL->NUMERIC synonym fold). On MySQL 8.0 the
    -- width is already absent, so this is a no-op there.
    --
    -- Only a pure display width (KEYWORD(<digits>)) on an integer/YEAR keyword is stripped;
    -- DECIMAL(p,s), VARCHAR(n), CHAR(n), BIT(n), ENUM(...)/SET(...) all keep their parenthesized content.
    DECLARE v_Paren INT;
    DECLARE v_Close INT;
    DECLARE v_Keyword TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Inside TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    IF p_DataType IS NULL THEN
        RETURN NULL;
    END IF;

    SET v_Paren = LOCATE('(', p_DataType);
    IF v_Paren = 0 THEN
        RETURN p_DataType;
    END IF;

    SET v_Keyword = UPPER(TRIM(LEFT(p_DataType, v_Paren - 1)));
    IF v_Keyword NOT IN ('TINYINT', 'SMALLINT', 'MEDIUMINT', 'INT', 'INTEGER', 'BIGINT', 'YEAR') THEN
        RETURN p_DataType;
    END IF;

    SET v_Close = LOCATE(')', p_DataType, v_Paren);
    IF v_Close = 0 THEN
        RETURN p_DataType;
    END IF;

    SET v_Inside = SUBSTRING(p_DataType, v_Paren + 1, v_Close - v_Paren - 1);
    IF v_Inside NOT REGEXP '^[0-9]+$' THEN
        RETURN p_DataType;
    END IF;

    -- Keep the keyword (and any UNSIGNED/ZEROFILL suffix after the width), drop the (digits).
    RETURN CONCAT(LEFT(p_DataType, v_Paren - 1), SUBSTRING(p_DataType, v_Close + 1));
END //

DELIMITER ;
