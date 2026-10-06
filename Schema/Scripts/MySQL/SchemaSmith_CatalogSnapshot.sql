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

CREATE PROCEDURE SchemaSmith_SnapshotCatalogTables(IN p_DatabaseName VARCHAR(128))
SQL SECURITY DEFINER
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CatTables;
    CREATE TEMPORARY TABLE _SchemaSmith_CatTables (
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
     WHERE TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(TABLE_SCHEMA) = v_DbKey;
END //

DROP PROCEDURE IF EXISTS SchemaSmith_SnapshotCatalogColumns//

CREATE PROCEDURE SchemaSmith_SnapshotCatalogColumns(IN p_DatabaseName VARCHAR(128))
SQL SECURITY DEFINER
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CatColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_CatColumns (
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

-- Marks the declared renames that are ready to run: the old name is in the catalog and the new one is not. Split into
-- an insert and a delete because a statement may not read the same temporary table twice.

DROP PROCEDURE IF EXISTS SchemaSmith_MarkTableRenames//

CREATE PROCEDURE SchemaSmith_MarkTableRenames(IN p_DatabaseName VARCHAR(128))
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

CREATE PROCEDURE SchemaSmith_MarkColumnRenames(IN p_DatabaseName VARCHAR(128))
SQL SECURITY DEFINER
BEGIN
    CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColRenameReady;
    CREATE TEMPORARY TABLE _SchemaSmith_ColRenameReady (RowId INT NOT NULL PRIMARY KEY) ENGINE=InnoDB;

    INSERT INTO _SchemaSmith_ColRenameReady (RowId)
    SELECT c.RowId FROM _SchemaSmith_Columns c
     WHERE c.OldName IS NOT NULL
       AND EXISTS (SELECT 1 FROM _SchemaSmith_CatColumns isc WHERE isc.TableKey = c.TableKey AND isc.ColumnKey = c.OldNameKey);

    DELETE r FROM _SchemaSmith_ColRenameReady r
      JOIN _SchemaSmith_Columns c ON c.RowId = r.RowId
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey;
END //
