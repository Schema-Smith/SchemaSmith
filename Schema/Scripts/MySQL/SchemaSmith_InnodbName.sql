-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_InnodbName//

-- A schema or table name as InnoDB's own catalog spells it (INNODB_TABLES.NAME is "schema/table" in this form), so
-- the placement readers can find a table whose name is not plain ASCII letters and digits: `a-b` is stored as
-- `a@002db`, and matching the raw name missed it, reading back no tablespace or data directory at all. Letters,
-- digits and '_' stay as they are; anything else becomes '@' and its four-digit UTF-16 code. That is exact for ASCII
-- punctuation and spaces. MySQL spells some non-ASCII letters with shorter codes, so such a name can still miss --
-- which reads back as no placement, as it did before.
CREATE FUNCTION SchemaSmith_InnodbName(p_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(400) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    DECLARE v_out VARCHAR(400) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_i INT DEFAULT 1;
    DECLARE v_c VARCHAR(4) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_a INT;
    WHILE v_i <= CHAR_LENGTH(p_Name) DO
        SET v_c = SUBSTRING(p_Name, v_i, 1);
        -- ASCII() reads the first byte, so a multi-byte character can never pass for a letter here.
        SET v_a = ASCII(v_c);
        SET v_out = CONCAT(v_out,
            IF(v_a BETWEEN 48 AND 57 OR v_a BETWEEN 65 AND 90 OR v_a BETWEEN 97 AND 122 OR v_a = 95,
               v_c, CONCAT('@', LOWER(HEX(CONVERT(v_c USING utf16))))));
        SET v_i = v_i + 1;
    END WHILE;
    RETURN v_out;
END //

DELIMITER ;
