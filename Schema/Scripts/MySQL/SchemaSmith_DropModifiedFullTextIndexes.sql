-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_DropModifiedFullTextIndexes//

-- #530 (MY-025). Drops a declared FULLTEXT index whose columns or comment differ from the live one, so the create pass
-- re-creates it as declared; a FULLTEXT index cannot be altered in place. The parser is not compared: neither engine
-- reports it outside SHOW CREATE TABLE, which a procedure cannot read.
CREATE PROCEDURE SchemaSmith_DropModifiedFullTextIndexes(
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTLive;
    CREATE TEMPORARY TABLE _SchemaSmith_FTLive (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        IndexKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ColumnList TEXT COLLATE utf8mb4_bin,
        IndexComment TEXT COLLATE utf8mb4_bin,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_FTLive (TableName, IndexName, TableKey, IndexKey, ColumnList, IndexComment)
    SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4),
           SchemaSmith_IdentifierKey(s.TABLE_NAME), SchemaSmith_NameKeyCI(s.INDEX_NAME),
           CONVERT(GROUP_CONCAT(LOWER(s.COLUMN_NAME) ORDER BY s.SEQ_IN_INDEX SEPARATOR ',') USING utf8mb4),
           CONVERT(MAX(s.INDEX_COMMENT) USING utf8mb4)
    FROM INFORMATION_SCHEMA.STATISTICS s
    WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
      AND s.INDEX_TYPE = 'FULLTEXT'
    GROUP BY CAST(s.TABLE_NAME AS BINARY), CAST(s.INDEX_NAME AS BINARY), s.TABLE_NAME, s.INDEX_NAME;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTModified;
    CREATE TEMPORARY TABLE _SchemaSmith_FTModified (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT IGNORE INTO _SchemaSmith_FTModified (TableName, IndexName)
    SELECT l.TableName, l.IndexName
    FROM _SchemaSmith_FullTextIndexes ft
    JOIN _SchemaSmith_FTLive l ON l.TableKey = ft.TableKey AND l.IndexKey = ft.IndexKey
    WHERE CAST(LOWER(REPLACE(REPLACE(ft.Columns, '`', ''), ' ', '')) AS BINARY) != CAST(l.ColumnList AS BINARY)
       OR CAST(COALESCE(ft.Comment, '') AS BINARY) != CAST(COALESCE(l.IndexComment, '') AS BINARY);

    IF EXISTS (SELECT 1 FROM _SchemaSmith_FTModified) THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop modified fulltext indexes');
        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop modified fulltext index: DROP INDEX `', IndexName, '` ON `',
                                         CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
            FROM _SchemaSmith_FTModified;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'fullTextIndex', CONCAT(TableName, '.', IndexName), 'wouldDrop'
            FROM _SchemaSmith_FTModified;
        ELSE
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTModStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_FTModStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, AuditName VARCHAR(257))
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_FTModStmts (Stmt, AuditName)
            SELECT CONCAT('ALTER TABLE `', REPLACE(CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`', '``'), '`.`',
                          REPLACE(TableName, '`', '``'), '` DROP INDEX `', REPLACE(IndexName, '`', '``'), '`'),
                   CONCAT(TableName, '.', IndexName)
            FROM _SchemaSmith_FTModified;
            SET @v_ftmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTModStmts);
            WHILE @v_ftmod_id IS NOT NULL DO
                SELECT Stmt, AuditName INTO @exec_sql, @ss_auditname FROM _SchemaSmith_FTModStmts WHERE RowId = @v_ftmod_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'fullTextIndex', @ss_auditname, 'dropped');
                DEALLOCATE PREPARE stmt;
                SET @v_ftmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTModStmts WHERE RowId > @v_ftmod_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTModStmts;
        END IF;
    END IF;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTModified;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTLive;
END//

DELIMITER ;
