-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_UpperDataType//

CREATE FUNCTION SchemaSmith_UpperDataType(
    p_DataType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- Upper-cases a column's data-type keyword while preserving the quoted literals inside
    -- enum(...)/set(...). Those values are case-sensitive DATA, not keywords: a naive
    -- UPPER(DataType) corrupted enum('web') into ENUM('WEB'), diverging the deployed
    -- definition from the declared one (and, once deployed, sticking because the enum/set
    -- comparison was case-insensitive). Every other type has no case-sensitive content
    -- inside its parens (numeric args), so it takes the plain UPPER path.
    DECLARE v_Paren INT;
    DECLARE v_Keyword TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    IF p_DataType IS NULL THEN
        RETURN NULL;
    END IF;

    SET v_Paren = LOCATE('(', p_DataType);
    IF v_Paren = 0 THEN
        RETURN UPPER(p_DataType);
    END IF;

    SET v_Keyword = UPPER(TRIM(LEFT(p_DataType, v_Paren - 1)));
    IF v_Keyword = 'ENUM' OR v_Keyword = 'SET' THEN
        -- Uppercase only the leading keyword; keep the parenthesized value list verbatim.
        RETURN CONCAT(UPPER(LEFT(p_DataType, v_Paren - 1)), SUBSTRING(p_DataType, v_Paren));
    END IF;

    RETURN UPPER(p_DataType);
END //

DELIMITER ;
