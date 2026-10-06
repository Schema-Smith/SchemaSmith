-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_RebuildBlockedReason//

CREATE FUNCTION SchemaSmith_RebuildBlockedReason(
    p_Schema VARCHAR(64),
    p_Table VARCHAR(64)
) RETURNS VARCHAR(255)
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    -- Answers "why can this table NOT be rebuilt?" -- a short reason naming the blocking state, or NULL when
    -- a rebuild (shadow-copy-and-swap) is safe.
    --
    -- MySQL has almost none of the states a rebuild would silently destroy: no system-versioned tables, no
    -- application-time periods, no table-level Change Data Capture or Change Tracking, and its replication is
    -- statement/row streaming off the binlog rather than a per-table article whose identity a swap would
    -- break. Partitioning is the exception, and it is checked below.
    --
    -- DO NOT extend this by copying the MariaDb override's system-versioning or period checks
    -- (Scripts/MariaDb/SchemaSmith_RebuildBlockedReason.sql). MariaDB genuinely has both; MySQL does not, and
    -- its INFORMATION_SCHEMA has no PERIODS table -- referencing one is ER_UNKNOWN_TABLE (1109) at CREATE
    -- time, which would fail the entire kindle rather than one query. Same shape as SchemaSmith_ColumnSrid,
    -- where the engine that lacks the catalog object simply returns nothing.
    DECLARE v_Count INT DEFAULT 0;

    -- PARTITIONING IS THE ONE STATE THIS ENGINE DOES HAVE. A partition definition lives in the table DDL,
    -- not in a separate catalog object, so a shadow built from the package's column list is unpartitioned by
    -- construction: the swap keeps every row and drops the layout, with nothing to report it. PostgreSQL's
    -- twin of this function already refuses for the same reason.
    --
    -- A NON-partitioned table yields ONE row here with every partition column NULL rather than no rows at
    -- all, so the test is PARTITION_NAME IS NOT NULL. INFORMATION_SCHEMA.PARTITIONS predates the supported
    -- floor on both engines (verified on MySQL 5.7 and MariaDB 10.2), so it is read statically.
    SELECT COUNT(*) INTO v_Count
    FROM INFORMATION_SCHEMA.PARTITIONS pt
    WHERE pt.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(pt.TABLE_SCHEMA) = v_DbKey
      AND SchemaSmith_IdentifierKey(pt.TABLE_NAME) = v_TableKey
      AND pt.PARTITION_NAME IS NOT NULL;

    IF v_Count > 0 THEN
        RETURN 'the table is partitioned';
    END IF;

    RETURN NULL;
END //

DELIMITER ;
