-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_ExpressionDefaultText//
CREATE FUNCTION SchemaSmith_ExpressionDefaultText(
    p_Default TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- The SQL of a live expression default (#520). MariaDB reports SQL as written. MySQL reports the expression with
    -- every string literal carrying the introducer of the client character set that CREATED the column
    -- (concat(_utf8mb4\'A\',_utf8mb4\'B\')) and every quote backslash-escaped, so its text is neither the declaration
    -- nor valid SQL until both are undone. The introducer is a client artefact, never part of the declaration.
    DECLARE v_Text TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Pos INT;
    DECLARE v_Start INT;
    IF p_Default IS NULL THEN RETURN NULL; END IF;
    IF VERSION() LIKE '%MariaDB%' THEN RETURN p_Default; END IF;
    SET v_Text = p_Default;
    -- Each escaped quote that opens a literal may be preceded by an introducer: an underscore and a run of
    -- letters and digits that is not itself the tail of a longer word. Remove the introducer, keep the quote.
    SET v_Pos = LOCATE('\\''', v_Text);
    WHILE v_Pos > 0 DO
        SET v_Start = v_Pos - 1;
        WHILE v_Start >= 1 AND SUBSTRING(v_Text, v_Start, 1) REGEXP '^[A-Za-z0-9]$' DO
            SET v_Start = v_Start - 1;
        END WHILE;
        IF v_Start >= 1 AND v_Start < v_Pos - 1 AND SUBSTRING(v_Text, v_Start, 1) = '_'
           AND (v_Start = 1 OR SUBSTRING(v_Text, v_Start - 1, 1) NOT REGEXP '^[A-Za-z0-9_]$') THEN
            SET v_Text = CONCAT(LEFT(v_Text, v_Start - 1), SUBSTRING(v_Text, v_Pos));
            SET v_Pos = LOCATE('\\''', v_Text, v_Start + 2);
        ELSE
            SET v_Pos = LOCATE('\\''', v_Text, v_Pos + 2);
        END IF;
    END WHILE;
    RETURN REPLACE(REPLACE(v_Text, '\\''', ''''), '\\\\', '\\');
END //
DELIMITER ;
