-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_TypeChangeKeepsForeignKey//

-- 1 when the engine makes a column's type change in place under a foreign key and keeps the key, so ModifiedTableQuench
-- STEP 2.9 need not drop it first: a VARCHAR or VARBINARY made longer, in the same character set, with its length on the
-- same side of 255 bytes (past it the length prefix grows, which is a rebuild). Probed with foreign_key_checks on, from
-- either side of the key: MySQL 5.7.22, 5.7, 8.0 and 8.4 make it; so does MariaDB until MDEV-31086, which refuses any
-- change to a key column (1832/1833) from 10.4.31, 10.5.22, 10.6.15, 10.9.8, 10.10.6, 10.11.5 and 11.0.3, and on every
-- 11.1 and later release. Probed on MariaDB 10.2.13 to 10.4.30 against 10.4.31, 10.5.21 against 10.5.22, 10.6.14 against
-- 10.6.15, 10.7.8 and 10.8.8 (never fixed), 10.9.7 against 10.9.8, 10.10.5 against 10.10.6, 10.11.4 against 10.11.5,
-- 11.0.2 against 11.0.3, and refused on 11.1.2, 11.4 and 11.8.
-- Narrowing, a change of type, of character set or of collation is refused by every engine.
CREATE FUNCTION SchemaSmith_TypeChangeKeepsForeignKey(
    p_LiveType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_DeclaredType TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_LiveCharacterSet VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_DeclaredCharacterSet VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
NOT DETERMINISTIC
READS SQL DATA
BEGIN
    DECLARE v_Patch INT DEFAULT SchemaSmith_ServerVersionPatchNum();
    DECLARE v_Live VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Declared VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_LiveBase VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_DeclaredBase VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_LiveLength VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_DeclaredLength VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_MaxBytes INT DEFAULT NULL;

    IF p_LiveType IS NULL OR p_DeclaredType IS NULL THEN
        RETURN 0;
    END IF;
    IF VERSION() LIKE '%MariaDB%'
       AND NOT (v_Patch < 100431
                OR v_Patch BETWEEN 100500 AND 100521
                OR v_Patch BETWEEN 100600 AND 100614
                OR v_Patch BETWEEN 100700 AND 100899
                OR v_Patch BETWEEN 100900 AND 100907
                OR v_Patch BETWEEN 101000 AND 101005
                OR v_Patch BETWEEN 101100 AND 101104
                OR v_Patch BETWEEN 110000 AND 110002) THEN
        RETURN 0;
    END IF;
    IF p_DeclaredCharacterSet IS NOT NULL AND TRIM(p_DeclaredCharacterSet) != ''
       AND IF(LOWER(TRIM(p_DeclaredCharacterSet)) = 'utf8', 'utf8mb3', LOWER(TRIM(p_DeclaredCharacterSet)))
           != IF(LOWER(TRIM(IFNULL(p_LiveCharacterSet, ''))) = 'utf8', 'utf8mb3', LOWER(TRIM(IFNULL(p_LiveCharacterSet, '')))) THEN
        RETURN 0;
    END IF;

    SET v_Live = LOWER(REPLACE(TRIM(p_LiveType), ' ', ''));
    SET v_Declared = LOWER(REPLACE(TRIM(p_DeclaredType), ' ', ''));
    SET v_LiveBase = SUBSTRING_INDEX(v_Live, '(', 1);
    SET v_DeclaredBase = SUBSTRING_INDEX(v_Declared, '(', 1);
    SET v_LiveLength = SUBSTRING_INDEX(SUBSTRING_INDEX(v_Live, '(', -1), ')', 1);
    SET v_DeclaredLength = SUBSTRING_INDEX(SUBSTRING_INDEX(v_Declared, '(', -1), ')', 1);
    IF v_LiveBase NOT IN ('varchar', 'varbinary') OR v_DeclaredBase != v_LiveBase
       OR v_Live != CONCAT(v_LiveBase, '(', v_LiveLength, ')') OR v_Declared != CONCAT(v_DeclaredBase, '(', v_DeclaredLength, ')')
       OR v_LiveLength NOT REGEXP '^[0-9]+$' OR v_DeclaredLength NOT REGEXP '^[0-9]+$'
       OR CAST(v_DeclaredLength AS UNSIGNED) <= CAST(v_LiveLength AS UNSIGNED) THEN
        RETURN 0;
    END IF;

    IF v_LiveBase = 'varbinary' THEN
        SET v_MaxBytes = 1;
    ELSE
        SELECT MAXLEN INTO v_MaxBytes FROM INFORMATION_SCHEMA.CHARACTER_SETS
         WHERE CHARACTER_SET_NAME = IF(LOWER(p_LiveCharacterSet) = 'utf8', 'utf8mb3', LOWER(p_LiveCharacterSet))
            OR CHARACTER_SET_NAME = LOWER(p_LiveCharacterSet)
         LIMIT 1;
    END IF;
    IF v_MaxBytes IS NULL THEN
        RETURN 0;
    END IF;
    RETURN IF((CAST(v_LiveLength AS UNSIGNED) * v_MaxBytes <= 255) = (CAST(v_DeclaredLength AS UNSIGNED) * v_MaxBytes <= 255), 1, 0);
END//

DELIMITER ;
