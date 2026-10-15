-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The predicate of the column-level CHECK a column carries, or NULL. MySQL has no column-level check: every CHECK is
-- a named table-level constraint whatever columns it reads, and INFORMATION_SCHEMA cannot tie one to a column, so the
-- MySQL body is NULL. The MariaDb override (Scripts/MariaDb/SchemaSmith_ColumnLevelCheck.sql) reads the check MariaDB
-- names after the column -- its own rule for a column-level check -- which stays column-level across a redeploy.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_ColumnLevelCheck//
CREATE FUNCTION SchemaSmith_ColumnLevelCheck(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    RETURN NULL;
END //
DELIMITER ;
