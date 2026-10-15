-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The application-time period a key is declared WITHOUT OVERLAPS over, as {"Name","StartColumn","EndColumn"}, or NULL.
-- MySQL has no periods, so the MySQL body is NULL. The MariaDb override (Scripts/MariaDb/SchemaSmith_IndexPeriodJson.sql)
-- reads INFORMATION_SCHEMA.KEY_PERIOD_USAGE, which arrives in 11.4 -- the same blind spot the periods themselves have.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_IndexPeriodJson//
CREATE FUNCTION SchemaSmith_IndexPeriodJson(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Index VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    RETURN NULL;
END //
DELIMITER ;
