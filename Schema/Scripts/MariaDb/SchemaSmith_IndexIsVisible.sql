-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IndexIsVisible//

CREATE FUNCTION SchemaSmith_IndexIsVisible(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Index VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_IndexKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_NameKeyCI(p_Index);
    -- MariaDb variant override of the shared MySQL function. MariaDB has no IS_VISIBLE column on
    -- INFORMATION_SCHEMA.STATISTICS; it exposes the inverted IGNORED column ('NO' = visible,
    -- 'YES' = ignored/invisible). This override is the whole reason the divergence is isolated to
    -- one small function instead of forking the three large caller scripts. See the MySQL base
    -- definition for the full rationale.
    --
    -- Invisible indexes (the IGNORED column) are a MariaDB 10.6 feature; the column is absent on the
    -- 10.2 floor, where a static read fails at runtime binding. Below 10.6 no index can be invisible,
    -- so return visible. The early RETURN leaves the IGNORED statement unreached -> unbound on 10.2
    -- (column resolution is deferred to execution). Mirrors the MySQL base guard (IS_VISIBLE, 8.0).
    IF SchemaSmith_ServerVersionNum() < 1006 THEN
        RETURN 1;
    END IF;

    RETURN (
        SELECT CASE WHEN MAX(s.IGNORED) = 'NO' THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(s.TABLE_NAME) = v_TableKey
          AND SchemaSmith_NameKeyCI(s.INDEX_NAME) = v_IndexKey
    );
END //

DELIMITER ;
