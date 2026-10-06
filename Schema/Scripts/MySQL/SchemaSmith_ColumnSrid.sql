-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ColumnSrid//

CREATE FUNCTION SchemaSmith_ColumnSrid(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS INT
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_ColumnKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_NameKeyCI(p_Column);
    -- Returns the column's live SRID restriction (INFORMATION_SCHEMA.COLUMNS.SRS_ID), or NULL when the
    -- column carries none.
    --
    -- WHY this exists: MySQL 8.0.3+ exposes SRS_ID, but unlike SchemaSmith_IndexIsVisible's
    -- IS_VISIBLE/IGNORED divergence (both engines have SOME column, just a different name), MariaDB has
    -- no SRS_ID column at all -- see SchemaSmith_SupportsColumnSrid. A static SRS_ID reference compiled
    -- unconditionally into a shared caller (GenerateTableJson, ModifiedTableQuench) would fail to bind on
    -- MariaDB with ER_BAD_FIELD_ERROR, breaking extraction for every table, not just spatial ones.
    -- Isolating the read here, mirrored by an always-NULL MariaDb override
    -- (Scripts/MariaDb/SchemaSmith_ColumnSrid.sql), keeps the divergence out of the shared caller
    -- scripts -- same shape as SchemaSmith_IndexIsVisible / SchemaSmith_SnapshotIndexVisibility.
    --
    -- Below MySQL 8.0.3 the SRS_ID column is also absent; the early RETURN leaves the SRS_ID statement
    -- unreached -> unbound on 5.7/8.0.0-8.0.2 (column resolution is deferred to execution).
    IF SchemaSmith_ServerVersionNum() < 800 THEN
        RETURN NULL;
    END IF;

    RETURN (
        SELECT c.SRS_ID
        FROM INFORMATION_SCHEMA.COLUMNS c
        WHERE c.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(c.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(c.TABLE_NAME) = v_TableKey
          AND SchemaSmith_NameKeyCI(c.COLUMN_NAME) = v_ColumnKey
    );
END //

DELIMITER ;
