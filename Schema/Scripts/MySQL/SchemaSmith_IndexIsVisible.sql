-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IndexIsVisible//

CREATE FUNCTION SchemaSmith_IndexIsVisible(
    p_Schema VARCHAR(64),
    p_Table VARCHAR(64),
    p_Index VARCHAR(64)
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_IndexKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_NameKeyCI(p_Index);
    -- Returns 1 if the named index is visible to the optimizer, 0 if invisible.
    --
    -- WHY this exists: MySQL exposes index visibility via INFORMATION_SCHEMA.STATISTICS.IS_VISIBLE
    -- ('YES'/'NO'), a column that does not exist on MariaDB. MariaDB instead exposes the inverted
    -- IGNORED column ('NO' = visible). The callers (GenerateTableJson, IndexOnlyQuench,
    -- MissingIndexesAndConstraintsQuench) are shared across the MySQL/MariaDb variant family, so the
    -- column-name divergence is isolated here: this MySQL definition reads IS_VISIBLE; the
    -- Scripts/MariaDb override reads IGNORED. IS_VISIBLE is uniform across an index's STATISTICS rows,
    -- so MAX collapses the per-column rows to one value.
    --
    -- Invisible indexes (the IS_VISIBLE column) are a MySQL 8.0 feature; the column is absent on the
    -- 5.7 floor, where a static read fails at runtime binding. Below 8.0 no index can be invisible, so
    -- return visible. The early RETURN leaves the IS_VISIBLE statement unreached -> unbound on 5.7
    -- (column resolution is deferred to execution). Mirrored in the MariaDb override (IGNORED, 10.6).
    IF SchemaSmith_ServerVersionNum() < 800 THEN
        RETURN 1;
    END IF;

    RETURN (
        SELECT CASE WHEN MAX(s.IS_VISIBLE) = 'YES' THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(s.TABLE_NAME) = v_TableKey
          AND SchemaSmith_NameKeyCI(s.INDEX_NAME) = v_IndexKey
    );
END //

DELIMITER ;
