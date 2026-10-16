-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_IsExpressionDefault//
CREATE FUNCTION SchemaSmith_IsExpressionDefault(
    p_Default TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Extra VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    -- Is this live column default an EXPRESSION, by the catalog's own account (#520)? MySQL flags one with
    -- EXTRA = DEFAULT_GENERATED and renders a literal unquoted; MariaDB quotes a literal and writes an expression bare.
    -- The temporal keywords are flagged on MySQL too, but they are ordinary column defaults every version accepts
    -- bare, so they are not expressions for this purpose -- the same exception the emit side keeps.
    IF p_Default IS NULL THEN RETURN 0; END IF;
    IF UPPER(TRIM(p_Default)) REGEXP '^(CURRENT_TIMESTAMP|NOW|LOCALTIME|LOCALTIMESTAMP|CURRENT_DATE|CURDATE|CURRENT_TIME|CURTIME|UTC_TIMESTAMP|UTC_DATE|UTC_TIME)(\\([0-9]*\\))?$' THEN
        RETURN 0;
    END IF;
    IF VERSION() LIKE '%MariaDB%' THEN
        IF p_Default = 'NULL' THEN RETURN 0; END IF;                       -- the no-default marker
        IF LEFT(p_Default, 1) = '''' THEN RETURN 0; END IF;                -- a quoted literal
        IF TRIM(p_Default) REGEXP '^-?[0-9]+(\\.[0-9]+)?([eE][-+]?[0-9]+)?$' THEN RETURN 0; END IF;   -- a number
        IF LOWER(LEFT(p_Default, 2)) IN ('b''', '0x') THEN RETURN 0; END IF;                       -- a bit or hex literal
        RETURN 1;
    END IF;
    RETURN IF(IFNULL(p_Extra, '') LIKE '%DEFAULT_GENERATED%', 1, 0);
END //
DELIMITER ;
