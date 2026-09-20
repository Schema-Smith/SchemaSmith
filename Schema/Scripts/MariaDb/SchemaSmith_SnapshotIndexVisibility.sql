-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotIndexVisibility//

CREATE PROCEDURE SchemaSmith_SnapshotIndexVisibility(
    IN p_Schema VARCHAR(64)
)
BEGIN
    -- MariaDb variant override of the shared MySQL procedure. MariaDB has no IS_VISIBLE column on
    -- INFORMATION_SCHEMA.STATISTICS; it exposes the inverted IGNORED column ('NO' = visible,
    -- 'YES' = ignored/invisible). This override mirrors SchemaSmith_IndexIsVisible's divergence, so the
    -- large caller procs stay shared across the variant. See the MySQL base for the full rationale.
    --
    -- IGNORED is a MariaDB 10.6 feature; the column is absent on the 10.2 floor, where a static read fails
    -- at runtime binding. Below 10.6 no index can be invisible, so the callers gate the visibility
    -- comparison behind SchemaSmith_SupportsInvisibleIndex() and never consult this snapshot; the early skip
    -- leaves the IGNORED statement unreached -> unbound on 10.2 (column resolution is deferred to execution).
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingIndexVisibility;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingIndexVisibility (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IsVisible TINYINT NOT NULL DEFAULT 1,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    IF SchemaSmith_ServerVersionNum() >= 1006 THEN
        INSERT INTO _SchemaSmith_ExistingIndexVisibility (TableName, IndexName, IsVisible)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4),
               CONVERT(s.INDEX_NAME USING utf8mb4),
               CASE WHEN MAX(s.IGNORED) = 'NO' THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_Schema
        GROUP BY s.TABLE_NAME, s.INDEX_NAME;
    END IF;
END //

DELIMITER ;
