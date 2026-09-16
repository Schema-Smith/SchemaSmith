-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

-- This procedure is ADD-ONLY where indexes are concerned: index rename, modification and REMOVAL all
-- live in ModifiedTableQuench on MySQL/MariaDB, matching SQL Server. That is why p_DropUnknownIndexes
-- and p_DropIndexesRemovedFromProduct are NOT parameters here. Check constraints are the exception --
-- their by-absence removal is still owned by this procedure.
-- Do not infer an engine's capability from which procedure carries a flag: parameter placement is a
-- division of labour between procedures, not a statement about what the engine supports. Reading a
-- signature and concluding MySQL cannot remove indexes is the specific mistake this comment exists to
-- prevent -- it was made six times in one audit, in both directions.
DROP PROCEDURE IF EXISTS SchemaSmith_MissingIndexesAndConstraintsQuench//

CREATE PROCEDURE SchemaSmith_MissingIndexesAndConstraintsQuench(
    IN p_ProductName VARCHAR(100),
    IN p_DatabaseName VARCHAR(128),
    IN p_WhatIf TINYINT,
    IN p_DropCheckConstraintsRemovedFromProduct TINYINT
)
SQL SECURITY DEFINER
BEGIN
    -- This procedure CREATES missing indexes and check constraints, and drops CHECK CONSTRAINTS removed
    -- from the product. All index rename, modification and removal live in ModifiedTableQuench.
    -- It reads from the _SchemaSmith_Indexes and _SchemaSmith_CheckConstraints
    -- temp tables populated by ParseTableJson.
    -- Foreign keys are handled separately by SchemaSmith_ForeignKeyQuench.
    --
    -- Execution model: each per-row DDL loop materializes its statements (plus the exact
    -- per-object progress message) into an AUTO_INCREMENT temp table, then a WHILE loop
    -- drains it by ascending RowId (SELECT ... INTO + PREPARE/EXECUTE). This replaces the
    -- old cursors and matches the materialize-then-WHILE idiom used by ForeignKeyQuench.
    -- Detection reads INFORMATION_SCHEMA through STEP-LOCAL snapshots, not per declared object.
    -- INFORMATION_SCHEMA is not a stored table on MySQL/MariaDB, so a correlated / per-row read
    -- re-materialises server-wide metadata once per object (cost = declared objects x tables-on-instance);
    -- the same shape ForeignKeyQuench eliminated. Each detection pass instead snapshots the catalog it
    -- needs into a temp table with ONE scan, then joins it. The snapshots are placed to preserve the
    -- exact point-in-time state each pass must see relative to THIS proc's own earlier mutations:
    --   * _SchemaSmith_IdxExistPostDrop -- taken after ModifiedTableQuench's index-rename and modified-index
    --     drops so STEP 3 sees a dropped modified index as MISSING and recreates it.
    --   * _SchemaSmith_ChkExist -- built after the STEP 3.5 / by-absence check drops for the STEP 4/4.5
    --     create passes, then REBUILT post-create for STEP 7 ownership.
    --   * _SchemaSmith_IdxExistFinal + the STEP 7 _SchemaSmith_ChkExist rebuild -- post-create, so STEP 7
    --     writes ownership only for objects that actually landed.
    -- In WhatIf mode nothing is mutated, so every snapshot reflects the unchanged catalog -- identical to
    -- the live reads.

    SET SESSION group_concat_max_len = 1000000;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'BEGIN MissingIndexesAndConstraintsQuench');

    -- _SchemaSmith_IndexRenames is now populated by ModifiedTableQuench (rename detection moved there to
    -- match SQL Server) but is still read three times below, in STEP 3, as the
    -- "not a renamed index" exclusion. Those are two SEPARATELY CHECKPOINTED steps in DatabaseQuench
    -- ("ModifiedTables" and "IndexesAndConstraints"), so a resume that finds ModifiedTables already
    -- complete skips ModifiedTableQuench entirely and this session never creates the table -- every
    -- reference below would then fail with ER_NO_SUCH_TABLE (1146). Temp tables are session-scoped, so a
    -- resumed run starts with none of them; ParseTableJson rebuilds the parse-level tables but has no
    -- reason to know about this one.
    --
    -- An empty table is the CORRECT state in that case, not merely a safe one: if ModifiedTableQuench was
    -- skipped, its renames were executed and committed by the earlier run, so there are no pending renames
    -- to exclude. On the normal path ModifiedTableQuench has already created and populated it and
    -- IF NOT EXISTS makes this a no-op. Mirrors the MySqlTempTablesExist / ParseMySqlTableJson re-parse
    -- defense DatabaseQuench.cs already applies to the parse-level tables.
    CREATE TEMPORARY TABLE IF NOT EXISTS _SchemaSmith_IndexRenames (
        TableName VARCHAR(128) NOT NULL,
        OldIndexName VARCHAR(128) NOT NULL,
        NewIndexName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, OldIndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- =========================================================================
    -- STEP 0: Add missing generated columns (deferred from MissingTableAndColumnQuench
    -- to ensure dependencies like functions are deployed first)
    -- =========================================================================
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add missing generated columns');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Add generated column: ', c.TableName, '.', c.ColumnName,
            CASE WHEN COALESCE(c.VariantName, '') <> '' THEN CONCAT(' (variant: ', c.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        WHERE c.GeneratedExpression IS NOT NULL
          AND TRIM(c.GeneratedExpression) != ''
          AND c.NewColumn = 1
        ORDER BY c.TableName, c.DependencyLevel, c.OrdinalPosition;

        -- #363: WhatIf twin of the ELSE-branch generated-column 'created' audit; same source, same
        -- AuditName form as _SchemaSmith_GenColStmts.AuditName.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'wouldCreate'
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        WHERE c.GeneratedExpression IS NOT NULL
          AND TRIM(c.GeneratedExpression) != ''
          AND c.NewColumn = 1;
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add missing generated columns');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_GenColStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT, AuditName TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_GenColStmts (LogMsg, Stmt, AuditName)
        SELECT
            CONCAT('  Add generated column: ',
                   CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                          ' ADD COLUMN ', c.ColumnScript),
                   CASE WHEN COALESCE(c.VariantName, '') <> '' THEN CONCAT(' (variant: ', c.VariantName, ')') ELSE '' END),
            CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                   ' ADD COLUMN ', c.ColumnScript),
            CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        WHERE c.GeneratedExpression IS NOT NULL
          AND TRIM(c.GeneratedExpression) != ''
          AND c.NewColumn = 1
        ORDER BY c.TableName, c.DependencyLevel, c.OrdinalPosition;

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenColStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt, AuditName INTO @ss_log, @exec_sql, @ss_auditname FROM _SchemaSmith_GenColStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'column', @ss_auditname, 'created');
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenColStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColStmts;
    END IF;

    -- =========================================================================
    -- STEP 3: Create missing indexes (non-primary)
    -- =========================================================================
    -- Post-mutation existence snapshot: taken HERE, after ModifiedTableQuench's renames and modified-index drops, so it
    -- reflects the current catalog -- exactly what the original per-index live NOT EXISTS reads saw at
    -- this point. This is the crux the "snapshot once at top" approach would get wrong: a just-dropped
    -- modified index (dropped by ModifiedTableQuench) must be seen as MISSING here and recreated, so this snapshot must be
    -- taken after the drops, not reused from the pre-STEP-1 detection snapshot. One row per index.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxExistPostDrop;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxExistPostDrop (
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_IdxExistPostDrop (TableName, IndexName)
    SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4)
    FROM INFORMATION_SCHEMA.STATISTICS s
    WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
      AND s.SEQ_IN_INDEX = 1;

    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT(
                      'CREATE ',
                      CASE WHEN UPPER(i.IndexType) = 'SPATIAL' THEN 'SPATIAL '
                           WHEN i.IsUnique = 1 AND i.IsPrimaryKey = 0 THEN 'UNIQUE '
                           ELSE '' END,
                      'INDEX ', i.IndexName,
                      ' ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', i.TableName,
                      ' (', i.IndexColumns, ')',
                      CASE WHEN UPPER(i.IndexType) = 'HASH' THEN ' USING HASH'
                           WHEN UPPER(i.IndexType) = 'BTREE' THEN ' USING BTREE'
                           ELSE '' END,
                      CASE WHEN i.Comment IS NOT NULL AND i.Comment != ''
                           THEN CONCAT(' COMMENT ''', REPLACE(i.Comment, '''', ''''''), '''')
                           ELSE '' END)
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          -- Not already renamed
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE r.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND r.NewIndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExistPostDrop s
              WHERE s.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND s.IndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          -- A declared functional index below the floor (see the STEP 0.7 degrade guard above) is
          -- never created -- it is a hard syntax error, not a clause that can be suppressed.
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0);

        -- #363: WhatIf twin of the ELSE-branch 'index'/'created' audit; same predicate, set-based wouldCreate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'index', CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName)), 'wouldCreate'
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE r.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND r.NewIndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExistPostDrop s
              WHERE s.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND s.IndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0);
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing indexes');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CreateIdxStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_CreateIdxStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT, AuditName TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        -- Detection reads the post-drop snapshot _SchemaSmith_IdxExistPostDrop (taken after ModifiedTableQuench's
        -- modified-index drops) so a just-dropped modified index is correctly seen as missing here and recreated.
        INSERT INTO _SchemaSmith_CreateIdxStmts (LogMsg, Stmt, AuditName)
        SELECT
            CONCAT('  Create index: ', SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName),
                CASE WHEN COALESCE(i.VariantName, '') <> '' THEN CONCAT(' (variant: ', i.VariantName, ')') ELSE '' END),
            CONCAT(
                'CREATE ',
                CASE WHEN UPPER(i.IndexType) = 'SPATIAL' THEN 'SPATIAL '
                     WHEN i.IsUnique = 1 AND i.IsPrimaryKey = 0 THEN 'UNIQUE '
                     ELSE '' END,
                'INDEX ', i.IndexName,
                ' ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', i.TableName,
                ' (', i.IndexColumns, ')',
                CASE WHEN UPPER(i.IndexType) = 'HASH' THEN ' USING HASH'
                     WHEN UPPER(i.IndexType) = 'BTREE' THEN ' USING BTREE'
                     ELSE '' END,
                CASE WHEN i.Comment IS NOT NULL AND i.Comment != ''
                     THEN CONCAT(' COMMENT ''', REPLACE(i.Comment, '''', ''''''), '''')
                     ELSE '' END,
                CASE WHEN i.IsVisible = 0 AND SchemaSmith_SupportsInvisibleIndex() = 1 THEN SchemaSmith_IndexInvisibleClause() ELSE '' END
            ),
            CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE r.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND r.NewIndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExistPostDrop s
              WHERE s.TableName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.TableName) COLLATE utf8mb4_unicode_ci
                AND s.IndexName COLLATE utf8mb4_unicode_ci = SchemaSmith_StripBacktickWrapping(i.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0);

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_CreateIdxStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt, AuditName INTO @ss_log, @exec_sql, @ss_auditname FROM _SchemaSmith_CreateIdxStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_auditname, 'created');
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_CreateIdxStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CreateIdxStmts;
    END IF;

    -- =========================================================================
    -- STEP 3.5: Drop modified check constraints (table-level AND column-level)
    -- =========================================================================
    -- MySQL has no DDL to change a CHECK expression in place, so a constraint whose live
    -- expression no longer matches the desired one is dropped here and re-created by the
    -- create passes below (STEP 4 for table-level, STEP 4.5 for column-level). This closes
    -- the Bug 2 gap (a modified TABLE-level check used to drift silently) and gives parity
    -- with SQL Server / PostgreSQL, which both drop-then-recreate a changed check.
    --
    -- Comparison uses SchemaSmith_NormalizeCheckExpression on BOTH sides so an unchanged
    -- check is NOT phantom-dropped: MySQL reformats CHECK_CLAUSE on storage (adds an outer
    -- paren pair and normalizes spacing), which the normalizer collapses to a canonical form.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedChecks;
    CREATE TEMPORARY TABLE _SchemaSmith_ModifiedChecks (
        TableName VARCHAR(128) NOT NULL,
        ConstraintName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Table-level checks: live CHECK_CLAUSE differs from the desired _SchemaSmith_CheckConstraints.Expression.
    -- INFORMATION_SCHEMA.CHECK_CONSTRAINTS does not exist on MySQL 5.7 and MySQL binds INFORMATION_SCHEMA
    -- references at CREATE time, so both CHECK_CONSTRAINTS reads below live only inside dynamically-built
    -- strings, gated by SchemaSmith_SupportsCheckConstraints() (see GenerateTableJson for the full rationale).
    -- Below the floor there are no check constraints to detect as modified, so _SchemaSmith_ModifiedChecks
    -- simply stays unpopulated by this step.
    SET @v_mcDbName = p_DatabaseName;
    IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
        SET @v_mcSql1 = 'INSERT IGNORE INTO _SchemaSmith_ModifiedChecks (TableName, ConstraintName)
SELECT
    SchemaSmith_StripBacktickWrapping(c.TableName) AS TableName,
    SchemaSmith_StripBacktickWrapping(c.ConstraintName) AS ConstraintName
FROM _SchemaSmith_CheckConstraints c
JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
    ON BINARY tc.TABLE_SCHEMA = BINARY @v_mcDbName
    AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
    AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
    AND tc.CONSTRAINT_TYPE = ''CHECK''
JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
    ON BINARY cc.CONSTRAINT_SCHEMA = BINARY @v_mcDbName
    AND BINARY cc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
WHERE BINARY SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4))
    != BINARY SchemaSmith_NormalizeCheckExpression(c.Expression)
  AND SchemaSmith_ExpressionMapUnchanged(@v_mcDbName, SchemaSmith_StripBacktickWrapping(c.TableName),
        ''CHECK'', SchemaSmith_StripBacktickWrapping(c.ConstraintName), ''expression'',
        c.Expression, SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4))) = 0';
        PREPARE stmt FROM @v_mcSql1;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;


    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop modified check constraints');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ', SchemaSmith_DropCheckClause(), ' `', ConstraintName, '`')
        FROM _SchemaSmith_ModifiedChecks;
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop modified check constraints');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropModChkStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_DropModChkStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_DropModChkStmts (LogMsg, Stmt)
        SELECT
            CONCAT('  Drop modified check constraint: ', TableName, '.', ConstraintName),
            CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ', SchemaSmith_DropCheckClause(), ' `', ConstraintName, '`')
        FROM _SchemaSmith_ModifiedChecks;

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropModChkStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt INTO @ss_log, @exec_sql FROM _SchemaSmith_DropModChkStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropModChkStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropModChkStmts;
    END IF;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedChecks;

    -- =========================================================================
    -- No-drop protection tier (#270): capture check constraints that WOULD have been dropped by
    -- absence but are suppressed. Same by-absence predicate as the _SchemaSmith_ChecksToDropByAbsence
    -- build below, minus the env p_DropCheckConstraintsRemovedFromProduct gate (protection forces it
    -- false, so the drop pass is skipped) but keeping the per-table opt-out. Materialize the
    -- INFORMATION_SCHEMA read into a temp first (crash-safety), then a discrete audit insert. Audit
    -- rows only, so it runs regardless of p_WhatIf. The capture signal is the session user-variable
    -- @ss_capture_would_drop set by the caller on the connection (this proc takes no new parameter).
    -- ObjectName mirrors the drop pass's 'constraint'/'dropped' audit: CONCAT(TableName, '.', ConstraintName).
    IF COALESCE(@ss_capture_would_drop, 0) = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropChecks;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropChecks (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_WouldDropChecks (TableName, ConstraintName)
        SELECT CONVERT(tc.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
               CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        JOIN _SchemaSmith_Tables t
            ON BINARY SchemaSmith_StripBacktickWrapping(t.TableName) = BINARY tc.TABLE_NAME
            AND t.NewTable = 0
            AND COALESCE(t.DropCheckConstraintsRemovedFromProduct, 1) = 1
        WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
          AND tc.CONSTRAINT_TYPE = 'CHECK'
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_CheckConstraints c
              WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY tc.TABLE_NAME
                AND BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName) = BINARY tc.CONSTRAINT_NAME)
;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'constraint', CONCAT(TableName, '.', ConstraintName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropChecks;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropChecks;
    END IF;

    -- =========================================================================
    -- Drop check constraints removed from the product (by-absence), gated by the cascade flag
    -- and per-table tightening. Scoped to the current quench's product tables. Table-level checks
    -- absent from _SchemaSmith_CheckConstraints are dropped. A CK_<table>_<column> left over from the
    -- retired column-level CheckExpression alias is an ordinary undeclared check now: declare it as a
    -- table-level CheckConstraint to keep it, or let drop-by-absence clean it up.
    -- =========================================================================
    IF p_DropCheckConstraintsRemovedFromProduct = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ChecksToDropByAbsence;
        CREATE TEMPORARY TABLE _SchemaSmith_ChecksToDropByAbsence (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_ChecksToDropByAbsence (TableName, ConstraintName)
        SELECT CONVERT(tc.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
               CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        JOIN _SchemaSmith_Tables t
            ON BINARY SchemaSmith_StripBacktickWrapping(t.TableName) = BINARY tc.TABLE_NAME
            AND t.NewTable = 0
            AND COALESCE(t.DropCheckConstraintsRemovedFromProduct, 1) = 1
        WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
          AND tc.CONSTRAINT_TYPE = 'CHECK'
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_CheckConstraints c
              WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY tc.TABLE_NAME
                AND BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName) = BINARY tc.CONSTRAINT_NAME)

          -- MariaDB backs an application-time period with a CHECK constraint named after the period
          -- (`start < end`), indistinguishable from a user check constraint in the catalog -- same
          -- CONSTRAINT_TYPE, same LEVEL. Without this it looks like an undeclared check and
          -- drop-by-absence tries to remove it, which the engine refuses outright:
          --   ERROR 4158: Can't DROP CONSTRAINT `x`. Use DROP PERIOD `x` for this
          -- That failed EVERY re-deploy of a table carrying a period -- a pre-existing defect reachable
          -- by any MariaDB user with one, whether or not SchemaSmith manages the period.
          --
          -- Identified by name against the period reader, which is truthful only from 11.4. Below that
          -- it returns '[]' and the exclusion cannot fire, so the defect remains on 10.4.3-11.3: nothing
          -- in that catalog distinguishes the two, and guessing from the check clause would risk
          -- silently preserving a genuine user constraint that happens to compare two columns.
          AND JSON_SEARCH(SchemaSmith_TablePeriodsJson(p_DatabaseName, tc.TABLE_NAME),
                          'one', CONVERT(tc.CONSTRAINT_NAME USING utf8mb4), NULL, '$[*].Name') IS NULL;

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop check constraints removed from product');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ', SchemaSmith_DropCheckClause(), ' `', ConstraintName, '`')
            FROM _SchemaSmith_ChecksToDropByAbsence;

            -- #363: WhatIf twin of the ELSE-branch 'constraint'/'dropped' audit; same source, wouldDrop.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'constraint', CONCAT(TableName, '.', ConstraintName), 'wouldDrop'
            FROM _SchemaSmith_ChecksToDropByAbsence;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop check constraints removed from product');

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropAbsChkStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_DropAbsChkStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, AuditName TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_DropAbsChkStmts (Stmt, AuditName)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ', SchemaSmith_DropCheckClause(), ' `', ConstraintName, '`'),
                   CONCAT(TableName, '.', ConstraintName)
            FROM _SchemaSmith_ChecksToDropByAbsence;

            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropAbsChkStmts);
            WHILE @ss_id IS NOT NULL DO
                SELECT Stmt, AuditName INTO @exec_sql, @ss_auditname FROM _SchemaSmith_DropAbsChkStmts WHERE RowId = @ss_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'constraint', @ss_auditname, 'dropped');
                DEALLOCATE PREPARE stmt;
                SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropAbsChkStmts WHERE RowId > @ss_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropAbsChkStmts;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ChecksToDropByAbsence;
    END IF;

    -- =========================================================================
    -- CHECK-constraint existence snapshot: taken HERE, after STEP 3.5's modified-check drops and the
    -- by-absence drops, so a just-dropped check is correctly seen as MISSING by the STEP 4 / STEP 4.5
    -- create passes and recreated -- the same reason STEP 3 snapshots after the index drops. In WhatIf
    -- mode nothing was dropped, so this reflects the unchanged catalog, matching the live reads it
    -- replaces. One row per CHECK constraint. STEP 7 (ownership) REBUILDS this same table post-create,
    -- so the create passes here see the pre-create state and the ownership pass sees the post-create state.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ChkExist;
    CREATE TEMPORARY TABLE _SchemaSmith_ChkExist (
        TableName VARCHAR(128) NOT NULL,
        ConstraintName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
        INSERT INTO _SchemaSmith_ChkExist (TableName, ConstraintName)
        SELECT CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
          AND tc.CONSTRAINT_TYPE = 'CHECK';
    END IF;

    -- =========================================================================
    -- STEP 4: Create missing check constraints (MySQL 8.0.16+)
    -- =========================================================================
    -- CHECK constraints require MySQL 8.0.16 (INFORMATION_SCHEMA.CHECK_CONSTRAINTS + enforcement); on MySQL
    -- 5.7 a declared CHECK is parsed-and-ignored, so it can neither be created nor detected as present -- which
    -- would make this create pass re-emit on every deploy. Degrade via the UnsupportedFeaturePolicy spine
    -- (mirrors the PostgreSQL NULLS-NOT-DISTINCT routing): 'fail' aborts naming the offending constraint(s);
    -- 'warn' (default) skips the emit and records one 'downgraded' manifest row per declared check so the run
    -- stays idempotent. MariaDB reports support at/above the 10.2 floor, so it never enters this branch.
    IF SchemaSmith_SupportsCheckConstraints() = 0 THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail'
           AND EXISTS (SELECT 1 FROM _SchemaSmith_CheckConstraints) THEN
            -- Log the full offending list to the run log first (SIGNAL MESSAGE_TEXT is capped at 128 chars,
            -- so the abort message stays concise + non-truncating and names the detail in the log instead).
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  CHECK constraint unsupported (requires MySQL 8.0.16): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName))
            FROM _SchemaSmith_CheckConstraints c;
            -- Keep < 128 chars: MySQL errors ("Data too long for condition item") on an over-long MESSAGE_TEXT.
            SET @ss_msg = CONCAT('CHECK constraints require MySQL 8.0.16 (detected ',
                                 SchemaSmith_ServerVersionNum(), '); see the deploy log for the unsupported check(s).');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            -- Surface the downgrade in the run log too (not only the ChangeAudit manifest), matching this
            -- proc's per-object status-message convention, so a 5.7 deploy visibly reports skipped checks.
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Skipping check constraint (requires MySQL 8.0.16 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName))
            FROM _SchemaSmith_CheckConstraints c;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'CHECK constraint (MySQL 8.0.16)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName)), 'downgraded'
            FROM _SchemaSmith_CheckConstraints c;
        END IF;
    ELSEIF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing check constraints');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                      ' ADD CONSTRAINT ', c.ConstraintName,
                      ' CHECK (', c.Expression, ')')
        FROM _SchemaSmith_CheckConstraints c
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_ChkExist tc
            WHERE BINARY tc.TableName = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
              AND BINARY tc.ConstraintName = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
        );

        -- #363: WhatIf twin of the ELSE-branch 'constraint'/'created' (check) audit; same predicate, wouldCreate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'constraint', CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName)), 'wouldCreate'
        FROM _SchemaSmith_CheckConstraints c
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_ChkExist tc
            WHERE BINARY tc.TableName = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
              AND BINARY tc.ConstraintName = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
        );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing check constraints');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CreateChkStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_CreateChkStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT, AuditName TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        -- Detection reads the pre-create snapshot _SchemaSmith_ChkExist (taken after STEP 3.5's drops)
        -- so a just-dropped modified table check (STEP 3.5) is correctly seen as missing here and recreated.
        INSERT INTO _SchemaSmith_CreateChkStmts (LogMsg, Stmt, AuditName)
        SELECT
            CONCAT('  Create check constraint: ', c.TableName, '.', c.ConstraintName,
                CASE WHEN COALESCE(c.VariantName, '') <> '' THEN CONCAT(' (variant: ', c.VariantName, ')') ELSE '' END),
            CONCAT(
                'ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                ' ADD CONSTRAINT ', c.ConstraintName,
                ' CHECK (', c.Expression, ')'
            ),
            CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName))
        FROM _SchemaSmith_CheckConstraints c
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_ChkExist tc
            WHERE BINARY tc.TableName = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
              AND BINARY tc.ConstraintName = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
        );

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_CreateChkStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt, AuditName INTO @ss_log, @exec_sql, @ss_auditname FROM _SchemaSmith_CreateChkStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'constraint', @ss_auditname, 'created');
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_CreateChkStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CreateChkStmts;
    END IF;

    -- =========================================================================
    -- STEP 7: Update ProductOwnership for managed objects
    -- Confirms objects created THIS run (STEP 3/4) via post-create existence snapshots taken here,
    -- so ownership is written only for what actually landed -- the same confirmation the original live
    -- INFORMATION_SCHEMA reads gave, now one scan each instead of one per declared object.
    -- =========================================================================
    IF p_WhatIf = 0 THEN
        -- Post-create existence snapshots (indexes + CHECK constraints), reflecting STEP 3/4 creates.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxExistFinal;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxExistFinal (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IdxExistFinal (TableName, IndexName)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.SEQ_IN_INDEX = 1;

        -- Rebuild the CHECK existence snapshot to the post-create state (the create passes above saw the
        -- pre-create build; ownership must see what now exists).
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ChkExist;
        CREATE TEMPORARY TABLE _SchemaSmith_ChkExist (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
            INSERT INTO _SchemaSmith_ChkExist (TableName, ConstraintName)
            SELECT CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
              AND tc.CONSTRAINT_TYPE = 'CHECK';
        END IF;

        -- Track indexes
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'INDEX',
               CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
        FROM _SchemaSmith_Indexes i
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExistFinal s
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
        );

        -- Track check constraints
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'CHECK CONSTRAINT',
               CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ConstraintName))
        FROM _SchemaSmith_CheckConstraints c
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_ChkExist tc
            WHERE BINARY tc.TableName = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
              AND BINARY tc.ConstraintName = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
        );

    END IF;

    -- Cleanup temporary tables
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexRenames;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedIndexes;

END//

DELIMITER ;
