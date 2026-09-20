-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotIndexVisibility//

CREATE PROCEDURE SchemaSmith_SnapshotIndexVisibility(
    IN p_Schema VARCHAR(64)
)
BEGIN
    -- Snapshot each index's optimizer visibility for one schema into _SchemaSmith_ExistingIndexVisibility
    -- (one row per index; IsVisible = 1 visible, 0 invisible), in a SINGLE pass over STATISTICS.
    --
    -- WHY this exists: the modified-index detection in MissingIndexesAndConstraintsQuench / IndexOnlyQuench
    -- needs each existing index's visibility, and read it per candidate row via SchemaSmith_IndexIsVisible(),
    -- which runs a whole-server STATISTICS materialisation on every call (INFORMATION_SCHEMA is not a stored
    -- table on MySQL/MariaDB). Snapshotting it once and joining collapses that per-row cost to one scan --
    -- the same treatment ForeignKeyQuench applied to its metadata. It is a per-engine PROCEDURE (not folded
    -- into the shared quench proc) for the same reason SchemaSmith_IndexIsVisible is a per-engine function:
    -- the visibility column diverges (MySQL IS_VISIBLE 'YES'=visible; the MariaDb override reads the inverted
    -- IGNORED 'NO'=visible), so isolating it here keeps the large caller procs shared across the variant.
    --
    -- IS_VISIBLE is a MySQL 8.0 feature; the column is absent on the 5.7 floor, where a static read fails at
    -- runtime binding. Below 8.0 no index can be invisible, so the callers gate the visibility comparison
    -- behind SchemaSmith_SupportsInvisibleIndex() and never consult this snapshot; the early skip leaves the
    -- IS_VISIBLE statement unreached -> unbound on 5.7 (column resolution is deferred to execution). IS_VISIBLE
    -- is uniform across an index's STATISTICS rows, so MAX collapses the per-column rows to one value.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingIndexVisibility;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingIndexVisibility (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IsVisible TINYINT NOT NULL DEFAULT 1,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    IF SchemaSmith_ServerVersionNum() >= 800 THEN
        INSERT INTO _SchemaSmith_ExistingIndexVisibility (TableName, IndexName, IsVisible)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4),
               CONVERT(s.INDEX_NAME USING utf8mb4),
               CASE WHEN MAX(s.IS_VISIBLE) = 'YES' THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_Schema
        GROUP BY s.TABLE_NAME, s.INDEX_NAME;
    END IF;
END //

DELIMITER ;
