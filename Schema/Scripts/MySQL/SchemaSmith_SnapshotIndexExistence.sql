-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotIndexExistence//

CREATE PROCEDURE SchemaSmith_SnapshotIndexExistence(
    IN p_Schema VARCHAR(64)
)
BEGIN
    -- (Re)build _SchemaSmith_IdxExist -- one row per index in the schema (TableName, IndexName, IndexType)
    -- -- in a SINGLE pass over STATISTICS. IndexOnlyQuench's create/ownership passes checked index
    -- existence per declared index against live INFORMATION_SCHEMA, which re-materialises server-wide
    -- metadata on every access (INFORMATION_SCHEMA is not a stored table on MySQL/MariaDB). Those passes
    -- run at different points in the pipeline (post-drop create, post-create ownership, and the fulltext
    -- equivalents), so this is CALLed to refresh the snapshot at each point where the catalog has just
    -- changed -- every existence read then reflects the state at its own position. IndexType is carried so
    -- the fulltext passes can discriminate. No engine divergence (INDEX_TYPE is standard), so no MariaDb
    -- override is needed.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxExist;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxExist (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexType VARCHAR(32),
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_IdxExist (TableName, IndexName, IndexType)
    SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4), CONVERT(MAX(s.INDEX_TYPE) USING utf8mb4)
    FROM INFORMATION_SCHEMA.STATISTICS s
    WHERE BINARY s.TABLE_SCHEMA = BINARY p_Schema
    GROUP BY s.TABLE_NAME, s.INDEX_NAME;
END //

DELIMITER ;
