-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- Whether a live column is an engine's own rendering of JSON, which the generator writes back as DataType JSON with no
-- check, character set or collation of its own. Never on MySQL, whose JSON column reports its type as json. The MariaDb
-- override (Scripts/MariaDb/SchemaSmith_IsJsonAliasColumn.sql) recognises longtext utf8mb4_bin with the column-level
-- json_valid check MariaDB adds itself.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_IsJsonAliasColumn//
CREATE FUNCTION SchemaSmith_IsJsonAliasColumn(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    RETURN 0;
END //
DELIMITER ;
