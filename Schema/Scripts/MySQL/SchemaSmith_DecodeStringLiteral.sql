-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_DecodeStringLiteral//

-- The value a string literal's body (the text between its outer quotes) stands for, read as the server reads it: a
-- doubled quote is one quote, a backslash escape is the character it names. Comparing a declared default with a live
-- one compares values, so 'it\'s' and 'it''s', or a backslash written either way, are one default and not a change.
CREATE FUNCTION SchemaSmith_DecodeStringLiteral(
    p_Body TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE v_Out TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_Pos INT DEFAULT 1;
    DECLARE v_Len INT;
    DECLARE v_Char VARCHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Next VARCHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

    IF p_Body IS NULL THEN
        RETURN NULL;
    END IF;
    IF LOCATE('\\', p_Body) = 0 AND LOCATE('''', p_Body) = 0 THEN
        RETURN p_Body;
    END IF;

    SET v_Len = CHAR_LENGTH(p_Body);
    WHILE v_Pos <= v_Len DO
        SET v_Char = SUBSTRING(p_Body, v_Pos, 1);
        IF v_Char = '\\' AND v_Pos < v_Len THEN
            SET v_Next = SUBSTRING(p_Body, v_Pos + 1, 1);
            -- The escapes MySQL and MariaDB define; \% and \_ keep their backslash, any other \x is x.
            SET v_Out = CONCAT(v_Out, CASE v_Next
                WHEN 'n' THEN CHAR(10 USING utf8mb4)
                WHEN 't' THEN CHAR(9 USING utf8mb4)
                WHEN 'r' THEN CHAR(13 USING utf8mb4)
                WHEN 'b' THEN CHAR(8 USING utf8mb4)
                WHEN '0' THEN CHAR(0 USING utf8mb4)
                WHEN 'Z' THEN CHAR(26 USING utf8mb4)
                WHEN '%' THEN '\\%'
                WHEN '_' THEN '\\_'
                ELSE v_Next END);
            SET v_Pos = v_Pos + 2;
        ELSEIF v_Char = '''' AND SUBSTRING(p_Body, v_Pos + 1, 1) = '''' THEN
            SET v_Out = CONCAT(v_Out, '''');
            SET v_Pos = v_Pos + 2;
        ELSE
            SET v_Out = CONCAT(v_Out, v_Char);
            SET v_Pos = v_Pos + 1;
        END IF;
    END WHILE;

    RETURN v_Out;
END //

DELIMITER ;
