-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IsGeneratedInvisiblePrimaryKey//

CREATE FUNCTION SchemaSmith_IsGeneratedInvisiblePrimaryKey(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- MariaDB never generates a primary key, so a my_row_id column there is the package's.
    RETURN 0;
END //

DELIMITER ;
