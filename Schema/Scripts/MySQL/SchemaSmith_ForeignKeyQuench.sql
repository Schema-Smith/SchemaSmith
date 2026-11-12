-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ForeignKeyQuench//

CREATE PROCEDURE SchemaSmith_ForeignKeyQuench(
    IN p_ProductName VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT,
    IN p_DropUnknownIndexes TINYINT,
    IN p_DropForeignKeysRemovedFromProduct TINYINT
)
SQL SECURITY DEFINER
BEGIN
    -- This procedure creates, modifies, and drops foreign keys.
    -- It reads from the _SchemaSmith_ForeignKeys temp table populated by ParseTableJson.
    -- Separated from MissingIndexesAndConstraintsQuench so it can run AFTER data delivery,
    -- avoiding the add-drop-readd cycle for circular FK dependencies.

    DECLARE v_Done INT DEFAULT FALSE;
    -- Names compare through their keys: tables through SchemaSmith_IdentifierKey, constraints and columns
    -- case-insensitively, as the engine does. v_DbCi is a case-insensitive utf8mb4 schema prefilter the catalog can
    -- serve without a full scan; the key compare against v_DbKey then decides exactly. Catalog reads land in keyed
    -- snapshots, so no correlated subquery calls a key function on its outer row (see ModifiedTableQuench).
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);

    -- STEP 2b: one key a table rebuild set aside at a time.
    DECLARE v_SaId INT;
    DECLARE v_SaChildSchema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaChildTable VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaKeyName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaParentSchema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaParentTable VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaChildCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
    DECLARE v_SaParentCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;
    DECLARE v_SaRefColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaDefinition TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaLabel VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaError VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_SaFailed TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_SaChildExists INT DEFAULT 0;
    DECLARE v_SaKeyExists INT DEFAULT 0;
    DECLARE v_SaParentReady INT DEFAULT 0;
    DECLARE v_SaRemoved TINYINT DEFAULT 0;
    DECLARE v_SaThisFailed TINYINT DEFAULT 0;
    DECLARE v_SavedChecks INT DEFAULT 1;
    DECLARE v_SaLock VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_Done = TRUE;

    SET SESSION group_concat_max_len = 1000000;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'BEGIN ForeignKeyQuench');

    -- =========================================================================
    -- STEP 1: Drop FKs that need modification (different definition)
    -- =========================================================================
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedFKs;
    CREATE TEMPORARY TABLE _SchemaSmith_ModifiedFKs (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableName, ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Snapshot the live FK picture ONCE before comparing.
    --
    -- This comparison used to read INFORMATION_SCHEMA inside the per-row path: two joins plus two
    -- correlated KEY_COLUMN_USAGE subqueries, with every predicate wrapped in BINARY and a stored
    -- function so nothing could be pushed down or indexed. INFORMATION_SCHEMA is not a real table --
    -- each access re-materialises server-wide metadata -- so the cost was (declared FKs x 4 scans).
    -- Measured on MariaDB 10.2 with 333 tables: TABLE_CONSTRAINTS 1.73s, KEY_COLUMN_USAGE 1.96s,
    -- REFERENTIAL_CONSTRAINTS 1.19s per scan, so 90 declared FKs cost ~600s to compare 90 rows.
    -- Hoisting the metadata into temp tables turns ~360 scans into 3.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingFKCols;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingFKCols (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        FkColumns TEXT,
        RefColumns TEXT,
        PRIMARY KEY (TableName, ConstraintName),
        KEY ix_key (ConstraintKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- One pass over KEY_COLUMN_USAGE, aggregated per constraint, each column in the canonical form
    -- SchemaSmith_NormalizeIndexColumns gives the declared list (`name`, backticks doubled, ',' between). Grouped per TABLE as well as name: from MariaDB 12.1 foreign-key names are
    -- unique per table (unnamed ones are all "1"), and grouping by name alone merged their column lists.
    INSERT INTO _SchemaSmith_ExistingFKCols (TableName, ConstraintName, ConstraintKey, FkColumns, RefColumns)
    SELECT kcu.TABLE_NAME,
           kcu.CONSTRAINT_NAME,
           SchemaSmith_NameKeyCI(kcu.CONSTRAINT_NAME),
           GROUP_CONCAT(CONCAT('`', SchemaSmith_EscapeBackticks(kcu.COLUMN_NAME), '`') ORDER BY kcu.ORDINAL_POSITION SEPARATOR ','),
           GROUP_CONCAT(CONCAT('`', SchemaSmith_EscapeBackticks(kcu.REFERENCED_COLUMN_NAME), '`') ORDER BY kcu.ORDINAL_POSITION SEPARATOR ',')
      FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
     WHERE kcu.CONSTRAINT_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(kcu.CONSTRAINT_SCHEMA) = v_DbKey
       AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
     GROUP BY kcu.TABLE_NAME, kcu.CONSTRAINT_NAME;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingFKs;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingFKs (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ReferencedTable VARCHAR(128),
        DeleteRule VARCHAR(64),
        UpdateRule VARCHAR(64),
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ConstraintKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ReferencedTableKey VARCHAR(260) COLLATE utf8mb4_bin,
        ReferencedSchemaKey VARCHAR(260) COLLATE utf8mb4_bin,
        PRIMARY KEY (TableName, ConstraintName),
        KEY ix_key (TableKey, ConstraintKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_ExistingFKs (TableName, ConstraintName, ReferencedTable, DeleteRule, UpdateRule,
                                          TableKey, ConstraintKey, ReferencedTableKey, ReferencedSchemaKey)
    SELECT tc.TABLE_NAME, tc.CONSTRAINT_NAME, rc.REFERENCED_TABLE_NAME, rc.DELETE_RULE, rc.UPDATE_RULE,
           SchemaSmith_IdentifierKey(tc.TABLE_NAME), SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME),
           SchemaSmith_IdentifierKey(rc.REFERENCED_TABLE_NAME),
           -- The referenced table's database: the unique key it references lives in it.
           SchemaSmith_IdentifierKey(rc.UNIQUE_CONSTRAINT_SCHEMA)
      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
      JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
        ON rc.CONSTRAINT_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(rc.CONSTRAINT_SCHEMA) = v_DbKey
       AND CAST(rc.CONSTRAINT_NAME AS BINARY) = CAST(tc.CONSTRAINT_NAME AS BINARY)
       AND CAST(rc.TABLE_NAME AS BINARY) = CAST(tc.TABLE_NAME AS BINARY)
     WHERE tc.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = v_DbKey
       AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

    -- Find FKs that exist but have different definition. Same predicates as before, now against the
    -- snapshots. The column join stays a LEFT JOIN so a constraint with no KEY_COLUMN_USAGE rows
    -- yields NULL and the comparison stays NULL (not flagged) -- the correlated-subquery behaviour.
    -- ConstraintName is the server's spelling: it is what the drop names, and it can differ from the package's.
    INSERT INTO _SchemaSmith_ModifiedFKs (TableName, ConstraintName)
    SELECT
        SchemaSmith_StripBacktickWrapping(f.TableName) AS TableName,
        e.ConstraintName AS ConstraintName
    FROM _SchemaSmith_ForeignKeys f
    JOIN _SchemaSmith_ExistingFKs e
        ON e.TableKey = f.TableKey
        AND e.ConstraintKey = f.KeyNameKey
    LEFT JOIN _SchemaSmith_ExistingFKCols c
        ON CAST(c.TableName AS BINARY) = CAST(e.TableName AS BINARY)
        AND CAST(c.ConstraintName AS BINARY) = CAST(e.ConstraintName AS BINARY)
    WHERE (
        -- Spelled differently only in case: the same constraint to the engine, so it is dropped and re-created under
        -- the package's spelling. MySQL and MariaDB both refuse that as one ALTER (the new name is a duplicate until
        -- the drop commits), so the drop and the create are separate statements here, as for any modified FK.
        CAST(e.ConstraintName AS BINARY) != CAST(SchemaSmith_StripBacktickWrapping(f.KeyName) AS BINARY)
        -- Or different referenced table
        OR e.ReferencedTableKey != f.RelatedTableKey
        -- Or the same table name in another database (MY-027). An omitted RelatedTableSchema is the table's own database.
        OR e.ReferencedSchemaKey != IF(IFNULL(CHAR_LENGTH(TRIM(f.RelatedTableSchema)), 0) = 0, SchemaSmith_IdentifierKey(p_DatabaseName),
                                       SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(f.RelatedTableSchema)))
        -- Or different delete or update action. InnoDB has one rule under two names: an unstated rule reads RESTRICT on
        -- MariaDB and MySQL 5.7 and NO ACTION on MySQL 8.0, so the two compare equal or the key churned every deploy.
        OR CAST(REPLACE(UPPER(TRIM(e.DeleteRule)), 'RESTRICT', 'NO ACTION') AS BINARY) != CAST(REPLACE(UPPER(TRIM(COALESCE(f.DeleteAction, 'NO ACTION'))), 'RESTRICT', 'NO ACTION') AS BINARY)
        OR CAST(REPLACE(UPPER(TRIM(e.UpdateRule)), 'RESTRICT', 'NO ACTION') AS BINARY) != CAST(REPLACE(UPPER(TRIM(COALESCE(f.UpdateAction, 'NO ACTION'))), 'RESTRICT', 'NO ACTION') AS BINARY)
        -- Or different columns, compared case-insensitively as the engine does. Both sides are in the normalized form,
        -- so a hand-written `a`, `b` matches the catalog's list and a name holding a comma, space or backtick survives.
        OR CAST(LOWER(c.FkColumns) AS BINARY) != CAST(LOWER(SchemaSmith_NormalizeIndexColumns(f.Columns)) AS BINARY)
        -- Or different referenced columns
        OR CAST(LOWER(c.RefColumns) AS BINARY) != CAST(LOWER(SchemaSmith_NormalizeIndexColumns(f.RelatedColumns)) AS BINARY)
    );

    -- Drop modified FKs
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop and recreate modified foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(TableName),
                      '` DROP FOREIGN KEY `', SchemaSmith_EscapeBackticks(ConstraintName), '`')
        FROM _SchemaSmith_ModifiedFKs;
    ELSE
        -- Per-FK progress messages, set-based (preserves the per-FK log lines).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop and recreate modified foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop modified FK: ', TableName, '.', ConstraintName)
        FROM _SchemaSmith_ModifiedFKs;

        -- Snapshot the folded drop statements once: each table's FK drops (Ord 0) then its
        -- leftover auto-created index drops (Ord 1 — the index shares the FK name and survives
        -- the FK drop). Reading INFORMATION_SCHEMA once into the temp table keeps it out of the
        -- execution loop; Ord guarantees every FK drop runs before the matching index drop.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKModStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FKModStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, IndexName VARCHAR(512) NULL)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        -- Two separate INSERTs (not one UNION) because a MySQL TEMPORARY table cannot be
        -- referenced twice in one statement. AUTO_INCREMENT RowId is monotonic across the two,
        -- so the FK drops (inserted first) always get lower RowIds than the index drops.
        INSERT INTO _SchemaSmith_FKModStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(TableName), '` ',
                      GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', SchemaSmith_EscapeBackticks(ConstraintName), '`') ORDER BY ConstraintName SEPARATOR ', '))
        FROM _SchemaSmith_ModifiedFKs
        GROUP BY TableName;
        -- One index to a statement: another key can need the index, and the rest of the table's drops still run.
        INSERT INTO _SchemaSmith_FKModStmts (Stmt, IndexName)
        SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(m.TableName), '` ',
                      'DROP INDEX `', SchemaSmith_EscapeBackticks(m.ConstraintName), '`'),
               CONCAT(m.TableName, '.', m.ConstraintName)
        FROM _SchemaSmith_ModifiedFKs m
        JOIN INFORMATION_SCHEMA.STATISTICS s
            ON s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
            AND SchemaSmith_IdentifierKey(s.TABLE_NAME) = SchemaSmith_IdentifierKey(m.TableName)
            AND SchemaSmith_NameKeyCI(s.INDEX_NAME) = SchemaSmith_NameKeyCI(m.ConstraintName)
            AND s.SEQ_IN_INDEX = 1
        -- An index of the key's name that the package declares is the package's: dropped here, after the index pass,
        -- it was gone until the next deploy put it back.
        WHERE NOT EXISTS (SELECT 1 FROM _SchemaSmith_Indexes i
                           WHERE i.TableKey = SchemaSmith_IdentifierKey(m.TableName)
                             AND i.IndexKey = SchemaSmith_NameKeyCI(m.ConstraintName));

        SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKModStmts) INTO @v_fkmod_id;
        WHILE @v_fkmod_id IS NOT NULL DO
            SELECT Stmt, IndexName INTO @exec_sql, @ss_fk_index FROM _SchemaSmith_FKModStmts WHERE RowId = @v_fkmod_id;
            PREPARE stmt FROM @exec_sql;
            SET @ss_fk_needs_index := 0;
            BEGIN
                -- A key the package does not own can use the dropped key's index; the index stays while it does.
                DECLARE CONTINUE HANDLER FOR 1553 SET @ss_fk_needs_index := 1;
                EXECUTE stmt;
            END;
            DEALLOCATE PREPARE stmt;
            IF @ss_fk_needs_index = 1 THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Kept index ', @ss_fk_index, ': a foreign key needs it'));
            END IF;
            SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKModStmts WHERE RowId > @v_fkmod_id) INTO @v_fkmod_id;
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKModStmts;
    END IF;

    -- =========================================================================
    -- STEP 2: Create missing foreign keys
    -- =========================================================================
    -- FK existence after the modified-FK drops above, so a dropped FK is seen as missing and recreated.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FkExist;
    CREATE TEMPORARY TABLE _SchemaSmith_FkExist (
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ConstraintKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableKey, ConstraintKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT IGNORE INTO _SchemaSmith_FkExist (TableKey, ConstraintKey, TableName, ConstraintName)
    SELECT SchemaSmith_IdentifierKey(tc.TABLE_NAME), SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME),
           CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
     WHERE tc.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = v_DbKey
       AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT(
                      'ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.', f.TableName,
                      ' ADD CONSTRAINT ', f.KeyName,
                      ' FOREIGN KEY (', f.Columns, ')',
                      ' REFERENCES ',
                      CASE WHEN f.RelatedTableSchema IS NOT NULL AND f.RelatedTableSchema != ''
                           THEN CONCAT('`', SchemaSmith_EscapeBackticks(f.RelatedTableSchema), '`.')
                           ELSE CONCAT('`', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.')
                      END,
                      f.RelatedTable, ' (', f.RelatedColumns, ')',
                      ' ON DELETE ', f.DeleteAction,
                      ' ON UPDATE ', f.UpdateAction)
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        );

        -- #363: WhatIf twin of the ELSE-branch 'foreignKey'/'created' audit; same source/predicate, wouldCreate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName)), 'wouldCreate'
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Create FK: ', SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName),
            CASE WHEN COALESCE(f.VariantName, '') <> '' THEN CONCAT(' (variant: ', f.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        );

        -- Fold each table's missing-FK creates into one multi-clause ALTER, materialize, execute.
        -- Object-change audit (#243 E5): one row per FK about to be created. The create statement
        -- below folds a table's FKs into one ALTER, so per-FK audit uses the same pre-create NOT
        -- EXISTS predicate (evaluated before the ALTER runs). Same INFORMATION_SCHEMA read pattern
        -- the statement build below uses — not the #337 set-based-UPDATE shape.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName)), 'created'
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        );

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FKCreateStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FKCreateStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.', f.TableName, ' ',
                      GROUP_CONCAT(
                          CONCAT(
                              'ADD CONSTRAINT ', f.KeyName,
                              ' FOREIGN KEY (', f.Columns, ')',
                              ' REFERENCES ',
                              CASE WHEN f.RelatedTableSchema IS NOT NULL AND f.RelatedTableSchema != ''
                                   THEN CONCAT('`', SchemaSmith_EscapeBackticks(f.RelatedTableSchema), '`.')
                                   ELSE CONCAT('`', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.')
                              END,
                              f.RelatedTable, ' (', f.RelatedColumns, ')',
                              ' ON DELETE ', f.DeleteAction,
                              ' ON UPDATE ', f.UpdateAction)
                          ORDER BY f.KeyName SEPARATOR ', '))
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        )
          AND NOT EXISTS (
            SELECT 1 FROM SchemaSmith_RebuildSetAsideForeignKeys sa
            WHERE SchemaSmith_IdentifierKey(sa.ChildSchema) = v_DbKey
              AND SchemaSmith_IdentifierKey(sa.ChildTable) = f.TableKey
              AND SchemaSmith_NameKeyCI(sa.KeyName) = f.KeyNameKey
        )
        GROUP BY f.TableName;

        SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateStmts) INTO @v_fkcreate_id;
        WHILE @v_fkcreate_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKCreateStmts WHERE RowId = @v_fkcreate_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateStmts WHERE RowId > @v_fkcreate_id) INTO @v_fkcreate_id;
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateStmts;

        -- A declared key a table rebuild set aside is added on its own: another deploy's STEP 2b can restore it between
        -- the look above and this ADD, and a key already back is done with (as STEP 2b treats it), not a failure.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateOne;
        CREATE TEMPORARY TABLE _SchemaSmith_FKCreateOne (
            RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT,
            TableKey VARCHAR(260) COLLATE utf8mb4_bin, KeyNameKey VARCHAR(260) COLLATE utf8mb4_bin, AuditName VARCHAR(300)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FKCreateOne (Stmt, TableKey, KeyNameKey, AuditName)
        SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.', f.TableName,
                      ' ADD CONSTRAINT ', f.KeyName,
                      ' FOREIGN KEY (', f.Columns, ')',
                      ' REFERENCES ',
                      CASE WHEN f.RelatedTableSchema IS NOT NULL AND f.RelatedTableSchema != ''
                           THEN CONCAT('`', SchemaSmith_EscapeBackticks(f.RelatedTableSchema), '`.')
                           ELSE CONCAT('`', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.')
                      END,
                      f.RelatedTable, ' (', f.RelatedColumns, ')',
                      ' ON DELETE ', f.DeleteAction,
                      ' ON UPDATE ', f.UpdateAction),
               f.TableKey, f.KeyNameKey,
               CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName))
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        )
          AND EXISTS (
            SELECT 1 FROM SchemaSmith_RebuildSetAsideForeignKeys sa
            WHERE SchemaSmith_IdentifierKey(sa.ChildSchema) = v_DbKey
              AND SchemaSmith_IdentifierKey(sa.ChildTable) = f.TableKey
              AND SchemaSmith_NameKeyCI(sa.KeyName) = f.KeyNameKey
        );

        SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateOne) INTO @v_fkone_id;
        WHILE @v_fkone_id IS NOT NULL DO
            SELECT Stmt, TableKey, KeyNameKey, AuditName INTO @exec_sql, @v_fkone_table, @v_fkone_key, @v_fkone_audit
              FROM _SchemaSmith_FKCreateOne WHERE RowId = @v_fkone_id;
            BEGIN
                DECLARE v_OneErrno INT DEFAULT 0;
                DECLARE v_OneError VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
                BEGIN
                    DECLARE EXIT HANDLER FOR SQLEXCEPTION
                        GET DIAGNOSTICS CONDITION 1 v_OneErrno = MYSQL_ERRNO, v_OneError = MESSAGE_TEXT;
                    PREPARE stmt FROM @exec_sql;
                    EXECUTE stmt;
                    DEALLOCATE PREPARE stmt;
                END;
                IF v_OneErrno <> 0 THEN
                    IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                                WHERE tc.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = v_DbKey
                                  AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = @v_fkone_table
                                  AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY' AND SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME) = @v_fkone_key) THEN
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                        VALUES (CONNECTION_ID(), CONCAT('  ', @v_fkone_audit, ': already in place, another deploy restored it first'));
                        DELETE FROM SchemaSmith_ChangeAudit
                         WHERE SessionId = CONNECTION_ID() AND ObjectType = 'foreignKey' AND ActionType = 'created'
                           AND CONVERT(ObjectName USING utf8mb4) COLLATE utf8mb4_unicode_ci = CONVERT(@v_fkone_audit USING utf8mb4) COLLATE utf8mb4_unicode_ci;
                    ELSE
                        -- SIGNAL carries 128 characters; the deploy log keeps the whole error.
                        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                        VALUES (CONNECTION_ID(), CONCAT('  ', @v_fkone_audit, ' failed: ', v_OneError));
                        SET @ss_msg = LEFT(v_OneError, 128);
                        SIGNAL SQLSTATE 'HY000' SET MESSAGE_TEXT = @ss_msg, MYSQL_ERRNO = v_OneErrno;
                    END IF;
                END IF;
            END;
            SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateOne WHERE RowId > @v_fkone_id) INTO @v_fkone_id;
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateOne;
    END IF;

    -- =========================================================================
    -- STEP 2b: Restore the foreign keys a table rebuild set aside
    -- =========================================================================
    -- SchemaSmith_RebuildTable drops every key that references the table it rebuilds and records it here. A key back
    -- on its table -- declared and added above, or re-created by hand -- is done with; one whose table is gone cannot
    -- come back; one this product owns and no longer declares stays dropped, as the pass below drops it on a table that
    -- was not rebuilt. Every other one is added again from its captured definition, with checks off so it comes back
    -- over the rows it had. A key whose table has no index for it yet waits for the run that puts that index back,
    -- which may not be this one. Any run restores any of them, and one that fails stays recorded, named in the error.
    IF EXISTS (SELECT 1 FROM SchemaSmith_RebuildSetAsideForeignKeys) THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Restore foreign keys set aside by a table rebuild');
        SET v_SavedChecks = @@SESSION.foreign_key_checks;
        SET v_SaId = (SELECT MIN(Id) FROM SchemaSmith_RebuildSetAsideForeignKeys);
        WHILE v_SaId IS NOT NULL DO
            SELECT ChildSchema, ChildTable, KeyName, ParentSchema, ParentTable, ReferencedColumns, Definition
              INTO v_SaChildSchema, v_SaChildTable, v_SaKeyName, v_SaParentSchema, v_SaParentTable, v_SaRefColumns, v_SaDefinition
              FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE Id = v_SaId;
            SET v_SaChildCi = v_SaChildSchema;
            SET v_SaParentCi = v_SaParentSchema;
            SET v_SaLabel = CONCAT(v_SaChildSchema, '.', v_SaChildTable, '.', v_SaKeyName);

            -- Taken before the key is looked for, and held until it is restored or cleared: a rebuild records its keys and
            -- drops them under this lock, so a key read as still there by a pass that only checked the lock afterwards had
            -- been dropped in between, and clearing its record lost it. Another pass holds it only for one key, so it waits.
            SET v_SaLock = CONCAT('ss_rbfk_', LEFT(SHA2(CONCAT(v_SaParentSchema, '.', v_SaParentTable), 256), 32));
            IF COALESCE(GET_LOCK(v_SaLock, 10), 0) <> 1 THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Foreign key ', v_SaLabel, ' waits: another deploy is still rebuilding ', v_SaParentSchema, '.', v_SaParentTable));
            ELSE
            BEGIN
            DECLARE EXIT HANDLER FOR SQLEXCEPTION
            BEGIN
                DO RELEASE_LOCK(v_SaLock);
                RESIGNAL;
            END;
            SELECT COUNT(*) INTO v_SaChildExists FROM INFORMATION_SCHEMA.TABLES t
             WHERE t.TABLE_SCHEMA = v_SaChildCi AND SchemaSmith_IdentifierKey(t.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(v_SaChildSchema)
               AND SchemaSmith_IdentifierKey(t.TABLE_NAME) = SchemaSmith_IdentifierKey(v_SaChildTable) AND t.TABLE_TYPE = 'BASE TABLE';
            SELECT COUNT(*) INTO v_SaKeyExists FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
             WHERE tc.TABLE_SCHEMA = v_SaChildCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(v_SaChildSchema)
               AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = SchemaSmith_IdentifierKey(v_SaChildTable)
               AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY' AND SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME) = SchemaSmith_NameKeyCI(v_SaKeyName);
            SET v_SaRemoved = 0;
            IF p_DropForeignKeysRemovedFromProduct = 1 AND SchemaSmith_IdentifierKey(v_SaChildSchema) = v_DbKey
               AND EXISTS (SELECT 1 FROM SchemaSmith_ProductOwnership po
                            WHERE SchemaSmith_OwnedNameMatches(po.ObjectName, v_SaChildTable, v_SaKeyName) = 1
                              AND po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                              AND SchemaSmith_IdentifierKey(po.ObjectSchema) = v_DbKey
                              AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci)
               AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_ForeignKeys f
                                WHERE f.TableKey = SchemaSmith_IdentifierKey(v_SaChildTable) AND f.KeyNameKey = SchemaSmith_NameKeyCI(v_SaKeyName))
               AND COALESCE((SELECT t.DropForeignKeysRemovedFromProduct FROM _SchemaSmith_Tables t
                              WHERE t.TableKey = SchemaSmith_IdentifierKey(v_SaChildTable) LIMIT 1), 1) = 1 THEN
                SET v_SaRemoved = 1;
            END IF;

            -- The referenced table, with an index whose leading columns are the key's, in order: what the engine needs.
            SELECT COUNT(*) INTO v_SaParentReady
              FROM (SELECT GROUP_CONCAT(CONCAT('`', SchemaSmith_EscapeBackticks(s.COLUMN_NAME), '`') ORDER BY s.SEQ_IN_INDEX SEPARATOR ',') AS Cols
                      FROM INFORMATION_SCHEMA.STATISTICS s
                     WHERE s.TABLE_SCHEMA = v_SaParentCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(v_SaParentSchema)
                       AND SchemaSmith_IdentifierKey(s.TABLE_NAME) = SchemaSmith_IdentifierKey(v_SaParentTable)
                       AND s.INDEX_TYPE NOT IN ('FULLTEXT', 'SPATIAL')
                     GROUP BY s.INDEX_NAME) x
             WHERE CAST(LOWER(LEFT(x.Cols, CHAR_LENGTH(v_SaRefColumns))) AS BINARY) = CAST(LOWER(v_SaRefColumns) AS BINARY)
               AND (CHAR_LENGTH(x.Cols) = CHAR_LENGTH(v_SaRefColumns)
                    OR CAST(SUBSTRING(x.Cols, CHAR_LENGTH(v_SaRefColumns) + 1, 1) AS BINARY) = CAST(',' AS BINARY));

            IF v_SaChildExists = 0 OR v_SaKeyExists > 0 OR v_SaRemoved = 1 THEN
                IF v_SaChildExists = 0 THEN
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Not restoring foreign key ', v_SaLabel, ': the table no longer exists'));
                END IF;
                IF p_WhatIf = 0 THEN
                    DELETE FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE Id = v_SaId;
                END IF;
            ELSEIF v_SaParentReady = 0 THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Foreign key ', v_SaLabel, ' waits: ', v_SaParentSchema, '.', v_SaParentTable,
                        ' has no index for it to reference yet'));
            ELSE
                SET @exec_sql = CONCAT('ALTER TABLE ', SchemaSmith_SafeBacktickWrap(v_SaChildSchema), '.', SchemaSmith_SafeBacktickWrap(v_SaChildTable),
                                       ' ADD CONSTRAINT ', SchemaSmith_SafeBacktickWrap(v_SaKeyName), ' ', v_SaDefinition);
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Restoring foreign key ', v_SaLabel));
                IF p_WhatIf = 1 THEN
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @exec_sql);
                    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                    VALUES (CONNECTION_ID(), 'foreignKey', CONCAT(v_SaChildTable, '.', v_SaKeyName), 'wouldCreate');
                ELSE
                    SET v_SaThisFailed = 0;
                    BEGIN
                        DECLARE EXIT HANDLER FOR SQLEXCEPTION
                        BEGIN
                            GET DIAGNOSTICS CONDITION 1 v_SaError = MESSAGE_TEXT;
                            SET SESSION foreign_key_checks = v_SavedChecks;
                            SET v_SaThisFailed = 1;
                        END;
                        SET SESSION foreign_key_checks = 0;
                        PREPARE stmt FROM @exec_sql;
                        EXECUTE stmt;
                        DEALLOCATE PREPARE stmt;
                        SET SESSION foreign_key_checks = v_SavedChecks;
                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'foreignKey', CONCAT(v_SaChildTable, '.', v_SaKeyName), 'created');
                        DELETE FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE Id = v_SaId;
                    END;
                    -- Another deploy in this database can restore the same key, or drop its table, between the look above
                    -- and the ADD. Either way there is nothing left to restore, and this deploy does not fail for it.
                    IF v_SaThisFailed = 1 THEN
                        SELECT COUNT(*) INTO v_SaChildExists FROM INFORMATION_SCHEMA.TABLES t
                         WHERE t.TABLE_SCHEMA = v_SaChildCi AND SchemaSmith_IdentifierKey(t.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(v_SaChildSchema)
                           AND SchemaSmith_IdentifierKey(t.TABLE_NAME) = SchemaSmith_IdentifierKey(v_SaChildTable) AND t.TABLE_TYPE = 'BASE TABLE';
                        SELECT COUNT(*) INTO v_SaKeyExists FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                         WHERE tc.TABLE_SCHEMA = v_SaChildCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(v_SaChildSchema)
                           AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = SchemaSmith_IdentifierKey(v_SaChildTable)
                           AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY' AND SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME) = SchemaSmith_NameKeyCI(v_SaKeyName);
                        IF v_SaChildExists = 0 OR v_SaKeyExists > 0 THEN
                            DELETE FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE Id = v_SaId;
                        ELSE
                            SET v_SaFailed = CONCAT_WS('; ', v_SaFailed, CONCAT(v_SaLabel, ' (', v_SaError, ')'));
                        END IF;
                    END IF;
                END IF;
            END IF;
            DO RELEASE_LOCK(v_SaLock);
            END;
            END IF;
            SET v_SaId = (SELECT MIN(Id) FROM SchemaSmith_RebuildSetAsideForeignKeys WHERE Id > v_SaId);
        END WHILE;
        IF v_SaFailed IS NOT NULL THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            VALUES (CONNECTION_ID(), CONCAT('  Foreign keys set aside by a table rebuild could not be restored, and stay recorded for the next run: ', v_SaFailed,
                    '. To give one up, delete its row from SchemaSmith_RebuildSetAsideForeignKeys.'));
            SET @ss_msg = LEFT(CONCAT('Foreign keys set aside by a table rebuild could not be restored: ', v_SaFailed), 128);
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- =========================================================================
    -- STEP 3: Update ProductOwnership for managed foreign keys
    -- =========================================================================
    -- Rebuilt to the post-create state for the ownership and by-absence passes below.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FkExist;
    CREATE TEMPORARY TABLE _SchemaSmith_FkExist (
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        ConstraintKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableKey, ConstraintKey)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT IGNORE INTO _SchemaSmith_FkExist (TableKey, ConstraintKey, TableName, ConstraintName)
    SELECT SchemaSmith_IdentifierKey(tc.TABLE_NAME), SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME),
           CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
     WHERE tc.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = v_DbKey
       AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

    IF p_WhatIf = 0 THEN
        -- Track foreign keys
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'FOREIGN KEY',
               CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName))
        FROM _SchemaSmith_ForeignKeys f
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_FkExist fe
            WHERE fe.TableKey = f.TableKey
              AND fe.ConstraintKey = f.KeyNameKey
        );
    END IF;

    -- =========================================================================
    -- No-drop protection tier (#270): capture foreign keys that WOULD have been dropped by absence
    -- but are suppressed. Same by-absence predicate as STEP 4's _SchemaSmith_FKsToDrop build below,
    -- minus the env p_DropForeignKeysRemovedFromProduct gate (protection forces it false, so STEP 4
    -- is skipped) but keeping the per-table opt-out. Materialize the INFORMATION_SCHEMA read into a
    -- temp first (crash-safety), then a discrete audit insert. Audit rows only, so it runs regardless
    -- of p_WhatIf. The capture signal is the session user-variable @ss_capture_would_drop set by the
    -- caller on the connection (this proc takes no new parameter). ObjectName mirrors STEP 4's
    -- 'foreignKey'/'dropped' audit: CONCAT(TableName, '.', ConstraintName).
    IF COALESCE(@ss_capture_would_drop, 0) = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropFKs;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropFKs (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT IGNORE INTO _SchemaSmith_WouldDropFKs (TableName, ConstraintName)
        SELECT fe.TableName, fe.ConstraintName
        FROM _SchemaSmith_FkExist fe
        JOIN SchemaSmith_ProductOwnership po
          ON SchemaSmith_OwnedNameMatches(po.ObjectName, fe.TableName, fe.ConstraintName) = 1
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND SchemaSmith_IdentifierKey(po.ObjectSchema) = SchemaSmith_IdentifierKey(p_DatabaseName)
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_ForeignKeys f
              WHERE f.TableKey = fe.TableKey AND f.KeyNameKey = fe.ConstraintKey
          )
          -- Per-table tightening: a table may set DropForeignKeysRemovedFromProduct:false to protect its own FKs.
          AND COALESCE((SELECT t.DropForeignKeysRemovedFromProduct
                          FROM _SchemaSmith_Tables t
                          WHERE t.TableKey = fe.TableKey
                          LIMIT 1), 1) = 1;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(TableName, '.', ConstraintName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropFKs;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropFKs;
    END IF;

    -- =========================================================================
    -- STEP 4: Drop FKs owned by product but not in definition
    -- Gated by DropForeignKeysRemovedFromProduct (decoupled from DropUnknownIndexes):
    -- MySQL FK cleanup no longer requires enabling index drops.
    -- =========================================================================
    IF p_DropForeignKeysRemovedFromProduct = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_FKsToDrop (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Find FKs owned by product but not in current definition
        INSERT IGNORE INTO _SchemaSmith_FKsToDrop (TableName, ConstraintName)
        SELECT fe.TableName, fe.ConstraintName
        FROM _SchemaSmith_FkExist fe
        JOIN SchemaSmith_ProductOwnership po
          ON SchemaSmith_OwnedNameMatches(po.ObjectName, fe.TableName, fe.ConstraintName) = 1
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND SchemaSmith_IdentifierKey(po.ObjectSchema) = SchemaSmith_IdentifierKey(p_DatabaseName)
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_ForeignKeys f
              WHERE f.TableKey = fe.TableKey AND f.KeyNameKey = fe.ConstraintKey
          )
          -- Per-table tightening: a table may set DropForeignKeysRemovedFromProduct:false to protect its own FKs.
          AND COALESCE((SELECT t.DropForeignKeysRemovedFromProduct
                          FROM _SchemaSmith_Tables t
                          WHERE t.TableKey = fe.TableKey
                          LIMIT 1), 1) = 1;

        IF p_WhatIf = 1 THEN
            -- WhatIf drops nothing, so a key the table step would drop is still in the catalog here; preview it once.
            DELETE f FROM _SchemaSmith_FKsToDrop f
             WHERE EXISTS (SELECT 1 FROM SchemaSmith_ChangeAudit a
                      WHERE a.SessionId = CONNECTION_ID() AND a.ObjectType = 'foreignKey' AND a.ActionType = 'wouldDrop'
                        AND CONVERT(a.ObjectName USING utf8mb4) COLLATE utf8mb4_bin = CONVERT(CONCAT(f.TableName, '.', f.ConstraintName) USING utf8mb4) COLLATE utf8mb4_bin);
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown foreign keys');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(TableName),
                          '` DROP FOREIGN KEY `', SchemaSmith_EscapeBackticks(ConstraintName), '`')
            FROM _SchemaSmith_FKsToDrop;

            -- #363: WhatIf twin of the ELSE-branch 'foreignKey'/'dropped' audit; same source, wouldDrop.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'foreignKey', CONCAT(TableName, '.', ConstraintName), 'wouldDrop'
            FROM _SchemaSmith_FKsToDrop;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown foreign keys');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop unknown FK: ', TableName, '.', ConstraintName)
            FROM _SchemaSmith_FKsToDrop;

            -- Same folded pattern as STEP 1: FK drops (Ord 0) then leftover auto-index drops (Ord 1).
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKDropStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_FKDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, IndexName VARCHAR(512) NULL)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            -- Two separate INSERTs (MySQL TEMPORARY table can't be referenced twice per statement);
            -- FK drops inserted first get lower RowIds than the index drops.
            INSERT INTO _SchemaSmith_FKDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(TableName), '` ',
                          GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', SchemaSmith_EscapeBackticks(ConstraintName), '`') ORDER BY ConstraintName SEPARATOR ', '))
            FROM _SchemaSmith_FKsToDrop
            GROUP BY TableName;
            -- One index to a statement: another key can need the index, and the rest of the table's drops still run.
            INSERT INTO _SchemaSmith_FKDropStmts (Stmt, IndexName)
            SELECT CONCAT('ALTER TABLE `', SchemaSmith_EscapeBackticks(CONVERT(p_DatabaseName USING utf8mb4)) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_EscapeBackticks(d.TableName), '` ',
                          'DROP INDEX `', SchemaSmith_EscapeBackticks(d.ConstraintName), '`'),
                   CONCAT(d.TableName, '.', d.ConstraintName)
            FROM _SchemaSmith_FKsToDrop d
            JOIN INFORMATION_SCHEMA.STATISTICS s
                ON s.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(s.TABLE_SCHEMA) = v_DbKey
                AND SchemaSmith_IdentifierKey(s.TABLE_NAME) = SchemaSmith_IdentifierKey(d.TableName)
                AND SchemaSmith_NameKeyCI(s.INDEX_NAME) = SchemaSmith_NameKeyCI(d.ConstraintName)
                AND s.SEQ_IN_INDEX = 1
            WHERE NOT EXISTS (SELECT 1 FROM _SchemaSmith_Indexes i
                               WHERE i.TableKey = SchemaSmith_IdentifierKey(d.TableName)
                                 AND i.IndexKey = SchemaSmith_NameKeyCI(d.ConstraintName));

            -- Object-change audit (#243 E5): one row per FK about to be dropped. Set-based over the
            -- computed _SchemaSmith_FKsToDrop temp (no INFORMATION_SCHEMA — not the #337 shape); the
            -- drop below folds a table's FKs into one ALTER, so per-FK audit is captured here.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'foreignKey', CONCAT(TableName, '.', ConstraintName), 'dropped'
            FROM _SchemaSmith_FKsToDrop;

            SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKDropStmts) INTO @v_fkdrop_id;
            WHILE @v_fkdrop_id IS NOT NULL DO
                SELECT Stmt, IndexName INTO @exec_sql, @ss_fk_index FROM _SchemaSmith_FKDropStmts WHERE RowId = @v_fkdrop_id;
                PREPARE stmt FROM @exec_sql;
                SET @ss_fk_needs_index := 0;
                BEGIN
                    -- A key the package does not own can use the dropped key's index; the index stays while it does.
                    DECLARE CONTINUE HANDLER FOR 1553 SET @ss_fk_needs_index := 1;
                    EXECUTE stmt;
                END;
                DEALLOCATE PREPARE stmt;
                IF @ss_fk_needs_index = 1 THEN
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Kept index ', @ss_fk_index, ': a foreign key needs it'));
                END IF;
                SELECT (SELECT MIN(RowId) FROM _SchemaSmith_FKDropStmts WHERE RowId > @v_fkdrop_id) INTO @v_fkdrop_id;
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKDropStmts;

            -- Remove dropped FKs from ProductOwnership
            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_FKsToDrop ftd
                ON SchemaSmith_OwnedNameMatches(po.ObjectName, ftd.TableName, ftd.ConstraintName) = 1
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND SchemaSmith_IdentifierKey(po.ObjectSchema) = SchemaSmith_IdentifierKey(p_DatabaseName)
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsToDrop;
    END IF;

    -- Cleanup temporary tables
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedFKs;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FkExist;

END//

DELIMITER ;
