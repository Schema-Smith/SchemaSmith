-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_ExpressionDefaultsEqual//
CREATE FUNCTION SchemaSmith_ExpressionDefaultsEqual(
    p_LiveDefault TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Extra VARCHAR(256) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Declared TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    -- Does a declared expression default equal the live one, by the one fold the renderings justify (#520)? The
    -- engine keeps a single outer pair of parentheses on an operator expression and none on a function call, while a
    -- declaration carries the pair the DDL grammar needs, so exactly one pair is removed from the declared side when
    -- the live side has none. Nothing else is folded: the engine lower-cases and renames (LOWER to lcase on MariaDB)
    -- and that is the expression map's job to remember, not a text rule's to guess. 1 only when both sides are
    -- expressions and they read the same; 0 leaves the decision to the ordinary comparison and the map.
    DECLARE v_Live TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Declared TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    IF p_LiveDefault IS NULL OR p_Declared IS NULL THEN RETURN 0; END IF;
    IF SchemaSmith_IsExpressionDefault(p_LiveDefault, p_Extra) = 0 THEN RETURN 0; END IF;
    SET v_Declared = TRIM(p_Declared);
    IF LEFT(v_Declared, 1) IN ('''', '"') THEN RETURN 0; END IF;
    SET v_Live = TRIM(SchemaSmith_ExpressionDefaultText(p_LiveDefault));
    IF LEFT(v_Live, 1) <> '(' AND LEFT(v_Declared, 1) = '(' AND RIGHT(v_Declared, 1) = ')' THEN
        SET v_Declared = TRIM(SUBSTRING(v_Declared, 2, CHAR_LENGTH(v_Declared) - 2));
    END IF;
    RETURN IF(CAST(v_Live AS BINARY) = CAST(v_Declared AS BINARY), 1, 0);
END //
DELIMITER ;
