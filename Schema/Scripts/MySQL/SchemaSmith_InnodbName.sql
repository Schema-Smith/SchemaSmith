-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_InnodbName//

-- A schema or table name as InnoDB's own catalog spells it (INNODB_TABLES.NAME is "schema/table" in this form), so
-- the placement readers can find a table whose name is not plain ASCII letters and digits: `a-b` is stored as
-- `a@002db`, and matching the raw name missed it, reading back no tablespace or data directory at all. Letters,
-- digits and '_' stay as they are; anything else becomes '@' and its four-digit UTF-16 code. That is exact for ASCII
-- punctuation and spaces. MySQL spells many non-ASCII letters with a two-character code instead (`é` is `@0p`), from a
-- table no SQL function on MySQL exposes, so SchemaSmith_InnodbTableName below resolves such a name by pattern.
-- With lower_case_table_names 1 or 2 InnoDB stores the name folded to lower case, and the readers compare it exactly
-- (a plain case-insensitive match would confuse `T` with `t` where the server keeps them apart), so it is folded first.
CREATE FUNCTION SchemaSmith_InnodbName(p_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(400) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    DECLARE v_out VARCHAR(400) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_i INT DEFAULT 1;
    DECLARE v_c VARCHAR(4) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_a INT;
    IF @@lower_case_table_names <> 0 THEN
        SET p_Name = LOWER(p_Name);
    END IF;
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

DROP FUNCTION IF EXISTS SchemaSmith_InnodbNamePattern//

-- The same name as a regular-expression fragment that matches either spelling of each non-ASCII character: '@' and
-- four hex digits, or '@' and one of MySQL's two-character codes (two characters from '0' to '~'). The ASCII part is
-- spelled exactly, and holds only letters, digits, '_' and '@', which are literal in a pattern.
CREATE FUNCTION SchemaSmith_InnodbNamePattern(p_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(1600) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    DECLARE v_out VARCHAR(1600) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_i INT DEFAULT 1;
    DECLARE v_c VARCHAR(4) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    IF @@lower_case_table_names <> 0 THEN
        SET p_Name = LOWER(p_Name);
    END IF;
    WHILE v_i <= CHAR_LENGTH(p_Name) DO
        SET v_c = SUBSTRING(p_Name, v_i, 1);
        SET v_out = CONCAT(v_out, IF(LENGTH(v_c) > 1, '@([0-9a-f]{4}|[0-~]{2})', SchemaSmith_InnodbName(v_c)));
        SET v_i = v_i + 1;
    END WHILE;
    RETURN v_out;
END //

DROP PROCEDURE IF EXISTS SchemaSmith_InnodbTableName//

-- The name InnoDB's catalog holds for a table ("schema/table" in its filename encoding), for the placement readers.
-- An ASCII name is encoded exactly. A name with a non-ASCII character is looked up by pattern and taken only when
-- exactly one table matches; otherwise the plain encoding is returned and, as before, may find nothing. A procedure,
-- not a function: the catalog view differs by version (INNODB_SYS_TABLES on 5.7), so it is named in dynamic SQL,
-- which a function cannot run.
CREATE PROCEDURE SchemaSmith_InnodbTableName(
    IN p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    OUT p_Name VARCHAR(810) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
)
SQL SECURITY DEFINER
BEGIN
    SET p_Name = CONCAT(SchemaSmith_InnodbName(p_Schema), '/', SchemaSmith_InnodbName(p_Table));
    IF CHAR_LENGTH(CONCAT(p_Schema, p_Table)) <> LENGTH(CONCAT(p_Schema, p_Table)) THEN
        SET @ss_itn_pattern = CONCAT('^', SchemaSmith_InnodbNamePattern(p_Schema), '/', SchemaSmith_InnodbNamePattern(p_Table), '$');
        SET @ss_itn_name = NULL;
        SET @ss_itn_count = 0;
        SET @ss_itn_sql = CONCAT('SELECT MIN(NAME), COUNT(*) INTO @ss_itn_name, @ss_itn_count FROM INFORMATION_SCHEMA.',
                                 IF(SchemaSmith_ServerVersionNum() < 800, 'INNODB_SYS_TABLES', 'INNODB_TABLES'),
                                 ' WHERE NAME REGEXP @ss_itn_pattern');
        PREPARE ss_itn_stmt FROM @ss_itn_sql;
        EXECUTE ss_itn_stmt;
        DEALLOCATE PREPARE ss_itn_stmt;
        IF @ss_itn_count = 1 THEN
            SET p_Name = @ss_itn_name;
        END IF;
    END IF;
END //

DELIMITER ;
