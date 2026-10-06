-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IsSystemTimePeriodColumn//

CREATE FUNCTION SchemaSmith_IsSystemTimePeriodColumn(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- Is this column one of a system-versioned table's SYSTEM_TIME period columns (row start / row end)?
    --
    -- Always 0 on MySQL: MySQL has no system versioning at any version, so the state cannot exist.
    --
    -- WHY this is a function rather than a predicate inlined into the shared caller: the answer lives in
    -- INFORMATION_SCHEMA.COLUMNS.IS_SYSTEM_TIME_PERIOD_START / _END, and those columns do not exist on
    -- MySQL at all. Column resolution inside a stored routine is DEFERRED to execution -- verified live
    -- on MySQL 8.0.45: a procedure referencing IS_SYSTEM_TIME_PERIOD_START CREATEs cleanly and then fails
    -- at CALL time with ER_BAD_FIELD_ERROR (1054). So a static reference in GenerateTableJson would
    -- deploy to MySQL looking healthy and break every MySQL extraction the first time it ran. Isolating
    -- the read here, with an always-0 MySQL definition and a real MariaDb override
    -- (Scripts/MariaDb/SchemaSmith_IsSystemTimePeriodColumn.sql), keeps the divergence out of the shared
    -- caller -- exactly the shape SchemaSmith_ColumnSrid uses for the mirrored SRS_ID problem.
    --
    -- Parameters are accepted only to keep the signature identical to the MariaDb override.
    RETURN 0;
END //

DELIMITER ;
