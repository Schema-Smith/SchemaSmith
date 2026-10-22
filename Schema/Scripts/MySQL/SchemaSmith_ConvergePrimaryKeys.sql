-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ConvergePrimaryKeys//

-- Gives an existing table the primary key its package declares: added where it has none, replaced where its columns
-- differ. A new table is created with its key, so it compares equal here. A key the package does not declare is never
-- dropped. Called by MissingIndexesAndConstraintsQuench and IndexOnlyQuench, after the secondary indexes are created,
-- so an AUTO_INCREMENT column leaving the key can already be covered by one of them.
CREATE PROCEDURE SchemaSmith_ConvergePrimaryKeys(
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT
)
BEGIN
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    -- The live key in the same per-part form as the index snapshots (a primary key has no functional parts).
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LivePrimaryKeys;
    CREATE TEMPORARY TABLE _SchemaSmith_LivePrimaryKeys (
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL PRIMARY KEY,
        NormColumns TEXT
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_LivePrimaryKeys (TableKey, NormColumns)
    SELECT SchemaSmith_IdentifierKey(s.TABLE_NAME),
           GROUP_CONCAT(CONCAT('`', s.COLUMN_NAME, '`',
                               IF(s.SUB_PART IS NOT NULL, CONCAT('(', s.SUB_PART, ')'), ''),
                               CASE WHEN CAST(s.COLLATION AS BINARY) = CAST('D' AS BINARY) THEN ' DESC' ELSE '' END)
                        ORDER BY s.SEQ_IN_INDEX SEPARATOR ',')
      FROM INFORMATION_SCHEMA.STATISTICS s
     WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
       AND s.INDEX_NAME = 'PRIMARY'
     GROUP BY CAST(s.TABLE_NAME AS BINARY), s.TABLE_NAME;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LiveTables;
    CREATE TEMPORARY TABLE _SchemaSmith_LiveTables (
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT IGNORE INTO _SchemaSmith_LiveTables (TableKey, TableName)
    SELECT SchemaSmith_IdentifierKey(t.TABLE_NAME), CONVERT(t.TABLE_NAME USING utf8mb4)
      FROM INFORMATION_SCHEMA.TABLES t
     WHERE t.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(t.TABLE_SCHEMA) = v_DbKey
       AND t.TABLE_TYPE = 'BASE TABLE';

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PrimaryKeyStmts;
    CREATE TEMPORARY TABLE _SchemaSmith_PrimaryKeyStmts (
        RowId INT AUTO_INCREMENT PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        AuditName TEXT,
        HasKey TINYINT NOT NULL,
        KeyColumns TEXT,
        NewColumns TEXT,
        AutoIncrementColumn VARCHAR(128) COLLATE utf8mb4_bin DEFAULT NULL,
        ForeignKeyBlocks TINYINT NOT NULL DEFAULT 0
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_PrimaryKeyStmts (TableName, TableKey, AuditName, HasKey, KeyColumns, NewColumns)
    SELECT lt.TableName, i.TableKey,
           CONCAT(lt.TableName, '.', SchemaSmith_StripBacktickWrapping(i.IndexName)),
           lp.TableKey IS NOT NULL, i.EmitColumns, LOWER(SchemaSmith_NormalizeIndexColumns(i.CompareColumns))
      FROM _SchemaSmith_Indexes i
      JOIN _SchemaSmith_LiveTables lt ON lt.TableKey = i.TableKey
      LEFT JOIN _SchemaSmith_LivePrimaryKeys lp ON lp.TableKey = i.TableKey
     WHERE i.IsPrimaryKey = 1
       AND (lp.TableKey IS NULL
            OR CAST(SchemaSmith_NormalizeIndexColumns(i.CompareColumns) AS BINARY) <> CAST(lp.NormColumns AS BINARY));

    -- A statement of its own: MySQL cannot open a temporary table twice in one statement.
    UPDATE _SchemaSmith_PrimaryKeyStmts p
      JOIN _SchemaSmith_Tables t ON t.TableKey = p.TableKey
       SET p.AutoIncrementColumn = CASE WHEN COALESCE(t.AutoIncrementKeyClause, '') <> ''
                                        THEN SchemaSmith_StripBacktickWrapping(SUBSTRING_INDEX(SUBSTRING_INDEX(t.AutoIncrementKeyClause, '(', -1), ')', 1))
                                        END;
    -- An AUTO_INCREMENT column that is not the key's first part needs an index of its own leading with it; one the
    -- table already has serves. The catalog is read into a snapshot, never joined by set-based DML (#337).
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LeadingIndexColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_LeadingIndexColumns (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ColumnKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        KEY ix_lead (TableKey, ColumnKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    IF EXISTS (SELECT 1 FROM _SchemaSmith_PrimaryKeyStmts WHERE AutoIncrementColumn IS NOT NULL) THEN
        INSERT INTO _SchemaSmith_LeadingIndexColumns (TableKey, ColumnKey)
        SELECT SchemaSmith_IdentifierKey(s.TABLE_NAME), SchemaSmith_NameKeyCI(s.COLUMN_NAME)
          FROM INFORMATION_SCHEMA.STATISTICS s
         WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
           AND s.INDEX_NAME <> 'PRIMARY' AND s.SEQ_IN_INDEX = 1 AND s.COLUMN_NAME IS NOT NULL;
        UPDATE _SchemaSmith_PrimaryKeyStmts p
          JOIN _SchemaSmith_LeadingIndexColumns l ON l.TableKey = p.TableKey AND l.ColumnKey = SchemaSmith_NameKeyCI(p.AutoIncrementColumn)
           SET p.AutoIncrementColumn = NULL;
    END IF;

    -- A key a foreign key needs is kept, as a modified index is. MySQL refuses the replacement (1553, handled below),
    -- but MariaDB accepts DROP PRIMARY KEY with an ADD that still contains the column anywhere, and leaves the foreign
    -- key unable to accept any row (1452). So the check is made here, on both: every foreign key into the table, and
    -- every one out of it, needs its columns to lead the new key or another index.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PkForeignKeyColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_PkForeignKeyColumns (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        Cols TEXT,
        KEY ix_fkcols (TableKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PkOtherIndexColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_PkOtherIndexColumns (
        RequiredPrimaryKey BIGINT AUTO_INCREMENT PRIMARY KEY,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        Cols TEXT,
        KEY ix_idxcols (TableKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    IF EXISTS (SELECT 1 FROM _SchemaSmith_PrimaryKeyStmts WHERE HasKey = 1) THEN
        INSERT INTO _SchemaSmith_PkForeignKeyColumns (TableKey, Cols)
        SELECT SchemaSmith_IdentifierKey(k.REFERENCED_TABLE_NAME),
               LOWER(GROUP_CONCAT(CONCAT('`', k.REFERENCED_COLUMN_NAME, '`') ORDER BY k.POSITION_IN_UNIQUE_CONSTRAINT SEPARATOR ','))
          FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
         WHERE k.REFERENCED_TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(k.REFERENCED_TABLE_SCHEMA) = v_DbKey
         GROUP BY k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME, k.REFERENCED_TABLE_NAME;
        INSERT INTO _SchemaSmith_PkForeignKeyColumns (TableKey, Cols)
        SELECT SchemaSmith_IdentifierKey(k.TABLE_NAME),
               LOWER(GROUP_CONCAT(CONCAT('`', k.COLUMN_NAME, '`') ORDER BY k.ORDINAL_POSITION SEPARATOR ','))
          FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
         WHERE k.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(k.TABLE_SCHEMA) = v_DbKey
           AND k.REFERENCED_TABLE_NAME IS NOT NULL
         GROUP BY k.TABLE_SCHEMA, k.TABLE_NAME, k.CONSTRAINT_NAME;
        INSERT INTO _SchemaSmith_PkOtherIndexColumns (TableKey, Cols)
        SELECT SchemaSmith_IdentifierKey(s.TABLE_NAME),
               LOWER(GROUP_CONCAT(CONCAT('`', s.COLUMN_NAME, '`') ORDER BY s.SEQ_IN_INDEX SEPARATOR ','))
          FROM INFORMATION_SCHEMA.STATISTICS s
         WHERE s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
           AND s.INDEX_NAME <> 'PRIMARY'
         GROUP BY CAST(s.TABLE_NAME AS BINARY), s.TABLE_NAME, s.INDEX_NAME;
        UPDATE _SchemaSmith_PrimaryKeyStmts p
          JOIN _SchemaSmith_PkForeignKeyColumns f ON f.TableKey = p.TableKey
           SET p.ForeignKeyBlocks = 1
         WHERE p.HasKey = 1
           AND LEFT(CONCAT(p.NewColumns, ','), CHAR_LENGTH(f.Cols) + 1) <> CONCAT(f.Cols, ',')
           AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_PkOtherIndexColumns o
                            WHERE o.TableKey = f.TableKey
                              AND LEFT(CONCAT(o.Cols, ','), CHAR_LENGTH(f.Cols) + 1) = CONCAT(f.Cols, ','));
    END IF;

    IF EXISTS (SELECT 1 FROM _SchemaSmith_PrimaryKeyStmts) THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Converge primary keys');
    END IF;

    SET @ss_pk_id := (SELECT MIN(RowId) FROM _SchemaSmith_PrimaryKeyStmts);
    WHILE @ss_pk_id IS NOT NULL DO
        SELECT TableName, AuditName, HasKey, ForeignKeyBlocks,
               -- The key MySQL generated for a keyless table goes with its column, in the statement that adds the
               -- declared key: the column cannot be dropped on its own, nor the key replaced while it stays.
               CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      IF(HasKey = 1, 'DROP PRIMARY KEY, ', ''),
                      'ADD PRIMARY KEY (', KeyColumns, ')',
                      IF(AutoIncrementColumn IS NULL, '', CONCAT(', ADD KEY (`', AutoIncrementColumn, '`)')))
          INTO @ss_pk_table, @ss_pk_audit, @ss_pk_haskey, @ss_pk_fk_needs, @exec_sql
          FROM _SchemaSmith_PrimaryKeyStmts WHERE RowId = @ss_pk_id;
        SET @ss_pk_generated := IF(@ss_pk_haskey = 1, SchemaSmith_IsGeneratedInvisiblePrimaryKey(p_DatabaseName, @ss_pk_table, 'my_row_id'), 0);
        IF @ss_pk_generated = 1 THEN
            SET @exec_sql = REPLACE(@exec_sql, '` DROP PRIMARY KEY, ADD PRIMARY KEY (', '` DROP PRIMARY KEY, DROP COLUMN `my_row_id`, ADD PRIMARY KEY (');
        END IF;
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT(IF(@ss_pk_haskey = 1, '  Replace primary key: ', '  Add primary key: '), @ss_pk_audit));
        IF @ss_pk_fk_needs = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            VALUES (CONNECTION_ID(), CONCAT('  Kept primary key of ', @ss_pk_table, ' unchanged: a foreign key needs its current columns'));
        ELSEIF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @exec_sql);
            IF @ss_pk_haskey = 1 THEN
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_pk_audit, 'wouldDrop');
            END IF;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_pk_audit, 'wouldCreate');
        ELSE
            SET @ss_pk_fk_needs := 0;
            SET @ss_pk_hidden := 0;
            PREPARE stmt FROM @exec_sql;
            BEGIN
                -- A foreign key needs the current key's columns: the key is kept, as a modified index would be.
                DECLARE CONTINUE HANDLER FOR 1553 SET @ss_pk_fk_needs := 1;
                -- A key the catalog does not show: MySQL's generated one, hidden by
                -- show_gipk_in_create_table_and_information_schema. It is replaced the same way, with its column.
                DECLARE CONTINUE HANDLER FOR 1068 SET @ss_pk_hidden := 1;
                DECLARE EXIT HANDLER FOR SQLEXCEPTION
                BEGIN
                    GET DIAGNOSTICS CONDITION 1 @ss_errno = MYSQL_ERRNO;
                    IF @ss_errno = 4119 THEN
                        SET @ss_errmsg = LEFT(CONCAT('Versioned table ', @ss_pk_table,
                                                     ': set SystemVersioningAlterHistory=KEEP to change its primary key'), 128);
                        SIGNAL SQLSTATE 'HY000' SET MESSAGE_TEXT = @ss_errmsg;
                    ELSE
                        RESIGNAL;
                    END IF;
                END;
                EXECUTE stmt;
            END;
            DEALLOCATE PREPARE stmt;
            IF @ss_pk_hidden = 1 AND @ss_pk_haskey = 0 THEN
                SET @exec_sql = REPLACE(@exec_sql, '` ADD PRIMARY KEY (', '` DROP COLUMN `my_row_id`, ADD PRIMARY KEY (');
                SET @ss_pk_haskey := 1;
                SET @ss_pk_generated := 1;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END IF;
            IF @ss_pk_fk_needs = 1 THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Kept primary key of ', @ss_pk_table, ' unchanged: a foreign key needs its current columns'));
            ELSE
                IF @ss_pk_haskey = 1 THEN
                    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_pk_audit, 'dropped');
                END IF;
                IF @ss_pk_generated = 1 THEN
                    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'column', CONCAT(@ss_pk_table, '.my_row_id'), 'dropped');
                END IF;
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_pk_audit, 'created');
            END IF;
        END IF;
        SET @ss_pk_id := (SELECT MIN(RowId) FROM _SchemaSmith_PrimaryKeyStmts WHERE RowId > @ss_pk_id);
    END WHILE;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PrimaryKeyStmts;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LeadingIndexColumns;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LiveTables;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PkForeignKeyColumns;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PkOtherIndexColumns;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_LivePrimaryKeys;
END//

DELIMITER ;
