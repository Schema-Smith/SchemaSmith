-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_CleanCheckClause//
CREATE FUNCTION SchemaSmith_CleanCheckClause(
    p_Text TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- A check expression as it is declared: the catalog writes its string literals with the client character set's
    -- introducer and their quotes backslash-escaped (_utf8mb4\'x\'), and a package extracted from it carries the same.
    -- An introducer is an underscore and a run of letters and digits that opens a quoted literal and is not the tail
    -- of a longer word, so `raw_binary` and 'x_latin1' are left as they are; only the introducer is removed, then the
    -- escaped quotes are unescaped. A loop, not REGEXP_REPLACE, which MySQL 5.7 does not have. The replace runs on the
    -- declared variable here, not over the result at the caller, where 5.7 refuses the mix of the result's collation
    -- with the literals' (1270).
    DECLARE v_Text TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Pos INT;
    DECLARE v_End INT;
    DECLARE v_Start INT;
    IF p_Text IS NULL THEN RETURN NULL; END IF;
    SET v_Text = p_Text;
    SET v_Pos = LOCATE('''', v_Text);
    WHILE v_Pos > 0 DO
        SET v_End = IF(v_Pos > 1 AND SUBSTRING(v_Text, v_Pos - 1, 1) = '\\', v_Pos - 2, v_Pos - 1);
        SET v_Start = v_End;
        WHILE v_Start >= 1 AND SUBSTRING(v_Text, v_Start, 1) REGEXP '^[A-Za-z0-9]$' DO
            SET v_Start = v_Start - 1;
        END WHILE;
        IF v_Start >= 1 AND v_Start < v_End AND SUBSTRING(v_Text, v_Start, 1) = '_'
           AND (v_Start = 1 OR SUBSTRING(v_Text, v_Start - 1, 1) NOT REGEXP '^[A-Za-z0-9_$`]$') THEN
            SET v_Text = CONCAT(LEFT(v_Text, v_Start - 1), SUBSTRING(v_Text, v_End + 1));
            SET v_Pos = LOCATE('''', v_Text, v_Start + 1);
        ELSE
            SET v_Pos = LOCATE('''', v_Text, v_Pos + 1);
        END IF;
    END WHILE;
    RETURN REPLACE(v_Text, '\\''', '''');
END //
DELIMITER ;
