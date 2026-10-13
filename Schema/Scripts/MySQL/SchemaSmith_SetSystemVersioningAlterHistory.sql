-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SetSystemVersioningAlterHistory//

CREATE PROCEDURE SchemaSmith_SetSystemVersioningAlterHistory(
    IN p_Mode VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
)
SQL SECURITY DEFINER
BEGIN
    -- Applies the operator's opt-in for altering a system-versioned table. No-op on MySQL, which has no
    -- system versioning at any version and therefore no such variable.
    --
    -- WHY THIS IS A SEPARATE PROCEDURE rather than a branch inside ModifiedTableQuench: MySQL rejects a
    -- routine that so much as MENTIONS @@system_versioning_alter_history, at CREATE time, even inside a
    -- branch that can never run. Verified live on MySQL 8.0.45 -- a procedure whose only reference sits
    -- inside IF VERSION() LIKE '%MariaDB%' fails to create with
    --   ERROR 1193 (HY000): Unknown system variable 'system_versioning_alter_history'
    -- System-variable resolution is NOT deferred the way column resolution is (contrast
    -- SchemaSmith_IsSystemTimePeriodColumn, where a missing COLUMN is legal until the statement runs).
    -- So a version-gated branch in the shared file would have broken kindling for every MySQL target,
    -- and the divergence has to live in a per-file MariaDb override instead -- the same shape
    -- SchemaSmith_ColumnSrid uses, and the one the temporal spike predicted would be needed here.
    --
    -- p_Mode is accepted only to keep the signature identical to the MariaDb override.
    SET @ss_unused_alter_history_mode = p_Mode;
END //

DELIMITER ;
