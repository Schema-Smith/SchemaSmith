-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ColumnChangeLoss//

-- H3. What MODIFY COLUMN does to the values already in a column when its type changes from p_From (the live COLUMN_TYPE)
-- to p_To (the declared type): 0 (every value survives), 1 (a value that does not fit stops the statement, so nothing is
-- lost), or 2 (values are rounded or cut without an error) -- a number, so no caller's collation meets it. Probed on MySQL 8.0 and MariaDB 11.4 with
-- data that does not fit (H3 P2): a scale or fractional-second cut, a decimal or float into an integer, a datetime into a
-- date, a double into a float and a bigint into a double all change values silently in every sql_mode; a length cut, an
-- integer narrowing, a precision overflow and text into a number raise an error only under a strict mode (p_Strict = 1) --
-- without one each of them clamps, cuts or zeroes the value instead. A pair this does not know is 2. Written without
-- REGEXP_REPLACE, which MySQL 5.7 does not have.
CREATE FUNCTION SchemaSmith_ColumnChangeLoss(
    p_From TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_To TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Strict TINYINT
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    DECLARE i INT DEFAULT 1;
    DECLARE v TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE vRaw TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE vb VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE vIn TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v1 BIGINT UNSIGNED;
    DECLARE v2 BIGINT UNSIGNED;
    DECLARE vu TINYINT;
    DECLARE vParen INT;
    DECLARE fb, tb VARCHAR(40) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE fIn, tIn TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE f1, f2, t1, t2 BIGINT UNSIGNED;
    DECLARE fu, tu TINYINT;
    DECLARE fr, tr, fdig INT;
    DECLARE fCap, tCap BIGINT UNSIGNED;
    DECLARE r VARCHAR(5) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE m TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE pos INT;

    IF p_From IS NULL OR p_To IS NULL THEN
        RETURN 0;
    END IF;

    -- One spelling per type: upper case, no display width, no MariaDB storage-format comment, the synonyms folded.
    WHILE i <= 2 DO
        SET vRaw = TRIM(REPLACE(REPLACE(IF(i = 1, p_From, p_To), ' /* mariadb-5.3 */', ''), ' /* MARIADB-5.3 */', ''));
        SET v = UPPER(vRaw);
        SET v = REPLACE(REPLACE(REPLACE(REPLACE(v, ' (', '('), '( ', '('), ' )', ')'), ', ', ',');
        SET vu = IF(v LIKE '% UNSIGNED%', 1, 0);
        SET v = TRIM(REPLACE(REPLACE(REPLACE(v, ' UNSIGNED', ''), ' ZEROFILL', ''), ' SIGNED', ''));
        IF v LIKE 'DOUBLE PRECISION%' THEN SET v = CONCAT('DOUBLE', SUBSTRING(v, 17)); END IF;
        IF v LIKE 'CHARACTER VARYING%' THEN SET v = CONCAT('VARCHAR', SUBSTRING(v, 18)); END IF;
        IF v LIKE 'NATIONAL VARCHAR%' THEN SET v = SUBSTRING(v, 10); END IF;
        IF v LIKE 'NATIONAL CHAR%' THEN SET v = SUBSTRING(v, 10); END IF;
        IF v LIKE 'LONG VARCHAR%' OR v = 'LONG' THEN SET v = 'MEDIUMTEXT'; END IF;
        SET vParen = LOCATE('(', v);
        IF vParen > 0 THEN
            SET vb = TRIM(LEFT(v, vParen - 1));
            SET vIn = SUBSTRING(v, vParen + 1, GREATEST(LOCATE(')', v, vParen) - vParen - 1, 0));
        ELSE
            SET vb = SUBSTRING_INDEX(v, ' ', 1);
            SET vIn = NULL;
        END IF;
        SET vb = CASE vb WHEN 'INTEGER' THEN 'INT' WHEN 'INT4' THEN 'INT' WHEN 'INT1' THEN 'TINYINT' WHEN 'BOOL' THEN 'TINYINT'
                         WHEN 'BOOLEAN' THEN 'TINYINT' WHEN 'INT2' THEN 'SMALLINT' WHEN 'INT3' THEN 'MEDIUMINT'
                         WHEN 'MIDDLEINT' THEN 'MEDIUMINT' WHEN 'INT8' THEN 'BIGINT' WHEN 'DEC' THEN 'DECIMAL'
                         WHEN 'NUMERIC' THEN 'DECIMAL' WHEN 'FIXED' THEN 'DECIMAL' WHEN 'REAL' THEN 'DOUBLE'
                         WHEN 'FLOAT8' THEN 'DOUBLE' WHEN 'FLOAT4' THEN 'FLOAT' WHEN 'NCHAR' THEN 'CHAR'
                         WHEN 'CHARACTER' THEN 'CHAR' WHEN 'NVARCHAR' THEN 'VARCHAR' ELSE vb END;
        IF vb = 'SERIAL' THEN SET vb = 'BIGINT'; SET vu = 1; END IF;
        SET v1 = NULL; SET v2 = NULL;
        IF vb NOT IN ('ENUM', 'SET') AND vIn IS NOT NULL AND vIn REGEXP '^[0-9]+(,[0-9]+)?$' THEN
            SET v1 = CAST(SUBSTRING_INDEX(vIn, ',', 1) AS UNSIGNED);
            IF LOCATE(',', vIn) > 0 THEN SET v2 = CAST(SUBSTRING_INDEX(vIn, ',', -1) AS UNSIGNED); END IF;
        END IF;
        -- An enum's or set's members are data, compared as written.
        IF vb IN ('ENUM', 'SET') THEN
            SET vIn = SUBSTRING(vRaw, LOCATE('(', vRaw) + 1);
            SET vIn = LEFT(vIn, CHAR_LENGTH(vIn) - 1);
        END IF;
        IF vb = 'FLOAT' AND v1 IS NOT NULL AND v2 IS NULL THEN
            SET vb = IF(v1 > 24, 'DOUBLE', 'FLOAT');
            SET v1 = NULL;
        END IF;
        IF vb IN ('TINYINT', 'SMALLINT', 'MEDIUMINT', 'INT', 'BIGINT', 'FLOAT', 'DOUBLE', 'YEAR') THEN SET v1 = NULL; SET v2 = NULL; END IF;
        IF vb = 'DECIMAL' THEN SET v1 = COALESCE(v1, 10); SET v2 = COALESCE(v2, 0); END IF;
        IF vb IN ('CHAR', 'BINARY') THEN SET v1 = COALESCE(v1, 1); END IF;
        IF vb IN ('DATETIME', 'TIMESTAMP', 'TIME') THEN SET v1 = COALESCE(v1, 0); END IF;
        IF i = 1 THEN
            SET fb = vb; SET fIn = vIn; SET f1 = v1; SET f2 = v2; SET fu = vu;
        ELSE
            SET tb = vb; SET tIn = vIn; SET t1 = v1; SET t2 = v2; SET tu = vu;
        END IF;
        SET i = i + 1;
    END WHILE;

    IF fb = tb AND fu = tu AND f1 <=> t1 AND f2 <=> t2 AND (fb NOT IN ('ENUM', 'SET') OR CAST(fIn AS BINARY) = CAST(tIn AS BINARY)) THEN
        RETURN 0;
    END IF;

    SET fr = CASE fb WHEN 'TINYINT' THEN 1 WHEN 'SMALLINT' THEN 2 WHEN 'MEDIUMINT' THEN 3 WHEN 'INT' THEN 4 WHEN 'BIGINT' THEN 5 END;
    SET tr = CASE tb WHEN 'TINYINT' THEN 1 WHEN 'SMALLINT' THEN 2 WHEN 'MEDIUMINT' THEN 3 WHEN 'INT' THEN 4 WHEN 'BIGINT' THEN 5 END;
    SET fdig = CASE fb WHEN 'TINYINT' THEN 3 WHEN 'SMALLINT' THEN 5 WHEN 'MEDIUMINT' THEN 8 WHEN 'INT' THEN 10 WHEN 'BIGINT' THEN 20 END;
    -- What a string type holds, in characters: a TEXT's limit is bytes, so as a target it holds a quarter of that in
    -- utf8mb4 and as a source up to all of it.
    SET fCap = CASE WHEN fb IN ('CHAR', 'VARCHAR', 'BINARY', 'VARBINARY') THEN f1
                    WHEN fb IN ('TINYTEXT', 'TINYBLOB') THEN 255 WHEN fb IN ('TEXT', 'BLOB') THEN 65535
                    WHEN fb IN ('MEDIUMTEXT', 'MEDIUMBLOB') THEN 16777215 WHEN fb IN ('LONGTEXT', 'LONGBLOB', 'JSON') THEN 4294967295 END;
    SET tCap = CASE WHEN tb IN ('CHAR', 'VARCHAR', 'BINARY', 'VARBINARY') THEN t1
                    WHEN tb = 'TINYTEXT' THEN 63 WHEN tb = 'TEXT' THEN 16383 WHEN tb = 'MEDIUMTEXT' THEN 4194303
                    WHEN tb = 'LONGTEXT' THEN 1073741823 WHEN tb = 'TINYBLOB' THEN 255 WHEN tb = 'BLOB' THEN 65535
                    WHEN tb = 'MEDIUMBLOB' THEN 16777215 WHEN tb = 'LONGBLOB' THEN 4294967295 END;

    -- Integers: a target that holds the source's range keeps every value; one that does not refuses or clamps.
    IF fr IS NOT NULL AND tr IS NOT NULL THEN
        SET r = IF((fu = tu AND tr >= fr) OR (fu = 1 AND tu = 0 AND tr > fr), 'none', 'fails');
    ELSEIF fr IS NOT NULL AND tb = 'DECIMAL' THEN
        SET r = IF(t1 - t2 >= fdig AND NOT (fu = 0 AND tu = 1), 'none', 'fails');
    ELSEIF fr IS NOT NULL AND tb = 'DOUBLE' THEN
        SET r = IF(fb = 'BIGINT', 'loses', 'none');
    ELSEIF fr IS NOT NULL AND tb = 'FLOAT' THEN
        SET r = IF(fr <= 3, 'none', 'loses');
    ELSEIF fr IS NOT NULL AND tCap IS NOT NULL AND tb NOT LIKE '%BINARY' AND tb NOT LIKE '%BLOB' THEN
        SET r = IF(tCap >= fdig + 1, 'none', 'fails');

    -- Decimals: a smaller scale rounds; fewer integer digits refuses or clamps.
    ELSEIF fb = 'DECIMAL' AND tb = 'DECIMAL' THEN
        SET r = CASE WHEN t2 < f2 THEN 'loses' WHEN t1 - t2 < f1 - f2 OR (fu = 0 AND tu = 1) THEN 'fails' ELSE 'none' END;
    ELSEIF fb = 'DECIMAL' AND tr IS NOT NULL THEN
        SET r = CASE WHEN f2 > 0 THEN 'loses' WHEN f1 < CASE tb WHEN 'TINYINT' THEN 3 WHEN 'SMALLINT' THEN 5 WHEN 'MEDIUMINT' THEN 7 WHEN 'INT' THEN 10 ELSE 19 END
                          AND NOT (fu = 0 AND tu = 1) THEN 'none' ELSE 'fails' END;
    ELSEIF fb = 'DECIMAL' AND tb = 'DOUBLE' THEN
        SET r = IF(f1 <= 15, 'none', 'loses');
    ELSEIF fb = 'DECIMAL' AND tb = 'FLOAT' THEN
        SET r = IF(f1 <= 6, 'none', 'loses');
    ELSEIF fb = 'DECIMAL' AND tCap IS NOT NULL AND tb NOT LIKE '%BINARY' AND tb NOT LIKE '%BLOB' THEN
        SET r = IF(tCap >= f1 + 2, 'none', 'fails');

    -- Floating point: into anything exact it rounds; a double into a float loses digits.
    ELSEIF fb = 'FLOAT' AND tb = 'DOUBLE' THEN
        SET r = 'none';
    ELSEIF fb IN ('FLOAT', 'DOUBLE') AND (tr IS NOT NULL OR tb IN ('DECIMAL', 'FLOAT')) THEN
        SET r = 'loses';
    ELSEIF fb IN ('FLOAT', 'DOUBLE') AND tCap IS NOT NULL AND tb NOT LIKE '%BINARY' AND tb NOT LIKE '%BLOB' THEN
        SET r = IF(tCap >= 23, 'none', 'fails');

    -- Dates and times: a smaller fractional-second precision rounds, a date drops the time, a timestamp cannot hold a
    -- datetime outside 1970-2038.
    ELSEIF fb = tb AND fb IN ('DATETIME', 'TIMESTAMP', 'TIME') THEN
        SET r = IF(t1 < f1, 'loses', 'none');
    ELSEIF fb = 'TIMESTAMP' AND tb = 'DATETIME' THEN
        SET r = IF(t1 < f1, 'loses', 'none');
    ELSEIF fb = 'DATETIME' AND tb = 'TIMESTAMP' THEN
        SET r = IF(t1 < f1, 'loses', 'fails');
    ELSEIF fb = 'DATE' AND tb = 'DATETIME' THEN
        SET r = 'none';
    ELSEIF fb = 'DATE' AND tb = 'TIMESTAMP' THEN
        SET r = 'fails';
    ELSEIF fb IN ('DATETIME', 'TIMESTAMP') AND tb IN ('DATE', 'TIME', 'YEAR') THEN
        SET r = 'loses';
    ELSEIF fb IN ('DATE', 'DATETIME', 'TIMESTAMP', 'TIME', 'YEAR') AND tCap IS NOT NULL AND tb NOT LIKE '%BINARY' AND tb NOT LIKE '%BLOB' THEN
        SET r = IF(tCap >= CASE fb WHEN 'DATE' THEN 10 WHEN 'YEAR' THEN 4 WHEN 'TIME' THEN 10 ELSE 19 END + IF(f1 > 0, f1 + 1, 0), 'none', 'fails');

    -- Enums and sets: every live member still declared keeps every value; a member gone refuses or blanks the value.
    ELSEIF fb IN ('ENUM', 'SET') AND tb = fb THEN
        SET r = 'none';
        SET m = fIn;
        WHILE r = 'none' AND CHAR_LENGTH(m) > 0 DO
            SET pos = LOCATE(''',''', m);
            IF LOCATE(CAST(IF(pos = 0, m, LEFT(m, pos)) AS BINARY), CAST(tIn AS BINARY)) = 0 THEN
                SET r = 'fails';
            END IF;
            SET m = IF(pos = 0, '', SUBSTRING(m, pos + 2));
        END WHILE;
    ELSEIF fb IN ('ENUM', 'SET') AND tCap IS NOT NULL AND tb NOT LIKE '%BINARY' AND tb NOT LIKE '%BLOB' THEN
        SET r = IF(tCap >= CHAR_LENGTH(fIn), 'none', 'fails');

    -- Strings: a shorter one refuses or cuts a value that does not fit; text into anything but text refuses or zeroes it.
    ELSEIF fCap IS NOT NULL AND tCap IS NOT NULL
           AND (fb LIKE '%BINARY' OR fb LIKE '%BLOB') = (tb LIKE '%BINARY' OR tb LIKE '%BLOB') THEN
        SET r = IF(tCap >= fCap, 'none', 'fails');
    ELSEIF fb = 'JSON' AND tb = 'JSON' THEN
        SET r = 'none';
    ELSEIF tb = 'JSON' THEN
        RETURN 1;
    ELSEIF fCap IS NOT NULL AND fb NOT LIKE '%BINARY' AND fb NOT LIKE '%BLOB'
           AND (tr IS NOT NULL OR tb IN ('DECIMAL', 'FLOAT', 'DOUBLE', 'DATE', 'DATETIME', 'TIMESTAMP', 'TIME', 'YEAR', 'ENUM', 'SET')) THEN
        SET r = 'fails';
    ELSE
        SET r = 'loses';
    END IF;

    -- Without a strict mode nothing refuses: the value is clamped, cut or zeroed instead.
    RETURN CASE WHEN r = 'none' THEN 0 WHEN r = 'fails' AND COALESCE(p_Strict, 0) = 1 THEN 1 ELSE 2 END;
END//

DELIMITER ;
