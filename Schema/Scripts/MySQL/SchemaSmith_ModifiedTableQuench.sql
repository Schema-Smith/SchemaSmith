-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

-- Index rename detection, modified-index handling and index REMOVAL (DropUnknownIndexes /
-- DropIndexesRemovedFromProduct) ALL live HERE on MySQL/MariaDB, matching SQL Server.
-- MissingIndexesAndConstraintsQuench is add-only where indexes are concerned.
-- Do not infer an engine's capability from which procedure carries a flag: parameter placement is a
-- division of labour between procedures, not a statement about what the engine supports. That inference
-- has been drawn wrong in both directions.
DROP PROCEDURE IF EXISTS SchemaSmith_ModifiedTableQuench//

CREATE PROCEDURE SchemaSmith_ModifiedTableQuench(
    IN p_ProductName VARCHAR(100),
    IN p_DatabaseName VARCHAR(128),
    IN p_WhatIf TINYINT,
    IN p_DropTablesRemovedFromProduct TINYINT,
    IN p_DropColumnsRemovedFromProduct TINYINT,
    IN p_DropCheckConstraintsRemovedFromProduct TINYINT,
    IN p_DropExcludeConstraintsRemovedFromProduct TINYINT,
    IN p_DropStatisticsRemovedFromProduct TINYINT,
    IN p_CaptureWouldDrop TINYINT,
    IN p_DropUnknownIndexes TINYINT,
    IN p_DropIndexesRemovedFromProduct TINYINT
)
SQL SECURITY DEFINER
BEGIN
    -- This procedure modifies existing tables to match the JSON definitions.
    -- It reads from the _SchemaSmith_Tables and _SchemaSmith_Columns temp tables.
    --
    -- Execution order:
    -- 0. Ownership validation (error if table owned by another product)
    -- 1. Table renames (when OldName is set and old table exists)
    -- 2. Column renames (when OldName is set and old column exists)
    -- 3. Column modifications (type, nullable, etc.)
    -- 4. Engine changes
    -- 5. Collation changes
    -- 5.5. Comment changes (table-level)
    -- 6. Row format changes
    -- 7. Auto-increment seed changes
    -- 8. ProductOwnership updates
    -- 9. Drop tables removed from product (if enabled)

    DECLARE v_ConflictingTable VARCHAR(128);
    DECLARE v_ConflictingOwner VARCHAR(100);

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'BEGIN ModifiedTableQuench');

    -- Folded multi-clause ALTER/RENAME statements below can GROUP_CONCAT many clauses for a
    -- wide table; raise the session limit so long clause lists aren't silently truncated.
    SET SESSION group_concat_max_len = 1000000;

    -- =========================================================================
    -- Degrade column DEFAULT expressions below MySQL 8.0.13 for EXISTING columns (new columns are
    -- gated in MissingTableAndColumnQuench, which runs earlier in the same deploy). MariaDB has
    -- supported expression defaults since 10.2.1 (MDEV-10134), at/below our 10.2 floor, so this branch
    -- is MySQL-only. Below the threshold an existing plain-default column cannot legally acquire an
    -- expression default -- a hard syntax error, not parsed-and-ignored -- so there is no safe partial
    -- MODIFY: 'fail' aborts naming the column(s); 'warn' (default) skips the column's modification
    -- entirely -- STEP 3 below excludes it via the identical predicate, leaving its current default in
    -- place -- and records a 'downgraded' manifest row per column.
    -- =========================================================================
    IF SchemaSmith_SupportsDefaultExpression() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE t.NewTable = 0 AND c.NewColumn = 0
                     AND c.IsAutoIncrement = 0
                     AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
                     AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%') THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column DEFAULT expression unsupported (requires MySQL 8.0.13): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
            SET @ss_msg = CONCAT('Column DEFAULT expressions require MySQL 8.0.13 (detected ',
                                 SchemaSmith_ServerVersionNum(), '); see the deploy log for the unsupported column(s).');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Skipping column modification (DEFAULT expression requires MySQL 8.0.13 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (DEFAULT expression, MySQL 8.0.13)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade invisible columns below MySQL 8.0.23 / MariaDB 10.3 for EXISTING columns (new columns are
    -- gated in MissingTableAndColumnQuench, which runs earlier in the same deploy). Below the threshold
    -- the INVISIBLE keyword is a hard syntax error, so ColumnScript never emits it there -- the MODIFY
    -- COLUMN emitted below (STEP 3) is still syntactically valid, it just leaves the column visible -- and
    -- STEP 3's predicate ignores the visibility difference so the deploy stays idempotent. This block only
    -- adds the user-facing report: 'fail' aborts naming the column(s); 'warn' (default) records a
    -- 'downgraded' manifest row per column.
    -- =========================================================================
    IF SchemaSmith_SupportsInvisibleColumn() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE t.NewTable = 0 AND c.NewColumn = 0
                     AND c.IsInvisible = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible column requires MySQL 8.0.23 / MariaDB 10.3 (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsInvisible = 1;
            SET @ss_msg = 'Invisible column requires MySQL 8.0.23 / MariaDB 10.3 (UnsupportedFeaturePolicy=fail). See the run log for the full list.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible column stored visible (requires MySQL 8.0.23 / MariaDB 10.3 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsInvisible = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (invisible, MySQL 8.0.23 / MariaDB 10.3)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.IsInvisible = 1;
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade column SRID restriction below MySQL 8.0.3 for EXISTING columns (new columns are gated in
    -- MissingTableAndColumnQuench, which runs earlier in the same deploy). MariaDB has no equivalent
    -- attribute at any version, so SchemaSmith_SupportsColumnSrid() is 0 there unconditionally -- this
    -- block fires for MariaDB the same way it fires for a genuinely old MySQL. Below the threshold the
    -- SRID keyword is a hard syntax error, so ColumnScript never emits it there -- the MODIFY COLUMN
    -- emitted below (STEP 3) is still syntactically valid, it just leaves the column unrestricted -- and
    -- STEP 3's predicate ignores the SRID difference so the deploy stays idempotent. This block only
    -- adds the user-facing report: 'fail' aborts naming the column(s); 'warn' (default) records a
    -- 'downgraded' manifest row per column.
    -- =========================================================================
    IF SchemaSmith_SupportsColumnSrid() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE t.NewTable = 0 AND c.NewColumn = 0
                     AND c.Srid IS NOT NULL) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column SRID requires MySQL 8.0.3 (MariaDB unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.Srid IS NOT NULL;
            SET @ss_msg = 'Column SRID requires MySQL 8.0.3 (MariaDB unsupported) (UnsupportedFeaturePolicy=fail). See the run log for the full list.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column SRID stored unrestricted (requires MySQL 8.0.3, MariaDB unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.Srid IS NOT NULL;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (SRID, MySQL 8.0.3)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE t.NewTable = 0 AND c.NewColumn = 0
              AND c.Srid IS NOT NULL;
        END IF;
    END IF;

    -- =======================
    -- STEP -0.5: PARTITIONING -- ADOPT AND VERIFY
    -- =======================
    -- Runs BEFORE any DDL, in live and WhatIf alike, because every disagreement it can find describes a
    -- statement that rewrites EVERY ROW of the table. ALTER TABLE ... PARTITION BY is a full table rebuild,
    -- and a state-based diff cannot derive the SPLIT/MERGE intent behind a changed boundary -- it can only
    -- see that two layouts differ -- so a mismatch is reported and refused, never applied.
    --
    -- An UNSET Partitioning means "SchemaSmith does not manage partitioning here" -- it is NOT a
    -- declaration that the table is unpartitioned. A package that never mentions it must keep deploying
    -- against a table a DBA partitioned by hand, which is every package in the wild today. Only a DECLARED
    -- method is compared.
    --
    -- THE EXPRESSION COMPARISON IS NORMALIZED, and the floor is why: MySQL 5.7 returns the text the user
    -- wrote while MySQL 8, MariaDB 10.2 and MariaDB 11.4 all return a rewritten form (year(`dt`)). A
    -- literal compare would refuse a package extracted on 5.7 and deployed on 8 -- a false alarm on an
    -- identical layout. See SchemaSmith_NormalizePartitionExpression.
    --
    -- Offending tables are logged individually first: MySQL caps SIGNAL MESSAGE_TEXT at 128 characters, so
    -- the detail goes to the run log and the signal stays short -- the same shape as the drop-by-absence
    -- guard in STEP 8.
    --
    -- GUARDED so the INFORMATION_SCHEMA.PARTITIONS scan runs ONLY when a declared table actually asks for
    -- partitioning. That scan opens metadata for every table in the schema on MariaDB, and the outer
    -- predicate (t.PartitionMethod IS NOT NULL) would prune the RESULT but not stop the eager catalog read.
    -- Every package in the wild declares no partitioning, so without this guard the feature adds a
    -- whole-schema metadata scan to every deploy for nothing -- real per-deploy load under concurrent runs.
    IF EXISTS (SELECT 1 FROM _SchemaSmith_Tables WHERE NewTable = 0 AND PartitionMethod IS NOT NULL) THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(),
               CONCAT('  Declared partitioning does not match the deployed table (refused -- repartitioning rewrites every row): ',
                      SchemaSmith_StripBacktickWrapping(t.TableName),
                      ' declares ', t.PartitionMethod, '(', t.PartitionExpression, ')',
                      ', deployed ', COALESCE(CONCAT(lp.PARTITION_METHOD, '(', lp.PARTITION_EXPRESSION, ')'), 'unpartitioned'))
          FROM _SchemaSmith_Tables t
          LEFT JOIN (SELECT p.TABLE_NAME, p.PARTITION_METHOD, p.PARTITION_EXPRESSION
                       FROM INFORMATION_SCHEMA.PARTITIONS p
                      WHERE CONVERT(p.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                        AND p.PARTITION_NAME IS NOT NULL
                        AND p.PARTITION_ORDINAL_POSITION = 1) lp
            ON CONVERT(lp.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
         WHERE t.NewTable = 0
           AND t.PartitionMethod IS NOT NULL
           AND (lp.PARTITION_METHOD IS NULL
                OR UPPER(lp.PARTITION_METHOD) <> t.PartitionMethod
                OR SchemaSmith_NormalizePartitionExpression(lp.PARTITION_EXPRESSION) <> SchemaSmith_NormalizePartitionExpression(t.PartitionExpression)
                -- Partition COUNT change (HASH/KEY declare a count, not named partitions): re-partitioning
                -- from N to M buckets rewrites every row, so it is refused like a method/expression change.
                -- Gated on a declared count so RANGE/LIST (named partitions, no declared count) is untouched.
                OR (t.PartitionCount IS NOT NULL AND t.PartitionCount > 0
                    AND (SELECT COUNT(*) FROM INFORMATION_SCHEMA.PARTITIONS pc
                          WHERE CONVERT(pc.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                            AND CONVERT(pc.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
                            AND pc.PARTITION_NAME IS NOT NULL) <> t.PartitionCount));

        IF ROW_COUNT() > 0 THEN
            SET @ss_msg = 'Declared partitioning does not match the deployed table -- see the run log. Repartitioning rewrites every row and is refused.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- =======================
    -- STEP -0.4: TABLESPACE -- REFUSE A MOVE (MySQL only, F2b)
    -- =======================
    -- Placement, exactly like partitioning (STEP -0.5 above), SQL Server FileGroup and PostgreSQL
    -- Tablespace: applied at CREATE only, never migrated by a state diff. ALTER TABLE ... TABLESPACE
    -- relocates the table's whole data file, and a state-based diff cannot tell "meant to move it" apart
    -- from "stale/typo'd package" -- so a mismatch is reported and refused, never applied.
    --
    -- Read through the per-engine SchemaSmith_TableTablespace PROCEDURE (not a function -- see that
    -- script for why: its MySQL body reaches INFORMATION_SCHEMA.INNODB_TABLES/INNODB_TABLESPACES only
    -- through dynamic SQL, which MySQL disallows inside a stored FUNCTION, so it takes its schema/table
    -- and an OUT param instead of returning a value). Being a procedure, it cannot be called inline inside
    -- a SELECT/subquery the way STEP -0.5's partitioning check reads INFORMATION_SCHEMA.PARTITIONS
    -- directly -- so this step CALLs it once per candidate table in a cursor loop rather than a single
    -- set-based comparison.
    --
    -- VERSION() NOT LIKE '%MariaDB%' guards the whole block, matching the CREATE-time emit gate in
    -- MissingTableAndColumnQuench: MariaDB has no general tablespaces, so SchemaSmith_TableTablespace's
    -- MariaDb override always sets its OUT param NULL. Without this guard, a package that carries a
    -- MySQL-authored Tablespace value into a shared/MariaDB deploy (harmless everywhere else -- the emit
    -- gate above already suppresses it) would compare that declared value against an always-NULL deployed
    -- read and FALSE-REFUSE every redeploy on MariaDB, forever, for a property MariaDB can never satisfy.
    --
    -- An UNSET declared Tablespace (NULL/'') means "SchemaSmith does not manage this table's tablespace
    -- placement" -- not a declaration that the table has none -- so only a DECLARED, non-empty value is
    -- compared; matching (or both unset) is a no-op. Guarded by the outer EXISTS so the cursor loop (and
    -- its per-table CALL) only runs at all when some table actually declares a tablespace.
    --
    -- Fires REGARDLESS of p_WhatIf, ahead of the p_WhatIf branch below -- mirroring STEP -0.5 above and
    -- STEP 7.5's DROP SYSTEM VERSIONING refuse further down: there is no safe "preview" of a refusal.
    IF EXISTS (SELECT 1 FROM _SchemaSmith_Tables
               WHERE NewTable = 0 AND Tablespace IS NOT NULL AND Tablespace != '' AND VERSION() NOT LIKE '%MariaDB%') THEN
        -- Reset explicitly: this session variable is only ASSIGNED below when a mismatch is found, so on
        -- a pooled connection a stale value from an earlier, unrelated call would otherwise survive a
        -- clean pass and fire a false SIGNAL after the loop.
        SET @ss_tablespace_refuse_table = NULL;

        BEGIN
            DECLARE v_TtsDone INT DEFAULT FALSE;
            DECLARE v_TtsTableName VARCHAR(128);
            DECLARE v_TtsDeclared VARCHAR(64);
            DECLARE v_TtsDeployed VARCHAR(64);
            DECLARE cur_TablespaceCandidates CURSOR FOR
                SELECT t.TableName, t.Tablespace
                FROM _SchemaSmith_Tables t
                WHERE t.NewTable = 0
                  AND t.Tablespace IS NOT NULL AND t.Tablespace != ''
                  AND VERSION() NOT LIKE '%MariaDB%';

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_TtsDone = TRUE;

            OPEN cur_TablespaceCandidates;

            tablespace_refuse_loop: LOOP
                FETCH cur_TablespaceCandidates INTO v_TtsTableName, v_TtsDeclared;
                IF v_TtsDone THEN
                    LEAVE tablespace_refuse_loop;
                END IF;

                SET v_TtsDeployed = NULL;
                CALL SchemaSmith_TableTablespace(p_DatabaseName, SchemaSmith_StripBacktickWrapping(v_TtsTableName), v_TtsDeployed);

                IF COALESCE(v_TtsDeployed, '') <> v_TtsDeclared THEN
                    -- Log every offending table (not just the one named in the SIGNAL below), same shape
                    -- as the partitioning guard above.
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
                        CONCAT('  Declared tablespace does not match the deployed table (refused -- SchemaSmith will not move a table between tablespaces): ',
                               SchemaSmith_StripBacktickWrapping(v_TtsTableName),
                               ' declares ', v_TtsDeclared,
                               ', deployed ', COALESCE(v_TtsDeployed, '(none)')));

                    -- SIGNAL MESSAGE_TEXT is capped at 128 characters (see STEP 7.5 and STEP 8's comments
                    -- on the same limit) -- name only the FIRST offender here, truncated defensively; the
                    -- run log above carries every offender and the full declared/deployed detail.
                    IF @ss_tablespace_refuse_table IS NULL THEN
                        SET @ss_tablespace_refuse_table = LEFT(SchemaSmith_StripBacktickWrapping(v_TtsTableName), 40);
                    END IF;
                END IF;
            END LOOP;

            CLOSE cur_TablespaceCandidates;
        END;

        IF @ss_tablespace_refuse_table IS NOT NULL THEN
            SET @ss_msg = CONCAT('Table ', @ss_tablespace_refuse_table, ': declared tablespace differs from deployed (refused) -- use a migration.');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- =======================
    -- STEP -0.35: DATA DIRECTORY -- REFUSE A MOVE (both engines, F2c)
    -- =======================
    -- Placement, same posture as STEP -0.4's Tablespace immediately above (and partitioning at STEP -0.5,
    -- SQL Server FileGroup, PostgreSQL Tablespace): applied at CREATE only, never migrated by a state diff.
    -- DATA DIRECTORY names the physical location of the table's data file, and a state-based diff cannot
    -- tell "meant to move it" apart from "stale/typo'd package" -- so a mismatch is reported and refused,
    -- never applied.
    --
    -- Read through the per-engine SchemaSmith_TableDataDirectory PROCEDURE -- same OUT-param shape as
    -- SchemaSmith_TableTablespace and for the same reason on the MySQL side (dynamic SQL cannot live in a
    -- FUNCTION); it cannot be called inline inside a SELECT/subquery, so this step CALLs it once per
    -- candidate table in a cursor loop rather than a single set-based comparison.
    --
    -- UNLIKE STEP -0.4, no VERSION() NOT LIKE '%MariaDB%' guard: DATA DIRECTORY is a real InnoDB clause on
    -- BOTH engines (MariaDB has no general tablespaces at all, but it does support DATA DIRECTORY), and
    -- SchemaSmith_TableDataDirectory has a real MariaDb body -- not an always-NULL override -- so the same
    -- false-refuse trap STEP -0.4's guard exists to avoid does not apply here.
    --
    -- An UNSET declared DataDirectory (NULL/'') means "SchemaSmith does not manage this table's data-file
    -- placement" -- not a declaration that the table has none -- so only a DECLARED, non-empty value is
    -- compared; matching (or both unset) is a no-op. Guarded by the outer EXISTS so the cursor loop (and
    -- its per-table CALL) only runs at all when some table actually declares a directory.
    --
    -- Fires REGARDLESS of p_WhatIf, ahead of the p_WhatIf branch below -- same reasoning as STEP -0.4: there
    -- is no safe "preview" of a refusal, and this pre-check runs before any CREATE/ALTER is attempted, so a
    -- refused redeploy never touches the table.
    IF EXISTS (SELECT 1 FROM _SchemaSmith_Tables
               WHERE NewTable = 0 AND DataDirectory IS NOT NULL AND DataDirectory != '') THEN
        -- Reset explicitly: this session variable is only ASSIGNED below when a mismatch is found, so on
        -- a pooled connection a stale value from an earlier, unrelated call would otherwise survive a
        -- clean pass and fire a false SIGNAL after the loop.
        SET @ss_datadirectory_refuse_table = NULL;

        BEGIN
            DECLARE v_TddDone INT DEFAULT FALSE;
            DECLARE v_TddTableName VARCHAR(128);
            DECLARE v_TddDeclared VARCHAR(512);
            DECLARE v_TddDeployed VARCHAR(512);
            DECLARE cur_DataDirectoryCandidates CURSOR FOR
                SELECT t.TableName, t.DataDirectory
                FROM _SchemaSmith_Tables t
                WHERE t.NewTable = 0
                  AND t.DataDirectory IS NOT NULL AND t.DataDirectory != '';

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_TddDone = TRUE;

            OPEN cur_DataDirectoryCandidates;

            datadirectory_refuse_loop: LOOP
                FETCH cur_DataDirectoryCandidates INTO v_TddTableName, v_TddDeclared;
                IF v_TddDone THEN
                    LEAVE datadirectory_refuse_loop;
                END IF;

                SET v_TddDeployed = NULL;
                CALL SchemaSmith_TableDataDirectory(p_DatabaseName, SchemaSmith_StripBacktickWrapping(v_TddTableName), v_TddDeployed);

                IF COALESCE(v_TddDeployed, '') <> v_TddDeclared THEN
                    -- Log every offending table (not just the one named in the SIGNAL below), same shape
                    -- as the tablespace guard above.
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
                        CONCAT('  Declared data directory does not match the deployed table (refused -- SchemaSmith will not move a table between data directories): ',
                               SchemaSmith_StripBacktickWrapping(v_TddTableName),
                               ' declares ', v_TddDeclared,
                               ', deployed ', COALESCE(v_TddDeployed, '(none)')));

                    -- SIGNAL MESSAGE_TEXT is capped at 128 characters (see STEP -0.4, STEP 7.5 and STEP 8's
                    -- comments on the same limit) -- name only the FIRST offender here, truncated
                    -- defensively; the run log above carries every offender and the full declared/deployed
                    -- detail.
                    IF @ss_datadirectory_refuse_table IS NULL THEN
                        SET @ss_datadirectory_refuse_table = LEFT(SchemaSmith_StripBacktickWrapping(v_TddTableName), 40);
                    END IF;
                END IF;
            END LOOP;

            CLOSE cur_DataDirectoryCandidates;
        END;

        IF @ss_datadirectory_refuse_table IS NOT NULL THEN
            SET @ss_msg = CONCAT('Table ', @ss_datadirectory_refuse_table, ': declared data directory differs from deployed (refused) -- use a migration.');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- =======================
    -- STEP 0: OWNERSHIP VALIDATION
    -- =======================
    -- Check if any tables in the definition are owned by a different product
    SELECT po.ObjectName, po.ProductName
    INTO v_ConflictingTable, v_ConflictingOwner
    FROM _SchemaSmith_Tables t
    INNER JOIN SchemaSmith_ProductOwnership po
        ON CONVERT(po.ObjectName USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
        AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
        AND po.ObjectType = 'TABLE'
    WHERE CONVERT(po.ProductName USING utf8mb4) != CONVERT(p_ProductName USING utf8mb4)
    LIMIT 1;

    IF v_ConflictingTable IS NOT NULL THEN
        -- Use user variable for dynamic message (SIGNAL requires constant or user variable)
        SET @schemasmith_error_msg = CONCAT('Table ', v_ConflictingTable, ' is already owned by another product: ', v_ConflictingOwner);
        SIGNAL SQLSTATE '45000'
            SET MESSAGE_TEXT = @schemasmith_error_msg;
    END IF;

    -- Existing-table snapshot: the "does table X exist" checks below (ownership reconcile, ownership
    -- write, the STEP 8 drop-candidate builds, the PreventDrop report) each read INFORMATION_SCHEMA.TABLES
    -- per owned/declared table. INFORMATION_SCHEMA is not a stored table on MySQL/MariaDB, so those per-row
    -- reads re-materialise the server-wide table list every time (cost = tables-processed x tables-on-instance).
    -- Snapshot the schema's table names ONCE here and check against it. Table existence is stable from proc
    -- start through STEP 7 (renames already ran upstream in MissingTableAndColumnQuench; STEPs 3-7 only ALTER,
    -- never create/drop), so this pre-STEP-8 snapshot is what every check before STEP 8 reads. STEP 9's prune
    -- needs the POST-drop state, so the snapshot is REBUILT there. In WhatIf nothing is dropped, so it stays
    -- accurate. _SchemaSmith_ExistingTablesN is a copy: STEP 1's rename reconcile references the snapshot twice
    -- in one statement (new-name EXISTS + old-name NOT EXISTS), which MySQL/MariaDB forbid for a TEMPORARY
    -- table (ER_CANT_REOPEN_TABLE 1137); the second reference reads the copy.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingTables (
        TableName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    -- No TABLE_TYPE filter: the original per-row checks read INFORMATION_SCHEMA.TABLES unfiltered (which
    -- includes views), so the snapshot must too, to stay behaviour-identical.
    INSERT INTO _SchemaSmith_ExistingTables (TableName)
    SELECT CONVERT(ist.TABLE_NAME USING utf8mb4)
    FROM INFORMATION_SCHEMA.TABLES ist
    WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTablesN;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingTablesN (
        TableName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
    INSERT INTO _SchemaSmith_ExistingTablesN (TableName) SELECT TableName FROM _SchemaSmith_ExistingTables;

    -- =======================
    -- STEP 1: RECONCILE OWNERSHIP FOR RENAMED TABLES
    -- =======================
    -- The physical table + column renames now run in MissingTableAndColumnQuench, BEFORE the
    -- add-columns step, so a carried or newly-added column targets the post-rename table name
    -- (parity with SQL Server / PostgreSQL, which rename before adding columns). Only the
    -- ProductOwnership reconciliation stays here, where the product name is in scope: rewrite the
    -- owner row old->new for tables whose rename has taken effect (new name now present in the
    -- catalog, old name gone). Read-only in WhatIf (nothing was renamed).
    -- Note: BINARY comparisons avoid collation clashes between INFORMATION_SCHEMA (utf8mb3),
    -- SchemaSmith functions (utf8mb4_unicode_ci), and connection params (utf8mb4_0900_ai_ci).
    IF p_WhatIf = 0 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TableRenames;
        CREATE TEMPORARY TABLE _SchemaSmith_TableRenames (
            OldTableName VARCHAR(128) NOT NULL,
            NewTableName VARCHAR(128) NOT NULL,
            PRIMARY KEY (OldTableName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Post-rename predicate: the new name now exists and the old name is gone (the rename in
        -- MissingTableAndColumnQuench succeeded), so the owner row still keyed on the old name needs
        -- to move to the new name.
        INSERT INTO _SchemaSmith_TableRenames (OldTableName, NewTableName)
        SELECT
            SchemaSmith_StripBacktickWrapping(t.OldName),
            SchemaSmith_StripBacktickWrapping(t.TableName)
        FROM _SchemaSmith_Tables t
        WHERE t.OldName IS NOT NULL
          AND t.NewTable = 0
          AND EXISTS (
              SELECT 1 FROM _SchemaSmith_ExistingTables ist
              WHERE BINARY ist.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_ExistingTablesN ist
              WHERE BINARY ist.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.OldName)
          );

        -- Update ProductOwnership for the renamed tables (set-based join, one UPDATE for all pairs).
        UPDATE SchemaSmith_ProductOwnership po
        INNER JOIN _SchemaSmith_TableRenames r
            ON po.ObjectName COLLATE utf8mb4_unicode_ci = r.OldTableName COLLATE utf8mb4_unicode_ci
        SET po.ObjectName = r.NewTableName
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'TABLE' COLLATE utf8mb4_unicode_ci;

        -- OldName constraint-rename parity: INDEX ownership rows are keyed as 'tablename.indexname'
        -- (see MissingIndexesAndConstraintsQuench). The TABLE update above only moved TABLE rows, so an
        -- index ownership row still carries the OLD table prefix after the rename. The downstream index
        -- rename/drop passes key on the NEW table name, so they would miss the carried-over index and
        -- leave the old-named index behind as a silent duplicate. Rewrite the table prefix old->new for
        -- INDEX rows on renamed tables so those passes recognise and rename (or drop) it.
        UPDATE SchemaSmith_ProductOwnership po
        INNER JOIN _SchemaSmith_TableRenames r
            ON po.ObjectName COLLATE utf8mb4_unicode_ci LIKE CONCAT(r.OldTableName, '.%') COLLATE utf8mb4_unicode_ci
        SET po.ObjectName = CONCAT(r.NewTableName, SUBSTRING(po.ObjectName FROM CHAR_LENGTH(r.OldTableName) + 1))
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TableRenames;
    END IF;

    -- =======================
    -- STEP 2: COLUMN RENAMES — moved to MissingTableAndColumnQuench
    -- =======================
    -- Declarative column renames now run there (before add-columns), alongside the table rename, so
    -- a renamed column is in place before the add-columns pass and a table+column rename in one
    -- deploy works. Nothing to do here.

    -- =======================
    -- STEP 2.95: THE REBUILD DECISION POINT
    -- =======================
    -- WHY HERE. This is the last point before anything is dismantled: STEP 2.9 immediately below carries
    -- the FIRST DDL this procedure executes, and what it executes is a dependent-object drop (the foreign
    -- keys that block a column-level collation change). Everything above it is bookkeeping that leaves
    -- every table's shape alone -- version-degrade manifest rows and the ownership reconcile for tables
    -- MissingTableAndColumnQuench already renamed. That the renames landed earlier matters:
    -- SchemaSmith_RebuildTable refuses outright while a table or column rename is pending, because the
    -- copy matches columns by their CURRENT name. Everything from STEP 2.9 on is taking apart objects a
    -- rebuild drops wholesale, and a rebuild inheriting a half-dismantled table would have RebuildTable's
    -- pre-rebuild refusals reasoning about a live state that no longer matches the declared file. Column
    -- detection is complete here because this engine has no live-column snapshot at all -- every column
    -- pass reads INFORMATION_SCHEMA.COLUMNS at the moment it runs -- so the two facts the decision needs
    -- are computed here, immediately below, from that same live source.
    --
    -- OPT-IN BY CONSTRUCTION. _SchemaSmith_RebuildElection is filled by a WHERE that only an explicit
    -- ALWAYS/THRESHOLD can satisfy. A package with no RebuildPolicy anywhere resolves to the domain
    -- object's NEVER default at every level, elects nothing, and the drain loop never runs: no rebuild,
    -- no statement, nothing added to the run. A rebuild moves user data, so a table that did not ask for
    -- one must never get one, and that has to be structurally true rather than true because the
    -- conditions happen not to match.
    --
    -- WHOLE-OBJECT RESOLUTION. RebuildPolicySpecified picks WHICH policy applies -- the table's own, or
    -- the resolved upper-tier one -- and then every field comes from that ONE policy. Never a per-field
    -- COALESCE: a table declaring only { "Mode": "ALWAYS" } must not inherit a product's Threshold.
    -- Mirrors ProductQuench.ResolveCascadedPolicy.
    --
    -- THE UPPER TIER ARRIVES IN SESSION VARIABLES, not parameters, following the same decision (and the
    -- same reasoning) as @ss_capture_would_drop in STEP 8: MySQL has no default parameter values, so a
    -- new parameter is a breaking change for every one of this procedure's ~30 direct call sites. An
    -- unset variable reads NULL and COALESCEs to NEVER, which is the safe direction -- a caller that
    -- forgets to set them elects no rebuild rather than an unintended one. DatabaseQuench sets all three
    -- immediately before each call so a pooled connection can never carry a stale policy from a previous
    -- template (see SetMySqlRebuildPolicy).
    --
    -- NO EXPLICIT BYPASS IS NEEDED ON THIS ENGINE, and that is a property of how the passes are written
    -- rather than luck: every column pass below (STEP 3 modify, STEP 3.5 generated-status recreate,
    -- STEP 4 drop) re-reads INFORMATION_SCHEMA.COLUMNS at the moment it runs. A rebuilt table's live
    -- columns already ARE the declared definition, so each of those predicates finds nothing for it.
    -- (SQL Server and PostgreSQL compare against a snapshot taken before this point, so there the
    -- snapshot has to be pruned; here there is no snapshot to go stale.) The one visible difference is
    -- in WhatIf: nothing is actually rebuilt in a preview, so a previewed rebuild is listed alongside
    -- the in-place statements it would have replaced. The preview is honest about the rebuild -- the
    -- 'wouldRebuild' audit row and the full printed sequence are both there -- and no statement runs.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildFacts;
    CREATE TEMPORARY TABLE _SchemaSmith_RebuildFacts (
        TableName VARCHAR(128) NOT NULL PRIMARY KEY,
        ModificationPasses INT NOT NULL DEFAULT 0,
        HasColumnDrop TINYINT NOT NULL DEFAULT 0,
        HasOrderMismatch TINYINT NOT NULL DEFAULT 0
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- THE THRESHOLD COUNT: column-MODIFICATION passes only, which is what a rebuild actually eliminates.
    -- The predicate is the union of the two passes that modify an existing column in place -- STEP 3's
    -- MODIFY COLUMN comparison and STEP 3.5's generated-status DROP+ADD -- expressed as one OR because
    -- STEP 3 carves the generated-status columns OUT and STEP 3.5 takes exactly those. MUST stay in
    -- lockstep with both. Columns that exist only in the package (adds, already applied by
    -- MissingTableAndColumnQuench before this procedure starts) and columns that exist only live
    -- (by-absence drops, metadata-only here) are NOT counted, and neither is index / constraint churn:
    -- counting work a rebuild does not save would fire rebuilds that cost data movement and buy nothing.
    INSERT INTO _SchemaSmith_RebuildFacts (TableName, ModificationPasses)
    SELECT c.TableName, COUNT(*)
    FROM _SchemaSmith_Columns c
    INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
    INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
        ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
        AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
    WHERE t.NewTable = 0
      AND c.NewColumn = 0
      AND (
          -- STEP 3.5's set: the column's generated status is changing (either direction).
          ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
           AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
          OR
          ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
           AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          -- STEP 3's set: any declared attribute differs from the live one.
          OR CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                    OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                  THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                  ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                    != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
          OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
          OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
          OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
              AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                  CASE WHEN c.DefaultValue LIKE '''%'''
                       THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                       ELSE c.DefaultValue END)
              AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                  c.DefaultValue, isc.DATA_TYPE) = 0)
          OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
          OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
          OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
              AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
          OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
          OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
          OR (SchemaSmith_SupportsColumnSrid() = 1
              AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
          OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
          OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
      )
    GROUP BY c.TableName;

    -- ALWAYS fires on ANY detected column change, which includes a column present live but absent from
    -- the package: a rebuild delivers that removal as part of building the replacement from the declared
    -- definition. Recorded separately from the count because it must NOT move the threshold -- a rebuild
    -- saves nothing on a metadata-only DROP COLUMN.
    INSERT INTO _SchemaSmith_RebuildFacts (TableName, HasColumnDrop)
    SELECT DISTINCT t.TableName, 1
    FROM _SchemaSmith_Tables t
    INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
        ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
        AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
    WHERE t.NewTable = 0
      AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                        WHERE c.TableName = t.TableName
                          AND BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName) = BINARY isc.COLUMN_NAME)
    ON DUPLICATE KEY UPDATE HasColumnDrop = 1;

    -- ================================================================================================
    -- COLUMN-ORDER DRIFT, the input to the OnOrderMismatch trigger.
    --
    -- Reordering existing columns is impossible in place on every engine SchemaSmith supports, so a
    -- rebuild is the only mechanism that can converge a table whose columns are in the wrong order.
    --
    -- DECLARED ORDER IS (OrdinalPosition, RowId) -- exactly what SchemaSmith_RebuildTable orders the
    -- shadow's CREATE by. Detection and repair MUST read the declared order off the same key: if this
    -- pass elected on one definition of "declared order" and the rebuild produced another, the table
    -- would be re-elected on every subsequent deploy and rebuilt forever -- which on this feature means
    -- copying every row of the table, every deploy.
    --
    -- THE COMPARISON IS RELATIVE, NEVER ABSOLUTE. The two tables below pair each column's declared
    -- position with its live ORDINAL_POSITION, and the test is for an INVERSION -- two columns whose
    -- declared order disagrees with their live order. Comparing the positions for EQUALITY would be
    -- wrong the moment a column is dropped and one side's numbering shifts relative to the other's,
    -- which would rebuild a correctly-ordered table on every deploy -- the infinite-rebuild trap. An
    -- inversion test never reads a position's value, only two positions' order, so any uniform shift is
    -- invisible to it and only genuine drift is detected.
    --
    -- THE COMPARED SET IS THE INTERSECTION, which the join to INFORMATION_SCHEMA produces by
    -- construction. A column that is live but NOT declared has no declared position to compare and is
    -- excluded: it is dropped by absence in this same run (a table RETAINING one cannot reach the
    -- election at all -- the HasColumnDrop guard in the election's WHERE excludes it), so it must not
    -- drag a correctly-ordered table into a rebuild. A column declared but NOT live cannot occur here:
    -- SchemaSmith_MissingTableAndColumnQuench added it earlier in this run. That ADD appends to the end
    -- of the table (no AFTER/FIRST clause is ever emitted), so a new column declared mid-file IS a
    -- genuine mismatch -- correctly, since only a rebuild can move it into place.
    --
    -- TWO IDENTICAL TABLES, deliberately. MySQL cannot open the same TEMPORARY table twice in one
    -- statement ("Can't reopen table"), so the inversion self-join needs a second physical copy. The
    -- alternative -- ROW_NUMBER() to rank each side -- is not available: the supported floor is MySQL
    -- 5.7, which has no window functions.
    -- ================================================================================================
    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
    VALUES (CONNECTION_ID(), 'ModifiedTableQuench: Detect declared-vs-deployed column order drift');

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildColumnOrder;
    CREATE TEMPORARY TABLE _SchemaSmith_RebuildColumnOrder (
        TableName VARCHAR(128) NOT NULL,
        DeclaredPos INT NOT NULL,
        DeclaredSeq INT NOT NULL,
        LivePos INT NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_RebuildColumnOrder (TableName, DeclaredPos, DeclaredSeq, LivePos)
    SELECT c.TableName, c.OrdinalPosition, c.RowId, isc.ORDINAL_POSITION
    FROM _SchemaSmith_Columns c
    INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
        ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
        AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName);

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildColumnOrderPeer;
    CREATE TEMPORARY TABLE _SchemaSmith_RebuildColumnOrderPeer (
        TableName VARCHAR(128) NOT NULL,
        DeclaredPos INT NOT NULL,
        DeclaredSeq INT NOT NULL,
        LivePos INT NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_RebuildColumnOrderPeer (TableName, DeclaredPos, DeclaredSeq, LivePos)
    SELECT TableName, DeclaredPos, DeclaredSeq, LivePos FROM _SchemaSmith_RebuildColumnOrder;

    INSERT INTO _SchemaSmith_RebuildFacts (TableName, HasOrderMismatch)
    SELECT DISTINCT a.TableName, 1
    FROM _SchemaSmith_RebuildColumnOrder a
    INNER JOIN _SchemaSmith_RebuildColumnOrderPeer b ON b.TableName = a.TableName
    -- The declared key is the PAIR, compared lexicographically, so this stays exactly in step with
    -- RebuildTable's "ORDER BY c.OrdinalPosition, c.RowId" even if two rows ever share an
    -- OrdinalPosition (the same table declared twice under mutually exclusive ShouldApply).
    WHERE (a.DeclaredPos < b.DeclaredPos
           OR (a.DeclaredPos = b.DeclaredPos AND a.DeclaredSeq < b.DeclaredSeq))
      AND a.LivePos > b.LivePos
    ON DUPLICATE KEY UPDATE HasOrderMismatch = 1;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildColumnOrder;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildColumnOrderPeer;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildElection;
    CREATE TEMPORARY TABLE _SchemaSmith_RebuildElection (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- The policy is resolved once in a derived table so the collation-normalising CONVERT/COLLATE around
    -- the session variable (its charset is the CONNECTION's, the column's is utf8mb4_unicode_ci, and
    -- mixing them inside IF() is an "Illegal mix of collations" error) is written once rather than five
    -- times. OnOrderMismatch COMPOSES with Mode rather than replacing it -- it is one more OR arm on the
    -- WHERE below, not a fourth Mode value. { "Mode": "THRESHOLD", "Threshold": 3, "OnOrderMismatch":
    -- true } therefore reads "rebuild once three modifications pile up OR once the column order has
    -- drifted", and pairing it with the NEVER default asks for a rebuild on order drift and nothing else
    -- -- the case the trigger mainly exists for.
    INSERT INTO _SchemaSmith_RebuildElection (TableName)
    SELECT p.TableName
    FROM (
        SELECT SchemaSmith_StripBacktickWrapping(t.TableName) AS TableName,
               UPPER(TRIM(COALESCE(IF(COALESCE(t.RebuildPolicySpecified, 0) = 1,
                                      t.RebuildPolicyMode,
                                      CONVERT(@ss_rebuild_policy_mode USING utf8mb4) COLLATE utf8mb4_unicode_ci),
                                   _utf8mb4'NEVER' COLLATE utf8mb4_unicode_ci))) AS PolicyMode,
               IF(COALESCE(t.RebuildPolicySpecified, 0) = 1,
                  t.RebuildPolicyThreshold,
                  CAST(@ss_rebuild_policy_threshold AS SIGNED)) AS PolicyThreshold,
               COALESCE(IF(COALESCE(t.RebuildPolicySpecified, 0) = 1,
                           t.RebuildPolicyOnOrderMismatch,
                           CAST(@ss_rebuild_policy_on_order_mismatch AS SIGNED)), 0) AS PolicyOnOrderMismatch,
               COALESCE(f.ModificationPasses, 0) AS ModificationPasses,
               COALESCE(f.HasColumnDrop, 0) AS HasColumnDrop,
               COALESCE(f.HasOrderMismatch, 0) AS HasOrderMismatch,
               COALESCE(t.DropColumnsRemovedFromProduct, 1) AS TableDropColumns
        FROM _SchemaSmith_Tables t
        LEFT JOIN _SchemaSmith_RebuildFacts f ON f.TableName = t.TableName
        WHERE t.NewTable = 0
    ) p
    -- A rebuild is a by-absence destroyer: the old table goes whole and only the DECLARED definition
    -- comes back, so anything this run deliberately declined to drop by absence would go anyway.
    -- p_CaptureWouldDrop is set exactly when the environment is in protected mode (ProductQuench sets
    -- CaptureWouldDrop = _protectedMode), which promises to destroy nothing by absence at all. The second
    -- arm is the same promise scoped to one table's columns: a live column the package no longer declares,
    -- whose drop THIS run is suppressing. Either one outranks the policy -- declining to rebuild costs an
    -- in-place ALTER, rebuilding anyway costs the user the data that protection existed to keep.
    WHERE p_CaptureWouldDrop = 0
      AND NOT (p.HasColumnDrop = 1
               AND NOT (p_DropColumnsRemovedFromProduct = 1 AND p.TableDropColumns = 1))
      AND (
          (p.PolicyMode = _utf8mb4'ALWAYS' COLLATE utf8mb4_unicode_ci
           AND (p.ModificationPasses > 0 OR p.HasColumnDrop = 1))
          OR
          (p.PolicyMode = _utf8mb4'THRESHOLD' COLLATE utf8mb4_unicode_ci
           AND p.PolicyThreshold IS NOT NULL
           AND p.ModificationPasses >= p.PolicyThreshold)
          OR
          -- Deliberately NOT conjoined with any Mode or with a detected column change: drifted column
          -- order is a standing reason to rebuild on its own, and on a table whose columns are merely in
          -- the wrong order there is no column CHANGE to detect -- requiring one would make the trigger
          -- unreachable in exactly the case it was added for.
          (p.PolicyOnOrderMismatch = 1 AND p.HasOrderMismatch = 1)
      );

    -- p_WhatIf goes straight through: SchemaSmith_RebuildTable prints its whole sequence and records
    -- 'wouldRebuild' without executing anything, and it applies its refusals in BOTH modes. So a policy
    -- that elects a rebuild on a BLOCKED table surfaces RebuildTable's refusal as a SIGNAL and the quench
    -- fails -- deliberately. It does NOT quietly fall back to altering in place: that would let a package
    -- ask for a rebuild and silently get something else, and the states that block a rebuild are exactly
    -- the ones where that difference matters.
    SET @v_rebuild_id := (SELECT MIN(RowId) FROM _SchemaSmith_RebuildElection);
    WHILE @v_rebuild_id IS NOT NULL DO
        SELECT TableName INTO @v_rebuild_table FROM _SchemaSmith_RebuildElection WHERE RowId = @v_rebuild_id;
        CALL SchemaSmith_RebuildTable(p_DatabaseName, @v_rebuild_table, p_WhatIf);
        SET @v_rebuild_id := (SELECT MIN(RowId) FROM _SchemaSmith_RebuildElection WHERE RowId > @v_rebuild_id);
    END WHILE;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildElection;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildFacts;

    -- =======================
    -- STEP 2.96: SYSTEM-VERSIONED TABLES -- HISTORY-REWRITE OPT-IN
    -- =======================
    -- MariaDB refuses every column DDL on a system-versioned table unless
    -- @@system_versioning_alter_history is KEEP, and KEEP rewrites the stored history to match the new
    -- shape. That is a data-retention decision, so it is opted into and off by default. Delegated to a
    -- procedure because MySQL will not CREATE a routine that merely mentions that variable -- verified
    -- live on 8.0.45, ERROR 1193, even inside an unreachable IF VERSION() LIKE '%MariaDB%' branch.
    -- System-variable resolution is not deferred the way column resolution is.
    --
    -- Placed here because STEP 2.9 below carries the first DDL this procedure executes.
    CALL SchemaSmith_SetSystemVersioningAlterHistory(@ss_system_versioning_alter_history);

    -- =======================
    -- STEP 2.9: DROP FOREIGN KEYS THAT BLOCK A COLUMN-LEVEL COLLATION CHANGE
    -- =======================
    -- Twin of the drop that guards the table-level CONVERT TO CHARACTER SET further down, hoisted here
    -- because it has to happen BEFORE Step 3 emits its per-column MODIFY COLUMN. The engine refuses to
    -- change a column a foreign key depends on ("Cannot change column ...: used in a foreign key
    -- constraint"), and a declared COLUMN collation that differs from the live one is exactly such a
    -- change -- the ordinary case when a package moves between servers with different defaults. The
    -- table-level block cannot cover it: it keys off ist.TABLE_COLLATION, which a column-only change
    -- leaves untouched. Both directions are collected, same as the table-level twin: the FK declared ON
    -- the column and the FK POINTING AT it, since MySQL requires the two sides' collations to match.
    -- Restoration is the foreign-key phase's job, which runs after -- the same division of labour the
    -- drop-column and table-collation paths already rely on. WhatIf only logs, mirroring that twin.
    IF p_WhatIf = 0 THEN
        BEGIN
            DECLARE v_ColCollFkDone INT DEFAULT FALSE;
            DECLARE v_ColCollFkSql TEXT;
            DECLARE cur_ColCollFks CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                              '`.`', TableName, '` DROP FOREIGN KEY `', ConstraintName, '`')
                  FROM _SchemaSmith_ColumnCollationFKsToDrop;
            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_ColCollFkDone = TRUE;

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColumnCollationFKsToDrop;
            CREATE TEMPORARY TABLE _SchemaSmith_ColumnCollationFKsToDrop (
                TableName VARCHAR(128) NOT NULL,
                ConstraintName VARCHAR(128) NOT NULL,
                PRIMARY KEY (TableName, ConstraintName)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            -- FKs declared ON a column whose collation is changing.
            INSERT IGNORE INTO _SchemaSmith_ColumnCollationFKsToDrop (TableName, ConstraintName)
            SELECT DISTINCT CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
              FROM _SchemaSmith_Columns c
              INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
                  ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
                  AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                  AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
              INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON CONVERT(kcu.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                  AND CONVERT(kcu.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.TableName) USING utf8mb4)
                  AND CONVERT(kcu.COLUMN_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.ColumnName) USING utf8mb4)
                  AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
             WHERE c.NewColumn = 0 AND c.Collation IS NOT NULL AND isc.COLLATION_NAME != c.Collation;

            -- And FKs POINTING AT one. Separate INSERT for the same optimizer reason as the table-level twin.
            INSERT IGNORE INTO _SchemaSmith_ColumnCollationFKsToDrop (TableName, ConstraintName)
            SELECT DISTINCT CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
              FROM _SchemaSmith_Columns c
              INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
                  ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
                  AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                  AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
              INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON CONVERT(kcu.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                  AND CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.TableName) USING utf8mb4)
                  AND CONVERT(kcu.REFERENCED_COLUMN_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.ColumnName) USING utf8mb4)
             WHERE c.NewColumn = 0 AND c.Collation IS NOT NULL AND isc.COLLATION_NAME != c.Collation;

            OPEN cur_ColCollFks;
            column_collation_fk_loop: LOOP
                FETCH cur_ColCollFks INTO v_ColCollFkSql;
                IF v_ColCollFkDone THEN LEAVE column_collation_fk_loop; END IF;
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Drop FK for column collation change: ', v_ColCollFkSql));
                SET @exec_sql = v_ColCollFkSql;
                PREPARE stmt FROM @exec_sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
            END LOOP;
            CLOSE cur_ColCollFks;
        END;
    END IF;

    -- =======================
    -- STEP 3: COLUMN MODIFICATIONS (excludes generated status changes)
    -- =======================
    -- Output column modifications
    -- NOTE: Excludes columns that are changing their generated status since MySQL
    -- doesn't support that via ALTER MODIFY - those are handled in Step 3.5
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Modify columns');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                      ' MODIFY COLUMN ', c.ColumnScript)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          -- Exclude columns where generated status is changing (regular->generated or generated->regular)
          AND NOT (
              -- Currently generated, wants to be regular
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              -- Currently regular, wants to be generated
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
          -- A desired expression-form default this target cannot legally MODIFY into (below MySQL
          -- 8.0.13 / see the degrade guard above) drops the whole column out of this pass; its
          -- current default is left in place rather than emitting a MODIFY that would hit a syntax error.
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
          AND (
              -- Normalize whitespace adjacent to structural delimiters and the DECIMAL/NUMERIC
              -- synonym so a hand-authored type that differs only by spacing/synonym is not a
              -- false "modified". ENUM/SET take a separate branch: their parenthesized values are
              -- case-sensitive DATA, so they compare keyword-case-insensitively but
              -- value-case-sensitively (BINARY) via SchemaSmith_UpperDataType — the whitespace/
              -- synonym normalization would wrongly fold a real value-case change.
              CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                     OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                   THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                   ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                     != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
              OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
              OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
              -- Default value changes (strip outer single quotes from JSON default for comparison,
              -- since GenerateTableJson wraps string/enum defaults in quotes for DDL but INFORMATION_SCHEMA stores raw values)
              OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
                  AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                      CASE WHEN c.DefaultValue LIKE '''%'''
                           THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                           ELSE c.DefaultValue END)
                  -- A DECIMAL default comes back at the column's scale ('0' declared, '0.00' stored), which
                  -- never matched as text and re-ALTERed the column on every deploy.
                  AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                      c.DefaultValue, isc.DATA_TYPE) = 0)
              OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
              -- Collation changes (only when JSON specifies a collation)
              OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
              -- Generated expression changes (both sides are generated, but expression differs)
              OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
                  AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
              -- AUTO_INCREMENT removal/addition (live EXTRA vs declared IsAutoIncrement) — parity with identity removal
              OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
              -- Invisible-column visibility differs. Gated behind SchemaSmith_SupportsInvisibleColumn(): below
              -- the floor (MySQL 8.0.23 / MariaDB 10.3) the column can never actually become invisible (the
              -- degrade guard above already reports it), so ignore the difference there or it churns every run.
              -- Symmetric in both directions -- a declared column newly marked invisible (visible -> invisible)
              -- and one whose Invisible flag was removed (invisible -> visible) both trip this <> compare.
              OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
              -- Column SRID differs. Gated behind SchemaSmith_SupportsColumnSrid() (MySQL 8.0.3+; MariaDB
              -- never -- the degrade guard above already reports it there), so ignore the difference below
              -- the floor or it churns every run. isc.SRS_ID cannot be read directly here (absent on
              -- MariaDB -- see SchemaSmith_ColumnSrid); <=> is null-safe so an unrestricted<->restricted
              -- change (NULL on one side) is detected the same as a value change on both sides.
              OR (SchemaSmith_SupportsColumnSrid() = 1
                  AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
              -- Comment differs (symmetric: covers added, changed, and cleared -- a declared NULL
              -- comment against a live comment counts as a difference the same as a value change).
              OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
              -- ON UPDATE CURRENT_TIMESTAMP[(n)] differs (symmetric: added, changed -- e.g. a precision
              -- change --, or removed). No SchemaSmith_Supports... gate: unlike Invisible/Srid above,
              -- this clause predates both engines' hard floors, so it is always legal to compare/emit.
              OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
          );

        -- #363: WhatIf twin of the ELSE-branch 'column'/'modified' audit; same source/predicate, wouldModify.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'wouldModify'
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND NOT (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
          -- A desired expression-form default this target cannot legally MODIFY into (below MySQL
          -- 8.0.13 / see the degrade guard above) drops the whole column out of this pass; its
          -- current default is left in place rather than emitting a MODIFY that would hit a syntax error.
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
          AND (
              CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                     OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                   THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                   ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                     != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
              OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
              OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
              OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
                  AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                      CASE WHEN c.DefaultValue LIKE '''%'''
                           THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                           ELSE c.DefaultValue END)
                  -- A DECIMAL default comes back at the column's scale ('0' declared, '0.00' stored), which
                  -- never matched as text and re-ALTERed the column on every deploy.
                  AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                      c.DefaultValue, isc.DATA_TYPE) = 0)
              OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
              OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
              OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
                  AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
              OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
              -- Invisible-column visibility differs. Gated behind SchemaSmith_SupportsInvisibleColumn(): below
              -- the floor (MySQL 8.0.23 / MariaDB 10.3) the column can never actually become invisible (the
              -- degrade guard above already reports it), so ignore the difference there or it churns every run.
              -- Symmetric in both directions -- a declared column newly marked invisible (visible -> invisible)
              -- and one whose Invisible flag was removed (invisible -> visible) both trip this <> compare.
              OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
              -- Column SRID differs. Gated behind SchemaSmith_SupportsColumnSrid() (MySQL 8.0.3+; MariaDB
              -- never -- the degrade guard above already reports it there), so ignore the difference below
              -- the floor or it churns every run. isc.SRS_ID cannot be read directly here (absent on
              -- MariaDB -- see SchemaSmith_ColumnSrid); <=> is null-safe so an unrestricted<->restricted
              -- change (NULL on one side) is detected the same as a value change on both sides.
              OR (SchemaSmith_SupportsColumnSrid() = 1
                  AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
              -- Comment differs (symmetric: covers added, changed, and cleared -- a declared NULL
              -- comment against a live comment counts as a difference the same as a value change).
              OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
              -- ON UPDATE CURRENT_TIMESTAMP[(n)] differs (symmetric: added, changed -- e.g. a precision
              -- change --, or removed). No SchemaSmith_Supports... gate: unlike Invisible/Srid above,
              -- this clause predates both engines' hard floors, so it is always legal to compare/emit.
              OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
          );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Modify columns');

        -- Per-column progress messages, set-based (preserves the per-column "ALTER TABLE ...
        -- MODIFY COLUMN ..." single-column text, even though execution below folds multiple
        -- columns of the same table into one statement).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Modify column: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                      ' MODIFY COLUMN ', c.ColumnScript)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          -- Exclude columns where generated status is changing (regular->generated or generated->regular)
          AND NOT (
              -- Currently generated, wants to be regular
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              -- Currently regular, wants to be generated
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
          -- A desired expression-form default this target cannot legally MODIFY into (below MySQL
          -- 8.0.13 / see the degrade guard above) drops the whole column out of this pass; its
          -- current default is left in place rather than emitting a MODIFY that would hit a syntax error.
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
          AND (
              -- Normalize whitespace adjacent to structural delimiters and the DECIMAL/NUMERIC
              -- synonym so a hand-authored type that differs only by spacing/synonym is not a
              -- false "modified". ENUM/SET take a separate branch: their parenthesized values are
              -- case-sensitive DATA, so they compare keyword-case-insensitively but
              -- value-case-sensitively (BINARY) via SchemaSmith_UpperDataType — the whitespace/
              -- synonym normalization would wrongly fold a real value-case change.
              CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                     OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                   THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                   ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                     != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
              OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
              OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
              -- Default value changes (strip outer single quotes from JSON default for comparison,
              -- since GenerateTableJson wraps string/enum defaults in quotes for DDL but INFORMATION_SCHEMA stores raw values)
              OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
                  AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                      CASE WHEN c.DefaultValue LIKE '''%'''
                           THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                           ELSE c.DefaultValue END)
                  -- A DECIMAL default comes back at the column's scale ('0' declared, '0.00' stored), which
                  -- never matched as text and re-ALTERed the column on every deploy.
                  AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                      c.DefaultValue, isc.DATA_TYPE) = 0)
              OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
              -- Collation changes (only when JSON specifies a collation)
              OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
              -- Generated expression changes (both sides are generated, but expression differs)
              OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
                  AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
              -- AUTO_INCREMENT removal/addition (live EXTRA vs declared IsAutoIncrement) — parity with identity removal
              OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
              -- Invisible-column visibility differs. Gated behind SchemaSmith_SupportsInvisibleColumn(): below
              -- the floor (MySQL 8.0.23 / MariaDB 10.3) the column can never actually become invisible (the
              -- degrade guard above already reports it), so ignore the difference there or it churns every run.
              -- Symmetric in both directions -- a declared column newly marked invisible (visible -> invisible)
              -- and one whose Invisible flag was removed (invisible -> visible) both trip this <> compare.
              OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
              -- Column SRID differs. Gated behind SchemaSmith_SupportsColumnSrid() (MySQL 8.0.3+; MariaDB
              -- never -- the degrade guard above already reports it there), so ignore the difference below
              -- the floor or it churns every run. isc.SRS_ID cannot be read directly here (absent on
              -- MariaDB -- see SchemaSmith_ColumnSrid); <=> is null-safe so an unrestricted<->restricted
              -- change (NULL on one side) is detected the same as a value change on both sides.
              OR (SchemaSmith_SupportsColumnSrid() = 1
                  AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
              -- Comment differs (symmetric: covers added, changed, and cleared -- a declared NULL
              -- comment against a live comment counts as a difference the same as a value change).
              OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
              -- ON UPDATE CURRENT_TIMESTAMP[(n)] differs (symmetric: added, changed -- e.g. a precision
              -- change --, or removed). No SchemaSmith_Supports... gate: unlike Invisible/Srid above,
              -- this clause predates both engines' hard floors, so it is always legal to compare/emit.
              OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
          );

        -- Object-change audit (#243 E5): one row per column about to be modified. Same join +
        -- predicate as the fold below, evaluated before the ALTER (INFORMATION_SCHEMA still reflects
        -- the OLD definition); per-column (no GROUP BY). Same INFORMATION_SCHEMA read pattern the
        -- statement-build below uses — not the #337 set-based-UPDATE shape.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'modified'
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND NOT (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
          -- A desired expression-form default this target cannot legally MODIFY into (below MySQL
          -- 8.0.13 / see the degrade guard above) drops the whole column out of this pass; its
          -- current default is left in place rather than emitting a MODIFY that would hit a syntax error.
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
          AND (
              CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                     OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                   THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                   ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                     != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
              OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
              OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
              OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
                  AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                      CASE WHEN c.DefaultValue LIKE '''%'''
                           THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                           ELSE c.DefaultValue END)
                  -- A DECIMAL default comes back at the column's scale ('0' declared, '0.00' stored), which
                  -- never matched as text and re-ALTERed the column on every deploy.
                  AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                      c.DefaultValue, isc.DATA_TYPE) = 0)
              OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
              OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
              OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
                  AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
              OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
              -- Invisible-column visibility differs. Gated behind SchemaSmith_SupportsInvisibleColumn(): below
              -- the floor (MySQL 8.0.23 / MariaDB 10.3) the column can never actually become invisible (the
              -- degrade guard above already reports it), so ignore the difference there or it churns every run.
              -- Symmetric in both directions -- a declared column newly marked invisible (visible -> invisible)
              -- and one whose Invisible flag was removed (invisible -> visible) both trip this <> compare.
              OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
              -- Column SRID differs. Gated behind SchemaSmith_SupportsColumnSrid() (MySQL 8.0.3+; MariaDB
              -- never -- the degrade guard above already reports it there), so ignore the difference below
              -- the floor or it churns every run. isc.SRS_ID cannot be read directly here (absent on
              -- MariaDB -- see SchemaSmith_ColumnSrid); <=> is null-safe so an unrestricted<->restricted
              -- change (NULL on one side) is detected the same as a value change on both sides.
              OR (SchemaSmith_SupportsColumnSrid() = 1
                  AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
              -- Comment differs (symmetric: covers added, changed, and cleared -- a declared NULL
              -- comment against a live comment counts as a difference the same as a value change).
              OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
              -- ON UPDATE CURRENT_TIMESTAMP[(n)] differs (symmetric: added, changed -- e.g. a precision
              -- change --, or removed). No SchemaSmith_Supports... gate: unlike Invisible/Srid above,
              -- this clause predates both engines' hard floors, so it is always legal to compare/emit.
              OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
          );

        -- Materialize: fold each table's column modifications into one multi-clause ALTER, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifyColStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_ModifyColStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_ModifyColStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName, ' ',
                      GROUP_CONCAT(CONCAT('MODIFY COLUMN ', c.ColumnScript) ORDER BY c.ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND NOT (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
          -- A desired expression-form default this target cannot legally MODIFY into (below MySQL
          -- 8.0.13 / see the degrade guard above) drops the whole column out of this pass; its
          -- current default is left in place rather than emitting a MODIFY that would hit a syntax error.
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
          AND (
              CASE WHEN UPPER(c.DataType) LIKE 'ENUM%' OR UPPER(c.DataType) LIKE 'SET%'
                     OR UPPER(isc.COLUMN_TYPE) LIKE 'ENUM%' OR UPPER(isc.COLUMN_TYPE) LIKE 'SET%'
                   THEN BINARY SchemaSmith_UpperDataType(isc.COLUMN_TYPE) != BINARY SchemaSmith_UpperDataType(c.DataType)
                   ELSE SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(isc.COLUMN_TYPE), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC'))
                     != SchemaSmith_StripIntDisplayWidth(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(UPPER(c.DataType), ' (', '('), '( ', '('), ' )', ')'), ', ', ','), ' ,', ','), 'DECIMAL', 'NUMERIC')) END
              OR (isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0)
              OR (isc.IS_NULLABLE = 'NO' AND c.IsNullable = 1)
              OR (c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) != '' AND c.IsAutoIncrement = 0
                  AND (SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NULL OR BINARY SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) != BINARY
                      CASE WHEN c.DefaultValue LIKE '''%'''
                           THEN REPLACE(SUBSTRING(c.DefaultValue, 2, CHAR_LENGTH(c.DefaultValue) - 2), '''''', '''')
                           ELSE c.DefaultValue END)
                  -- A DECIMAL default comes back at the column's scale ('0' declared, '0.00' stored), which
                  -- never matched as text and re-ALTERed the column on every deploy.
                  AND SchemaSmith_NumericDefaultsEqual(SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT),
                                                      c.DefaultValue, isc.DATA_TYPE) = 0)
              OR ((c.DefaultValue IS NULL OR TRIM(c.DefaultValue) = '') AND SchemaSmith_NormalizeColumnDefault(isc.COLUMN_DEFAULT) IS NOT NULL)
              OR (c.Collation IS NOT NULL AND TRIM(c.Collation) != '' AND BINARY isc.COLLATION_NAME != BINARY c.Collation)
              OR (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''
                  AND (isc.GENERATION_EXPRESSION IS NULL OR BINARY TRIM(isc.GENERATION_EXPRESSION) != BINARY TRIM(c.GeneratedExpression)))
              OR ((isc.EXTRA LIKE '%auto_increment%') <> (c.IsAutoIncrement = 1))
              -- Invisible-column visibility differs. Gated behind SchemaSmith_SupportsInvisibleColumn(): below
              -- the floor (MySQL 8.0.23 / MariaDB 10.3) the column can never actually become invisible (the
              -- degrade guard above already reports it), so ignore the difference there or it churns every run.
              -- Symmetric in both directions -- a declared column newly marked invisible (visible -> invisible)
              -- and one whose Invisible flag was removed (invisible -> visible) both trip this <> compare.
              OR (SchemaSmith_SupportsInvisibleColumn() = 1 AND (isc.EXTRA LIKE '%INVISIBLE%') <> (c.IsInvisible = 1))
          OR (SchemaSmith_SupportsSystemVersioning() = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES vt
                           WHERE vt.TABLE_SCHEMA = p_DatabaseName
                             AND vt.TABLE_NAME = SchemaSmith_StripBacktickWrapping(c.TableName)
                             AND vt.TABLE_TYPE = 'SYSTEM VERSIONED')
              AND (isc.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%') <> (c.IsWithoutSystemVersioning = 1))
              -- Column SRID differs. Gated behind SchemaSmith_SupportsColumnSrid() (MySQL 8.0.3+; MariaDB
              -- never -- the degrade guard above already reports it there), so ignore the difference below
              -- the floor or it churns every run. isc.SRS_ID cannot be read directly here (absent on
              -- MariaDB -- see SchemaSmith_ColumnSrid); <=> is null-safe so an unrestricted<->restricted
              -- change (NULL on one side) is detected the same as a value change on both sides.
              OR (SchemaSmith_SupportsColumnSrid() = 1
                  AND NOT (SchemaSmith_ColumnSrid(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) <=> c.Srid))
              -- Comment differs (symmetric: covers added, changed, and cleared -- a declared NULL
              -- comment against a live comment counts as a difference the same as a value change).
              OR (BINARY COALESCE(isc.COLUMN_COMMENT, '') != BINARY COALESCE(c.Comment, ''))
              -- ON UPDATE CURRENT_TIMESTAMP[(n)] differs (symmetric: added, changed -- e.g. a precision
              -- change --, or removed). No SchemaSmith_Supports... gate: unlike Invisible/Srid above,
              -- this clause predates both engines' hard floors, so it is always legal to compare/emit.
              OR (BINARY COALESCE(SchemaSmith_ColumnOnUpdateClause(isc.EXTRA), '') != BINARY COALESCE(c.OnUpdateCurrentTimestamp, ''))
          )
        GROUP BY c.TableName;

        SET @v_modifycol_id := (SELECT MIN(RowId) FROM _SchemaSmith_ModifyColStmts);
        WHILE @v_modifycol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_ModifyColStmts WHERE RowId = @v_modifycol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_modifycol_id := (SELECT MIN(RowId) FROM _SchemaSmith_ModifyColStmts WHERE RowId > @v_modifycol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ModifyColStmts;
    END IF;

    -- =======================
    -- STEP 3.5: GENERATED STATUS CHANGES (via DROP+ADD)
    -- =======================
    -- MySQL doesn't support changing a column's generated status via ALTER MODIFY
    -- So we must DROP the column and ADD it with the new definition
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Recreate columns (generated status changes)');
        -- Show DROP COLUMN statements
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                      SchemaSmith_StripBacktickWrapping(c.TableName), '` DROP COLUMN `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '`')
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          );
        -- Show ADD COLUMN statements
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                      SchemaSmith_StripBacktickWrapping(c.TableName), '` ADD COLUMN ', c.ColumnScript)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          );
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Recreate columns (generated status changes)');

        -- Per-column progress messages, set-based (preserves the per-column log line).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Recreate column: ', SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          );

        -- Materialize DROP-phase then ADD-phase, folded per table. Two separate INSERTs (a
        -- MySQL TEMPORARY table cannot be referenced twice in one statement); the DROP-phase
        -- rows are inserted first so AUTO_INCREMENT RowId guarantees every column drop for a
        -- table runs before that table's re-adds (mirrors the FK-drop/index-drop two-phase
        -- ordering in SchemaSmith_ForeignKeyQuench).
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenStatusStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_GenStatusStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_GenStatusStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_StripBacktickWrapping(c.TableName), '` ',
                      GROUP_CONCAT(CONCAT('DROP COLUMN `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '`') ORDER BY c.ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
        GROUP BY c.TableName;

        INSERT INTO _SchemaSmith_GenStatusStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_StripBacktickWrapping(c.TableName), '` ',
                      GROUP_CONCAT(CONCAT('ADD COLUMN ', c.ColumnScript) ORDER BY c.ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
            AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 0
          AND (
              ((isc.GENERATION_EXPRESSION IS NOT NULL AND isc.GENERATION_EXPRESSION != '')
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = ''))
              OR
              ((isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
               AND (c.GeneratedExpression IS NOT NULL AND TRIM(c.GeneratedExpression) != ''))
          )
        GROUP BY c.TableName;

        SET @v_genstatus_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenStatusStmts);
        WHILE @v_genstatus_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_GenStatusStmts WHERE RowId = @v_genstatus_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_genstatus_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenStatusStmts WHERE RowId > @v_genstatus_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenStatusStmts;
    END IF;

    -- =======================
    -- STEP 4: DROP UNUSED COLUMNS
    -- =======================
    -- Drop columns that exist in the database but are not in the JSON definition
    -- Must drop dependencies first (FKs, check constraints, indexes, generated columns)
    --
    -- Note: We use helper temp tables to avoid MySQL's "Can't reopen table" error
    -- (Error 1137) which occurs when a temp table is referenced multiple times
    -- in the same query (including in subqueries).

    -- Create helper table to copy defined columns (avoids referencing _SchemaSmith_Columns multiple times)
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DefinedColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_DefinedColumns (
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        OldName VARCHAR(128) NULL,
        INDEX idx_table_col (TableName, ColumnName),
        INDEX idx_table_old (TableName, OldName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_DefinedColumns (TableName, ColumnName, OldName)
    SELECT
        SchemaSmith_StripBacktickWrapping(c.TableName),
        SchemaSmith_StripBacktickWrapping(c.ColumnName),
        CASE WHEN c.OldName IS NOT NULL THEN SchemaSmith_StripBacktickWrapping(c.OldName) ELSE NULL END
    FROM _SchemaSmith_Columns c;

    -- Create helper table for columns to drop (used by all subsequent cursors)
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColumnsToDrop;
    CREATE TEMPORARY TABLE _SchemaSmith_ColumnsToDrop (
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, ColumnName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Identify columns to drop: exist in DB but not in JSON definition
    INSERT INTO _SchemaSmith_ColumnsToDrop (TableName, ColumnName)
    SELECT
        SchemaSmith_StripBacktickWrapping(t.TableName) AS TableName,
        isc.COLUMN_NAME AS ColumnName
    FROM _SchemaSmith_Tables t
    INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
        ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
        AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
    LEFT JOIN _SchemaSmith_DefinedColumns dc
        ON dc.TableName = SchemaSmith_StripBacktickWrapping(t.TableName)
        AND (
            BINARY dc.ColumnName = BINARY isc.COLUMN_NAME
            OR (dc.OldName IS NOT NULL AND BINARY dc.OldName = BINARY isc.COLUMN_NAME)
        )
    WHERE t.NewTable = 0
      AND dc.ColumnName IS NULL
      AND p_DropColumnsRemovedFromProduct = 1
      AND COALESCE(t.DropColumnsRemovedFromProduct, 1) = 1;

    -- No-drop protection tier (#270): capture columns that WOULD have been dropped by absence but
    -- are suppressed. Same by-absence predicate as the _SchemaSmith_ColumnsToDrop build above, minus
    -- the env p_DropColumnsRemovedFromProduct gate (protection forces it false) but keeping the
    -- per-table opt-out. Materialize the INFORMATION_SCHEMA read first (Index-B crash-safety), then
    -- a discrete audit insert. Audit rows only, so it runs regardless of p_WhatIf.
    IF p_CaptureWouldDrop = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropColumns;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropColumns (
            TableName VARCHAR(128) NOT NULL,
            ColumnName VARCHAR(128) NOT NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_WouldDropColumns (TableName, ColumnName)
        SELECT SchemaSmith_StripBacktickWrapping(t.TableName), isc.COLUMN_NAME
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        LEFT JOIN _SchemaSmith_DefinedColumns dc
            ON dc.TableName = SchemaSmith_StripBacktickWrapping(t.TableName)
            AND (
                BINARY dc.ColumnName = BINARY isc.COLUMN_NAME
                OR (dc.OldName IS NOT NULL AND BINARY dc.OldName = BINARY isc.COLUMN_NAME)
            )
        WHERE t.NewTable = 0
          AND dc.ColumnName IS NULL
          AND COALESCE(t.DropColumnsRemovedFromProduct, 1) = 1;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(p_DatabaseName, '.', TableName, '.', ColumnName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropColumns;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropColumns;
    END IF;

    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unused columns');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` DROP COLUMN `', ColumnName, '`')
        FROM _SchemaSmith_ColumnsToDrop;
    ELSE
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unused columns');
        -- First, drop foreign keys that reference columns we're about to drop
        -- Note: Uses a helper table keyed on (TableName, ConstraintName), same shape as
        -- SchemaSmith_ForeignKeyQuench's _SchemaSmith_ModifiedFKs, with two separate INSERTs to
        -- avoid the MySQL optimizer bug where an OR in a JOIN against
        -- INFORMATION_SCHEMA.KEY_COLUMN_USAGE with multiple temp table rows produces incorrect
        -- (empty) results. The PRIMARY KEY also dedupes a self-referencing FK that shows up in
        -- both branches (an improvement over the original's per-branch-only DISTINCT, which
        -- could otherwise attempt to drop the same FK twice and error on the second attempt).
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_FKsToDrop (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Branch 1: FK source columns being dropped
        INSERT IGNORE INTO _SchemaSmith_FKsToDrop (TableName, ConstraintName)
        SELECT DISTINCT
            CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
            ON CONVERT(kcu.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(kcu.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND CONVERT(kcu.COLUMN_NAME USING utf8mb4) = CONVERT(ctd.ColumnName USING utf8mb4)
        INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            ON CONVERT(tc.TABLE_SCHEMA USING utf8mb4) = CONVERT(kcu.TABLE_SCHEMA USING utf8mb4)
            AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(kcu.TABLE_NAME USING utf8mb4)
            AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4)
            AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

        -- Branch 2: FK referenced (target) columns being dropped
        INSERT IGNORE INTO _SchemaSmith_FKsToDrop (TableName, ConstraintName)
        SELECT DISTINCT
            CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
            ON CONVERT(kcu.REFERENCED_TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND CONVERT(kcu.REFERENCED_COLUMN_NAME USING utf8mb4) = CONVERT(ctd.ColumnName USING utf8mb4)
        INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            ON CONVERT(tc.TABLE_SCHEMA USING utf8mb4) = CONVERT(kcu.TABLE_SCHEMA USING utf8mb4)
            AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(kcu.TABLE_NAME USING utf8mb4)
            AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4)
            AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop FK for column: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                      '` DROP FOREIGN KEY `', ConstraintName, '`')
        FROM _SchemaSmith_FKsToDrop;

        -- Materialize: fold each table's FK drops into one multi-clause ALTER, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKDropForColStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_FKDropForColStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_FKDropForColStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
        FROM _SchemaSmith_FKsToDrop
        GROUP BY TableName;

        SET @v_fkcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKDropForColStmts);
        WHILE @v_fkcol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_FKDropForColStmts WHERE RowId = @v_fkcol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_fkcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_FKDropForColStmts WHERE RowId > @v_fkcol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKDropForColStmts;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FKsToDrop;

        -- Drop check constraints that reference columns being dropped
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CKsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_CKsToDrop (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- Check if constraint references this column (explicit COLLATE to avoid collation mismatch).
        -- INFORMATION_SCHEMA.CHECK_CONSTRAINTS does not exist on MySQL 5.7 and MySQL binds
        -- INFORMATION_SCHEMA references at CREATE time, so the read lives only inside this
        -- dynamically-built string, gated by SchemaSmith_SupportsCheckConstraints() (see
        -- GenerateTableJson for the full rationale). Below the floor there are no check
        -- constraints to drop, so _SchemaSmith_CKsToDrop simply stays unpopulated by this step.
        IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
            SET @v_ckDbName = p_DatabaseName;
            SET @v_ckSql = 'INSERT IGNORE INTO _SchemaSmith_CKsToDrop (TableName, ConstraintName)
SELECT DISTINCT
    CONVERT(tc.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
    CONVERT(cc.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
FROM _SchemaSmith_ColumnsToDrop ctd
INNER JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
    ON CONVERT(cc.CONSTRAINT_SCHEMA USING utf8mb4) = CONVERT(@v_ckDbName USING utf8mb4)
    AND CONVERT(cc.CHECK_CLAUSE USING utf8mb4) COLLATE utf8mb4_unicode_ci
        LIKE CONCAT(''%`'', ctd.ColumnName COLLATE utf8mb4_unicode_ci, ''`%'')
INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
    ON CONVERT(tc.CONSTRAINT_SCHEMA USING utf8mb4) = CONVERT(cc.CONSTRAINT_SCHEMA USING utf8mb4)
    AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(cc.CONSTRAINT_NAME USING utf8mb4)
    AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
    AND tc.CONSTRAINT_TYPE = ''CHECK''';
            PREPARE stmt FROM @v_ckSql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
        END IF;

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop check constraint for column: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                      '` DROP CONSTRAINT `', ConstraintName, '`')
        FROM _SchemaSmith_CKsToDrop;

        -- Materialize: fold each table's check-constraint drops into one multi-clause ALTER, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CKDropStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_CKDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_CKDropStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP CONSTRAINT `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
        FROM _SchemaSmith_CKsToDrop
        GROUP BY TableName;

        SET @v_ckcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_CKDropStmts);
        WHILE @v_ckcol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_CKDropStmts WHERE RowId = @v_ckcol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_ckcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_CKDropStmts WHERE RowId > @v_ckcol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CKDropStmts;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CKsToDrop;

        -- Drop indexes that use columns being dropped
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxsToDrop (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT IGNORE INTO _SchemaSmith_IdxsToDrop (TableName, IndexName)
        SELECT DISTINCT
            CONVERT(s.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
            CONVERT(s.INDEX_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.STATISTICS s
            ON CONVERT(s.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(s.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND CONVERT(s.COLUMN_NAME USING utf8mb4) = CONVERT(ctd.ColumnName USING utf8mb4)
        WHERE UPPER(s.INDEX_NAME) != 'PRIMARY';

        -- Message text preserves the original standalone "DROP INDEX ... ON ..." wording even
        -- though the statement actually executed below uses the equivalent
        -- "ALTER TABLE ... DROP INDEX ..." form so multiple indexes on the same table can fold
        -- into one statement.
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop index for column: DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
        FROM _SchemaSmith_IdxsToDrop;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDropStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_IdxDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_IdxDropStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP INDEX `', IndexName, '`') ORDER BY IndexName SEPARATOR ', '))
        FROM _SchemaSmith_IdxsToDrop
        GROUP BY TableName;

        SET @v_idxcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_IdxDropStmts);
        WHILE @v_idxcol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_IdxDropStmts WHERE RowId = @v_idxcol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_idxcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_IdxDropStmts WHERE RowId > @v_idxcol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDropStmts;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxsToDrop;

        -- Drop generated columns that reference columns being dropped
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_GenColsToDrop (
            TableName VARCHAR(128) NOT NULL,
            ColumnName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, ColumnName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- INSERT IGNORE: a generated column referencing two-or-more dropped columns produces
        -- multiple join rows for the same (TableName, ColumnName); the original loop had no
        -- DISTINCT here either, but the folded form needs the PK to hold a single row per column.
        INSERT IGNORE INTO _SchemaSmith_GenColsToDrop (TableName, ColumnName)
        SELECT
            CONVERT(isc_gen.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
            CONVERT(isc_gen.COLUMN_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc_gen
            ON CONVERT(isc_gen.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(isc_gen.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND isc_gen.GENERATION_EXPRESSION IS NOT NULL
            AND isc_gen.GENERATION_EXPRESSION != ''
            -- Explicit COLLATE to avoid collation mismatch between INFORMATION_SCHEMA and temp table
            AND CONVERT(isc_gen.GENERATION_EXPRESSION USING utf8mb4) COLLATE utf8mb4_unicode_ci
                LIKE CONCAT('%`', ctd.ColumnName COLLATE utf8mb4_unicode_ci, '`%')
        -- Only drop generated columns that are also not in the definition
        -- (use _SchemaSmith_DefinedColumns to avoid MySQL's "Can't reopen table" error)
        LEFT JOIN _SchemaSmith_DefinedColumns dc_gen
            ON dc_gen.TableName = ctd.TableName
            AND BINARY dc_gen.ColumnName = BINARY isc_gen.COLUMN_NAME
        WHERE dc_gen.ColumnName IS NULL;

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop generated column: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                      '` DROP COLUMN `', ColumnName, '`')
        FROM _SchemaSmith_GenColsToDrop;

        -- Materialize: fold each table's generated-column drops into one multi-clause ALTER, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDropStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_GenColDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_GenColDropStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                      GROUP_CONCAT(CONCAT('DROP COLUMN `', ColumnName, '`') ORDER BY ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_GenColsToDrop
        GROUP BY TableName;

        SET @v_gencol_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenColDropStmts);
        WHILE @v_gencol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_GenColDropStmts WHERE RowId = @v_gencol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_gencol_id := (SELECT MIN(RowId) FROM _SchemaSmith_GenColDropStmts WHERE RowId > @v_gencol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDropStmts;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColsToDrop;

        -- Now drop the columns themselves
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Drop column: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                      CONVERT(ctd.TableName USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                      '` DROP COLUMN `', CONVERT(ctd.ColumnName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`')
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON CONVERT(isc.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(isc.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND CONVERT(isc.COLUMN_NAME USING utf8mb4) = CONVERT(ctd.ColumnName USING utf8mb4)
        -- Not a generated column (those were handled above)
        WHERE (isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '');

        -- Materialize: fold each table's remaining column drops into one multi-clause ALTER, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColDropStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_ColDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_ColDropStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                      CONVERT(ctd.TableName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '` ',
                      GROUP_CONCAT(CONCAT('DROP COLUMN `', CONVERT(ctd.ColumnName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`') ORDER BY ctd.ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_ColumnsToDrop ctd
        INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON CONVERT(isc.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND CONVERT(isc.TABLE_NAME USING utf8mb4) = CONVERT(ctd.TableName USING utf8mb4)
            AND CONVERT(isc.COLUMN_NAME USING utf8mb4) = CONVERT(ctd.ColumnName USING utf8mb4)
        WHERE (isc.GENERATION_EXPRESSION IS NULL OR isc.GENERATION_EXPRESSION = '')
        GROUP BY ctd.TableName;

        SET @v_dropcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_ColDropStmts);
        WHILE @v_dropcol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_ColDropStmts WHERE RowId = @v_dropcol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_dropcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_ColDropStmts WHERE RowId > @v_dropcol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColDropStmts;
    END IF;

    -- Clean up helper tables
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColumnsToDrop;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DefinedColumns;

    -- =======================
    -- STEP 5: ALTER TABLE ENGINE
    -- =======================
    -- Alter table engine if different
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table engine');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ENGINE = ', t.Engine)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.Engine IS NOT NULL
          AND UPPER(ist.ENGINE) != UPPER(t.Engine);
    ELSE
        BEGIN
            DECLARE v_EngineDone INT DEFAULT FALSE;
            DECLARE v_EngineSql TEXT;
            DECLARE cur_EngineChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ENGINE = ', t.Engine) AS AlterEngineStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND t.Engine IS NOT NULL
                  AND UPPER(ist.ENGINE) != UPPER(t.Engine);

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_EngineDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table engine');
            SET v_EngineDone = FALSE;
            OPEN cur_EngineChanges;

            engine_changes_loop: LOOP
                FETCH cur_EngineChanges INTO v_EngineSql;
                IF v_EngineDone THEN
                    LEAVE engine_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Change engine: ', v_EngineSql));
                SET @exec_sql = v_EngineSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_EngineChanges;
        END;
    END IF;

    -- Alter table collation if different
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table collation');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                      ' CONVERT TO CHARACTER SET ',
                      SUBSTRING_INDEX(t.Collation, '_', 1),
                      ' COLLATE ', t.Collation)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.Collation IS NOT NULL
          AND ist.TABLE_COLLATION != t.Collation;
    ELSE
        BEGIN
            DECLARE v_CollationDone INT DEFAULT FALSE;
            DECLARE v_CollationSql TEXT;
            DECLARE cur_CollationChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                              ' CONVERT TO CHARACTER SET ',
                              SUBSTRING_INDEX(t.Collation, '_', 1),
                              ' COLLATE ', t.Collation) AS AlterCollationStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND t.Collation IS NOT NULL
                  AND ist.TABLE_COLLATION != t.Collation;

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_CollationDone = TRUE;

            -- CONVERT TO CHARACTER SET rewrites every character column on the table, and the engine refuses
            -- outright while a foreign key references any of them ("Cannot change column ... used in a
            -- foreign key constraint") -- a hard deploy failure, not churn. Drop the dependents first; the
            -- foreign-key phase that runs after this reconciles declared FKs and puts them back, which is
            -- the same division of labour the drop-column path above relies on.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CollationFKsToDrop;
            CREATE TEMPORARY TABLE _SchemaSmith_CollationFKsToDrop (
                TableName VARCHAR(128) NOT NULL,
                ConstraintName VARCHAR(128) NOT NULL,
                PRIMARY KEY (TableName, ConstraintName)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            -- FKs declared ON a converting table.
            INSERT IGNORE INTO _SchemaSmith_CollationFKsToDrop (TableName, ConstraintName)
            SELECT DISTINCT CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
              FROM _SchemaSmith_Tables t
              INNER JOIN INFORMATION_SCHEMA.TABLES ist
                  ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                  AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
              INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON CONVERT(kcu.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                  AND CONVERT(kcu.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
                  AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
             WHERE t.NewTable = 0 AND t.Collation IS NOT NULL AND ist.TABLE_COLLATION != t.Collation;

            -- And FKs POINTING AT one: the referencing column must keep a matching collation, so the engine
            -- rejects the convert from that side too. Separate INSERT for the optimizer bug noted above.
            INSERT IGNORE INTO _SchemaSmith_CollationFKsToDrop (TableName, ConstraintName)
            SELECT DISTINCT CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                            CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
              FROM _SchemaSmith_Tables t
              INNER JOIN INFORMATION_SCHEMA.TABLES ist
                  ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                  AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
              INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON CONVERT(kcu.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                  AND CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
             WHERE t.NewTable = 0 AND t.Collation IS NOT NULL AND ist.TABLE_COLLATION != t.Collation;

            BEGIN
                DECLARE v_ColFkDone INT DEFAULT FALSE;
                DECLARE v_ColFkSql TEXT;
                DECLARE cur_ColFks CURSOR FOR
                    SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                  '`.`', TableName, '` DROP FOREIGN KEY `', ConstraintName, '`')
                      FROM _SchemaSmith_CollationFKsToDrop;
                DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_ColFkDone = TRUE;

                OPEN cur_ColFks;
                collation_fk_loop: LOOP
                    FETCH cur_ColFks INTO v_ColFkSql;
                    IF v_ColFkDone THEN LEAVE collation_fk_loop; END IF;
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Drop FK for collation change: ', v_ColFkSql));
                    SET @exec_sql = v_ColFkSql;
                    PREPARE stmt FROM @exec_sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
                END LOOP;
                CLOSE cur_ColFks;
            END;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table collation');
            SET v_CollationDone = FALSE;
            OPEN cur_CollationChanges;

            collation_changes_loop: LOOP
                FETCH cur_CollationChanges INTO v_CollationSql;
                IF v_CollationDone THEN
                    LEAVE collation_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Change collation: ', v_CollationSql));
                SET @exec_sql = v_CollationSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_CollationChanges;
        END;
    END IF;

    -- =======================
    -- STEP 5.5: ALTER TABLE COMMENT
    -- =======================
    -- Symmetric compare (unlike Engine/RowFormat above, which only ever apply a declared value and
    -- never clear one): a declared NULL comment against a live comment counts as a difference the
    -- same as a value change, so removing a Comment from the JSON clears it in the database too --
    -- matching the symmetric column-comment predicate in STEP 3 above. Escaping matches the
    -- established _SchemaSmith_FullTextIndexes.Comment form (double the embedded single quotes).
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table comment');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                      ' COMMENT=''', REPLACE(COALESCE(t.Comment, ''), '''', ''''''), '''')
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND BINARY COALESCE(ist.TABLE_COMMENT, '') != BINARY COALESCE(t.Comment, '');
    ELSE
        BEGIN
            DECLARE v_CommentDone INT DEFAULT FALSE;
            DECLARE v_CommentSql TEXT;
            DECLARE cur_CommentChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                              ' COMMENT=''', REPLACE(COALESCE(t.Comment, ''), '''', ''''''), '''') AS AlterCommentStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND BINARY COALESCE(ist.TABLE_COMMENT, '') != BINARY COALESCE(t.Comment, '');

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_CommentDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table comment');
            SET v_CommentDone = FALSE;
            OPEN cur_CommentChanges;

            comment_changes_loop: LOOP
                FETCH cur_CommentChanges INTO v_CommentSql;
                IF v_CommentDone THEN
                    LEAVE comment_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Change comment: ', v_CommentSql));
                SET @exec_sql = v_CommentSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_CommentChanges;
        END;
    END IF;

    -- =======================
    -- STEP 6: ALTER TABLE ROW_FORMAT
    -- =======================
    -- Alter table row format if different
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table row format');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ROW_FORMAT=', t.RowFormat)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.RowFormat IS NOT NULL
          AND t.RowFormat != ''
          AND UPPER(ist.ROW_FORMAT) != UPPER(t.RowFormat);
    ELSE
        BEGIN
            DECLARE v_RowFormatDone INT DEFAULT FALSE;
            DECLARE v_RowFormatSql TEXT;
            DECLARE cur_RowFormatChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ROW_FORMAT=', t.RowFormat) AS AlterRowFormatStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND t.RowFormat IS NOT NULL
                  AND t.RowFormat != ''
                  AND UPPER(ist.ROW_FORMAT) != UPPER(t.RowFormat);

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_RowFormatDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table row format');
            SET v_RowFormatDone = FALSE;
            OPEN cur_RowFormatChanges;

            rowformat_changes_loop: LOOP
                FETCH cur_RowFormatChanges INTO v_RowFormatSql;
                IF v_RowFormatDone THEN
                    LEAVE rowformat_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Change row format: ', v_RowFormatSql));
                SET @exec_sql = v_RowFormatSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_RowFormatChanges;
        END;
    END IF;

    -- =======================
    -- STEP 6.5: ALTER TABLE ENCRYPTION (F2a)
    -- =======================
    -- At-rest tablespace encryption is ALTERable the same way ROW_FORMAT is (STEP 6 directly above) --
    -- unlike system versioning (STEP 7.5 below), there is no data-loss direction to refuse: toggling
    -- either engine's encryption clause rebuilds the tablespace in place, it does not purge anything, so
    -- both directions (declared-on/deployed-off AND declared-off/deployed-on) converge symmetrically here.
    --
    -- Deployed state is read via SchemaSmith_CreateOption over INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS --
    -- a plain column, safe on both engines (see that function's own header) -- so this whole step needs
    -- no @@system-variable or MariaDB-only catalog reference despite living in the file shared by both
    -- engines. VERSION() picks the engine-specific branch at execution time, exactly like the CREATE-path
    -- emit in MissingTableAndColumnQuench.
    --
    -- A server without an encryption keyring rejects the ALTER with its own error -- that is server
    -- configuration, not a version floor SchemaSmith can degrade around (like a missing filegroup), so no
    -- SchemaSmith_Supports... gate exists for this step.
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table encryption');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                      CASE WHEN VERSION() NOT LIKE '%MariaDB%'
                           THEN CONCAT(' ENCRYPTION=''', t.Encryption, '''')
                           ELSE CONCAT(' ENCRYPTED=', CASE WHEN t.Encrypted = 1 THEN 'YES' ELSE 'NO' END,
                                       CASE WHEN t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL
                                            THEN CONCAT(' ENCRYPTION_KEY_ID=', t.EncryptionKeyId) ELSE '' END)
                      END)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          -- BINARY on BOTH sides of every option comparison, deliberately. COALESCE(fn(), '<literal>')
          -- combines the function's return collation (the database default, fixed when the function was
          -- created) with the literal's (the routine's stored collation_connection). When those two
          -- differ, MariaDB/MySQL resolve the COALESCE to the charset's BINARY collation with
          -- coercibility NONE -- which can then be compared against nothing at all, and the whole
          -- ModifiedTableQuench dies with "Illegal mix of collations (utf8mb4_bin,NONE) and
          -- (<db collation>,IMPLICIT) for operation '<>'".
          --
          -- Not hypothetical, and not rare: it fails EVERY deploy into a database whose collation differs
          -- from the connection default. The shipped demos (utf8mb4_unicode_ci) hit it on both the 10.2
          -- floor and 11.4; the integration suite missed it only because its TestMain happens to match.
          -- Both operands are already UPPER-ed (or plain digits), so an exact binary compare is the
          -- intended semantic regardless -- this is the same idiom the column comparisons above use.
          AND (
              (VERSION() NOT LIKE '%MariaDB%'
               AND t.Encryption IS NOT NULL AND t.Encryption != ''
               AND BINARY UPPER(COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTION'), 'N')) != BINARY UPPER(t.Encryption))
              OR
              (VERSION() LIKE '%MariaDB%'
               AND (
                   (CASE WHEN BINARY UPPER(COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTED'), 'NO')) = BINARY 'YES' THEN 1 ELSE 0 END) != t.Encrypted
                   OR (t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL
                       AND BINARY COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTION_KEY_ID'), '') != BINARY CAST(t.EncryptionKeyId AS CHAR))
               ))
          );
    ELSE
        BEGIN
            DECLARE v_EncryptionDone INT DEFAULT FALSE;
            DECLARE v_EncryptionSql TEXT;
            DECLARE cur_EncryptionChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName,
                              CASE WHEN VERSION() NOT LIKE '%MariaDB%'
                                   THEN CONCAT(' ENCRYPTION=''', t.Encryption, '''')
                                   ELSE CONCAT(' ENCRYPTED=', CASE WHEN t.Encrypted = 1 THEN 'YES' ELSE 'NO' END,
                                               CASE WHEN t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL
                                                    THEN CONCAT(' ENCRYPTION_KEY_ID=', t.EncryptionKeyId) ELSE '' END)
                              END) AS AlterEncryptionStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  -- BINARY on both sides -- see the identical predicate in the p_WhatIf branch above for
                  -- why (COALESCE across two collations resolves to utf8mb4_bin/NONE and then compares
                  -- against nothing, killing every deploy into a differently-collated database).
                  AND (
                      (VERSION() NOT LIKE '%MariaDB%'
                       AND t.Encryption IS NOT NULL AND t.Encryption != ''
                       AND BINARY UPPER(COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTION'), 'N')) != BINARY UPPER(t.Encryption))
                      OR
                      (VERSION() LIKE '%MariaDB%'
                       AND (
                           (CASE WHEN BINARY UPPER(COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTED'), 'NO')) = BINARY 'YES' THEN 1 ELSE 0 END) != t.Encrypted
                           OR (t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL
                               AND BINARY COALESCE(SchemaSmith_CreateOption(ist.CREATE_OPTIONS, 'ENCRYPTION_KEY_ID'), '') != BINARY CAST(t.EncryptionKeyId AS CHAR))
                       ))
                  );

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_EncryptionDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Change table encryption');
            SET v_EncryptionDone = FALSE;
            OPEN cur_EncryptionChanges;

            encryption_changes_loop: LOOP
                FETCH cur_EncryptionChanges INTO v_EncryptionSql;
                IF v_EncryptionDone THEN
                    LEAVE encryption_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Change encryption: ', v_EncryptionSql));
                SET @exec_sql = v_EncryptionSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_EncryptionChanges;
        END;
    END IF;

    -- =======================
    -- STEP 7: ALTER TABLE AUTO_INCREMENT
    -- =======================
    -- Set auto-increment seed when declared value is higher than the live value (set-if-higher, idempotent).
    -- COALESCE(ist.AUTO_INCREMENT, 0): MySQL returns NULL for AUTO_INCREMENT on an empty InnoDB table,
    -- treating NULL as 0 ensures a declared seed is applied even on tables that have never had rows.
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Set auto-increment seed');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' AUTO_INCREMENT=', t.AutoIncrementValue)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.AutoIncrementValue IS NOT NULL
          AND t.AutoIncrementValue > COALESCE(ist.AUTO_INCREMENT, 0);
    ELSE
        BEGIN
            DECLARE v_AutoIncDone INT DEFAULT FALSE;
            DECLARE v_AutoIncSql TEXT;
            DECLARE cur_AutoIncChanges CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' AUTO_INCREMENT=', t.AutoIncrementValue) AS AlterAutoIncStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND t.AutoIncrementValue IS NOT NULL
                  AND t.AutoIncrementValue > COALESCE(ist.AUTO_INCREMENT, 0);

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_AutoIncDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Set auto-increment seed');
            SET v_AutoIncDone = FALSE;
            OPEN cur_AutoIncChanges;

            autoinc_changes_loop: LOOP
                FETCH cur_AutoIncChanges INTO v_AutoIncSql;
                IF v_AutoIncDone THEN
                    LEAVE autoinc_changes_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Set auto-increment: ', v_AutoIncSql));
                SET @exec_sql = v_AutoIncSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_AutoIncChanges;
        END;
    END IF;

    -- =======================
    -- STEP 7.5: SYSTEM VERSIONING CONVERGENCE (EXISTING TABLES)
    -- =======================
    -- F1S1 (MissingTableAndColumnQuench) handles a NEW table's WITH SYSTEM VERSIONING clause. This step
    -- converges an EXISTING table (t.NewTable = 0) whose declared _SchemaSmith_Tables.IsSystemVersioned
    -- disagrees with what is deployed (INFORMATION_SCHEMA.TABLES.TABLE_TYPE = 'SYSTEM VERSIONED'). The
    -- two directions are NOT symmetric, by design:
    --
    --   declared 1 / deployed 0 -> CONVERGE. ADD SYSTEM VERSIONING is additive (MariaDB implicitly adds
    --   the hidden ROW_START/ROW_END columns) and destroys nothing, so it is applied the same way the
    --   other table-attribute converge steps above are (Engine/Collation/Comment/RowFormat/AutoIncrement)
    --   -- gated on SchemaSmith_SupportsSystemVersioning(), mirroring the CREATE path's gate. Below that
    --   gate (MariaDB <10.3, or MySQL at any version) this is NOT a silent no-op: a dedicated degrade
    --   block below (mirroring F1S1's NewTable=1 degrade in MissingTableAndColumnQuench) fails or warns
    --   per UnsupportedFeaturePolicy exactly like the CREATE path does.
    --
    --   declared 0 / deployed 1 -> REFUSE, never DROP. MariaDB's DROP SYSTEM VERSIONING purges the row
    --   history outright, and a state diff cannot tell "never wanted this" apart from "still want the
    --   history, just stopped declaring it". This is a data-loss guard, not a version degrade, so it
    --   fires REGARDLESS of UnsupportedFeaturePolicy and BEFORE the p_WhatIf branch below -- there is no
    --   safe "preview" of a refusal, it must abort the run in both modes, mirroring STEP -0.5's
    --   partitioning refuse further up this procedure. It does not break round-trip: extraction only
    --   ever emits IsSystemVersioned when true (SchemaSmith_GenerateTableJson), so a re-extracted package
    --   of a versioned table always re-declares it -- this only fires on a deliberate hand-edit that
    --   removes the property from an otherwise-versioned table's package.
    --
    -- NO SystemVersioningAlterHistory OPT-IN for the ADD direction (investigated against
    -- SchemaSmith_SetSystemVersioningAlterHistory.sql / STEP 2.96 above): that session variable exists
    -- because "MariaDB refuses every column DDL on a system-versioned table by default" (ERROR 4119, "Not
    -- allowed for system-versioned ... Change @@system_versioning_alter_history to proceed with ALTER") --
    -- it governs ALTERs against a table that IS ALREADY versioned. A table converging here is NOT YET
    -- versioned at the moment this ALTER runs, so that restriction does not apply and KEEP is never
    -- required just to add versioning to a plain table. (The REFUSE direction never emits DROP SYSTEM
    -- VERSIONING at all, so it needs no opt-in either -- the whole point is that statement never runs.)
    --
    -- SIGNAL MESSAGE_TEXT is capped at 128 characters (see STEP 8's comment on the same limit). Unlike
    -- the partitioning/PreventDrop guards, which can name arbitrarily many offending tables and so keep
    -- the SIGNAL generic and push detail to the run log, this refuse only ever names the single first
    -- offender and truncates it defensively -- comfortably inside the cap even at MySQL's 64-character
    -- identifier ceiling -- so the exception message itself names the table.
    SET @ss_sysver_refuse_table := (
        SELECT LEFT(SchemaSmith_StripBacktickWrapping(t.TableName), 40)
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.IsSystemVersioned = 0
          AND ist.TABLE_TYPE = 'SYSTEM VERSIONED'
        LIMIT 1
    );

    IF @ss_sysver_refuse_table IS NOT NULL THEN
        -- Log every offending table (not just the first named in the SIGNAL below) to the run log, same
        -- shape as the partitioning/PreventDrop guards.
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Table is system-versioned and no longer declared -- DROP SYSTEM VERSIONING refused (would purge row history): ',
               SchemaSmith_StripBacktickWrapping(t.TableName))
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.IsSystemVersioned = 0
          AND ist.TABLE_TYPE = 'SYSTEM VERSIONED';

        SET @ss_msg = CONCAT('Table ', @ss_sysver_refuse_table, ': DROP SYSTEM VERSIONING refused (data loss) -- use a migration.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- =========================================================================
    -- Below-floor degrade: declared 1 / deployed 0, but SchemaSmith_SupportsSystemVersioning() = 0
    -- (below MariaDB 10.3, or MySQL at any version). Mirrors SchemaSmith_MissingTableAndColumnQuench's
    -- F1S1 degrade block EXACTLY (same message text, same ObjectType wording, same fail/warn split) --
    -- that block is scoped to t.NewTable = 1 only, so an EXISTING ordinary table whose package NEWLY
    -- declares IsSystemVersioned was falling through both there and here with no report and no
    -- UnsupportedFeaturePolicy=fail honored, silently losing the declared attribute exactly like the gap
    -- F1S1's block exists to close for CREATE. Runs unconditionally (both WhatIf and live, ahead of the
    -- converge cursor below), matching F1S1's placement ahead of its own p_WhatIf branch.
    -- =========================================================================
    IF SchemaSmith_SupportsSystemVersioning() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables t
                   INNER JOIN INFORMATION_SCHEMA.TABLES ist
                       ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                       AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                   WHERE t.NewTable = 0
                     AND t.IsSystemVersioned = 1
                     AND ist.TABLE_TYPE != 'SYSTEM VERSIONED') THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  System versioning requires MariaDB 10.3 (MySQL unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(t.TableName))
            FROM _SchemaSmith_Tables t
            INNER JOIN INFORMATION_SCHEMA.TABLES ist
                ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
            WHERE t.NewTable = 0
              AND t.IsSystemVersioned = 1
              AND ist.TABLE_TYPE != 'SYSTEM VERSIONED';
            SET @ss_msg = 'System versioning needs MariaDB 10.3 (UnsupportedFeaturePolicy=fail). See the run log.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Table deployed without system versioning (requires MariaDB 10.3, MySQL unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(t.TableName))
            FROM _SchemaSmith_Tables t
            INNER JOIN INFORMATION_SCHEMA.TABLES ist
                ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
            WHERE t.NewTable = 0
              AND t.IsSystemVersioned = 1
              AND ist.TABLE_TYPE != 'SYSTEM VERSIONED';
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'table without its WITH SYSTEM VERSIONING clause',
                   SchemaSmith_StripBacktickWrapping(t.TableName), 'downgraded'
            FROM _SchemaSmith_Tables t
            INNER JOIN INFORMATION_SCHEMA.TABLES ist
                ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
            WHERE t.NewTable = 0
              AND t.IsSystemVersioned = 1
              AND ist.TABLE_TYPE != 'SYSTEM VERSIONED';
        END IF;
    END IF;

    -- Converge direction: declared 1 / deployed 0. Same WhatIf-preview / live-cursor shape as the
    -- Engine/Collation/Comment/RowFormat/AutoIncrement steps above; no ChangeAudit row, matching those
    -- (only the column-level and table-drop passes elsewhere in this file audit their WhatIf twin).
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add system versioning to existing tables');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ADD SYSTEM VERSIONING')
        FROM _SchemaSmith_Tables t
        INNER JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        WHERE t.NewTable = 0
          AND t.IsSystemVersioned = 1
          AND ist.TABLE_TYPE != 'SYSTEM VERSIONED'
          AND SchemaSmith_SupportsSystemVersioning() = 1;
    ELSE
        BEGIN
            DECLARE v_AddVersioningDone INT DEFAULT FALSE;
            DECLARE v_AddVersioningSql TEXT;
            DECLARE cur_AddVersioning CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' ADD SYSTEM VERSIONING') AS AlterAddVersioningStatement
                FROM _SchemaSmith_Tables t
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
                WHERE t.NewTable = 0
                  AND t.IsSystemVersioned = 1
                  AND ist.TABLE_TYPE != 'SYSTEM VERSIONED'
                  AND SchemaSmith_SupportsSystemVersioning() = 1;

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_AddVersioningDone = TRUE;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add system versioning to existing tables');
            SET v_AddVersioningDone = FALSE;
            OPEN cur_AddVersioning;

            add_versioning_loop: LOOP
                FETCH cur_AddVersioning INTO v_AddVersioningSql;
                IF v_AddVersioningDone THEN
                    LEAVE add_versioning_loop;
                END IF;

                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Add system versioning: ', v_AddVersioningSql));
                SET @exec_sql = v_AddVersioningSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;

            CLOSE cur_AddVersioning;
        END;
    END IF;

    -- =======================
    -- STEP 7.6: APPLY PER-COLUMN "WITHOUT SYSTEM VERSIONING" AFTER VERSIONING (#408 / F1S2)
    -- =======================
    -- STEP 3's per-column exclusion drift is gated on the table ALREADY reading SYSTEM VERSIONED, and STEP 3
    -- runs BEFORE STEP 7.5 -- so a table CONVERGING to versioned in THIS deploy had its declared column
    -- exclusions skipped, and a newly-added excluded column had its clause stripped at ADD COLUMN time
    -- (MissingTableAndColumnQuench, to avoid ERROR 4124 on a not-yet-versioned table). Now that STEP 7.5 has
    -- versioned the table, apply the exclusion to any declared-excluded column that does not yet carry it.
    -- Idempotent (fires only where the deployed column lacks the clause), so it is a harmless no-op for the
    -- columns STEP 3 already handled on already-versioned tables. Requires the SystemVersioningAlterHistory
    -- opt-in (STEP 2.96): MariaDB refuses column DDL on a versioned table without it (ERROR 4119). The gate
    -- reads the OPT-IN MODE from the @ss_system_versioning_alter_history user variable (what STEP 2.96 passes
    -- to the per-engine setter), NOT the @@system_versioning_alter_history SYSTEM variable -- MySQL rejects a
    -- routine that merely mentions that MariaDB-only system variable at CREATE time (ERROR 1193, even in an
    -- unreachable branch; see STEP 2.96's note), which would break kindle on MySQL. When the opt-in is off
    -- this is skipped and the exclusion converges on a later deploy, like any exclusion change on a versioned
    -- table. SchemaSmith_SupportsSystemVersioning() is already 0 on MySQL, so this whole block is MariaDB-only.
    IF SchemaSmith_SupportsSystemVersioning() = 1 AND UPPER(COALESCE(@ss_system_versioning_alter_history, '')) = 'KEEP' AND p_WhatIf = 0 THEN
        BEGIN
            DECLARE v_ExclDone INT DEFAULT FALSE;
            DECLARE v_ExclSql TEXT;
            DECLARE cur_Excl CURSOR FOR
                SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                              ' MODIFY COLUMN ', c.ColumnScript)
                FROM _SchemaSmith_Columns c
                INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                INNER JOIN INFORMATION_SCHEMA.TABLES ist
                    ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                INNER JOIN INFORMATION_SCHEMA.COLUMNS isc
                    ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
                    AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                    AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
                WHERE t.NewTable = 0
                  AND c.IsWithoutSystemVersioning = 1
                  AND ist.TABLE_TYPE = 'SYSTEM VERSIONED'
                  AND isc.EXTRA NOT LIKE '%WITHOUT SYSTEM VERSIONING%';

            DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_ExclDone = TRUE;

            SET v_ExclDone = FALSE;
            OPEN cur_Excl;
            excl_loop: LOOP
                FETCH cur_Excl INTO v_ExclSql;
                IF v_ExclDone THEN LEAVE excl_loop; END IF;
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Apply WITHOUT SYSTEM VERSIONING: ', v_ExclSql));
                SET @exec_sql = v_ExclSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END LOOP;
            CLOSE cur_Excl;
        END;
    END IF;

    -- Update ProductOwnership for managed tables (non-WhatIf mode only)
    IF p_WhatIf = 0 THEN
        INSERT IGNORE INTO SchemaSmith_ProductOwnership (ProductName, TemplateName, ObjectSchema, ObjectType, ObjectName, PreventDrop)
        SELECT p_ProductName, '', p_DatabaseName, 'TABLE', SchemaSmith_StripBacktickWrapping(t.TableName), COALESCE(t.PreventDrop, 0)
        FROM _SchemaSmith_Tables t
        WHERE EXISTS (
            SELECT 1 FROM _SchemaSmith_ExistingTables ist
            WHERE BINARY ist.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        );

        -- INSERT IGNORE skips existing ownership rows, so a toggled PreventDrop would not take
        -- effect without this refresh UPDATE carrying the current per-table flag onto the row.
        UPDATE SchemaSmith_ProductOwnership po
          JOIN _SchemaSmith_Tables t
            ON CONVERT(po.ObjectName USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4)
         SET po.PreventDrop = COALESCE(t.PreventDrop, 0)
         WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
           AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
           AND po.ObjectType = 'TABLE';
    END IF;

    -- =======================
    -- No-drop protection tier (#270): when protected mode is active the caller forces
    -- p_DropTablesRemovedFromProduct to 0 so STEP 8 is skipped. Record the tables that WOULD have
    -- been dropped by absence (owned, present in the catalog, absent from the package, not sticky
    -- PreventDrop) to the ChangeAudit seam as 'dropSuppressed' so the run can surface a manifest. Same
    -- by-absence predicate as STEP 8; materialized first (INFORMATION_SCHEMA read out of the DML,
    -- Index-B crash-safety), then a discrete audit insert. Audit rows only, so it runs regardless
    -- of p_WhatIf.
    IF p_CaptureWouldDrop = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Capture tables suppressed by PreventDrop (would drop by absence)');
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropTables;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropTables (
            TableName VARCHAR(128) NOT NULL
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        INSERT INTO _SchemaSmith_WouldDropTables (TableName)
        SELECT po.ObjectName
        FROM SchemaSmith_ProductOwnership po
        WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
          AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
          AND po.ObjectType = 'TABLE'
          AND COALESCE(po.PreventDrop, 0) = 0
          AND EXISTS (
              SELECT 1 FROM _SchemaSmith_ExistingTables ist
              WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_Tables t
              WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
          );

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'table', CONCAT(p_DatabaseName, '.', TableName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropTables;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropTables;
    END IF;

    -- =======================
    -- STEP 8: DROP TABLES REMOVED FROM PRODUCT
    -- =======================
    -- Drop tables that are owned by this product but no longer in the definition
    IF p_DropTablesRemovedFromProduct = 1 THEN
        -- Data-loss guard: a partitioned table spreads data across partitions that DROP TABLE
        -- destroys outright. It fires whether the partitioning was DECLARED (#partitioning, K3) or added
        -- by hand after deployment -- and the hand-added case is still the common one, since partitioning
        -- usually happens once a table has grown -- so either way an ordinary product-owned table can end
        -- up looking like an ordinary drop-by-absence candidate.
        -- Fail closed before any DDL below, in both live and WhatIf mode, mirroring the
        -- UnsupportedFeaturePolicy=fail SIGNAL pattern used elsewhere in this proc. Table names are
        -- logged individually first (the SIGNAL MESSAGE_TEXT below stays well under MySQL's 128-char
        -- limit -- see STEP 4's comment on that limit).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT DISTINCT CONNECTION_ID(), CONCAT('  Partitioned table removed from product, not dropped (data-loss guard): ', po.ObjectName)
        FROM SchemaSmith_ProductOwnership po
        INNER JOIN INFORMATION_SCHEMA.PARTITIONS ip
            ON CONVERT(ip.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
           AND CONVERT(ip.TABLE_NAME USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
           AND ip.PARTITION_NAME IS NOT NULL
        WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
          AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
          AND po.ObjectType = 'TABLE'
          AND COALESCE(po.PreventDrop, 0) = 0
          AND EXISTS (
              SELECT 1 FROM _SchemaSmith_ExistingTables ist
              WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
          )
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_Tables t
              WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
          );

        IF ROW_COUNT() > 0 THEN
            SET @ss_msg = 'Partitioned table(s) skipped by drop-by-absence guard; drop manually or mark PreventDrop.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;

        -- #289: drop any foreign key that REFERENCES a table about to be removed BEFORE the table
        -- drop below, otherwise the DROP TABLE fails on a still-present inbound dependency.
        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop inbound foreign keys referencing tables removed from product');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT DISTINCT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                                   CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                   '` DROP FOREIGN KEY `', CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`')
            FROM SchemaSmith_ProductOwnership po
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON CONVERT(kcu.REFERENCED_TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
               AND CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
            INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                ON CONVERT(tc.TABLE_SCHEMA USING utf8mb4) = CONVERT(kcu.TABLE_SCHEMA USING utf8mb4)
               AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(kcu.TABLE_NAME USING utf8mb4)
               AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4)
               AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE'
              AND COALESCE(po.PreventDrop, 0) = 0
              AND EXISTS (
                  SELECT 1 FROM _SchemaSmith_ExistingTables ist
                  WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Tables t
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              );
        ELSE
            -- Helper temp table mirrors the column-drop FK pattern above (avoids the MySQL
            -- optimizer bug with INFORMATION_SCHEMA.KEY_COLUMN_USAGE joins), keyed on
            -- (TableName, ConstraintName) like SchemaSmith_ForeignKeyQuench's approach.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_InboundFKsToDrop;
            CREATE TEMPORARY TABLE _SchemaSmith_InboundFKsToDrop (
                TableName VARCHAR(128) NOT NULL,
                ConstraintName VARCHAR(128) NOT NULL,
                PRIMARY KEY (TableName, ConstraintName)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            INSERT IGNORE INTO _SchemaSmith_InboundFKsToDrop (TableName, ConstraintName)
            SELECT DISTINCT
                CONVERT(kcu.TABLE_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci
            FROM SchemaSmith_ProductOwnership po
            INNER JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON CONVERT(kcu.REFERENCED_TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
               AND CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
            INNER JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                ON CONVERT(tc.TABLE_SCHEMA USING utf8mb4) = CONVERT(kcu.TABLE_SCHEMA USING utf8mb4)
               AND CONVERT(tc.TABLE_NAME USING utf8mb4) = CONVERT(kcu.TABLE_NAME USING utf8mb4)
               AND CONVERT(tc.CONSTRAINT_NAME USING utf8mb4) = CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4)
               AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE'
              AND COALESCE(po.PreventDrop, 0) = 0
              AND EXISTS (
                  SELECT 1 FROM _SchemaSmith_ExistingTables ist
                  WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Tables t
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              );

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop inbound foreign keys referencing tables removed from product');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop inbound FK: ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName,
                          '` DROP FOREIGN KEY `', ConstraintName, '`')
            FROM _SchemaSmith_InboundFKsToDrop;

            -- Materialize: fold each table's inbound FK drops into one multi-clause ALTER, then drain.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_InboundFKDropStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_InboundFKDropStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_InboundFKDropStmts (Stmt)
            SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                          GROUP_CONCAT(CONCAT('DROP FOREIGN KEY `', ConstraintName, '`') ORDER BY ConstraintName SEPARATOR ', '))
            FROM _SchemaSmith_InboundFKsToDrop
            GROUP BY TableName;

            SET @v_inboundfk_id := (SELECT MIN(RowId) FROM _SchemaSmith_InboundFKDropStmts);
            WHILE @v_inboundfk_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_InboundFKDropStmts WHERE RowId = @v_inboundfk_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_inboundfk_id := (SELECT MIN(RowId) FROM _SchemaSmith_InboundFKDropStmts WHERE RowId > @v_inboundfk_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_InboundFKDropStmts;

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_InboundFKsToDrop;
        END IF;

        -- A CustomTableDrop hook (e.g. a recyclebin pattern) replaces the plain DROP TABLE when the
        -- user has installed a SchemaSmith_CustomTableDrop procedure in this database, mirroring the
        -- SQL Server / PostgreSQL hook.
        SET @has_custom_drop = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.ROUTINES
                                WHERE CONVERT(ROUTINE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                                  AND ROUTINE_NAME = 'SchemaSmith_CustomTableDrop'
                                  AND ROUTINE_TYPE = 'PROCEDURE');

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop tables removed from product');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(),
                   CASE WHEN @has_custom_drop = 1
                        THEN CONCAT('CALL SchemaSmith_CustomTableDrop(''', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, ''', ''', po.ObjectName, ''')')
                        ELSE CONCAT('DROP TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', po.ObjectName, '`')
                        END
            FROM SchemaSmith_ProductOwnership po
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE'
              AND COALESCE(po.PreventDrop, 0) = 0
              -- Table exists
              AND EXISTS (
                  SELECT 1 FROM _SchemaSmith_ExistingTables ist
                  WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              )
              -- Not in current definition
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Tables t
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              );

            -- #363: WhatIf twin of the ELSE-branch 'table'/'dropped' audit; same predicate, ObjectName
            -- is po.ObjectName (= _SchemaSmith_TablesToDrop.TableName in the real branch).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'table', po.ObjectName, 'wouldDrop'
            FROM SchemaSmith_ProductOwnership po
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE'
              AND COALESCE(po.PreventDrop, 0) = 0
              AND EXISTS (
                  SELECT 1 FROM _SchemaSmith_ExistingTables ist
                  WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Tables t
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              );
        ELSE
            -- Materialize tables to drop: table name + the exact per-table drop statement
            -- (CALL SchemaSmith_CustomTableDrop(...) when the hook is installed, else DROP TABLE).
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TablesToDrop;
            CREATE TEMPORARY TABLE _SchemaSmith_TablesToDrop (
                RowId INT AUTO_INCREMENT PRIMARY KEY,
                TableName VARCHAR(128) NOT NULL,
                DropSql TEXT NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

            INSERT INTO _SchemaSmith_TablesToDrop (TableName, DropSql)
            SELECT
                po.ObjectName,
                CASE WHEN @has_custom_drop = 1
                     THEN CONCAT('CALL SchemaSmith_CustomTableDrop(''', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, ''', ''', po.ObjectName, ''')')
                     ELSE CONCAT('DROP TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', po.ObjectName, '`')
                     END
            FROM SchemaSmith_ProductOwnership po
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE'
              AND COALESCE(po.PreventDrop, 0) = 0
              -- Table exists
              AND EXISTS (
                  SELECT 1 FROM _SchemaSmith_ExistingTables ist
                  WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              )
              -- Not in current definition
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Tables t
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4)
              );

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop tables removed from product');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Drop table: ', TableName)
            FROM _SchemaSmith_TablesToDrop;

            -- Object-change audit (#243 E5): one row per table about to be dropped (set-based over the
            -- computed _SchemaSmith_TablesToDrop temp; the drop below folds them into one statement).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'table', TableName, 'dropped'
            FROM _SchemaSmith_TablesToDrop;

            IF @has_custom_drop = 1 THEN
                -- The CustomTableDrop hook takes exactly one table per CALL, so multiple tables
                -- can't fold into a single statement here. Drain via WHILE (still removes the
                -- cursor, even though each row stays its own statement).
                SET @v_droptbl_id := (SELECT MIN(RowId) FROM _SchemaSmith_TablesToDrop);
                WHILE @v_droptbl_id IS NOT NULL DO
                    SELECT DropSql INTO @exec_sql FROM _SchemaSmith_TablesToDrop WHERE RowId = @v_droptbl_id;
                    PREPARE stmt FROM @exec_sql;
                    EXECUTE stmt;
                    DEALLOCATE PREPARE stmt;
                    SET @v_droptbl_id := (SELECT MIN(RowId) FROM _SchemaSmith_TablesToDrop WHERE RowId > @v_droptbl_id);
                END WHILE;
            ELSE
                -- Fold ALL tables to drop into ONE multi-target DROP TABLE statement.
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropTableFoldStmt;
                CREATE TEMPORARY TABLE _SchemaSmith_DropTableFoldStmt (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                    ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                INSERT INTO _SchemaSmith_DropTableFoldStmt (Stmt)
                SELECT CONCAT('DROP TABLE ',
                              GROUP_CONCAT(CONCAT('`', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`') ORDER BY TableName SEPARATOR ', '))
                FROM _SchemaSmith_TablesToDrop
                HAVING COUNT(*) > 0;

                SET @v_droptblfold_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropTableFoldStmt);
                WHILE @v_droptblfold_id IS NOT NULL DO
                    SELECT Stmt INTO @exec_sql FROM _SchemaSmith_DropTableFoldStmt WHERE RowId = @v_droptblfold_id;
                    PREPARE stmt FROM @exec_sql;
                    EXECUTE stmt;
                    DEALLOCATE PREPARE stmt;
                    SET @v_droptblfold_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropTableFoldStmt WHERE RowId > @v_droptblfold_id);
                END WHILE;
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropTableFoldStmt;
            END IF;

            -- Remove dropped tables (and their indexes) from ProductOwnership, set-based.
            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_TablesToDrop d
                ON CONVERT(po.ObjectName USING utf8mb4) = CONVERT(d.TableName USING utf8mb4)
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'TABLE';

            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_TablesToDrop d
                ON CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4) = CONVERT(d.TableName USING utf8mb4)
            WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
              AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
              AND po.ObjectType = 'INDEX';

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TablesToDrop;
        END IF;

        -- #270: report tables removed from the product but retained because PreventDrop is set.
        -- The protected table still exists and is still absent from _SchemaSmith_Tables in both the
        -- WhatIf and live paths (it was excluded from the drop candidate sets above), so this mirror
        -- set is correct either way. Discrete INFORMATION_SCHEMA read (Index-B crash-safety).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('Table ', po.ObjectName, ' removed from product but PreventDrop is set — skipping drop (protected)')
        FROM SchemaSmith_ProductOwnership po
        WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
          AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
          AND po.ObjectType = 'TABLE'
          AND COALESCE(po.PreventDrop, 0) = 1
          AND EXISTS (SELECT 1 FROM _SchemaSmith_ExistingTables ist
                       WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4))
          AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_Tables t
                            WHERE CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4));
    END IF;

    -- =======================
    -- STEP 9 (D5): CATALOG-RECONCILE PRUNE
    -- =======================
    -- Parity prune for tables dropped OUT of band (a migration or DBA dropped the physical table
    -- without going through SchemaSmith), so their ownership rows don't go stale. The STEP 8 cleanup
    -- only removes ownership for tables THIS run dropped, so protected tables keep ownership (intended).
    -- Live path only (WhatIf must not mutate ownership). Materialize the catalog read into a temp
    -- table first (a SELECT, not DML), then DELETE against the temp — matching the STEP 8 ELSE
    -- crash-safety convention so no INFORMATION_SCHEMA read runs inside DML (Index-B).
    IF p_WhatIf = 0 THEN
        -- Rebuild the existing-table snapshot to the POST-drop state: the prune must see tables STEP 8 just
        -- dropped (and any dropped out-of-band) as gone, so their ownership rows are reconciled here.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;
        CREATE TEMPORARY TABLE _SchemaSmith_ExistingTables (
            TableName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_ExistingTables (TableName)
        SELECT CONVERT(ist.TABLE_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.TABLES ist
        WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OrphanedOwnership;
        CREATE TEMPORARY TABLE _SchemaSmith_OrphanedOwnership (Id INT PRIMARY KEY)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_OrphanedOwnership (Id)
        SELECT po.Id
          FROM SchemaSmith_ProductOwnership po
          WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
            AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
            AND po.ObjectType = 'TABLE'
            AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_ExistingTables ist
                             WHERE CONVERT(ist.TableName USING utf8mb4) = CONVERT(po.ObjectName USING utf8mb4));
        DELETE po FROM SchemaSmith_ProductOwnership po
          JOIN _SchemaSmith_OrphanedOwnership o ON o.Id = po.Id;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_OrphanedOwnership;
    END IF;

    -- =========================================================================
    -- STEP 0.5: Degrade descending index key parts below MySQL 8.0 / MariaDB 10.8
    -- =========================================================================
    -- These engines parse-and-ignore a DESC key part (silently storing it ascending). There is no
    -- equivalent, so a declared DESC index is stored + compared as ascending (SchemaSmith_NormalizeIndexColumns
    -- drops the DESC suffix below the floor, so the create/compare steps below see an ascending index and stay
    -- idempotent). Record one 'downgraded' manifest row + a run-log line per affected index so the downgrade is
    -- visible, mirroring the CHECK-constraint degrade. At/above the floor this is a no-op.
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
    -- STEP 0.6: Degrade invisible indexes below MySQL 8.0 / MariaDB 10.6
    -- =========================================================================
    -- The INVISIBLE (MySQL) / IGNORED (MariaDB) keyword is a hard syntax error below these versions (unlike a
    -- DESC key part, which parses-and-ignores). A declared invisible index degrades: the visibility clause is
    -- suppressed (see the create pass) and the modified-index compare ignores the visibility difference so the
    -- deploy stays idempotent. 'fail' aborts naming the offending index(es); 'warn' (default) records one
    -- 'downgraded' manifest row + a run-log line per declared invisible index. At/above the floor this is a no-op.
    IF SchemaSmith_SupportsInvisibleIndex() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Indexes WHERE IsVisible = 0) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible index requires MySQL 8.0 / MariaDB 10.6 (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
            FROM _SchemaSmith_Indexes i WHERE i.IsVisible = 0;
            SET @ss_msg = 'Invisible index requires MySQL 8.0 / MariaDB 10.6 (UnsupportedFeaturePolicy=fail). See the run log for the full list.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible index stored visible (requires MySQL 8.0 / MariaDB 10.6 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
            FROM _SchemaSmith_Indexes i WHERE i.IsVisible = 0;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'INDEX (invisible, MySQL 8.0 / MariaDB 10.6)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName)), 'downgraded'
            FROM _SchemaSmith_Indexes i WHERE i.IsVisible = 0;
        END IF;
    END IF;

    -- =========================================================================
    -- STEP 0.7: Degrade functional/expression indexes below MySQL 8.0.13 (MariaDB: always)
    -- =========================================================================
    -- Unlike a DESC key part (parsed-and-ignored) or INVISIBLE (a suppressible clause), a functional/
    -- expression key part is a hard syntax error below the floor -- there is no reduced form to fall
    -- back to, so the whole index is skipped rather than degraded in place (same shape as
    -- SchemaSmith_SupportsDefaultExpression's column skip). MariaDB has no equivalent in this form at
    -- ANY version (SchemaSmith_SupportsFunctionalIndex() is unconditionally 0 there), so this block
    -- fires on MariaDB every time a functional index is declared, not just below some threshold -- the
    -- mirror image of SchemaSmith_SupportsDefaultExpression, which is unconditionally 1 on MariaDB. A
    -- multi-valued index (CAST(... AS ... ARRAY)) is a functional key part too and rides this same gate;
    -- see SchemaSmith_SupportsFunctionalIndex for why it needs no gate of its own. 'fail' aborts naming
    -- the offending index(es); 'warn' (default) records one 'downgraded' manifest row + a run-log line
    -- per declared functional index -- STEP 2 (modified-detect) and STEP 3 (create) below exclude it via
    -- the identical predicate, leaving any live index of the same name untouched.
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
    -- Detection snapshot: the live index picture read ONCE here into a temp table so STEP 1 (rename)
    -- and STEP 2 (modified) join it instead of re-reading INFORMATION_SCHEMA.STATISTICS per declared
    -- index. INFORMATION_SCHEMA is not a stored table on MySQL/MariaDB -- each access re-materialises
    -- server-wide metadata -- so the original per-row reads (a STATISTICS join plus a correlated
    -- GROUP_CONCAT column-list subquery, per declared index) cost (declared indexes x whole-server
    -- scans). This snapshot reflects the catalog BEFORE STEP 1's renames and STEP 2's drops, which is
    -- exactly the state both passes read: STEP 1 renames only where the column list already matches
    -- (so it never changes what STEP 2 compares), and STEP 2 excludes just-renamed indexes via
    -- _SchemaSmith_IndexRenames -- so the pre-mutation snapshot is equivalent to the live reads it
    -- replaces. STEP 3 (create-missing) needs the POST-drop state and takes its own later snapshot.
    -- One row per index; NormColumns is built by the same GROUP_CONCAT expression the correlated
    -- subqueries used, so composite index column lists compare byte-for-byte as before.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDetectSnap;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxDetectSnap (
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
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

    -- A names-only copy of the same snapshot. STEP 1 references the snapshot twice in one statement
    -- (the main join AND the "new index name doesn't exist" NOT EXISTS); MySQL/MariaDB forbid opening a
    -- TEMPORARY table twice in a single query (ER_CANT_REOPEN_TABLE 1137) -- the original could because
    -- it read INFORMATION_SCHEMA (not a temp) on both sides. The second reference reads this copy.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IdxDetectNames;
    CREATE TEMPORARY TABLE _SchemaSmith_IdxDetectNames (
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
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
        TableName VARCHAR(128) NOT NULL,
        OldIndexName VARCHAR(128) NOT NULL,
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

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RenameStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_RenameStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_RenameStmts (LogMsg, Stmt)
        SELECT
            CONCAT('  Rename index: ', TableName, '.', OldIndexName, ' -> ', NewIndexName),
            CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '` ',
                   SchemaSmith_BuildIndexRenameClause(p_DatabaseName, TableName, OldIndexName, NewIndexName))
        FROM _SchemaSmith_IndexRenames;

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_RenameStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt INTO @ss_log, @exec_sql FROM _SchemaSmith_RenameStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_RenameStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RenameStmts;

        -- Update ProductOwnership with the new names. Done set-based after the renames: the
        -- STEP 1 "new name doesn't exist" filter rules out rename chains (a->b where b is itself
        -- a live index), so no old name equals another rename's new name and one pass suffices.
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
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
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
      -- the degrade guard above) is left untouched rather than flagged modified-and-dropped: STEP 3 below
      -- excludes it from recreation via the identical predicate, so dropping it here would lose it for good.
      AND NOT (SchemaSmith_IndexHasFunctionalKeyPart(i.IndexColumns) = 1 AND SchemaSmith_SupportsFunctionalIndex() = 0)
      -- Check if definition differs
      AND (
          -- Columns differ
          BINARY SchemaSmith_NormalizeIndexColumns(i.IndexColumns) != BINARY snap.NormColumns
          -- Or uniqueness differs
          OR i.IsUnique != (snap.NonUnique = 0)
          -- Or visibility differs (FULLTEXT indexes don't support INVISIBLE, skip them). Below the
          -- invisible-index floor (MySQL 8.0 / MariaDB 10.6) the keyword can't be emitted, so a declared
          -- invisible index is stored visible; ignore the visibility difference there or it churns every run.
          -- viz.IsVisible is the once-snapshotted per-engine visibility (IS_VISIBLE / IGNORED), replacing
          -- the per-candidate SchemaSmith_IndexIsVisible() read; it is populated only at/above the floor,
          -- which is exactly when SchemaSmith_SupportsInvisibleIndex() = 1 gates this term.
          OR (BINARY UPPER(snap.IndexType) != BINARY 'FULLTEXT'
              AND SchemaSmith_SupportsInvisibleIndex() = 1
              AND i.IsVisible != viz.IsVisible)
          -- Or comment differs (symmetric: covers added, changed, and cleared, matching the column
          -- comment predicate in ModifiedTableQuench). FULLTEXT indexes never reach this file (parsed
          -- separately into _SchemaSmith_FullTextIndexes), so no FULLTEXT exclusion is needed here.
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

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropModIdxStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_DropModIdxStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_DropModIdxStmts (LogMsg, Stmt)
        SELECT
            CONCAT('  Drop and recreate index: ', TableName, '.', IndexName),
            CONCAT('DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
        FROM _SchemaSmith_ModifiedIndexes;

        SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropModIdxStmts);
        WHILE @ss_id IS NOT NULL DO
            SELECT LogMsg, Stmt INTO @ss_log, @exec_sql FROM _SchemaSmith_DropModIdxStmts WHERE RowId = @ss_id;
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropModIdxStmts WHERE RowId > @ss_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropModIdxStmts;
    END IF;

    -- =========================================================================
    -- No-drop protection tier (#270): capture indexes that WOULD have been dropped by absence but
    -- are suppressed. STEP 8 below is skipped entirely under protection (QuenchModifiedTables, the
    -- caller, forces both p_DropUnknownIndexes and p_DropIndexesRemovedFromProduct false), so its _SchemaSmith_Step8Idx
    -- snapshot is never built; this block is self-contained — it snapshots the catalog
    -- (INFORMATION_SCHEMA out of the set-based DML, #337 crash-safety), then computes BOTH STEP 8
    -- axes' by-absence candidates MINUS their env gates into one temp — AXIS 1 removed-from-product
    -- (keeps the per-table COALESCE(t.DropIndexesRemovedFromProduct,1) opt-out, PRIMARY/rename/
    -- owned-by-product exclusions) UNION AXIS 2 unknown/out-of-band — then a discrete audit insert.
    -- Modified/for-change indexes are NOT captured: they remain in _SchemaSmith_Indexes so both axes'
    -- "not in current definition" predicate excludes them (ModifiedTableQuench drops-then-recreates them, a
    -- transient change, never a protection-withheld drop). Audit rows only, so it runs regardless of
    -- p_WhatIf. The capture signal is the session user-variable @ss_capture_would_drop set by the
    -- caller on the connection (this proc takes no new parameter). ObjectName/ObjectType mirror STEP
    -- 8's 'index'/'dropped' audit: ObjectType 'index', ObjectName CONCAT(TableName, '.', IndexName).
    IF COALESCE(@ss_capture_would_drop, 0) = 1 THEN
        -- Catalog snapshot (one row per index; SEQ_IN_INDEX = 1). Mirrors STEP 8's _SchemaSmith_Step8Idx.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8IdxCat;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropStep8IdxCat (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_WouldDropStep8IdxCat (TableName, IndexName)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.SEQ_IN_INDEX = 1;

        -- Defined-index snapshot (table.index pairs from the current definition). This is the
        -- "not in current definition" bridge and is exactly what keeps modified indexes (still in
        -- _SchemaSmith_Indexes) out of the capture set.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8DefIdx;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropStep8DefIdx (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_WouldDropStep8DefIdx (TableName, IndexName)
        SELECT CONVERT(SchemaSmith_StripBacktickWrapping(i.TableName) USING utf8mb4),
               CONVERT(SchemaSmith_StripBacktickWrapping(i.IndexName) USING utf8mb4)
        FROM _SchemaSmith_Indexes i;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8Indexes;
        CREATE TEMPORARY TABLE _SchemaSmith_WouldDropStep8Indexes (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- AXIS 1 — removed-from-product (env gate p_DropIndexesRemovedFromProduct dropped; per-table opt-out kept).
        INSERT IGNORE INTO _SchemaSmith_WouldDropStep8Indexes (TableName, IndexName)
        SELECT DISTINCT
            SUBSTRING_INDEX(po.ObjectName, '.', 1),
            SUBSTRING_INDEX(po.ObjectName, '.', -1)
        FROM SchemaSmith_ProductOwnership po
        INNER JOIN _SchemaSmith_Tables t
            ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
        LEFT JOIN _SchemaSmith_WouldDropStep8DefIdx di
            ON CONVERT(di.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
        LEFT JOIN _SchemaSmith_WouldDropStep8IdxCat ei
            ON CONVERT(ei.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            AND CONVERT(ei.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
        WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
          AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci
          -- Per-table tightening (the env/product gate is removed for capture)
          AND COALESCE(t.DropIndexesRemovedFromProduct, 1) = 1
          -- Never drop PRIMARY KEY
          AND UPPER(SUBSTRING_INDEX(po.ObjectName, '.', -1) COLLATE utf8mb4_unicode_ci) != _utf8mb4'PRIMARY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition (LEFT JOIN produces NULL when not found) — also excludes modified indexes
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
        INSERT IGNORE INTO _SchemaSmith_WouldDropStep8Indexes (TableName, IndexName)
        SELECT CONVERT(ei.TableName USING utf8mb4), CONVERT(ei.IndexName USING utf8mb4)
        FROM _SchemaSmith_WouldDropStep8IdxCat ei
        INNER JOIN _SchemaSmith_Tables t
            ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
        WHERE UPPER(ei.IndexName COLLATE utf8mb4_unicode_ci) != _utf8mb4'PRIMARY' COLLATE utf8mb4_unicode_ci
          -- Not in current definition — also excludes modified indexes (still present in _SchemaSmith_Indexes)
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_WouldDropStep8DefIdx di
              WHERE CONVERT(di.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                AND CONVERT(di.IndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
          )
          -- Not owned by this product
          AND NOT EXISTS (
              SELECT 1 FROM SchemaSmith_ProductOwnership po
              WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci
                AND po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(ei.TableName, '.', ei.IndexName) COLLATE utf8mb4_unicode_ci
          )
          -- Not a renamed index
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_IndexRenames r
              WHERE CONVERT(r.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                AND CONVERT(r.OldIndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
          );

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'index', CONCAT(TableName, '.', IndexName), 'dropSuppressed'
        FROM _SchemaSmith_WouldDropStep8Indexes;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8Indexes;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8DefIdx;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_WouldDropStep8IdxCat;
    END IF;

    -- =========================================================================
    -- STEP 8: Drop indexes not in the definition — two disjoint axes, matching SQL Server / PostgreSQL:
    --   * removed-from-product (owned by this product, no longer in the JSON) — gated by
    --     p_DropIndexesRemovedFromProduct (default on) + per-table tightening.
    --   * out-of-band (in the catalog, NOT owned by this product) — gated by p_DropUnknownIndexes
    --     (default off).
    -- =========================================================================
    IF p_DropUnknownIndexes = 1 OR p_DropIndexesRemovedFromProduct = 1 THEN
        -- Crash-safe snapshot for STEP 8 ONLY. Taken HERE, after this procedure's own column work and
        -- its index rename / modified-index drops, so it reflects the current catalog state at this
        -- point. Index CREATION now runs later, in MissingIndexesAndConstraintsQuench -- the one
        -- ordering difference from before this block moved here. It is benign: an index that creation
        -- would add is present in the declared definition, so both axes below exclude it either way.
        -- STEP 8's ProductOwnership x STATISTICS detection is the frequent-run segfault trigger
        -- the roadmap identifies, so it (and its FK-drop join) read these snapshots instead of live
        -- INFORMATION_SCHEMA. SEQ_IN_INDEX = 1 yields one row per index (NON_UNIQUE is index-level),
        -- collapsing the original LEFT JOIN s + EXISTS s2 into a single snapshot reference (no 1137).
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8Idx;
        CREATE TEMPORARY TABLE _SchemaSmith_Step8Idx (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            NonUnique TINYINT DEFAULT 0,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_Step8Idx (TableName, IndexName, NonUnique)
        SELECT CONVERT(s.TABLE_NAME USING utf8mb4), CONVERT(s.INDEX_NAME USING utf8mb4), s.NON_UNIQUE
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE BINARY s.TABLE_SCHEMA = BINARY p_DatabaseName
          AND s.SEQ_IN_INDEX = 1;

        -- FK rows referencing a product table (for the FK-before-index drop join). Same-schema FKs.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8KCU;
        CREATE TEMPORARY TABLE _SchemaSmith_Step8KCU (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            ReferencedTableName VARCHAR(128) DEFAULT NULL,
            KEY ix_s8kcu (ReferencedTableName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_Step8KCU (TableName, ConstraintName, ReferencedTableName)
        SELECT CONVERT(kcu.TABLE_NAME USING utf8mb4), CONVERT(kcu.CONSTRAINT_NAME USING utf8mb4), CONVERT(kcu.REFERENCED_TABLE_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
        WHERE BINARY kcu.REFERENCED_TABLE_SCHEMA = BINARY p_DatabaseName
          AND BINARY kcu.TABLE_SCHEMA = BINARY p_DatabaseName
          AND kcu.REFERENCED_TABLE_NAME IS NOT NULL;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8TC;
        CREATE TEMPORARY TABLE _SchemaSmith_Step8TC (
            TableName VARCHAR(128) NOT NULL,
            ConstraintName VARCHAR(128) NOT NULL,
            KEY ix_s8tc (TableName, ConstraintName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_Step8TC (TableName, ConstraintName)
        SELECT CONVERT(tc.TABLE_NAME USING utf8mb4), CONVERT(tc.CONSTRAINT_NAME USING utf8mb4)
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        WHERE BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
          AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY';

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexesToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_IndexesToDrop (
            TableName VARCHAR(128) NOT NULL,
            IndexName VARCHAR(128) NOT NULL,
            IsUnique TINYINT DEFAULT 0,
            PRIMARY KEY (TableName, IndexName)
        ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

        -- AXIS 1 — removed-from-product: indexes OWNED by this product but no longer in the
        -- definition. Gated by p_DropIndexesRemovedFromProduct (env/product) + per-table tightening.
        -- IMPORTANT: Only consider indexes on tables that ARE in the current definition (_SchemaSmith_Tables)
        -- so an index on a table not in the current JSON is never dropped.
        -- Reads _SchemaSmith_Step8Idx (single LEFT JOIN): existence == ei.IndexName IS NOT NULL,
        -- IsUnique == (ei.NonUnique = 0) -- collapses the original STATISTICS s + s2 references.
        IF p_DropIndexesRemovedFromProduct = 1 THEN
            INSERT INTO _SchemaSmith_IndexesToDrop (TableName, IndexName, IsUnique)
            SELECT
                SUBSTRING_INDEX(po.ObjectName, '.', 1) AS TableName,
                SUBSTRING_INDEX(po.ObjectName, '.', -1) AS IndexName,
                COALESCE(ei.NonUnique = 0, 0) AS IsUnique
            FROM SchemaSmith_ProductOwnership po
            -- Join with _SchemaSmith_Tables to only consider indexes on tables in the current definition
            INNER JOIN _SchemaSmith_Tables t
                ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
            LEFT JOIN _SchemaSmith_Step8Idx ei
                ON CONVERT(ei.TableName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(ei.IndexName USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
            LEFT JOIN _SchemaSmith_Indexes i
                ON CONVERT(SchemaSmith_StripBacktickWrapping(i.TableName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', 1) USING utf8mb4)
                AND CONVERT(SchemaSmith_StripBacktickWrapping(i.IndexName) USING utf8mb4) = CONVERT(SUBSTRING_INDEX(po.ObjectName, '.', -1) USING utf8mb4)
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci
              -- Per-table tightening (the env/product gate is the enclosing IF)
              AND COALESCE(t.DropIndexesRemovedFromProduct, 1) = 1
              -- Never drop PRIMARY KEY
              AND UPPER(SUBSTRING_INDEX(po.ObjectName, '.', -1) COLLATE utf8mb4_unicode_ci) != _utf8mb4'PRIMARY' COLLATE utf8mb4_unicode_ci
              -- Not in current definition (LEFT JOIN produces NULL when not found)
              AND i.IndexName IS NULL
              -- Not a renamed index
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_IndexRenames r
                  WHERE r.TableName = SUBSTRING_INDEX(po.ObjectName, '.', 1)
                    AND r.OldIndexName = SUBSTRING_INDEX(po.ObjectName, '.', -1)
              )
              -- Verify index actually exists
              AND ei.IndexName IS NOT NULL;
        END IF;

        -- AXIS 2 — out-of-band: an index present in the catalog on a current-quench table that is
        -- NOT in the definition AND NOT owned by this product (e.g. hand-created via DDL). Gated by
        -- p_DropUnknownIndexes. Crash-safe: reads the Step8Idx snapshot + temp/real tables only —
        -- never INFORMATION_SCHEMA in this set-based DML (#337). INSERT IGNORE dedupes against any
        -- removed-from-product row already queued (shared PK on _SchemaSmith_IndexesToDrop).
        IF p_DropUnknownIndexes = 1 THEN
            INSERT IGNORE INTO _SchemaSmith_IndexesToDrop (TableName, IndexName, IsUnique)
            SELECT CONVERT(ei.TableName USING utf8mb4), CONVERT(ei.IndexName USING utf8mb4), COALESCE(ei.NonUnique = 0, 0)
            FROM _SchemaSmith_Step8Idx ei
            INNER JOIN _SchemaSmith_Tables t
                ON CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
            WHERE UPPER(ei.IndexName COLLATE utf8mb4_unicode_ci) != _utf8mb4'PRIMARY' COLLATE utf8mb4_unicode_ci
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_Indexes i
                  WHERE CONVERT(SchemaSmith_StripBacktickWrapping(i.TableName) USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                    AND CONVERT(SchemaSmith_StripBacktickWrapping(i.IndexName) USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
              )
              AND NOT EXISTS (
                  SELECT 1 FROM SchemaSmith_ProductOwnership po
                  WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                    AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
                    AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci
                    AND po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(ei.TableName, '.', ei.IndexName) COLLATE utf8mb4_unicode_ci
              )
              AND NOT EXISTS (
                  SELECT 1 FROM _SchemaSmith_IndexRenames r
                  WHERE CONVERT(r.TableName USING utf8mb4) = CONVERT(ei.TableName USING utf8mb4)
                    AND CONVERT(r.OldIndexName USING utf8mb4) = CONVERT(ei.IndexName USING utf8mb4)
              );
        END IF;

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown indexes');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`')
            FROM _SchemaSmith_IndexesToDrop;

            -- #363: WhatIf twin of the ELSE-branch 'index'/'dropped' audit; same source + AuditName form.
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'index', CONCAT(TableName, '.', IndexName), 'wouldDrop'
            FROM _SchemaSmith_IndexesToDrop;
        ELSE
            -- First, drop any foreign keys that reference unique indexes we're about to drop.
            -- FK drops MUST complete before the index drops below (a FK backed by a unique index
            -- blocks dropping that index); these are two sequential loops, preserving that order.
            -- Reads the STEP 8 snapshots (Step8Idx existence bridge, Step8KCU, Step8TC).
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropFKForIdxStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_DropFKForIdxStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            -- DISTINCT: one DROP FOREIGN KEY per FK. A composite FK yields multiple KEY_COLUMN_USAGE
            -- rows (one per column), and several dropped indexes on the same referenced table re-join
            -- the same FK; both would otherwise emit a duplicate DROP FOREIGN KEY (error 1091 on the
            -- second). The original per-row cursor had the same latent duplication.
            INSERT INTO _SchemaSmith_DropFKForIdxStmts (LogMsg, Stmt)
            SELECT DISTINCT
                CONCAT('  Drop FK for index: ',
                       CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', kcu.TableName, '` DROP FOREIGN KEY `', tc.ConstraintName, '`')),
                CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', kcu.TableName, '` DROP FOREIGN KEY `', tc.ConstraintName, '`')
            FROM _SchemaSmith_IndexesToDrop itd
            JOIN _SchemaSmith_Step8Idx ei
                ON CONVERT(ei.TableName USING utf8mb4) = CONVERT(itd.TableName USING utf8mb4)
                AND CONVERT(ei.IndexName USING utf8mb4) = CONVERT(itd.IndexName USING utf8mb4)
            JOIN _SchemaSmith_Step8KCU kcu
                ON CONVERT(kcu.ReferencedTableName USING utf8mb4) = CONVERT(itd.TableName USING utf8mb4)
            JOIN _SchemaSmith_Step8TC tc
                ON CONVERT(tc.TableName USING utf8mb4) = CONVERT(kcu.TableName USING utf8mb4)
                AND CONVERT(tc.ConstraintName USING utf8mb4) = CONVERT(kcu.ConstraintName USING utf8mb4)
            WHERE itd.IsUnique = 1;

            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropFKForIdxStmts);
            WHILE @ss_id IS NOT NULL DO
                SELECT LogMsg, Stmt INTO @ss_log, @exec_sql FROM _SchemaSmith_DropFKForIdxStmts WHERE RowId = @ss_id;
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropFKForIdxStmts WHERE RowId > @ss_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropFKForIdxStmts;

            -- Now drop the unknown indexes
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Drop unknown indexes');

            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropIdxStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_DropIdxStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, LogMsg TEXT, Stmt TEXT, AuditName TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_DropIdxStmts (LogMsg, Stmt, AuditName)
            SELECT
                CONCAT('  Drop unknown index: ', TableName, '.', IndexName),
                CONCAT('DROP INDEX `', IndexName, '` ON `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', TableName, '`'),
                CONCAT(TableName, '.', IndexName)
            FROM _SchemaSmith_IndexesToDrop;

            SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropIdxStmts);
            WHILE @ss_id IS NOT NULL DO
                SELECT LogMsg, Stmt, AuditName INTO @ss_log, @exec_sql, @ss_auditname FROM _SchemaSmith_DropIdxStmts WHERE RowId = @ss_id;
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), @ss_log);
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
                INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'index', @ss_auditname, 'dropped');
                DEALLOCATE PREPARE stmt;
                SET @ss_id := (SELECT MIN(RowId) FROM _SchemaSmith_DropIdxStmts WHERE RowId > @ss_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_DropIdxStmts;

            -- Remove dropped indexes from ProductOwnership
            DELETE po FROM SchemaSmith_ProductOwnership po
            INNER JOIN _SchemaSmith_IndexesToDrop itd
                ON po.ObjectName COLLATE utf8mb4_unicode_ci = CONCAT(itd.TableName, '.', itd.IndexName) COLLATE utf8mb4_unicode_ci
            WHERE po.ProductName COLLATE utf8mb4_unicode_ci = CONVERT(p_ProductName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectSchema COLLATE utf8mb4_unicode_ci = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci
              AND po.ObjectType COLLATE utf8mb4_unicode_ci = _utf8mb4'INDEX' COLLATE utf8mb4_unicode_ci;
        END IF;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_IndexesToDrop;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8Idx;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8KCU;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Step8TC;
    END IF;

    -- =======================
    -- STEP 9: ADD DECLARED APPLICATION-TIME PERIODS TO EXISTING TABLES
    -- =======================
    -- MariaDB `ALTER TABLE ... ADD PERIOD FOR <name>(start, end)`. A new table gets its periods inside
    -- the CREATE (MissingTableAndColumnQuench); this is the existing-table half.
    --
    -- WHY LAST: a period can only be declared once its columns exist, and by this point every column
    -- pass in this procedure has run. The ordering rule is satisfied by placement rather than by a
    -- check that could drift out of step with it.
    --
    -- GATED ON 11.4, NOT 10.4.3, and the difference is the whole reason this is careful. Deciding
    -- whether to ADD requires knowing whether the period is already present, and
    -- INFORMATION_SCHEMA.PERIODS -- the only catalog that can answer -- does not arrive until 11.4.
    -- Below that SchemaSmith_TablePeriodsJson returns '[]' because it genuinely cannot see, and acting
    -- on that would emit ADD PERIOD on every deploy for a period that already exists and fail the run.
    -- So below 11.4 an existing table is left alone and the reason is logged, rather than guessed at.
    --
    -- MySQL never reaches this: it has no periods, so _SchemaSmith_Periods is always empty there.
    -- Enters when there is period work of EITHER kind. The declared-periods test alone was not enough:
    -- removing a table's only period leaves _SchemaSmith_Periods empty for it, and MariaDB permits at
    -- most one period per table, so gating solely on "something is declared" made the drop unreachable
    -- in exactly the case it exists for.
    -- Counted in TWO statements, not one OR-ed EXISTS. MySQL and MariaDB refuse to reference the same
    -- TEMPORARY table twice in a single statement -- "Can't reopen table" (1137) -- and both arms need
    -- _SchemaSmith_Tables. Folding them into one expression cost 37 tests across the suite.
    SET @ss_pd_work = (SELECT COUNT(*) FROM _SchemaSmith_Periods pd
                       INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
                       WHERE COALESCE(t.NewTable, 0) = 0);
    SET @ss_pd_work = COALESCE(@ss_pd_work, 0) + (SELECT COUNT(*) FROM _SchemaSmith_Tables t
                       WHERE COALESCE(t.NewTable, 0) = 0
                         AND COALESCE(t.DropPeriodsRemovedFromProduct, @ss_drop_periods_removed, 0) = 1);

    -- Enters when there is period work of EITHER kind. The declared-periods test alone was not enough:
    -- removing a table's only period leaves _SchemaSmith_Periods empty for it, and MariaDB permits at
    -- most one period per table, so gating solely on "something is declared" made the drop unreachable
    -- in exactly the case it exists for.
    IF COALESCE(@ss_pd_work, 0) > 0 THEN

        IF VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 1104 THEN

            -- ---- DROP a period the package no longer declares -------------------------------------
            -- Off unless asked for. Extraction OMITS the Periods key when a table has none, so a package
            -- authored before periods existed -- or extracted from 10.4.3-11.3, where the catalog cannot
            -- report them -- reads as "no periods declared" while the table plainly has one. Dropping on
            -- that absence would remove a declaration the package never had the chance to make, which is
            -- why this is the one drop-by-absence flag that defaults to FALSE.
            --
            -- Ordered before the ADD below so a period whose COLUMNS changed is replaced in a single
            -- deploy -- drop then add -- rather than the add colliding with the period already there.
            --
            -- Not data-destructive, and that is verified rather than assumed: on 11.4.12 DROP PERIOD FOR
            -- leaves every column in place and takes only the period and its backing check constraint.
            -- What is lost is the temporal semantics, not rows and not columns.
            IF EXISTS (SELECT 1 FROM _SchemaSmith_Tables t2
                       WHERE COALESCE(t2.NewTable, 0) = 0
                         -- Table tier wins over the environment tier, nearest declaration first --
                         -- the same shape as every other drop flag. Neither set means off.
                         AND COALESCE(t2.DropPeriodsRemovedFromProduct, @ss_drop_periods_removed, 0) = 1) THEN
                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PeriodsToDrop;
                CREATE TEMPORARY TABLE _SchemaSmith_PeriodsToDrop (
                    RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
                    TableName VARCHAR(128) NOT NULL,
                    PeriodName VARCHAR(128) NOT NULL
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

                -- Only tables the package contains. There is deliberately NO "and the package already
                -- declares a period" guard: MariaDB permits at most ONE application-time period per
                -- table (error 4154), so such a guard would make the drop impossible to ever fire --
                -- removing the only period leaves nothing to satisfy it. The FLAG is the safety here,
                -- not a shape test: it is off by default, so a package that predates periods is
                -- untouched, and turning it on is the operator saying the package is authoritative.
                -- DYNAMIC, and not for style: JSON_TABLE is MySQL 8.0.4+ / MariaDB 10.6+, and a stored
                -- procedure that so much as MENTIONS it fails to CREATE below those versions -- MySQL 5.7
                -- and MariaDB 10.2 are both supported floors, and both took the whole kindle down with a
                -- parser error before this was deferred. The runtime 11.4 gate above never gets a chance:
                -- parsing happens at CREATE. Every other JSON_TABLE in this codebase was already replaced
                -- with a version-agnostic aggregation for exactly this reason (see BootstrapTableQuench
                -- and ParseTableJson); this one is gated to 11.4 anyway, so deferring the parse to EXECUTE
                -- is enough and keeps the set-based shape.
                SET @ss_pdd_sql = CONCAT(
                    'INSERT INTO _SchemaSmith_PeriodsToDrop (TableName, PeriodName) ',
                    'SELECT t.TableName, ',
                    '       SchemaSmith_SafeBacktickWrap(JSON_UNQUOTE(JSON_EXTRACT(live.PeriodJson, ''$.Name''))) ',
                    'FROM _SchemaSmith_Tables t ',
                    'JOIN JSON_TABLE( ',
                    '        SchemaSmith_TablePeriodsJson(', QUOTE(p_DatabaseName), ', ',
                    '            SchemaSmith_StripBacktickWrapping(t.TableName)), ',
                    '        ''$[*]'' COLUMNS (PeriodJson JSON PATH ''$'')) live ',
                    'WHERE COALESCE(t.NewTable, 0) = 0 ',
                    '  AND COALESCE(t.DropPeriodsRemovedFromProduct, @ss_drop_periods_removed, 0) = 1 ',
                    '  AND NOT EXISTS ( ',
                    '      SELECT 1 FROM _SchemaSmith_Periods d ',
                    '      WHERE d.TableName = t.TableName ',
                    '        AND BINARY SchemaSmith_StripBacktickWrapping(d.PeriodName) ',
                    '          = BINARY JSON_UNQUOTE(JSON_EXTRACT(live.PeriodJson, ''$.Name'')))');
                PREPARE ss_pdd_stmt FROM @ss_pdd_sql;
                EXECUTE ss_pdd_stmt;
                DEALLOCATE PREPARE ss_pdd_stmt;

                SET @ss_pdd_id := (SELECT MIN(RowId) FROM _SchemaSmith_PeriodsToDrop);
                WHILE @ss_pdd_id IS NOT NULL DO
                    SELECT TableName, PeriodName INTO @ss_pdd_table, @ss_pdd_name
                      FROM _SchemaSmith_PeriodsToDrop WHERE RowId = @ss_pdd_id;

                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Dropping application-time period removed from product: ',
                            SchemaSmith_StripBacktickWrapping(@ss_pdd_table), '.',
                            SchemaSmith_StripBacktickWrapping(@ss_pdd_name)));

                    IF p_WhatIf = 0 THEN
                        -- DROP PERIOD FOR, not the DROP PERIOD the engine's own 4158 message suggests --
                        -- that shorthand does not parse.
                        SET @ss_pdd_stmt = CONCAT('ALTER TABLE `', p_DatabaseName, '`.', @ss_pdd_table,
                                                  ' DROP PERIOD FOR ', @ss_pdd_name);
                        PREPARE ss_pdd_exec FROM @ss_pdd_stmt;
                        EXECUTE ss_pdd_exec;
                        DEALLOCATE PREPARE ss_pdd_exec;

                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'PERIOD',
                                CONCAT(SchemaSmith_StripBacktickWrapping(@ss_pdd_table), '.',
                                       SchemaSmith_StripBacktickWrapping(@ss_pdd_name)), 'dropped');
                    END IF;

                    SET @ss_pdd_id := (SELECT MIN(RowId) FROM _SchemaSmith_PeriodsToDrop WHERE RowId > @ss_pdd_id);
                END WHILE;

                DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_PeriodsToDrop;
            END IF;

            SET @ss_pd_done = 0;
            SET @ss_pd_id := (SELECT MIN(pd.RowId) FROM _SchemaSmith_Periods pd
                              INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
                              WHERE COALESCE(t.NewTable, 0) = 0);
            WHILE @ss_pd_id IS NOT NULL DO
                SELECT pd.TableName, pd.PeriodName, pd.StartColumn, pd.EndColumn
                  INTO @ss_pd_table, @ss_pd_name, @ss_pd_start, @ss_pd_end
                  FROM _SchemaSmith_Periods pd WHERE pd.RowId = @ss_pd_id;

                -- Present already? Compared against the live catalog through the same reader extraction
                -- uses, so the two can never disagree about what "already there" means.
                IF JSON_SEARCH(SchemaSmith_TablePeriodsJson(p_DatabaseName,
                                   SchemaSmith_StripBacktickWrapping(@ss_pd_table)),
                               'one', SchemaSmith_StripBacktickWrapping(@ss_pd_name),
                               NULL, '$[*].Name') IS NULL THEN
                    SET @ss_pd_sql = CONCAT('ALTER TABLE `', p_DatabaseName, '`.', @ss_pd_table,
                                            ' ADD PERIOD FOR ', @ss_pd_name,
                                            '(', @ss_pd_start, ', ', @ss_pd_end, ')');
                    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                    VALUES (CONNECTION_ID(), CONCAT('  Adding application-time period: ',
                            SchemaSmith_StripBacktickWrapping(@ss_pd_table), '.',
                            SchemaSmith_StripBacktickWrapping(@ss_pd_name)));

                    IF p_WhatIf = 0 THEN
                        SET @ss_pd_stmt = @ss_pd_sql;
                        PREPARE ss_pd_exec FROM @ss_pd_stmt;
                        EXECUTE ss_pd_exec;
                        DEALLOCATE PREPARE ss_pd_exec;

                        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                        VALUES (CONNECTION_ID(), 'PERIOD',
                                CONCAT(SchemaSmith_StripBacktickWrapping(@ss_pd_table), '.',
                                       SchemaSmith_StripBacktickWrapping(@ss_pd_name)), 'added');
                    END IF;
                END IF;

                SET @ss_pd_id := (SELECT MIN(pd.RowId) FROM _SchemaSmith_Periods pd
                                  INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
                                  WHERE COALESCE(t.NewTable, 0) = 0 AND pd.RowId > @ss_pd_id);
            END WHILE;
        ELSE
            -- Not a degrade row: nothing was suppressed and nothing was lost. The period may well
            -- already be correct; this server simply cannot be asked, so convergence is declined rather
            -- than attempted blind.
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Application-time period on an existing table not reconciled '
                   '(needs MariaDB 11.4 to read the current state): ',
                   SchemaSmith_StripBacktickWrapping(pd.TableName), '.',
                   SchemaSmith_StripBacktickWrapping(pd.PeriodName))
            FROM _SchemaSmith_Periods pd
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
            WHERE COALESCE(t.NewTable, 0) = 0;
        END IF;
    END IF;


END//

DELIMITER ;
