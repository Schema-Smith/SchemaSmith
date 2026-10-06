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
    -- MariaDb variant override of the shared MySQL function (which returns NULL unconditionally -- MySQL has
    -- none of these concepts). MariaDB does: a system-versioned table carries row history, and an
    -- application-time period carries a temporal contract, and neither survives a shadow-copy-and-swap
    -- rebuild or can be reconstructed from the schema package. Fail closed and leave those tables to
    -- Before/After migration scripts. See the MySQL base definition for the full rationale.
    DECLARE v_Count INT DEFAULT 0;

    -- MariaDB reports a system-versioned table as 'SYSTEM VERSIONED' rather than 'BASE TABLE' (the same
    -- divergence SchemaSmith_ParseTableJson's table-type filter accounts for). TABLE_TYPE itself is a
    -- standard INFORMATION_SCHEMA.TABLES column, so this reads safely at the 10.2 floor -- the value simply
    -- never appears below 10.3, where system versioning arrived.
    SELECT COUNT(*) INTO v_Count
    FROM INFORMATION_SCHEMA.TABLES t
    WHERE t.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(t.TABLE_SCHEMA) = v_DbKey
      AND SchemaSmith_IdentifierKey(t.TABLE_NAME) = v_TableKey
      AND t.TABLE_TYPE = 'SYSTEM VERSIONED';

    IF v_Count > 0 THEN
        RETURN 'system versioning is enabled';
    END IF;

    -- INFORMATION_SCHEMA.PERIODS is MariaDB 11.4 -- the application-time period FEATURE arrived in 10.4.3,
    -- but the catalog table exposing it did not follow until 11.4. Unlike a missing COLUMN (deferred to
    -- execution, which is what lets SchemaSmith_IndexIsVisible / SchemaSmith_ColumnSrid guard with an early
    -- RETURN), a missing INFORMATION_SCHEMA TABLE is resolved by the PARSER: a static reference is
    -- ER_UNKNOWN_TABLE at CREATE time on 10.2 and 10.6 -- both supported, both in CI -- and would fail the
    -- whole kindle, not one query. A stored function cannot use PREPARE/EXECUTE to defer it either.
    --
    -- So the read is staged inside the MariaDB-only executable comment on the SET below: its six-digit
    -- version prefix (110400) makes the content compile on 11.4.0 and above and stay an inert comment below
    -- it, so the parser never sees PERIODS on a server that lacks the table. The staged text is an
    -- EXPRESSION fragment, not a statement, because statement delimiters are not permitted inside an
    -- executable comment -- hence a version-staged addend on a SET rather than a guarded IF block. Below
    -- 11.4 the SET leaves v_Count at 0 and no period is reported; that is a genuine detection gap on
    -- 10.4.3-11.3, where the state exists but nothing in the catalog can be asked about it.
    --
    -- SYSTEM_TIME is excluded so the reason names the state accurately: the system-versioning check above
    -- already owns that case and returns first.
    SET v_Count = 0 /*M!110400 + (SELECT COUNT(*) FROM INFORMATION_SCHEMA.PERIODS pd WHERE pd.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(pd.TABLE_SCHEMA) = v_DbKey AND SchemaSmith_IdentifierKey(pd.TABLE_NAME) = v_TableKey AND pd.PERIOD <> 'SYSTEM_TIME') */;

    IF v_Count > 0 THEN
        RETURN 'an application-time period is defined';
    END IF;

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
