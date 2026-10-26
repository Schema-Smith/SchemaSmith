-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_CreateMissingFullTextIndexes//

-- #530. Creates the declared FULLTEXT indexes (_SchemaSmith_FullTextIndexes) that do not exist, and records them in
-- ProductOwnership. Called by IndexOnlyQuench and by MissingIndexesAndConstraintsQuench: a table's FullTextIndexes
-- used to be created only by an index-only quench, so a normal deploy left every one of them out.
CREATE PROCEDURE SchemaSmith_CreateMissingFullTextIndexes(
    IN p_ProductName VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
BEGIN
    -- Create missing fulltext indexes
    -- Post-ftdrop existence snapshot: after the fulltext drops just above, so a just-dropped fulltext
    -- index is seen as MISSING by the create pass (WhatIf: no drops ran, reflects the unchanged catalog).
    CALL SchemaSmith_SnapshotIndexExistence(p_DatabaseName);
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing fulltext indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT(
                      'CREATE FULLTEXT INDEX ', ft.IndexName,
                      ' ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', ft.TableName,
                      ' (', ft.Columns, ')',
                      CASE WHEN ft.Comment IS NOT NULL AND ft.Comment != ''
                           THEN CONCAT(' COMMENT ''', REPLACE(ft.Comment, '''', ''''''), '''')
                           ELSE '' END,
                      CASE WHEN ft.Parser IS NOT NULL AND ft.Parser != ''
                           THEN CONCAT(' WITH PARSER ', ft.Parser)
                           ELSE '' END)
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE s.TableKey = ft.TableKey
              AND s.IndexKey = ft.IndexKey
              AND s.IndexType = 'FULLTEXT'
        );

        -- #363: WhatIf twin of the ELSE-branch 'fullTextIndex'/'created' audit; same predicate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'fullTextIndex',
               CONCAT(SchemaSmith_StripBacktickWrapping(ft.TableName), '.', SchemaSmith_StripBacktickWrapping(ft.IndexName)), 'wouldCreate'
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE s.TableKey = ft.TableKey
              AND s.IndexKey = ft.IndexKey
              AND s.IndexType = 'FULLTEXT'
        );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing fulltext indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Create fulltext index: ', ft.TableName, '.', ft.IndexName,
            CASE WHEN COALESCE(ft.VariantName, '') <> '' THEN CONCAT(' (variant: ', ft.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE s.TableKey = ft.TableKey
              AND s.IndexKey = ft.IndexKey
              AND s.IndexType = 'FULLTEXT'
        );

        -- NOTE: InnoDB only supports adding one FULLTEXT index per ALTER TABLE statement, so unlike
        -- the other loops in this file these do NOT fold multiple rows into one multi-clause ALTER —
        -- each row materializes as its own standalone ALTER TABLE ... ADD FULLTEXT INDEX statement.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTCreateStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FTCreateStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, AuditName VARCHAR(257), TableRef TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FTCreateStmts (Stmt, AuditName, TableRef)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', ft.TableName,
                      ' ADD FULLTEXT INDEX ', ft.IndexName,
                      ' (', ft.Columns, ')',
                      CASE WHEN ft.Comment IS NOT NULL AND ft.Comment != ''
                           THEN CONCAT(' COMMENT ''', REPLACE(ft.Comment, '''', ''''''), '''')
                           ELSE '' END,
                      CASE WHEN ft.Parser IS NOT NULL AND ft.Parser != ''
                           THEN CONCAT(' WITH PARSER ', ft.Parser)
                           ELSE '' END),
               CONCAT(SchemaSmith_StripBacktickWrapping(ft.TableName), '.', SchemaSmith_StripBacktickWrapping(ft.IndexName)),
               CONCAT('`', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', ft.TableName)
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE s.TableKey = ft.TableKey
              AND s.IndexKey = ft.IndexKey
              AND s.IndexType = 'FULLTEXT'
        )
        ORDER BY ft.RowId;

        SET @v_ftcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTCreateStmts);
        WHILE @v_ftcreate_id IS NOT NULL DO
            SELECT Stmt, AuditName, TableRef INTO @exec_sql, @ss_auditname, @ss_ft_table FROM _SchemaSmith_FTCreateStmts WHERE RowId = @v_ftcreate_id;
            -- MariaDB 10.4 and later leave the rows already in an InnoDB table out of a full-text index added to it in
            -- place once the table has (or had) one, so a search misses them until the table is rebuilt. Copying the
            -- table builds the index over every row; an empty table has nothing to miss and keeps the cheaper build.
            IF VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 1004 THEN
                SET @ss_ft_rows = 0;
                SET @ss_ft_check = CONCAT('SELECT EXISTS (SELECT 1 FROM ', @ss_ft_table, ') INTO @ss_ft_rows');
                PREPARE ft_check FROM @ss_ft_check;
                EXECUTE ft_check;
                DEALLOCATE PREPARE ft_check;
                IF @ss_ft_rows = 1 THEN
                    SET @exec_sql = CONCAT(@exec_sql, ', ALGORITHM=COPY');
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Building fulltext index ', @ss_auditname, ' by copying the table, so it covers the rows already in it'));
                END IF;
            END IF;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'fullTextIndex', @ss_auditname, 'created');
            DEALLOCATE PREPARE stmt;
            SET @v_ftcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTCreateStmts WHERE RowId > @v_ftcreate_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTCreateStmts;

        -- Track fulltext indexes in ProductOwnership. Rebuild the existence snapshot to the post-ftcreate
        -- state so ownership is written only for fulltext indexes that now exist.
        CALL SchemaSmith_SnapshotIndexExistence(p_DatabaseName);
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'INDEX',
               CONCAT(SchemaSmith_StripBacktickWrapping(ft.TableName), '.', SchemaSmith_StripBacktickWrapping(ft.IndexName))
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE s.TableKey = ft.TableKey
              AND s.IndexKey = ft.IndexKey
        );
    END IF;
END//

DELIMITER ;
