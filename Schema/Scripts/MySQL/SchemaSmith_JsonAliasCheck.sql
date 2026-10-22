-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The check an engine adds to a JSON column itself, or NULL. MySQL has a native JSON type and adds nothing. The
-- MariaDb override (Scripts/MariaDb/SchemaSmith_JsonAliasCheck.sql) returns the column-level json_valid check MariaDB
-- names after the column from 10.4.3, which is what a declared JSON is compared with there.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_JsonAliasCheck//
CREATE FUNCTION SchemaSmith_JsonAliasCheck(
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
NOT DETERMINISTIC
NO SQL
BEGIN
    RETURN NULL;
END //
DELIMITER ;
