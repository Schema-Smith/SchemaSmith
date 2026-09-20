-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ForeignKeyQuench//

CREATE PROCEDURE SchemaSmith_ForeignKeyQuench(
    IN p_ProductName VARCHAR(100),
    IN p_DatabaseName VARCHAR(128),
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
    -- The catalog is utf8mb3. Comparing a bare catalog column against a value in the SAME charset lets
    -- MariaDB push the schema filter down (EXPLAIN: "Scanned 1 database" rather than "Scanned all
    -- databases"); wrapping the column in CONVERT(... USING utf8mb4) defeated it and cost ~1.8ms per
    -- database ON THE SERVER, on every deploy. A bare parameter does NOT work -- it carries the
    -- connection charset -- so the declared local is load-bearing, not decoration.
    DECLARE v_IsDbName VARCHAR(128) CHARACTER SET utf8mb3 COLLATE utf8mb3_general_ci DEFAULT p_DatabaseName;

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
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        FkColumns TEXT,
        RefColumns TEXT,
        PRIMARY KEY (ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- One pass over KEY_COLUMN_USAGE, aggregated per constraint. GROUP_CONCAT ordering and the
    -- default ',' separator match the correlated subqueries this replaces, so composite FKs compare
    -- byte-for-byte as before.
    INSERT INTO _SchemaSmith_ExistingFKCols (ConstraintName, FkColumns, RefColumns)
    SELECT kcu.CONSTRAINT_NAME,
           GROUP_CONCAT(kcu.COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION),
           GROUP_CONCAT(kcu.REFERENCED_COLUMN_NAME ORDER BY kcu.ORDINAL_POSITION)
      FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
     WHERE BINARY kcu.CONSTRAINT_SCHEMA = BINARY p_DatabaseName
       AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
     GROUP BY kcu.CONSTRAINT_NAME;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingFKs;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingFKs (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ReferencedTable VARCHAR(128),
        DeleteRule VARCHAR(64),
        UpdateRule VARCHAR(64),
        PRIMARY KEY (TableName, ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_ExistingFKs (TableName, ConstraintName, ReferencedTable, DeleteRule, UpdateRule)
    SELECT tc.TABLE_NAME, tc.CONSTRAINT_NAME, rc.REFERENCED_TABLE_NAME, rc.DELETE_RULE, rc.UPDATE_RULE
      FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
      JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
        ON BINARY rc.CONSTRAINT_SCHEMA = BINARY p_DatabaseName
       AND BINARY rc.CONSTRAINT_NAME = BINARY tc.CONSTRAINT_NAME
     WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
       AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

    -- Find FKs that exist but have different definition. Same predicates as before, now against the
    -- snapshots. The column join stays a LEFT JOIN so a constraint with no KEY_COLUMN_USAGE rows
    -- yields NULL and the comparison stays NULL (not flagged) -- the correlated-subquery behaviour.
    INSERT INTO _SchemaSmith_ModifiedFKs (TableName, ConstraintName)
    SELECT
        SchemaSmith_StripBacktickWrapping(f.TableName) AS TableName,
        SchemaSmith_StripBacktickWrapping(f.KeyName) AS ConstraintName
    FROM _SchemaSmith_ForeignKeys f
    JOIN _SchemaSmith_ExistingFKs e
        ON BINARY e.TableName = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
        AND BINARY e.ConstraintName = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
    LEFT JOIN _SchemaSmith_ExistingFKCols c
        ON BINARY c.ConstraintName = BINARY e.ConstraintName
    WHERE (
        -- Different referenced table
        BINARY e.ReferencedTable != BINARY SchemaSmith_StripBacktickWrapping(f.RelatedTable)
        -- Or different delete action
        OR BINARY e.DeleteRule != BINARY COALESCE(f.DeleteAction, 'NO ACTION')
        -- Or different update action
        OR BINARY e.UpdateRule != BINARY COALESCE(f.UpdateAction, 'NO ACTION')
        -- Or different columns (aggregate comparison handles composite FKs;
        -- REPLACE strips backticks from comma-separated column lists like `Col1`,`Col2`)
        OR BINARY c.FkColumns != BINARY REPLACE(f.Columns, '`', '')
        -- Or different referenced columns
        OR BINARY c.RefColumns != BINARY REPLACE(f.RelatedColumns, '`', '')
    );

    -- Drop modified FKs
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop and recreate modified foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                      '` DROP FOREIGN KEY `', ConstraintName, '`')
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
        CREATE TEMPORARY TABLE _SchemaSmith_FKModStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        -- Two separate INSERTs (not one UNION) because a MySQL TEMPORARY table cannot be
        -- referenced twice in one statement. AUTO_INCREMENT RowId is monotonic across the two,
        -- so the FK drops (inserted first) always get lower RowIds than the index drops.
        INSERT INTO _SchemaSmith_FKModStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
        FROM _SchemaSmith_ModifiedFKs
        GROUP BY TableName;
        INSERT INTO _SchemaSmith_FKModStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', m.TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP INDEX `', m.ConstraintName, '`') ORDER BY m.ConstraintName SEPARATOR ', '))
        FROM _SchemaSmith_ModifiedFKs m
        JOIN INFORMATION_SCHEMA.STATISTICS s
            ON BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY s.TABLE_NAME = BINARY m.TableName
            AND BINARY s.INDEX_NAME = BINARY m.ConstraintName
            AND s.SEQ_IN_INDEX = 1
        GROUP BY m.TableName;

        SET @v_fkmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKModStmts);
        WHILE @v_fkmod_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKModStmts WHERE RowId = @v_fkmod_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_fkmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKModStmts WHERE RowId > @v_fkmod_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKModStmts;
    END IF;

    -- =========================================================================
    -- STEP 2: Create missing foreign keys
    -- =========================================================================
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT(
                      'ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', f.TableName,
                      ' ADD CONSTRAINT ', f.KeyName,
                      ' FOREIGN KEY (', f.Columns, ')',
                      ' REFERENCES ',
                      CASE WHEN f.RelatedTableSchema IS NOT NULL AND f.RelatedTableSchema != ''
                           THEN CONCAT('`', f.RelatedTableSchema, '`.')
                           ELSE CONCAT('`', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.')
                      END,
                      f.RelatedTable, ' (', f.RelatedColumns, ')',
                      ' ON DELETE ', f.DeleteAction,
                      ' ON UPDATE ', f.UpdateAction)
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
        );

        -- #363: WhatIf twin of the ELSE-branch 'foreignKey'/'created' audit; same source/predicate, wouldCreate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName)), 'wouldCreate'
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
        );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing foreign keys');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Create FK: ', SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName),
            CASE WHEN COALESCE(f.VariantName, '') <> '' THEN CONCAT(' (variant: ', f.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
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
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
        );

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FKCreateStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FKCreateStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', f.TableName, ' ',
                      GROUP_CONCAT(
                          CONCAT(
                              'ADD CONSTRAINT ', f.KeyName,
                              ' FOREIGN KEY (', f.Columns, ')',
                              ' REFERENCES ',
                              CASE WHEN f.RelatedTableSchema IS NOT NULL AND f.RelatedTableSchema != ''
                                   THEN CONCAT('`', f.RelatedTableSchema, '`.')
                                   ELSE CONCAT('`', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.')
                              END,
                              f.RelatedTable, ' (', f.RelatedColumns, ')',
                              ' ON DELETE ', f.DeleteAction,
                              ' ON UPDATE ', f.UpdateAction)
                          ORDER BY f.KeyName SEPARATOR ', '))
        FROM _SchemaSmith_ForeignKeys f
        WHERE NOT EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
        )
        GROUP BY f.TableName;

        SET @v_fkcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateStmts);
        WHILE @v_fkcreate_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKCreateStmts WHERE RowId = @v_fkcreate_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_fkcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKCreateStmts WHERE RowId > @v_fkcreate_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKCreateStmts;
    END IF;

    -- =========================================================================
    -- STEP 3: Update ProductOwnership for managed foreign keys
    -- =========================================================================
    IF p_WhatIf = 0 THEN
        -- Track foreign keys
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'FOREIGN KEY',
               CONCAT(SchemaSmith_StripBacktickWrapping(f.TableName), '.', SchemaSmith_StripBacktickWrapping(f.KeyName))
        FROM _SchemaSmith_ForeignKeys f
        WHERE EXISTS (
            SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.TableName)
              AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(f.KeyName)
              AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
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

        INSERT INTO _SchemaSmith_WouldDropFKs (TableName, ConstraintName)
        SELECT
            SUBSTRING_INDEX(po.ObjectName, '.', 1) AS TableName,
            SUBSTRING_INDEX(po.ObjectName, '.', -1) AS ConstraintName
        FROM SchemaSmith_ProductOwnership po
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_ForeignKeys f
              WHERE CONVERT(SchemaSmith_StripBacktickWrapping(f.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(SchemaSmith_StripBacktickWrapping(f.KeyName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
          )
          -- Verify FK actually exists
          AND EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              WHERE tc.TABLE_SCHEMA = v_IsDbName
                AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
                AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
          )
          -- Per-table tightening: a table may set DropForeignKeysRemovedFromProduct:false to protect its own FKs.
          AND COALESCE((SELECT t.DropForeignKeysRemovedFromProduct
                          FROM _SchemaSmith_Tables t
                          WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
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
        INSERT INTO _SchemaSmith_FKsToDrop (TableName, ConstraintName)
        SELECT
            SUBSTRING_INDEX(po.ObjectName, '.', 1) AS TableName,
            SUBSTRING_INDEX(po.ObjectName, '.', -1) AS ConstraintName
        FROM SchemaSmith_ProductOwnership po
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_ForeignKeys f
              WHERE CONVERT(SchemaSmith_StripBacktickWrapping(f.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(SchemaSmith_StripBacktickWrapping(f.KeyName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
          )
          -- Verify FK actually exists
          AND EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
              WHERE tc.TABLE_SCHEMA = v_IsDbName
                AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
                AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
          )
          -- Per-table tightening: a table may set DropForeignKeysRemovedFromProduct:false to protect its own FKs.
          AND COALESCE((SELECT t.DropForeignKeysRemovedFromProduct
                          FROM _SchemaSmith_Tables t
                          WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                          LIMIT 1), 1) = 1;

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown foreign keys');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                          '` DROP FOREIGN KEY `', ConstraintName, '`')
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
            CREATE TEMPORARY TABLE _SchemaSmith_FKDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            -- Two separate INSERTs (MySQL TEMPORARY table can't be referenced twice per statement);
            -- FK drops inserted first get lower RowIds than the index drops.
            INSERT INTO _SchemaSmith_FKDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
            FROM _SchemaSmith_FKsToDrop
            GROUP BY TableName;
            INSERT INTO _SchemaSmith_FKDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', d.TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP INDEX `', d.ConstraintName, '`') ORDER BY d.ConstraintName SEPARATOR ', '))
            FROM _SchemaSmith_FKsToDrop d
            JOIN INFORMATION_SCHEMA.STATISTICS s
                ON BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY s.TABLE_NAME = BINARY d.TableName
                AND BINARY s.INDEX_NAME = BINARY d.ConstraintName
                AND s.SEQ_IN_INDEX = 1
            GROUP BY d.TableName;

            -- Object-change audit (#243 E5): one row per FK about to be dropped. Set-based over the
            -- computed _SchemaSmith_FKsToDrop temp (no INFORMATION_SCHEMA — not the #337 shape); the
            -- drop below folds a table's FKs into one ALTER, so per-FK audit is captured here.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'foreignKey', CONCAT(TableName, '.', ConstraintName), 'dropped'
            FROM _SchemaSmith_FKsToDrop;

            SET @v_fkdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKDropStmts);
            WHILE @v_fkdrop_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKDropStmts WHERE RowId = @v_fkdrop_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_fkdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKDropStmts WHERE RowId > @v_fkdrop_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKDropStmts;

            -- Remove dropped FKs from ProductOwnership
            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_FKsToDrop ftd
                ON po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(ftd.TableName, '.', ftd.ConstraintName) COLLATE utf8mb4_unicode_ci
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'FOREIGN KEY' COLLATE utf8mb4_unicode_ci;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsToDrop;
    END IF;

    -- Cleanup temporary tables
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedFKs;

END//

DELIMITER ;
