-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_TablePeriodsJson//

CREATE FUNCTION SchemaSmith_TablePeriodsJson(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    -- The table's application-time periods as a JSON array, or '[]' when it has none.
    --
    -- Always '[]' on MySQL: application-time periods are a MariaDB feature and MySQL has no equivalent
    -- at any version.
    --
    -- WHY A SEPARATE FUNCTION rather than a read inlined into GenerateTableJson: the answer lives in
    -- INFORMATION_SCHEMA.PERIODS, and MySQL rejects a routine that references a table which does not
    -- exist -- at CREATE time, ERROR 1109, even inside a branch that can never run. TABLE resolution is
    -- not deferred the way COLUMN resolution is (contrast SchemaSmith_IsSystemTimePeriodColumn, which
    -- depends on exactly that deferral). A static reference in the shared caller would therefore have
    -- broken kindling for every MySQL target.
    --
    -- Parameters are accepted only to keep the signature identical to the MariaDb override.
    RETURN '[]';
END //

DELIMITER ;
