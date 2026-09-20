-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_IndexOnlyQuench//

CREATE PROCEDURE SchemaSmith_IndexOnlyQuench(
    IN p_ProductName VARCHAR(100),
    IN p_DatabaseName VARCHAR(128),
    IN p_WhatIf TINYINT,
    IN p_DropUnknownIndexes TINYINT,
    IN p_DropIndexesRemovedFromProduct TINYINT
)
SQL SECURITY DEFINER
BEGIN
    -- This procedure handles index-only quenching.
    -- It creates, modifies, and drops indexes but does NOT touch:
    -- - Table structure (columns, data types)
    -- - Foreign keys
    -- - Check constraints
    --
    -- It reads from the _SchemaSmith_Indexes and _SchemaSmith_FullTextIndexes
    -- temp tables populated by ParseTableJson.


    SET SESSION group_concat_max_len = 1000000;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'BEGIN IndexOnlyQuench');

    -- Ensure _SchemaSmith_FullTextIndexes exists (ParseTableJson may not create it if there are no fulltext indexes).
    -- Keep this fallback schema in lockstep with ParseTableJson's primary definition (see
    -- SchemaSmith_ParseTableJson.sql) — both use a synthetic RowId so two same-named entries
    -- with mutually exclusive ShouldApplyExpression can coexist until the ShouldApply DELETE pass.
    CREATE TEMPORARY TABLE IF NOT EXISTS _SchemaSmith_FullTextIndexes (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
        Columns TEXT NOT NULL,
        Parser VARCHAR(128) DEFAULT NULL,
        -- Widened from VARCHAR(255) to match MySQL's actual 1024-char index-comment ceiling and stay
        -- in lockstep with ParseTableJson's primary definition -- see the rationale there.
        Comment VARCHAR(1024) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_ft_table_name (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- =========================================================================
    -- STEP 0.5: Degrade descending index key parts below MySQL 8.0 / MariaDB 10.8
    -- =========================================================================
    -- Mirrors MissingIndexesAndConstraintsQuench STEP 0.5: these engines silently store a DESC key part
    -- ascending, so SchemaSmith_NormalizeIndexColumns drops the DESC suffix below the floor (keeping the
    -- compare/create steps idempotent) and one 'downgraded' manifest row + run-log line is recorded here.
    IF SchemaSmith_SupportsDescendingIndex() = 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Descending index key part stored ascending (requires MySQL 8.0 / MariaDB 10.8 - downgraded): ',
               SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
        FROM _SchemaSmith_Indexes i
        WHERE UPPER(CONVERT(i.IndexColumns USING utf8mb4)) LIKE '% DESC%';
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'INDEX (descending key part, MySQL 8.0 / MariaDB 10.8)',
               CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName)), 'downgraded'
        FROM _SchemaSmith_Indexes i
        WHERE UPPER(CONVERT(i.IndexColumns USING utf8mb4)) LIKE '% DESC%';
    END IF;

    -- =========================================================================
    -- STEP 0.6: Degrade functional/expression indexes below MySQL 8.0.13 (MariaDB: always)
    -- =========================================================================
    -- Mirrors MissingIndexesAndConstraintsQuench STEP 0.7. Unlike a DESC key part (parsed-and-ignored)
    -- or INVISIBLE (a suppressible clause), a functional/expression key part is a hard syntax error
    -- below the floor -- there is no reduced form to fall back to, so the whole index is skipped rather
    -- than degraded in place. MariaDB has no equivalent in this form at ANY version
    -- (SchemaSmith_SupportsFunctionalIndex() is unconditionally 0 there), so this block fires on MariaDB
    -- every time a functional index is declared, not just below some threshold. A multi-valued index
    -- (CAST(... AS ... ARRAY)) is a functional key part too and rides this same gate; see
    -- SchemaSmith_SupportsFunctionalIndex for why it needs no gate of its own. 'fail' aborts naming the
    -- offending index(es); 'warn' (default) records one 'downgraded' manifest row + a run-log line per
    -- declared functional index -- STEP 2 (modified-detect) and STEP 4 (create) below exclude it via the
    -- identical predicate, leaving any live index of the same name untouched.
    -- =========================================================================
    IF SchemaSmith_SupportsFunctionalIndex() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Indexes i
                   WHERE i.IsPrimaryKey = 0 AND SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Functional/expression index requires MySQL 8.0.13 (MariaDB unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
            FROM _SchemaSmith_Indexes i
            WHERE i.IsPrimaryKey = 0 AND SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1;
            SET @ss_msg = 'Functional/expression index requires MySQL 8.0.13; MariaDB unsupported (policy=fail). See the run log.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Skipping index (functional/expression key part requires MySQL 8.0.13, MariaDB unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
            FROM _SchemaSmith_Indexes i
            WHERE i.IsPrimaryKey = 0 AND SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'INDEX (functional/expression, MySQL 8.0.13)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName)), 'downgraded'
            FROM _SchemaSmith_Indexes i
            WHERE i.IsPrimaryKey = 0 AND SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1;
        END IF;
    END IF;

    -- =========================================================================
    -- Detection snapshot: the live index picture read ONCE here so STEP 1 (rename) and STEP 2
    -- (modified) join it instead of re-reading INFORMATION_SCHEMA.STATISTICS per declared index.
    -- INFORMATION_SCHEMA is not a stored table on MySQL/MariaDB, so the original per-row reads cost
    -- (declared indexes x whole-server scans). Pre-mutation state, which is what both passes read
    -- (STEP 1 renames only where columns match; STEP 2 excludes just-renamed indexes). STEP 4
    -- (create-missing) takes its own POST-drop snapshot. One row per index; NormColumns is the same
    -- GROUP_CONCAT the correlated subqueries used, so composite index column lists compare byte-for-byte.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDetectSnap;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxDetectSnap (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        NonUnique TINYINT DEFAULT 0,
        IndexType VARCHAR(32),
        NormColumns TEXT,
        -- MySQL's index comment ceiling is 1024 characters; MAX() alongside the other per-index
        -- aggregates below since INDEX_COMMENT is constant across a composite index's key parts.
        IndexComment VARCHAR(1024),
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    -- A functional/expression key part (MySQL 8.0.13+) has NULL COLUMN_NAME and reports its text via
    -- EXPRESSION instead; that column does not exist below the floor or on MariaDB, so the branch that
    -- reads it is gated behind SchemaSmith_SupportsFunctionalIndex() as two whole statements -- column
    -- resolution is deferred to the execution of whichever statement actually runs (see
    -- SchemaSmith_IndexIsVisible / SchemaSmith_SnapshotIndexVisibility for the same IS_VISIBLE-below-8.0
    -- shape), so the unreached branch's EXPRESSION reference is never bound on an engine that lacks it.
    -- Must produce the exact same per-key-part form as SchemaSmith_NormalizeIndexColumns and
    -- GenerateTableJson (one extra paren pair around the expression, charset-introducer noise AND
    -- the backslash-escaped quotes EXPRESSION carries around a literal's quotes both stripped the
    -- same two-pass way via REGEXP_REPLACE -- safe here despite the 5.7 floor because this branch
    -- only executes at 8.0.13+ and never on 5.7/MariaDB; see GenerateTableJson.sql for the full
    -- explanation) or the compare below never converges.
    IF SchemaSmith_SupportsFunctionalIndex() = 1 THEN
        INSERT INTO _SchemaSmith_IdxDetectSnap (TableName, IndexName, NonUnique, IndexType, NormColumns, IndexComment)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4),
               CONVERT(s.INDEX_NAME USING utf8mb4),
               MAX(s.NON_UNIQUE),
               CONVERT(MAX(s.INDEX_TYPE) USING utf8mb4),
               GROUP_CONCAT(
                   CASE WHEN s.COLUMN_NAME IS NOT NULL THEN
                       CONCAT('`', s.COLUMN_NAME, '`',
                              -- SPATIAL's SUB_PART is a phantom internal value (always 32), not a declared prefix; exclude it or spatial indexes never converge
                              IF(s.SUB_PART IS NOT NULL AND s.INDEX_TYPE != 'SPATIAL', CONCAT('(', s.SUB_PART, ')'), ''),
                              CASE WHEN BINARY s.COLLATION = BINARY 'D' THEN ' DESC' ELSE '' END)
                   ELSE
                       CONCAT('(', REGEXP_REPLACE(
                           REPLACE(s.EXPRESSION, CONCAT(CHAR(92), CHAR(39)), CHAR(39)),
                           '_[A-Za-z0-9]+''', ''''), ')',
                              CASE WHEN BINARY s.COLLATION = BINARY 'D' THEN ' DESC' ELSE '' END)
                   END
                   ORDER BY s.SEQ_IN_INDEX
                   SEPARATOR ','
               ),
               CONVERT(MAX(s.INDEX_COMMENT) USING utf8mb4)
          FROM INFORMATION_SCHEMA.STATISTICS s
         WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
         GROUP BY s.TABLE_NAME, s.INDEX_NAME;
    ELSE
        INSERT INTO _SchemaSmith_IdxDetectSnap (TableName, IndexName, NonUnique, IndexType, NormColumns, IndexComment)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4),
               CONVERT(s.INDEX_NAME USING utf8mb4),
               MAX(s.NON_UNIQUE),
               CONVERT(MAX(s.INDEX_TYPE) USING utf8mb4),
               GROUP_CONCAT(
                   CONCAT('`', s.COLUMN_NAME, '`',
                          IF(s.SUB_PART IS NOT NULL AND s.INDEX_TYPE != 'SPATIAL', CONCAT('(', s.SUB_PART, ')'), ''),
                          CASE WHEN BINARY s.COLLATION = BINARY 'D' THEN ' DESC' ELSE '' END)
                   ORDER BY s.SEQ_IN_INDEX
                   SEPARATOR ','
               ),
               CONVERT(MAX(s.INDEX_COMMENT) USING utf8mb4)
          FROM INFORMATION_SCHEMA.STATISTICS s
         WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
         GROUP BY s.TABLE_NAME, s.INDEX_NAME;
    END IF;

    -- Names-only copy: STEP 1 references the snapshot twice in one statement (main join + the
    -- "new index name doesn't exist" NOT EXISTS), which MySQL/MariaDB forbid for a TEMPORARY table
    -- (ER_CANT_REOPEN_TABLE 1137). The second reference reads this copy.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDetectNames;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxDetectNames (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_IdxDetectNames (TableName, IndexName)
    SELECT TableName, IndexName FROM _SchemaSmith_IdxDetectSnap;

    -- Per-engine index-visibility snapshot (MySQL IS_VISIBLE / MariaDb IGNORED), one scan, for STEP 2's
    -- modified-index visibility comparison -- replaces the per-candidate SchemaSmith_IndexIsVisible() call.
    CALL SchemaSmith_SnapshotIndexVisibility(p_DatabaseName);

    -- =========================================================================
    -- STEP 1: Detect index renames (same columns, different name)
    -- =========================================================================
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexRenames;
    CREATE TEMPORARY TABLE _SchemaSmith_IndexRenames (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        OldIndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        NewIndexName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, OldIndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Find indexes where the definition matches but the name is different
    -- An index is considered a rename candidate if:
    -- 1. The new index name doesn't exist
    -- 2. The old index exists and is owned by the product
    -- 3. The column list matches exactly
    INSERT INTO _SchemaSmith_IndexRenames (TableName, OldIndexName, NewIndexName)
    SELECT
        SchemaSmith_StripBacktickWrapping(i.TableName) AS TableName,
        snap.IndexName COLLATE utf8mb4_unicode_ci AS OldIndexName,
        SchemaSmith_StripBacktickWrapping(i.IndexName) AS NewIndexName
    FROM _SchemaSmith_Indexes i
    JOIN _SchemaSmith_IdxDetectSnap snap
        ON BINARY snap.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
    JOIN SchemaSmith_ProductOwnership po
        ON BINARY po.ProductName = BINARY p_ProductName
        AND BINARY po.ObjectSchema = BINARY p_DatabaseName
        AND po.ObjectType = 'INDEX'
        AND BINARY po.ObjectName = BINARY CONCAT(snap.TableName, '.', snap.IndexName)
    WHERE i.IsPrimaryKey = 0
      -- New index name doesn't exist
      AND NOT EXISTS (
          SELECT 1 FROM _SchemaSmith_IdxDetectNames s2
          WHERE BINARY s2.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
            AND BINARY s2.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
      )
      -- Old index exists with same columns (compare normalized column list)
      AND BINARY SchemaSmith_NormalizeIndexColumns(i.IndexColumns) = BINARY snap.NormColumns
      -- Same uniqueness
      AND i.IsUnique = (snap.NonUnique = 0);

    -- Handle renames
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Handle index renames');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      SchemaSmith_BuildIndexRenameClause(p_DatabaseName, TableName, OldIndexName, NewIndexName))
        FROM _SchemaSmith_IndexRenames;
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Handle index renames');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Rename index: ', TableName, '.', OldIndexName, ' -> ', NewIndexName)
        FROM _SchemaSmith_IndexRenames;

        -- Fold each table's index renames into one multi-clause ALTER, materialize, execute.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexRenameStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_IndexRenameStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IndexRenameStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(SchemaSmith_BuildIndexRenameClause(p_DatabaseName, TableName, OldIndexName, NewIndexName) ORDER BY OldIndexName SEPARATOR ', '))
        FROM _SchemaSmith_IndexRenames
        GROUP BY TableName;

        SET @v_rename_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexRenameStmts);
        WHILE @v_rename_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_IndexRenameStmts WHERE RowId = @v_rename_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_rename_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexRenameStmts WHERE RowId > @v_rename_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexRenameStmts;

        -- Update ProductOwnership with new names (set-based; mirrors the per-row UPDATE this loop replaced).
        UPDATE SchemaSmith_ProductOwnership po
        INNER JOIN _SchemaSmith_IndexRenames r
            ON BINARY po.ObjectName = BINARY CONCAT(r.TableName, '.', r.OldIndexName)
        SET po.ObjectName = CONCAT(r.TableName, '.', r.NewIndexName)
        WHERE BINARY po.ProductName = BINARY p_ProductName
          AND BINARY po.ObjectSchema = BINARY p_DatabaseName
          AND po.ObjectType = 'INDEX';
    END IF;

    -- STEP 1 executed its renames in the live branch, so each renamed index's OLD name is now gone from
    -- the catalog. Drop those old names from the detection snapshots before STEP 2 -- otherwise a declared
    -- index whose name equals a renamed-away old name (e.g. two indexes on one column, where one is renamed
    -- and the other declared under the freed-up old name) would match a stale snapshot row and be wrongly
    -- flagged as modified, generating a DROP for an index that no longer exists under that name. The
    -- original read live INFORMATION_SCHEMA here, which already reflected the rename. WhatIf executes no
    -- rename, so it must keep the old names to match that live-read behaviour -- hence the p_WhatIf guard.
    IF p_WhatIf = 0 THEN
        DELETE snap FROM _SchemaSmith_IdxDetectSnap snap
            JOIN _SchemaSmith_IndexRenames r
              ON BINARY r.TableName = BINARY snap.TableName AND BINARY r.OldIndexName = BINARY snap.IndexName;
        DELETE nm FROM _SchemaSmith_IdxDetectNames nm
            JOIN _SchemaSmith_IndexRenames r
              ON BINARY r.TableName = BINARY nm.TableName AND BINARY r.OldIndexName = BINARY nm.IndexName;
    END IF;

    -- =========================================================================
    -- STEP 2: Detect modified indexes (same name, different definition)
    -- =========================================================================
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedIndexes;
    CREATE TEMPORARY TABLE _SchemaSmith_ModifiedIndexes (
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        PRIMARY KEY (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Find indexes where the name matches but the definition is different
    INSERT INTO _SchemaSmith_ModifiedIndexes (TableName, IndexName)
    SELECT
        SchemaSmith_StripBacktickWrapping(i.TableName) AS TableName,
        SchemaSmith_StripBacktickWrapping(i.IndexName) AS IndexName
    FROM _SchemaSmith_Indexes i
    JOIN _SchemaSmith_IdxDetectSnap snap
        ON BINARY snap.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
        AND BINARY snap.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
    LEFT JOIN _SchemaSmith_ExistingIndexVisibility viz
        ON BINARY viz.TableName = BINARY snap.TableName
        AND BINARY viz.IndexName = BINARY snap.IndexName
    WHERE i.IsPrimaryKey = 0
      -- Skip indexes that were just renamed
      AND NOT EXISTS (
          SELECT 1 FROM _SchemaSmith_IndexRenames r
          WHERE BINARY r.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
            AND BINARY r.NewIndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
      )
      -- A declared functional/expression index this target cannot legally CREATE (below the floor / see
      -- the STEP 0.6 degrade guard above) is left untouched rather than flagged modified-and-dropped:
      -- STEP 4 below excludes it from recreation via the identical predicate, so dropping it here would
      -- lose it for good.
      AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0)
      -- Check if definition differs (columns, uniqueness, or index type)
      AND (
          -- Columns differ
          BINARY SchemaSmith_NormalizeIndexColumns(i.IndexColumns) != BINARY snap.NormColumns
          -- Or uniqueness differs
          OR i.IsUnique != (snap.NonUnique = 0)
          -- Or index type differs (BTREE vs HASH)
          OR (BINARY UPPER(COALESCE(i.IndexType, 'BTREE')) != BINARY UPPER(snap.IndexType)
              AND NOT (BINARY UPPER(COALESCE(i.IndexType, 'BTREE')) = BINARY 'BTREE' AND BINARY UPPER(snap.IndexType) = BINARY 'BTREE'))
          -- Or visibility differs (FULLTEXT indexes don't support INVISIBLE, skip them). Below the
          -- invisible-index floor (MySQL 8.0 / MariaDB 10.6) the keyword can't be emitted, so a declared
          -- invisible index is stored visible; ignore the visibility difference there or it churns every run.
          -- viz.IsVisible is the once-snapshotted per-engine visibility, replacing SchemaSmith_IndexIsVisible().
          OR (BINARY UPPER(snap.IndexType) != BINARY 'FULLTEXT'
              AND SchemaSmith_SupportsInvisibleIndex() = 1
              AND i.IsVisible != viz.IsVisible)
          -- Or comment differs (symmetric: covers added, changed, and cleared, matching the column
          -- comment predicate in ModifiedTableQuench). FULLTEXT indexes never reach this comparison
          -- (they are parsed separately into _SchemaSmith_FullTextIndexes and have no modified-index
          -- detection pass at all -- see this file's Comment column note), so no FULLTEXT exclusion
          -- is needed here.
          OR (BINARY COALESCE(snap.IndexComment, '') != BINARY COALESCE(i.Comment, ''))
      );

    -- Drop modified indexes (they'll be recreated later)
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop and recreate modified indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
        FROM _SchemaSmith_ModifiedIndexes;
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop and recreate modified indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop and recreate index: ', TableName, '.', IndexName)
        FROM _SchemaSmith_ModifiedIndexes;

        -- Fold each table's modified-index drops into one multi-clause ALTER, materialize, execute.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexModStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_IndexModStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IndexModStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP INDEX `', IndexName, '`') ORDER BY IndexName SEPARATOR ', '))
        FROM _SchemaSmith_ModifiedIndexes
        GROUP BY TableName;

        SET @v_indexmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexModStmts);
        WHILE @v_indexmod_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_IndexModStmts WHERE RowId = @v_indexmod_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_indexmod_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexModStmts WHERE RowId > @v_indexmod_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexModStmts;
    END IF;

    -- =========================================================================
    -- No-drop protection tier (#270): capture indexes that WOULD have been dropped by absence but
    -- are suppressed. STEP 3 below is skipped entirely under protection (the caller forces both
    -- p_DropUnknownIndexes and p_DropIndexesRemovedFromProduct false), so this block is
    -- self-contained: it snapshots the catalog (INFORMATION_SCHEMA out of the set-based DML, #337
    -- crash-safety), then computes BOTH STEP 3 axes' by-absence candidates MINUS their env gates
    -- into one temp — AXIS 1 removed-from-product (keeps the per-table
    -- COALESCE(t.DropIndexesRemovedFromProduct,1) opt-out) UNION AXIS 2 unknown/out-of-band — then a
    -- discrete audit insert. Audit rows only, so it runs regardless of p_WhatIf. The capture signal
    -- is the session user-variable @ss_capture_would_drop set by the caller on the connection (these
    -- procs take no new parameter). ObjectName mirrors the sibling 'index'/'dropped' audit in
    -- MissingIndexesAndConstraintsQuench STEP 8: CONCAT(TableName, '.', IndexName).
    IF COALESCE(@ss_capture_would_drop, 0) = 1 THEN
        -- Catalog snapshot (one row per index; SEQ_IN_INDEX = 1). Mirrors STEP 3's _SchemaSmith_IdxOnlyIdx.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropIdxCat;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropIdxCat (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_WouldDropIdxCat (TableName, IndexName)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.SEQ_IN_INDEX = 1;

        -- Defined-index snapshot (table.index pairs from the current definition). Mirrors STEP 3's _SchemaSmith_DefinedIndexes.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropDefIdx;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropDefIdx (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_WouldDropDefIdx (TableName, IndexName)
        SELECT CONVERT(SchemaSmith_StripBacktickWrapping(i.TableName) USING utf8mb4),
               CONVERT(SchemaSmith_StripBacktickWrapping(i.IndexName) USING utf8mb4)
        FROM _SchemaSmith_Indexes i;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropIndexes;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropIndexes (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- AXIS 1 — removed-from-product (env gate p_DropIndexesRemovedFromProduct dropped; per-table opt-out kept).
        INSERT IGNORE INTO _SchemaSmith_WouldDropIndexes (TableName, IndexName)
        SELECT DISTINCT
            SUBSTRING_INDEX(po.ObjectName, '.', 1),
            SUBSTRING_INDEX(po.ObjectName, '.', -1)
        FROM SchemaSmith_ProductOwnership po
        INNER JOIN _SchemaSmith_Tables t
            ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
        LEFT JOIN _SchemaSmith_WouldDropDefIdx di
            ON CONVERT(di.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
        LEFT JOIN _SchemaSmith_WouldDropIdxCat ei
            ON CONVERT(ei.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            AND CONVERT(ei.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = 'INDEX' COLLATE utf8mb4_unicode_ci
          -- Per-table tightening (the env/product gate is removed for capture)
          AND COALESCE(t.DropIndexesRemovedFromProduct, 1) = 1
          -- Never drop PRIMARY KEY
          AND UPPER(SUBSTRING_INDEX(po.ObjectName, '.', -1) COLLATE utf8mb4_unicode_ci) != 'PRIMARY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition (LEFT JOIN produces NULL when not found)
          AND di.IndexName IS NULL
          -- Not a renamed index
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE r.TableName = SUBSTRING_INDEX(po.ObjectName, '.', 1)
                AND r.OldIndexName = SUBSTRING_INDEX(po.ObjectName, '.', -1)
          )
          -- Verify index actually exists (snapshot bridge)
          AND ei.IndexName IS NOT NULL;

        -- AXIS 2 — out-of-band (env gate p_DropUnknownIndexes dropped).
        INSERT IGNORE INTO _SchemaSmith_WouldDropIndexes (TableName, IndexName)
        SELECT CONVERT(ei.TableName USING utf8mb4), CONVERT(ei.IndexName USING utf8mb4)
        FROM _SchemaSmith_WouldDropIdxCat ei
        INNER JOIN _SchemaSmith_Tables t
            ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
        WHERE UPPER(ei.IndexName COLLATE utf8mb4_unicode_ci) != 'PRIMARY' COLLATE utf8mb4_unicode_ci
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_WouldDropDefIdx di
              WHERE CONVERT(di.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
          )
          AND NOT EXISTS (
              SELECT 1 FROM SchemaSmith_ProductOwnership po
              WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                AND po.ObjectType COLLATE utf8mb4_unicode_ci = 'INDEX' COLLATE utf8mb4_unicode_ci
                AND po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(ei.TableName, '.', ei.IndexName) COLLATE utf8mb4_unicode_ci
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE CONVERT(r.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                AND CONVERT(r.OldIndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
          );

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'index', CONCAT(TableName, '.', IndexName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropIndexes;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropIndexes;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropDefIdx;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropIdxCat;
    END IF;

    -- =========================================================================
    -- STEP 3: Drop unknown indexes (owned by product but not in definition)
    -- =========================================================================
    IF p_DropUnknownIndexes = 1 OR p_DropIndexesRemovedFromProduct = 1 THEN
        -- Crash-safe snapshots (mirrors MissingIndexesAndConstraintsQuench STEP 8): decoupling
        -- removed-from-product from DropUnknownIndexes means this block runs on every IndexOnly
        -- quench (DropIndexesRemovedFromProduct defaults on), so detection + FK-drop read snapshots
        -- rather than live INFORMATION_SCHEMA in set-based DML (the frequent-run segfault trigger, #337).
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyIdx;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxOnlyIdx (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            NonUnique TINYINT DEFAULT 0,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IdxOnlyIdx (TableName, IndexName, NonUnique)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4), s.NON_UNIQUE
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.SEQ_IN_INDEX = 1;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyKCU;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxOnlyKCU (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            ReferencedTableName VARCHAR(128) COLLATE utf8mb4_bin DEFAULT NULL,
            KEY ix_iokcu (ReferencedTableName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IdxOnlyKCU (TableName, ConstraintName, ReferencedTableName)
        SELECT CONVERT(kcu.TABLE_NAME USING utf8mb4), CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4), CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
        WHERE BINARY kcu.REFERENCED_TABLE_SCHEMA = BINARY p_DatabaseName
          AND BINARY kcu.TABLE_SCHEMA = BINARY p_DatabaseName
          AND kcu.REFERENCED_TABLE_NAME IS NOT NULL;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyTC;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxOnlyTC (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            KEY ix_iotc (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IdxOnlyTC (TableName, ConstraintName)
        SELECT CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
          AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexesToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_IndexesToDrop (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IsUnique TINYINT DEFAULT 0,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Create helper temp table to copy index definitions
        -- MySQL cannot reference the same temporary table multiple times in a query
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DefinedIndexes;
        CREATE TEMPORARY TABLE _SchemaSmith_DefinedIndexes (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Populate with table.index pairs from the definition
        INSERT INTO _SchemaSmith_DefinedIndexes (TableName, IndexName)
        SELECT CONVERT(SchemaSmith_StripBacktickWrapping(i.TableName) USING utf8mb4),
               CONVERT(SchemaSmith_StripBacktickWrapping(i.IndexName) USING utf8mb4)
        FROM _SchemaSmith_Indexes i;

        -- AXIS 1 — removed-from-product: indexes OWNED by this product but no longer in the
        -- definition. Gated by p_DropIndexesRemovedFromProduct (+ per-table tightening), NOT by
        -- p_DropUnknownIndexes (Index-B decouple, matching SQL Server / PostgreSQL).
        -- IMPORTANT: Only consider indexes on tables that ARE in the current definition (_SchemaSmith_Tables).
        -- Reads the _SchemaSmith_IdxOnlyIdx snapshot (existence == ei.IndexName IS NOT NULL,
        -- IsUnique == ei.NonUnique = 0) instead of live INFORMATION_SCHEMA.
        IF p_DropIndexesRemovedFromProduct = 1 THEN
            INSERT INTO _SchemaSmith_IndexesToDrop (TableName, IndexName, IsUnique)
            SELECT DISTINCT
                SUBSTRING_INDEX(po.ObjectName, '.', 1) AS TableName,
                SUBSTRING_INDEX(po.ObjectName, '.', -1) AS IndexName,
                COALESCE(ei.NonUnique = 0, 0) AS IsUnique
            FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_Tables t
                ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            LEFT JOIN _SchemaSmith_DefinedIndexes di
                ON CONVERT(di.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
            LEFT JOIN _SchemaSmith_IdxOnlyIdx ei
                ON CONVERT(ei.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(ei.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = 'INDEX' COLLATE utf8mb4_unicode_ci
              -- Per-table tightening (the env/product gate is the enclosing IF)
              AND COALESCE(t.DropIndexesRemovedFromProduct, 1) = 1
              -- Never drop PRIMARY KEY
              AND UPPER(SUBSTRING_INDEX(po.ObjectName, '.', -1) COLLATE utf8mb4_unicode_ci) != 'PRIMARY' COLLATE utf8mb4_unicode_ci
              -- Not in current definition (LEFT JOIN produces NULL when not found)
              AND di.IndexName IS NULL
              -- Not a renamed index
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_IndexRenames r
                  WHERE r.TableName = SUBSTRING_INDEX(po.ObjectName, '.', 1)
                    AND r.OldIndexName = SUBSTRING_INDEX(po.ObjectName, '.', -1)
              )
              -- Verify index actually exists (snapshot bridge)
              AND ei.IndexName IS NOT NULL;
        END IF;

        -- AXIS 2 — out-of-band: an index in the catalog on a current-quench table that is NOT in the
        -- definition AND NOT owned by this product. Gated by p_DropUnknownIndexes. Crash-safe: reads
        -- the snapshot + temp/real tables only. INSERT IGNORE dedupes against any removed-from-product
        -- row already queued (shared PK on _SchemaSmith_IndexesToDrop).
        IF p_DropUnknownIndexes = 1 THEN
            INSERT IGNORE INTO _SchemaSmith_IndexesToDrop (TableName, IndexName, IsUnique)
            SELECT CONVERT(ei.TableName USING utf8mb4), CONVERT(ei.IndexName USING utf8mb4), COALESCE(ei.NonUnique = 0, 0)
            FROM _SchemaSmith_IdxOnlyIdx ei
            INNER JOIN _SchemaSmith_Tables t
                ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
            WHERE UPPER(ei.IndexName COLLATE utf8mb4_unicode_ci) != 'PRIMARY' COLLATE utf8mb4_unicode_ci
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_DefinedIndexes di
                  WHERE CONVERT(di.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                    AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM SchemaSmith_ProductOwnership po
                  WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                    AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                    AND po.ObjectType COLLATE utf8mb4_unicode_ci = 'INDEX' COLLATE utf8mb4_unicode_ci
                    AND po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(ei.TableName, '.', ei.IndexName) COLLATE utf8mb4_unicode_ci
              )
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_IndexRenames r
                  WHERE CONVERT(r.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                    AND CONVERT(r.OldIndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
              );
        END IF;

        -- Cleanup helper temp table
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DefinedIndexes;

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown indexes');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
            FROM _SchemaSmith_IndexesToDrop;
        ELSE
            -- First, drop any foreign keys that reference unique indexes we're about to drop.
            -- Snapshot the referencing FKs into a temp table (grouped by table) so the drop-FK
            -- and drop-index phases each fold into one ALTER per table instead of per-row PREPAREs.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsForIndexDrop;
            CREATE TEMPORARY TABLE _SchemaSmith_FKsForIndexDrop (
                TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
                ConstraintName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
                PRIMARY KEY (TableName, ConstraintName)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            -- DISTINCT: a composite FK produces one KEY_COLUMN_USAGE row per column, which would
            -- otherwise duplicate the (table, constraint) pair and fold into an invalid
            -- "DROP FOREIGN KEY x, DROP FOREIGN KEY x" clause.
            -- Reads the STEP 3 snapshots (IdxOnlyKCU / IdxOnlyTC), not live INFORMATION_SCHEMA.
            -- itd.IsUnique already carries the unique-index flag, so no STATISTICS re-check is needed.
            INSERT INTO _SchemaSmith_FKsForIndexDrop (TableName, ConstraintName)
            SELECT DISTINCT
                CONVERT(kcu.TableName USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                CONVERT(tc.ConstraintName USING utf8mb4) COLLATE utf8mb4_unicode_ci
            FROM _SchemaSmith_IndexesToDrop itd
            JOIN _SchemaSmith_IdxOnlyKCU kcu
                ON CONVERT(kcu.ReferencedTableName USING utf8mb4) = CONVERT(itd.TableName USING utf8mb4)
            JOIN _SchemaSmith_IdxOnlyTC tc
                ON CONVERT(tc.TableName USING utf8mb4) = CONVERT(kcu.TableName USING utf8mb4)
                AND CONVERT(tc.ConstraintName USING utf8mb4) = CONVERT(kcu.ConstraintName USING utf8mb4)
            WHERE itd.IsUnique = 1;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop FK for index: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                          '` DROP FOREIGN KEY `', ConstraintName, '`')
            FROM _SchemaSmith_FKsForIndexDrop;

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKForIndexDropStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_FKForIndexDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_FKForIndexDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
            FROM _SchemaSmith_FKsForIndexDrop
            GROUP BY TableName;

            SET @v_fkforindex_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKForIndexDropStmts);
            WHILE @v_fkforindex_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKForIndexDropStmts WHERE RowId = @v_fkforindex_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_fkforindex_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKForIndexDropStmts WHERE RowId > @v_fkforindex_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKForIndexDropStmts;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsForIndexDrop;

            -- Now drop the unknown indexes
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown indexes');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop unknown index: ', TableName, '.', IndexName)
            FROM _SchemaSmith_IndexesToDrop;

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexDropStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_IndexDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_IndexDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP INDEX `', IndexName, '`') ORDER BY IndexName SEPARATOR ', '))
            FROM _SchemaSmith_IndexesToDrop
            GROUP BY TableName;

            SET @v_indexdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexDropStmts);
            WHILE @v_indexdrop_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_IndexDropStmts WHERE RowId = @v_indexdrop_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_indexdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexDropStmts WHERE RowId > @v_indexdrop_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexDropStmts;

            -- Remove dropped indexes from ProductOwnership
            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_IndexesToDrop itd
                ON po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(itd.TableName, '.', itd.IndexName) COLLATE utf8mb4_unicode_ci
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = 'INDEX' COLLATE utf8mb4_unicode_ci;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexesToDrop;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyIdx;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyKCU;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxOnlyTC;
    END IF;

    -- =========================================================================
    -- STEP 4: Create missing indexes (non-primary)
    -- =========================================================================
    -- Post-drop index-existence snapshot: after STEP 2's modified-index drops and STEP 3's unknown-index
    -- drops, so a just-dropped index is seen as MISSING here and recreated (WhatIf: catalog unchanged).
    CALL SchemaSmith_SnapshotIndexExistence(p_DatabaseName);
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
                           ELSE '' END,
                      CASE WHEN i.IsVisible = 0 AND SchemaSmith_SupportsInvisibleIndex() = 1 THEN SchemaSmith_IndexInvisibleClause() ELSE '' END)
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE BINARY r.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY r.NewIndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExist s
              WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          -- A declared functional index below the floor (see the STEP 0.6 degrade guard above) is
          -- never created -- it is a hard syntax error, not a clause that can be suppressed.
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0);
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing indexes');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Create index: ', i.TableName, '.', i.IndexName,
            CASE WHEN COALESCE(i.VariantName, '') <> '' THEN CONCAT(' (variant: ', i.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE BINARY r.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY r.NewIndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExist s
              WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0);

        -- Fold each table's missing-index creates into one multi-clause ALTER, materialize, execute.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexCreateStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_IndexCreateStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IndexCreateStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', i.TableName, ' ',
                      GROUP_CONCAT(
                          CONCAT(
                              'ADD ',
                              CASE WHEN UPPER(i.IndexType) = 'SPATIAL' THEN 'SPATIAL INDEX '
                                   WHEN i.IsUnique = 1 AND i.IsPrimaryKey = 0 THEN 'UNIQUE INDEX '
                                   ELSE 'INDEX ' END,
                              i.IndexName,
                              ' (', i.IndexColumns, ')',
                              CASE WHEN UPPER(i.IndexType) = 'HASH' THEN ' USING HASH'
                                   WHEN UPPER(i.IndexType) = 'BTREE' THEN ' USING BTREE'
                                   ELSE '' END,
                              CASE WHEN i.Comment IS NOT NULL AND i.Comment != ''
                                   THEN CONCAT(' COMMENT ''', REPLACE(i.Comment, '''', ''''''), '''')
                                   ELSE '' END,
                              CASE WHEN i.IsVisible = 0 AND SchemaSmith_SupportsInvisibleIndex() = 1 THEN SchemaSmith_IndexInvisibleClause() ELSE '' END)
                          ORDER BY i.IndexName SEPARATOR ', '))
        FROM _SchemaSmith_Indexes i
        WHERE i.IsPrimaryKey = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE BINARY r.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY r.NewIndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IdxExist s
              WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
                AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
          )
          AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0)
        GROUP BY i.TableName;

        SET @v_indexcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexCreateStmts);
        WHILE @v_indexcreate_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_IndexCreateStmts WHERE RowId = @v_indexcreate_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_indexcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_IndexCreateStmts WHERE RowId > @v_indexcreate_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexCreateStmts;
    END IF;

    -- =========================================================================
    -- STEP 5: Update ProductOwnership for managed indexes
    -- =========================================================================
    IF p_WhatIf = 0 THEN
        -- Post-create index-existence snapshot: ownership is written only for indexes that now exist
        -- (STEP 4 created the missing ones), so rebuild the snapshot to the post-create state here.
        CALL SchemaSmith_SnapshotIndexExistence(p_DatabaseName);
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName)
        SELECT p_ProductName, '', p_DatabaseName, 'INDEX',
               CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
        FROM _SchemaSmith_Indexes i
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(i.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(i.IndexName)
        );
    END IF;

    -- =========================================================================
    -- STEP 6: Handle fulltext indexes
    -- =========================================================================
    -- Drop fulltext indexes that no longer exist in definition
    -- Candidate detection runs under WhatIf too: a WhatIf run must report a fulltext drop it would
    -- make, and record the wouldDrop audit row, the same as every other object type.
    IF p_DropUnknownIndexes = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown fulltext indexes');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTIndexesToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_FTIndexesToDrop (
            TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            IndexName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_FTIndexesToDrop (TableName, IndexName)
        SELECT
            CONVERT(s.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
            CONVERT(s.INDEX_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.INDEX_TYPE = 'FULLTEXT'
          AND s.SEQ_IN_INDEX = 1
          AND EXISTS (
              SELECT 1 FROM SchemaSmith_ProductOwnership po
              WHERE BINARY po.ProductName = BINARY p_ProductName
                AND BINARY po.ObjectSchema = BINARY p_DatabaseName
                AND po.ObjectType = 'INDEX'
                AND BINARY po.ObjectName = BINARY CONCAT(CONVERT(s.TABLE_NAME USING utf8mb4), '.', CONVERT(s.INDEX_NAME USING utf8mb4))
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_FullTextIndexes ft
              WHERE BINARY SchemaSmith_StripBacktickWrapping(ft.TableName) = BINARY s.TABLE_NAME
                AND BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName) = BINARY s.INDEX_NAME
          );

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop unknown fulltext index: DROP INDEX `', IndexName,
                      '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
        FROM _SchemaSmith_FTIndexesToDrop;

        IF p_WhatIf = 1 THEN
            -- #363 pattern: an audit insert is not DDL, so it is safe inside the WhatIf branch.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'fullTextIndex', CONCAT(TableName, '.', IndexName), 'wouldDrop'
            FROM _SchemaSmith_FTIndexesToDrop;
        ELSE
            -- Fold each table's fulltext-index drops into one multi-clause ALTER, materialize, execute.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTDropStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_FTDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_FTDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP INDEX `', IndexName, '`') ORDER BY IndexName SEPARATOR ', '))
            FROM _SchemaSmith_FTIndexesToDrop
            GROUP BY TableName;

            SET @v_ftdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTDropStmts);
            WHILE @v_ftdrop_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FTDropStmts WHERE RowId = @v_ftdrop_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_ftdrop_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTDropStmts WHERE RowId > @v_ftdrop_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTDropStmts;

            -- Object-change audit (#243 E5). Set-based after the loop: the drops are folded per table
            -- into one multi-clause ALTER, so there is no per-index EXECUTE to hang an insert off.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'fullTextIndex', CONCAT(TableName, '.', IndexName), 'dropped'
            FROM _SchemaSmith_FTIndexesToDrop;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTIndexesToDrop;
    END IF;

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
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(ft.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName)
              AND s.IndexType = 'FULLTEXT'
        );

        -- #363: WhatIf twin of the ELSE-branch 'fullTextIndex'/'created' audit; same predicate.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'fullTextIndex',
               CONCAT(SchemaSmith_StripBacktickWrapping(ft.TableName), '.', SchemaSmith_StripBacktickWrapping(ft.IndexName)), 'wouldCreate'
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(ft.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName)
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
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(ft.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName)
              AND s.IndexType = 'FULLTEXT'
        );

        -- NOTE: InnoDB only supports adding one FULLTEXT index per ALTER TABLE statement, so unlike
        -- the other loops in this file these do NOT fold multiple rows into one multi-clause ALTER —
        -- each row materializes as its own standalone ALTER TABLE ... ADD FULLTEXT INDEX statement.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FTCreateStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FTCreateStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT, AuditName VARCHAR(257))
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FTCreateStmts (Stmt, AuditName)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', ft.TableName,
                      ' ADD FULLTEXT INDEX ', ft.IndexName,
                      ' (', ft.Columns, ')',
                      CASE WHEN ft.Comment IS NOT NULL AND ft.Comment != ''
                           THEN CONCAT(' COMMENT ''', REPLACE(ft.Comment, '''', ''''''), '''')
                           ELSE '' END,
                      CASE WHEN ft.Parser IS NOT NULL AND ft.Parser != ''
                           THEN CONCAT(' WITH PARSER ', ft.Parser)
                           ELSE '' END),
               CONCAT(SchemaSmith_StripBacktickWrapping(ft.TableName), '.', SchemaSmith_StripBacktickWrapping(ft.IndexName))
        FROM _SchemaSmith_FullTextIndexes ft
        WHERE NOT EXISTS (
            SELECT 1 FROM _SchemaSmith_IdxExist s
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(ft.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName)
              AND s.IndexType = 'FULLTEXT'
        )
        ORDER BY ft.RowId;

        SET @v_ftcreate_id := (SELECT MIN(RowId) FROM _SchemaSmith_FTCreateStmts);
        WHILE @v_ftcreate_id IS NOT NULL DO
            SELECT Stmt, AuditName INTO @exec_sql, @ss_auditname FROM _SchemaSmith_FTCreateStmts WHERE RowId = @v_ftcreate_id;
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
            WHERE BINARY s.TableName = BINARY SchemaSmith_StripBacktickWrapping(ft.TableName)
              AND BINARY s.IndexName = BINARY SchemaSmith_StripBacktickWrapping(ft.IndexName)
        );
    END IF;

    -- Cleanup temporary tables
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexRenames;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifiedIndexes;

END//

DELIMITER ;
