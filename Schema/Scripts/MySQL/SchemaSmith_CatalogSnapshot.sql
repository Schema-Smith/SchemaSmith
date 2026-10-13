-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Keyed snapshots of the target database's TABLES and COLUMNS rows. A caller refreshes one immediately before the
-- statement that reads it, so the read is as current as a direct catalog read. Only static columns are copied: a
-- cached statistic such as TABLES.AUTO_INCREMENT would make every deploy populate MySQL 8's statistics cache for the
-- whole schema.
--
-- Why a snapshot rather than a direct join: the catalog may spell a table differently from the package (and, on
-- MariaDB with lower_case_table_names=2, differently between TABLES and COLUMNS), so names must be compared through
-- their keys. A key function on every catalog row of a join is several times slower than a plain BINARY compare on
-- MySQL; one schema-filtered read into an indexed temp table, then a key-to-key join, is faster than either on every
-- engine. The schema filter keeps a case-insensitive utf8mb4 prefilter that the catalog can serve without a full
-- scan, and the key compare then decides exactly.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotCatalogTables//

CREATE PROCEDURE SchemaSmith_SnapshotCatalogTables(IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
SQL SECURITY DEFINER
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CatTables;
    CREATE TEMPORARY TABLE _SchemaSmith_CatTables (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        TABLE_NAME VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        TABLE_TYPE VARCHAR(64) DEFAULT NULL,
        ENGINE VARCHAR(64) DEFAULT NULL,
        ROW_FORMAT VARCHAR(20) DEFAULT NULL,
        TABLE_COLLATION VARCHAR(64) DEFAULT NULL,
        CREATE_OPTIONS VARCHAR(2048) DEFAULT NULL,
        TABLE_COMMENT TEXT DEFAULT NULL,
        KEY ix_cattables_key (TableKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_CatTables (TableKey, TABLE_NAME, TABLE_TYPE, ENGINE, ROW_FORMAT, TABLE_COLLATION, CREATE_OPTIONS, TABLE_COMMENT)
    SELECT SchemaSmith_IdentifierKey(TABLE_NAME), TABLE_NAME, TABLE_TYPE, ENGINE, ROW_FORMAT, TABLE_COLLATION, CREATE_OPTIONS, TABLE_COMMENT
      FROM INFORMATION_SCHEMA.TABLES
     -- MariaDB 11.2+ also lists the session's own temporary tables here.
     WHERE TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(TABLE_SCHEMA) = v_DbKey
       AND TABLE_TYPE <> 'TEMPORARY';
END //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotCatalogColumns//

CREATE PROCEDURE SchemaSmith_SnapshotCatalogColumns(IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
SQL SECURITY DEFINER
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CatColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_CatColumns (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ColumnKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        TABLE_NAME VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        COLUMN_NAME VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ORDINAL_POSITION BIGINT UNSIGNED DEFAULT NULL,
        COLUMN_DEFAULT LONGTEXT DEFAULT NULL,
        IS_NULLABLE VARCHAR(3) DEFAULT NULL,
        DATA_TYPE LONGTEXT DEFAULT NULL,
        COLUMN_TYPE LONGTEXT DEFAULT NULL,
        CHARACTER_SET_NAME VARCHAR(64) DEFAULT NULL,
        COLLATION_NAME VARCHAR(64) DEFAULT NULL,
        COLUMN_KEY VARCHAR(3) DEFAULT NULL,
        EXTRA VARCHAR(256) DEFAULT NULL,
        COLUMN_COMMENT TEXT DEFAULT NULL,
        GENERATION_EXPRESSION LONGTEXT DEFAULT NULL,
        KEY ix_catcolumns_key (TableKey, ColumnKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_CatColumns (TableKey, ColumnKey, TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION, COLUMN_DEFAULT, IS_NULLABLE,
                                         DATA_TYPE, COLUMN_TYPE, CHARACTER_SET_NAME, COLLATION_NAME, COLUMN_KEY, EXTRA, COLUMN_COMMENT,
                                         GENERATION_EXPRESSION)
    SELECT SchemaSmith_IdentifierKey(TABLE_NAME), SchemaSmith_NameKeyCI(COLUMN_NAME), TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION,
           COLUMN_DEFAULT, IS_NULLABLE, DATA_TYPE, COLUMN_TYPE, CHARACTER_SET_NAME, COLLATION_NAME, COLUMN_KEY, EXTRA, COLUMN_COMMENT,
           GENERATION_EXPRESSION
      FROM INFORMATION_SCHEMA.COLUMNS
     WHERE TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(TABLE_SCHEMA) = v_DbKey;
END //

-- MySQL 8 serves TABLES.AUTO_INCREMENT and the other table statistics from a server-wide cache that lives for
-- information_schema_stats_expiry (24 hours by default), so a read can return a value from before the table was dropped,
-- re-created or truncated. A caller that decides something from a statistic brackets the read with these two. The
-- variable does not exist on MySQL 5.7 or MariaDB, which have no such cache, so it is reached only through PREPARE
-- under a handler: naming it in the body would fail the CREATE there.

DROP PROCEDURE IF EXISTS SchemaSmith_BypassStatisticsCache//

CREATE PROCEDURE SchemaSmith_BypassStatisticsCache()
SQL SECURITY DEFINER
BEGIN
    DECLARE CONTINUE HANDLER FOR SQLEXCEPTION SET @ss_saved_stats_expiry = NULL;
    SET @ss_saved_stats_expiry = NULL;
    SET @ss_stats_sql = 'SELECT @@SESSION.information_schema_stats_expiry INTO @ss_saved_stats_expiry';
    PREPARE ss_stats_cache FROM @ss_stats_sql;
    EXECUTE ss_stats_cache;
    DEALLOCATE PREPARE ss_stats_cache;
    IF @ss_saved_stats_expiry IS NOT NULL THEN
        SET @ss_stats_sql = 'SET SESSION information_schema_stats_expiry = 0';
        PREPARE ss_stats_cache FROM @ss_stats_sql;
        EXECUTE ss_stats_cache;
        DEALLOCATE PREPARE ss_stats_cache;
    END IF;
END //

DROP PROCEDURE IF EXISTS SchemaSmith_RestoreStatisticsCache//

CREATE PROCEDURE SchemaSmith_RestoreStatisticsCache()
SQL SECURITY DEFINER
BEGIN
    DECLARE CONTINUE HANDLER FOR SQLEXCEPTION SET @ss_saved_stats_expiry = NULL;
    IF @ss_saved_stats_expiry IS NOT NULL THEN
        SET @ss_stats_sql = CONCAT('SET SESSION information_schema_stats_expiry = ', @ss_saved_stats_expiry);
        PREPARE ss_stats_cache FROM @ss_stats_sql;
        EXECUTE ss_stats_cache;
        DEALLOCATE PREPARE ss_stats_cache;
        SET @ss_saved_stats_expiry = NULL;
    END IF;
END //

-- Marks the declared renames that are ready to run: the old name is in the catalog and the new one is not. Split into
-- an insert and a delete because a statement may not read the same temporary table twice.

DROP PROCEDURE IF EXISTS SchemaSmith_MarkTableRenames//

CREATE PROCEDURE SchemaSmith_MarkTableRenames(IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
SQL SECURITY DEFINER
BEGIN
    CALL SchemaSmith_SnapshotCatalogTables(p_DatabaseName);
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TableRenameReady;
    CREATE TEMPORARY TABLE _SchemaSmith_TableRenameReady (RowId INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;

    INSERT INTO _SchemaSmith_TableRenameReady (RowId)
    SELECT t.RowId FROM _SchemaSmith_Tables t
     WHERE t.OldName IS NOT NULL AND t.NewTable = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_CatTables ist WHERE ist.TableKey = t.OldNameKey);

    DELETE r FROM _SchemaSmith_TableRenameReady r
      JOIN _SchemaSmith_Tables t ON t.RowId = r.RowId
      JOIN _SchemaSmith_CatTables ist ON ist.TableKey = t.TableKey;
END //

DROP PROCEDURE IF EXISTS SchemaSmith_MarkColumnRenames//

-- Column renames also cover a column the server holds under a spelling that differs from the package's only in
-- case: the engine treats it as the same column, so it is renamed to the package's spelling rather than left
-- alone, and extraction then round-trips the package. LiveName is the catalog's spelling of the column to rename.
CREATE PROCEDURE SchemaSmith_MarkColumnRenames(IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
SQL SECURITY DEFINER
BEGIN
    CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColRenameReady;
    CREATE TEMPORARY TABLE _SchemaSmith_ColRenameReady (
        RowId INT NOT NULL PRIMARY KEY,
        LiveName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

    INSERT IGNORE INTO _SchemaSmith_ColRenameReady (RowId, LiveName)
    SELECT c.RowId, isc.COLUMN_NAME FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.OldNameKey
     WHERE c.OldName IS NOT NULL;

    DELETE r FROM _SchemaSmith_ColRenameReady r
      JOIN _SchemaSmith_Columns c ON c.RowId = r.RowId
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey;

    INSERT IGNORE INTO _SchemaSmith_ColRenameReady (RowId, LiveName)
    SELECT c.RowId, isc.COLUMN_NAME FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey
     WHERE CAST(isc.COLUMN_NAME AS BINARY) <> CAST(SchemaSmith_StripBacktickWrapping(c.ColumnName) AS BINARY);
END //
