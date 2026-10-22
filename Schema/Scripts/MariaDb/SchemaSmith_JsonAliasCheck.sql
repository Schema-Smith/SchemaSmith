-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- MariaDB has no JSON type: JSON is a longtext in utf8mb4_bin, and from 10.4.3 the engine adds a column-level check,
-- json_valid(`<column>`), named after the column. That check is the only thing in the catalog that tells a JSON column
-- from a longtext, so a declared JSON is compared with it and extraction recognises the column by it. NULL below
-- 10.4.3, where the column is a bare longtext and nothing distinguishes it.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_JsonAliasCheck//
CREATE FUNCTION SchemaSmith_JsonAliasCheck(
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
NOT DETERMINISTIC
NO SQL
BEGIN
    IF SchemaSmith_ServerVersionPatchNum() < 100403 THEN
        RETURN NULL;
    END IF;
    RETURN CONCAT('json_valid(`', REPLACE(p_Column, '`', '``'), '`)');
END //
DELIMITER ;
