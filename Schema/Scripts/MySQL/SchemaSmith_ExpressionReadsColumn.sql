-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ExpressionReadsColumn//

CREATE FUNCTION SchemaSmith_ExpressionReadsColumn(
    p_Expression TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    -- Whether a declared generation expression names the column as a whole identifier, bare or in backticks. A
    -- substring test read `total AS (price + tax_amount)` as reading `tax`, so two independent generated columns
    -- looked like a cycle and the deploy stopped ("Circular dependency detected") on a table the engine accepts.
    -- Column names are case-insensitive, so both sides are lower-cased.
    DECLARE v_Expression TEXT CHARSET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Column VARCHAR(128) CHARSET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Pos INT DEFAULT 1;
    DECLARE v_Before VARCHAR(1);
    DECLARE v_After VARCHAR(1);

    IF p_Expression IS NULL OR p_Column IS NULL OR p_Column = '' THEN
        RETURN 0;
    END IF;

    -- A backtick is a boundary like any other non-identifier character.
    SET v_Expression = CONVERT(LOWER(REPLACE(p_Expression, '`', ' ')) USING utf8mb4) COLLATE utf8mb4_bin;
    SET v_Column = CONVERT(LOWER(p_Column) USING utf8mb4) COLLATE utf8mb4_bin;

    find_loop: LOOP
        SET v_Pos = LOCATE(v_Column, v_Expression, v_Pos);
        IF v_Pos = 0 THEN
            RETURN 0;
        END IF;
        SET v_Before = IF(v_Pos = 1, ' ', SUBSTRING(v_Expression, v_Pos - 1, 1));
        SET v_After = SUBSTRING(v_Expression, v_Pos + CHAR_LENGTH(v_Column), 1);
        IF v_Before NOT REGEXP '[a-z0-9_$]' AND (v_After = '' OR v_After NOT REGEXP '[a-z0-9_$]') THEN
            RETURN 1;
        END IF;
        SET v_Pos = v_Pos + 1;
    END LOOP find_loop;
END //

DELIMITER ;
