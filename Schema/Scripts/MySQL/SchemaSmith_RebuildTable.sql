-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

-- Replaces one table with a table built to the DECLARED definition, carrying its rows across:
-- refuse-if-blocked, capture the AUTO_INCREMENT counter, create a shadow, copy, reseed, swap, drop the
-- inbound foreign keys, drop the old one. Nothing in here decides WHEN a rebuild should happen -- the
-- caller decides that and calls this; the procedure is also directly callable, which is what makes it
-- testable before any decision path exists.
--
-- Deliberately NOT this procedure's job: secondary indexes, unique/check constraints, foreign keys, and
-- the table attributes the convergence passes own (ROW_FORMAT, COMMENT, the declared AUTO_INCREMENT
-- seed). The old table is dropped whole, which takes all of them with it, and the ordinary quench passes
-- that follow re-add them from the same JSON that produced _SchemaSmith_Columns. Re-adding them here
-- would duplicate that logic against a second source of truth, so the surface stays small on purpose and
-- the one thing this procedure owns is the DATA.
--
-- The ONE exception, and it is engine-forced rather than chosen: the shadow's CREATE carries the declared
-- PRIMARY KEY, because MySQL and MariaDB refuse a CREATE TABLE whose AUTO_INCREMENT column is not part of
-- a key. The SQL Server and PostgreSQL twins have no such constraint and emit no key at all. See section
-- 6 for the full reasoning and why it is not a second source of truth.
--
-- Reads the declared definition from _SchemaSmith_Columns / _SchemaSmith_Tables, so it MUST be called
-- after SchemaSmith_ParseTableJson has run on the same CONNECTION -- the same contract every quench
-- procedure already has (MySQL TEMPORARY tables are session-scoped). Called with no parse in scope it
-- refuses rather than reading an absent working set. Those temp tables carry no schema column, because a
-- parse is per-DATABASE on MySQL, so p_Schema must name the database that parse ran for; that is a caller
-- contract this procedure has no way to verify.
--
-- On MySQL schema == database, so p_Schema IS the database name -- the same value the sibling quench
-- procedures take as p_DatabaseName -- and every identifier below is qualified `p_Schema`.`name`.
--
-- =====================================================================================================
-- ATOMICITY -- READ THIS BEFORE CHANGING THE ORDER OF ANYTHING BELOW.
--
-- MySQL and MariaDB DDL is NOT transactional. START TRANSACTION / ROLLBACK around a CREATE TABLE leaves
-- the table behind, and every DDL statement issues an implicit COMMIT that also releases any row locks
-- taken before it. The single-transaction protection the SQL Server and PostgreSQL twins rely on -- a
-- mid-failure leaving the original untouched under its own name -- DOES NOT EXIST HERE, and this file
-- deliberately does not open a transaction and call it a rollback guarantee. A reader arriving from
-- either of those engines must not assume a rollback they are not getting.
--
-- Two things make the operation workable instead, and the step order is built on both:
--
--   1. THE SWAP IS ATOMIC IN ONE STATEMENT. RENAME TABLE t TO t_old, t_shadow TO t swaps both names
--      atomically, so there is never an instant where the application's table name resolves to nothing --
--      which is precisely the window the other engines' transaction exists to close.
--
--   2. BECAUSE THE SWAP IS ATOMIC, THE DESTRUCTIVE STEP MOVES AFTER IT. On SQL Server and PostgreSQL the
--      inbound foreign keys must come out BEFORE the swap because the rename is not atomic with anything.
--      Here the reversible work goes first, so the irreversible part is as short as possible.
--
-- WHAT A MID-FAILURE LEAVES BEHIND, STEP BY STEP -- said out loud precisely because the other two
-- engines do give a rollback and this one cannot:
--
--   Step 1 (create shadow / copy / reseed) -- FULLY REVERSIBLE. A failure strands the shadow table and
--     nothing else. The original is untouched under its own name with all of its rows, its inbound
--     foreign keys, its indexes and its constraints. The stranded shadow is NOT auto-dropped: the
--     shadow/old name-collision refusal below catches it on the next run and makes a human look at it,
--     which is the same judgement the collision refusal already makes about any leftover.
--
--   Step 2 (RENAME TABLE t TO t_old, t_shadow TO t) -- ATOMIC. It either happens or it does not.
--
--   Step 3 (drop the inbound foreign keys, then DROP TABLE t_old) -- a failure here leaves the LIVE table
--     already correct and complete under its own name with all of the data, plus a parked t_old holding
--     the pre-rebuild rows. No data is lost either way; the operator drops t_old once satisfied.
--
--   Referential integrity is missing only between step 3 and the foreign-key quench pass that re-creates
--     the inbound keys from the CHILD tables' own JSON. On the other two engines that gap spans the whole
--     copy; here it spans two statements.
--
-- WHAT THIS CANNOT PROTECT AGAINST, stated rather than papered over: because every DDL statement commits
-- implicitly, no lock can be held from the copy through to the swap, and LOCK TABLES is unusable inside a
-- stored procedure (it would also make every other table -- the shadow, the working set, the audit --
-- unreachable for the rest of the run). A row inserted by another session after the copy scan and before
-- the swap is therefore copied nowhere and dropped with t_old. The row-count check immediately before the
-- swap DETECTS the common case and aborts loudly rather than losing it silently, but detection is not
-- prevention: a rebuild wants a quiesced target on this engine in a way it does not on the other two.
-- =====================================================================================================
DROP PROCEDURE IF EXISTS SchemaSmith_RebuildTable//

CREATE PROCEDURE SchemaSmith_RebuildTable(
    IN p_Schema VARCHAR(64),
    IN p_Table VARCHAR(64),
    IN p_WhatIf TINYINT
)
SQL SECURITY DEFINER
BEGIN
    DECLARE v_SchemaRaw VARCHAR(64);
    DECLARE v_TableRaw VARCHAR(64);
    DECLARE v_Qualified VARCHAR(200);
    DECLARE v_ShadowRaw VARCHAR(64);
    DECLARE v_OldRaw VARCHAR(64);
    DECLARE v_ShadowQualified VARCHAR(200);
    DECLARE v_OldQualified VARCHAR(200);
    DECLARE v_BlockedReason VARCHAR(255);
    DECLARE v_WorkingSetMissing TINYINT DEFAULT 0;
    DECLARE v_Probe INT DEFAULT 0;
    DECLARE v_Count INT DEFAULT 0;
    DECLARE v_ShadowColumnList LONGTEXT;
    DECLARE v_CopyColumnList LONGTEXT;
    DECLARE v_LiveCollation VARCHAR(100);
    DECLARE v_Collation VARCHAR(100);
    DECLARE v_Engine VARCHAR(50);
    DECLARE v_AutoIncrementKeyClause VARCHAR(500);
    DECLARE v_PrimaryKeyClause TEXT;
    DECLARE v_CapturedAutoIncrement BIGINT UNSIGNED DEFAULT NULL;
    DECLARE v_AutoIncrementInCopy TINYINT DEFAULT 0;
    DECLARE v_StatsExpirySwapped TINYINT DEFAULT 0;
    DECLARE v_CreateShadowSql LONGTEXT;
    DECLARE v_CopySql LONGTEXT;
    DECLARE v_ZeroIdOnSql LONGTEXT;
    DECLARE v_ZeroIdOffSql LONGTEXT;
    DECLARE v_ReseedSql LONGTEXT;
    DECLARE v_SwapSql LONGTEXT;
    DECLARE v_DropOldSql LONGTEXT;
    DECLARE v_RowsBefore BIGINT DEFAULT 0;
    DECLARE v_RowsAfter BIGINT DEFAULT -1;
    DECLARE v_RowsFinal BIGINT DEFAULT -1;
    DECLARE v_FkId INT;

    -- The shadow CREATE folds every declared column into one statement, so a wide table needs the same
    -- raised GROUP_CONCAT ceiling the sibling quench procedures set. Truncation here would silently build
    -- the replacement from a PREFIX of the declared columns, which is the worst failure this file could
    -- have -- the CREATE would most likely fail on a severed clause, but a lucky cut would not.
    SET SESSION group_concat_max_len = 1000000;

    -- Callers pass names in either form ('Foo' from a catalog read, '`Foo`' from _SchemaSmith_Tables), so
    -- normalize once: the raw name for catalog lookups and RENAME targets, the backticked form for DDL.
    SET v_SchemaRaw = SchemaSmith_StripBacktickWrapping(TRIM(COALESCE(p_Schema, '')));
    SET v_TableRaw = SchemaSmith_StripBacktickWrapping(TRIM(COALESCE(p_Table, '')));
    SET v_Qualified = CONCAT(SchemaSmith_SafeBacktickWrap(v_SchemaRaw), '.', SchemaSmith_SafeBacktickWrap(v_TableRaw));

    -- ================================================================================================
    -- 1. REFUSE WHEN BLOCKED -- before any DDL, and in WhatIf too.
    --
    -- SchemaSmith_RebuildBlockedReason names the live state a shadow copy would silently destroy. The
    -- MySQL body is always NULL (MySQL has none of these concepts); the MariaDb variant detects system
    -- versioning and application-time periods, where a copy discards row history or a temporal contract
    -- that no re-deploy can put back. A WhatIf preview that hid the refusal would tell the operator a
    -- rebuild is available on a table where it can never be, so the refusal fires in both modes.
    --
    -- MySQL caps SIGNAL's MESSAGE_TEXT at 128 characters, so every refusal below follows the convention
    -- the partitioned-table drop guard established in SchemaSmith_ModifiedTableQuench: the full
    -- explanation goes to SchemaSmith_StatusMessages (TEXT, uncapped) and the signalled message is a
    -- short line naming the table and the reason.
    -- ================================================================================================
    SET v_BlockedReason = SchemaSmith_RebuildBlockedReason(v_SchemaRaw, v_TableRaw);
    IF v_BlockedReason IS NOT NULL THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified, ': ', v_BlockedReason,
                '. A rebuild replaces the table with a shadow copy, and that state lives outside the schema package -- the copy discards it and no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the blocking state first and re-run.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': ', v_BlockedReason);
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- ================================================================================================
    -- 2. CONTRACT AND SAFETY REFUSALS -- all before any DDL, all in both modes.
    -- ================================================================================================

    -- p_WhatIf must be an explicit 0 or 1. The sibling quench procedures all branch on `= 1` and let
    -- anything else fall through to the doing branch, which is harmless where the doing branch adds an
    -- index. Here the doing branch destroys a table, and `NULL = 1` is NULL, so a caller that forgot the
    -- argument or passed a NULL variable would get a REAL rebuild where it asked for a preview. That is
    -- too sharp an edge to leave on this particular procedure, so an unusable value is refused outright
    -- rather than resolved in the more destructive direction.
    IF p_WhatIf IS NULL OR p_WhatIf NOT IN (0, 1) THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': p_WhatIf must be 0 (rebuild) or 1 (preview). A missing or NULL value would otherwise fall through to the branch that replaces the table, which is not a default anything should get by accident.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': p_WhatIf must be 0 or 1.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- No parsed working set on this connection. Reaching the copy without one would build a shadow from
    -- nothing. MySQL cannot see a TEMPORARY table through INFORMATION_SCHEMA, so existence is probed by
    -- reading it under a handler for 1146 (ER_NO_SUCH_TABLE). The handler is declared in a block of its
    -- own containing ONLY the probe: a procedure-wide handler for that error would also swallow a genuine
    -- "table doesn't exist" from the destructive statements further down, which is the last error this
    -- file should ever hide.
    BEGIN
        DECLARE CONTINUE HANDLER FOR 1146 SET v_WorkingSetMissing = 1;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_Tables;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_Columns;
        -- _SchemaSmith_Indexes is probed too because the shadow CREATE reads the declared primary key
        -- from it (section 6). All three are created by the same parse, so in practice they arrive
        -- together -- but a dependency that is read must be a dependency that is checked.
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_Indexes;
    END;

    IF v_WorkingSetMissing = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': SchemaSmith_RebuildTable was called with no parsed table definition in scope. It reads the declared column set from the _SchemaSmith_Tables / _SchemaSmith_Columns / _SchemaSmith_Indexes temporary tables that SchemaSmith_ParseTableJson populates, so it must be called on a connection where that parse has already run.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': no parsed table definition in scope. See the deploy log.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- BASE TABLE only. A view is not something this procedure knows how to replace, and a MariaDB
    -- SYSTEM VERSIONED table is already refused above with the reason that actually explains it.
    SELECT COUNT(*) INTO v_Count
      FROM INFORMATION_SCHEMA.TABLES ist
     WHERE BINARY ist.TABLE_SCHEMA = BINARY v_SchemaRaw
       AND BINARY ist.TABLE_NAME = BINARY v_TableRaw
       AND ist.TABLE_TYPE = 'BASE TABLE';

    IF v_Count = 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused: ', v_Qualified,
                ' does not exist as a base table. There is nothing to rebuild. If this table is mid-rename, the rename pass has to land before a rebuild can be considered.'));
        SET @ss_msg = CONCAT('Table rebuild refused: ', v_TableRaw, ' does not exist as a base table.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    SELECT COUNT(*) INTO v_Count
      FROM _SchemaSmith_Tables t
     WHERE BINARY SchemaSmith_StripBacktickWrapping(t.TableName) = BINARY v_TableRaw;

    IF v_Count = 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': the parsed working set carries no declaration for this table. Rebuilding to a definition that is not in the package would replace the table with an empty one.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': not declared in the parsed working set.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- An UNAPPLIED TABLE RENAME. The package renames OldName -> Name; if BOTH names resolve to live
    -- tables the rename has not happened (or has been re-declared), and rebuilding the destination would
    -- act on the wrong table while the source still holds rows. Refuse rather than pick one.
    SELECT COUNT(*) INTO v_Count
      FROM _SchemaSmith_Tables t
     WHERE BINARY SchemaSmith_StripBacktickWrapping(t.TableName) = BINARY v_TableRaw
       AND t.OldName IS NOT NULL
       AND EXISTS (SELECT 1
                     FROM INFORMATION_SCHEMA.TABLES o
                    WHERE BINARY o.TABLE_SCHEMA = BINARY v_SchemaRaw
                      AND BINARY o.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.OldName)
                      AND o.TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED'));

    IF v_Count > 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': the package declares an OldName that still resolves to a live table, so a table rename is pending. Let the rename land first -- rebuilding now would copy from the wrong table.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': a table rename is pending.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- An UNAPPLIED COLUMN RENAME. The copy matches columns BY CURRENT NAME. A column declared under its
    -- new name whose data still lives under OldName would match nothing, and the rebuild would drop that
    -- column's data with no error at all. This is the quietest data-loss shape in the whole procedure, so
    -- it is refused outright rather than guessed at.
    SELECT COUNT(*) INTO v_Count
      FROM _SchemaSmith_Columns c
     WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw
       AND c.OldName IS NOT NULL
       AND EXISTS (SELECT 1
                     FROM INFORMATION_SCHEMA.COLUMNS o
                    WHERE BINARY o.TABLE_SCHEMA = BINARY v_SchemaRaw
                      AND BINARY o.TABLE_NAME = BINARY v_TableRaw
                      AND BINARY o.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.OldName))
       AND NOT EXISTS (SELECT 1
                         FROM INFORMATION_SCHEMA.COLUMNS n
                        WHERE BINARY n.TABLE_SCHEMA = BINARY v_SchemaRaw
                          AND BINARY n.TABLE_NAME = BINARY v_TableRaw
                          AND BINARY n.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName));

    IF v_Count > 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': a declared column carries an OldName that still exists on the live table under that old name, so a column rename is pending. The copy matches columns by their current name and would silently discard that column''s data. Let the rename land first.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': a column rename is pending.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- A DECLARED EXPRESSION DEFAULT THIS TARGET CANNOT EXPRESS (MySQL below 8.0.13; MariaDB has had them
    -- since 10.2.1, at/below the floor, so this never fires there). Everywhere else in the codebase such
    -- a column is DEGRADED -- MissingTableAndColumnQuench and ModifiedTableQuench skip it and record a
    -- 'downgraded' row, leaving the live column alone. A rebuild has no equivalent middle: emitting the
    -- clause is a hard syntax error that would fail the CREATE, and skipping the column would drop it out
    -- of the shadow ENTIRELY -- taking its data with it, since the following passes would then decline to
    -- add it back for the same reason. Both answers are worse than not rebuilding, so refuse.
    IF SchemaSmith_SupportsDefaultExpression() = 0 THEN
        SELECT COUNT(*) INTO v_Count
          FROM _SchemaSmith_Columns c
         WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw
           AND c.IsAutoIncrement = 0
           AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
           AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';

        IF v_Count > 0 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Table rebuild refused (DEFAULT expression requires MySQL 8.0.13): ',
                   v_Qualified, '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
              FROM _SchemaSmith_Columns c
             WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw
               AND c.IsAutoIncrement = 0
               AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
               AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
            SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw,
                                 ': a declared DEFAULT expression requires MySQL 8.0.13. See the deploy log.');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- ================================================================================================
    -- 3. NAMES FOR THE SHADOW AND THE RENAMED-OUT ORIGINAL.
    --
    -- MySQL and MariaDB cap an identifier at 64 CHARACTERS (not bytes -- which is why this needs no
    -- equivalent of the PostgreSQL twin's 63-byte refusal: LEFT(name, 45) plus the longest suffix is 64
    -- characters exactly, on any alphabet). Both names are refused if already taken -- a leftover from an
    -- interrupted rebuild is an operator decision, not something to overwrite, and on this engine that
    -- refusal is doing double duty: it is also what surfaces a shadow stranded by a step-1 failure.
    -- ================================================================================================
    SET v_ShadowRaw = CONCAT(LEFT(v_TableRaw, 45), '_SchemaSmithRebuild');
    SET v_OldRaw = CONCAT(LEFT(v_TableRaw, 45), '_SchemaSmithOld');
    SET v_ShadowQualified = CONCAT(SchemaSmith_SafeBacktickWrap(v_SchemaRaw), '.', SchemaSmith_SafeBacktickWrap(v_ShadowRaw));
    SET v_OldQualified = CONCAT(SchemaSmith_SafeBacktickWrap(v_SchemaRaw), '.', SchemaSmith_SafeBacktickWrap(v_OldRaw));

    SELECT COUNT(*) INTO v_Count
      FROM INFORMATION_SCHEMA.TABLES ist
     WHERE BINARY ist.TABLE_SCHEMA = BINARY v_SchemaRaw
       AND (BINARY ist.TABLE_NAME = BINARY v_ShadowRaw OR BINARY ist.TABLE_NAME = BINARY v_OldRaw);

    IF v_Count > 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified, ': the working names ',
                v_ShadowQualified, ' / ', v_OldQualified,
                ' are already in use. That is normally a leftover from an interrupted rebuild -- inspect it and drop it deliberately rather than having this run overwrite it.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': the rebuild working names are already in use.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    -- ================================================================================================
    -- 4. AUTO_INCREMENT -- capture the ORIGINAL counter BEFORE anything is created or copied.
    --
    -- INFORMATION_SCHEMA.TABLES.AUTO_INCREMENT is the NEXT value the table will hand out. That differs
    -- from both siblings and is worth stating because the three engines genuinely disagree: SQL Server's
    -- IDENT_CURRENT is the LAST value issued (so 3a's reseed lands one below MySQL's number for the same
    -- table), and PostgreSQL's sequence last_value is likewise the last value written. MySQL's is already
    -- the next value, so it is applied verbatim -- no +1, no -1.
    --
    -- It must be the ORIGINAL's counter, never max(id) on the copied rows. Proven: with ids 1-3 and id 3
    -- deleted, the counter reads 4 while the copied max is 2, so reseeding from the data re-issues 3 -- an
    -- identifier the old table had already given to a row that existed. Anything that recorded the old 3
    -- then aliases two different entities, and nothing errors. (InnoDB also clamps ALTER TABLE
    -- AUTO_INCREMENT UPWARD to max+1, so a too-low value is silently accepted as exactly that defect.)
    --
    -- A NULL capture means one of two things and both want the same answer: the table has no
    -- AUTO_INCREMENT column at all, or it has one that has never issued a value (InnoDB reports NULL for
    -- a table that has never held a row -- the same case ModifiedTableQuench's seed pass COALESCEs to 0).
    -- Either way the reseed is skipped and the shadow keeps whatever counter it built itself, which is
    -- exactly what the SQL Server twin does when sys.identity_columns.last_value is NULL: forcing a
    -- never-used counter would burn its start value for no reason.
    --
    -- THE CACHE. From MySQL 8.0.3 the AUTO_INCREMENT column of INFORMATION_SCHEMA.TABLES is served from a
    -- cached statistics snapshot whose default lifetime (information_schema_stats_expiry) is 24 HOURS and
    -- is SERVER-wide, not per session -- so the deploy's own catalog read at parse time does not refresh
    -- an entry some other session populated an hour ago. A stale-low read here is not a cosmetic drift,
    -- it is the re-issued-identifier defect above, so the cache is turned off for the duration of the
    -- read and put back afterwards. Both halves go through PREPARE and sit under a handler, because
    -- information_schema_stats_expiry does not exist on MySQL 5.7 or on MariaDB at any version: naming it
    -- in the procedure body would risk the CREATE, and referencing it dynamically keeps the parser away
    -- from it entirely. Where the variable is absent there is no cache to defeat, and the guarded block
    -- simply does nothing.
    -- ================================================================================================
    SET @ss_rebuild_stats_expiry = NULL;

    -- The prepared-statement handle here is deliberately NOT the `stmt` name the rest of this file (and
    -- every sibling quench procedure) uses: inside a block that swallows exceptions, a PREPARE that fails
    -- would leave a LATER `EXECUTE stmt` running whatever `stmt` happened to be, and on this code path
    -- that would be an arbitrary statement fired at a live table. A private handle cannot exist here
    -- unless this block's own PREPARE created it.
    BEGIN
        DECLARE CONTINUE HANDLER FOR SQLEXCEPTION SET v_StatsExpirySwapped = 0;
        SET v_StatsExpirySwapped = 1;
        SET @exec_sql = 'SELECT @@SESSION.information_schema_stats_expiry INTO @ss_rebuild_stats_expiry';
        PREPARE ss_rebuild_probe FROM @exec_sql;
        EXECUTE ss_rebuild_probe;
        DEALLOCATE PREPARE ss_rebuild_probe;
        SET @exec_sql = 'SET SESSION information_schema_stats_expiry = 0';
        PREPARE ss_rebuild_probe FROM @exec_sql;
        EXECUTE ss_rebuild_probe;
        DEALLOCATE PREPARE ss_rebuild_probe;
    END;

    -- Aggregates, so the read always yields exactly one row and the variables land NULL rather than
    -- keeping a previous value on a miss.
    SELECT MAX(ist.AUTO_INCREMENT), MAX(ist.TABLE_COLLATION)
      INTO v_CapturedAutoIncrement, v_LiveCollation
      FROM INFORMATION_SCHEMA.TABLES ist
     WHERE BINARY ist.TABLE_SCHEMA = BINARY v_SchemaRaw
       AND BINARY ist.TABLE_NAME = BINARY v_TableRaw;

    IF v_StatsExpirySwapped = 1 AND @ss_rebuild_stats_expiry IS NOT NULL THEN
        BEGIN
            DECLARE CONTINUE HANDLER FOR SQLEXCEPTION SET v_StatsExpirySwapped = 0;
            SET @exec_sql = CONCAT('SET SESSION information_schema_stats_expiry = ', @ss_rebuild_stats_expiry);
            PREPARE ss_rebuild_probe FROM @exec_sql;
            EXECUTE ss_rebuild_probe;
            DEALLOCATE PREPARE ss_rebuild_probe;
        END;
    END IF;

    -- The reseed only means something when the declared AUTO_INCREMENT column is ALSO live, i.e. when its
    -- values are actually being carried across. A brand-new AUTO_INCREMENT column is not in the copy list
    -- at all, so the engine generates its values and the old table's counter -- which belonged to some
    -- other column -- says nothing about them. Same gate 3a puts on IDENTITY_INSERT, for the same reason.
    --
    -- The reverse case needs no branch on this engine: when a live plain column is newly declared
    -- AUTO_INCREMENT the capture is NULL, and InnoDB advances a counter on an EXPLICIT-value insert, so
    -- the shadow is already sitting at max(copied)+1 after the copy. (PostgreSQL needs a whole seed-past-
    -- the-data branch there precisely because its sequences do NOT advance on an explicit insert.)
    SELECT COUNT(*) INTO v_Count
      FROM _SchemaSmith_Columns c
     WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw
       AND c.IsAutoIncrement = 1
       AND EXISTS (SELECT 1
                     FROM INFORMATION_SCHEMA.COLUMNS isc
                    WHERE BINARY isc.TABLE_SCHEMA = BINARY v_SchemaRaw
                      AND BINARY isc.TABLE_NAME = BINARY v_TableRaw
                      AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName));

    SET v_AutoIncrementInCopy = CASE WHEN v_Count > 0 THEN 1 ELSE 0 END;

    -- ================================================================================================
    -- 5. COLUMN LISTS.
    --
    -- The shadow's CREATE takes the WHOLE declared column set in declared order (OrdinalPosition -- the
    -- order the columns appear in the package file). Generated columns are included and are NOT hoisted or
    -- deferred: MySQL and MariaDB both accept a GENERATED ALWAYS AS (...) expression that references a
    -- column declared LATER in the same CREATE TABLE, verified on both, so declared order is honoured
    -- literally with no follow-up ALTER. (ParseTableJson's DependencyLevel exists for the ADD COLUMN path,
    -- where the referenced column may not exist in the table yet.)
    --
    -- Including them is not optional, either. MissingTableAndColumnQuench adds a generated column only
    -- where NewColumn = 1, and NewColumn was decided at parse time against the PRE-rebuild table, where
    -- the column existed -- so a generated column left out of this CREATE would be dropped by the rebuild
    -- and never put back by anything.
    --
    -- The COPY moves only the INTERSECTION of declared and live, which is what makes the three cases fall
    -- out without special-casing: a column declared but not live is new (it takes its DEFAULT or NULL and
    -- must not appear in the SELECT), a column live but not declared is being removed (it appears in
    -- neither list), and a column on both sides carries its data.
    --
    -- Generated columns are excluded from the copy on the DECLARED side only, because the SHADOW derives
    -- them and INSERT cannot target a generated column at all. Declared-side only is deliberate: the live
    -- table is only ever READ from, and a live generated column the package now declares plain is
    -- perfectly selectable -- carrying its computed values across is the right answer, and testing the
    -- live side would lose them silently. Nothing else needs excluding here: unlike SQL Server there is no
    -- ROWVERSION and no column set, an AUTO_INCREMENT column accepts an explicit value with no session
    -- switch, and an INVISIBLE column is invisible only to SELECT *, which this never emits.
    --
    -- A newly declared NOT NULL column with no DEFAULT on a non-empty table is NOT special-cased: the copy
    -- fails on the null violation and the original is untouched (see the step-1 note in the header).
    -- Failing loudly beats inventing a value.
    --
    -- The insert list and the select list are ONE string by construction: same columns, same order, so
    -- they cannot drift apart into a positional mismatch that would write data into the wrong column.
    -- ================================================================================================
    SELECT GROUP_CONCAT(c.ColumnScript ORDER BY c.OrdinalPosition, c.RowId SEPARATOR ', ')
      INTO v_ShadowColumnList
      FROM _SchemaSmith_Columns c
     WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw;

    IF v_ShadowColumnList IS NULL OR TRIM(v_ShadowColumnList) = '' THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                ': the declared definition produced no columns to build the replacement from.'));
        SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': the declared definition produced no columns.');
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;

    SELECT GROUP_CONCAT(c.ColumnName ORDER BY c.OrdinalPosition, c.RowId SEPARATOR ', ')
      INTO v_CopyColumnList
      FROM _SchemaSmith_Columns c
     WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY v_TableRaw
       AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
       AND EXISTS (SELECT 1
                     FROM INFORMATION_SCHEMA.COLUMNS isc
                    WHERE BINARY isc.TABLE_SCHEMA = BINARY v_SchemaRaw
                      AND BINARY isc.TABLE_NAME = BINARY v_TableRaw
                      AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName));

    -- Nothing to copy AND rows to lose. Every live column is being removed, so the rows would survive only
    -- as empty shells -- and manufacturing those is a guess about intent, not a data-preserving rebuild.
    IF v_CopyColumnList IS NULL OR TRIM(v_CopyColumnList) = '' THEN
        SET v_CopyColumnList = NULL;
        SET @ss_rebuild_rows = 0;
        SET @exec_sql = CONCAT('SELECT COUNT(*) INTO @ss_rebuild_rows FROM ', v_Qualified);
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;

        IF @ss_rebuild_rows > 0 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            VALUES (CONNECTION_ID(), CONCAT('  Table rebuild refused for ', v_Qualified,
                    ': no declared column also exists on the live table, so there is nothing to copy, and the table is not empty. Rebuilding would destroy every row. Use Before/After migration scripts if the rows are meant to survive a full column replacement.'));
            SET @ss_msg = CONCAT('Table rebuild refused for ', v_TableRaw, ': nothing to copy and the table is not empty.');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;
    END IF;

    -- ================================================================================================
    -- 6. BUILD EVERY STATEMENT UP FRONT.
    --
    -- Built before anything executes so WhatIf can print exactly what a real run would do, from exactly
    -- the same source -- a preview assembled by a second code path is a preview of something else. MySQL
    -- PREPARE accepts one statement at a time, so each of these is a single statement and the inbound
    -- foreign-key drops are materialized one row per statement below.
    -- ================================================================================================

    -- ENGINE and COLLATE are the only table options the shadow carries, and both are load-bearing HERE in
    -- a way the rest are not. COLLATE decides whether the characters in every row survive the copy: a
    -- shadow built at the DATABASE default would silently transcode a table that is not at that default.
    -- ENGINE is taken from the declaration exactly as MissingTableAndColumnQuench takes it on a fresh
    -- CREATE (ParseTableJson defaults it to InnoDB), so a rebuilt table is the table a fresh create would
    -- have produced rather than a third variant. Declared collation wins when the package states one --
    -- ModifiedTableQuench would converge to it moments later anyway -- and the LIVE collation is
    -- inherited when it does not, which is precisely SchemaSmith's existing "leave collation alone unless
    -- declared" behaviour. ROW_FORMAT, COMMENT and the declared AUTO_INCREMENT seed are deliberately NOT
    -- carried: they are pure attributes owned by the convergence passes, and the declared seed in
    -- particular would fight the captured counter this procedure exists to preserve.
    SELECT COALESCE(NULLIF(TRIM(MAX(t.Collation)), ''), v_LiveCollation), MAX(t.Engine),
           COALESCE(MAX(t.AutoIncrementKeyClause), '')
      INTO v_Collation, v_Engine, v_AutoIncrementKeyClause
      FROM _SchemaSmith_Tables t
     WHERE BINARY SchemaSmith_StripBacktickWrapping(t.TableName) = BINARY v_TableRaw;

    -- THE PRIMARY KEY IS THE ONE INDEX THIS PROCEDURE EMITS, AND ONLY BECAUSE THE ENGINE FORCES IT.
    -- MySQL and MariaDB reject a CREATE TABLE whose AUTO_INCREMENT column is not part of a key (error
    -- 1075, "there can be only one auto column and it must be defined as a key"), so a shadow built with
    -- the columns alone cannot be created at all for the single most common shape of table there is. That
    -- is an engine constraint on the CREATE, not this procedure deciding to take over index management --
    -- everything else (secondary indexes, unique and check constraints, foreign keys) is still left to the
    -- passes that own it. The clause is read from _SchemaSmith_Indexes and paired with
    -- AutoIncrementKeyClause exactly as MissingTableAndColumnQuench builds a fresh CREATE, so there is one
    -- source of truth for what a table's primary key is, and MissingIndexesAndConstraintsQuench then finds
    -- the key already present and does nothing.
    --
    -- A package that declares an AUTO_INCREMENT column and no key for it fails the shadow CREATE with the
    -- engine's own 1075 -- at step 1, which is fully reversible -- and it is exactly the same failure a
    -- FRESH create of that same declaration would produce. That is a package defect surfaced identically,
    -- so it gets no bespoke pre-check here that would duplicate the engine's own validation.
    SELECT CONCAT(', PRIMARY KEY (', MAX(i.IndexColumns), ')')
      INTO v_PrimaryKeyClause
      FROM _SchemaSmith_Indexes i
     WHERE BINARY SchemaSmith_StripBacktickWrapping(i.TableName) = BINARY v_TableRaw
       AND i.IsPrimaryKey = 1;

    SET v_CreateShadowSql = CONCAT('CREATE TABLE ', v_ShadowQualified, ' (', v_ShadowColumnList,
        v_AutoIncrementKeyClause, COALESCE(v_PrimaryKeyClause, ''), ')',
        ' ENGINE=', COALESCE(v_Engine, 'InnoDB'),
        -- Same charset derivation ModifiedTableQuench's table-collation pass uses (the charset is the
        -- collation name up to the first underscore), so the two agree on what a collation means.
        CASE WHEN v_Collation IS NOT NULL AND TRIM(v_Collation) <> ''
             THEN CONCAT(' DEFAULT CHARACTER SET ', SUBSTRING_INDEX(v_Collation, '_', 1), ' COLLATE ', v_Collation)
             ELSE '' END);

    SET v_CopySql = CASE WHEN v_CopyColumnList IS NULL THEN NULL
                         ELSE CONCAT('INSERT INTO ', v_ShadowQualified, ' (', v_CopyColumnList, ')',
                                     ' SELECT ', v_CopyColumnList, ' FROM ', v_Qualified)
                    END;

    -- A stored 0 in an AUTO_INCREMENT column is the one value an ordinary copy does NOT carry across:
    -- MySQL treats an explicit 0 as "generate a new value" unless NO_AUTO_VALUE_ON_ZERO is set, so a row
    -- whose id is genuinely 0 (which is exactly how it got there -- someone inserted it under that mode)
    -- would come out the other side renumbered, with no error. The mode is added for the copy statement
    -- and put straight back, so nothing else in the deploy runs under it.
    SET v_ZeroIdOnSql = CASE WHEN v_CopySql IS NOT NULL AND v_AutoIncrementInCopy = 1
                             THEN 'SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, '',NO_AUTO_VALUE_ON_ZERO'')' END;
    SET v_ZeroIdOffSql = CASE WHEN v_ZeroIdOnSql IS NOT NULL
                              THEN 'SET SESSION sql_mode = @ss_rebuild_saved_sql_mode' END;

    SET v_ReseedSql = CASE WHEN v_AutoIncrementInCopy = 1 AND v_CapturedAutoIncrement IS NOT NULL
                           THEN CONCAT('ALTER TABLE ', v_ShadowQualified, ' AUTO_INCREMENT = ', v_CapturedAutoIncrement) END;

    -- ONE statement, on purpose. RENAME TABLE with two clauses is atomic: there is no instant at which
    -- the application's table name resolves to nothing. Emitting two separate RENAME TABLE statements
    -- would open exactly the window this engine has no transaction to close.
    SET v_SwapSql = CONCAT('RENAME TABLE ', v_Qualified, ' TO ', v_OldQualified, ', ',
                           v_ShadowQualified, ' TO ', v_Qualified);

    SET v_DropOldSql = CONCAT('DROP TABLE ', v_OldQualified);

    -- Inbound foreign keys: OTHER tables pointing AT this one, plus this table's own self-references.
    -- Enumerated HERE, before the swap, because after it the catalog no longer knows they ever pointed at
    -- this name -- MySQL rewrites a referencing constraint to follow a RENAME TABLE, so post-swap they
    -- all point at t_old. That is also why they are dropped rather than left: DROP TABLE refuses while a
    -- foreign key still references the table, and if the drop ever were permitted the child's referential
    -- integrity would be severed silently instead of failing loudly.
    --
    -- They are NOT re-added here. Each one is defined in its OWNING table's JSON, so that table's
    -- foreign-key quench pass sees it missing and re-creates it from the package. Re-adding them inside
    -- this procedure would mean maintaining foreign-key construction against a second source of truth.
    --
    -- AlterTable is the child's name AFTER the rename, which differs from ChildTable in exactly one case:
    -- a SELF-reference, whose owning table is the one being renamed aside. Dropping that by its original
    -- name would aim the ALTER at the brand-new replacement, which carries no such constraint, and fail.
    -- ChildTable is kept alongside it so the audit still names the object the operator knows.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildInboundFks;
    CREATE TEMPORARY TABLE _SchemaSmith_RebuildInboundFks (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        ChildSchema VARCHAR(64) NOT NULL,
        ChildTable VARCHAR(64) NOT NULL,
        AlterTable VARCHAR(64) NOT NULL,
        ConstraintName VARCHAR(64) COLLATE utf8mb4_bin NOT NULL,
        Stmt TEXT
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Landed raw first and rewritten in place afterwards, rather than folded into one SELECT with a CASE:
    -- an INFORMATION_SCHEMA column and a procedure variable do not share a collation, and combining them
    -- in one expression is an illegal-mix error rather than a wrong answer. Every statement below either
    -- reads I_S alone or reads the temp table alone, so no expression ever spans the two.
    INSERT INTO _SchemaSmith_RebuildInboundFks (ChildSchema, ChildTable, AlterTable, ConstraintName)
    SELECT DISTINCT kcu.TABLE_SCHEMA, kcu.TABLE_NAME, kcu.TABLE_NAME, kcu.CONSTRAINT_NAME
      FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
     WHERE BINARY kcu.REFERENCED_TABLE_SCHEMA = BINARY v_SchemaRaw
       AND BINARY kcu.REFERENCED_TABLE_NAME = BINARY v_TableRaw;

    UPDATE _SchemaSmith_RebuildInboundFks
       SET AlterTable = v_OldRaw
     WHERE BINARY ChildSchema = BINARY v_SchemaRaw
       AND BINARY ChildTable = BINARY v_TableRaw;

    UPDATE _SchemaSmith_RebuildInboundFks
       SET Stmt = CONCAT('ALTER TABLE ', SchemaSmith_SafeBacktickWrap(ChildSchema), '.',
                         SchemaSmith_SafeBacktickWrap(AlterTable),
                         ' DROP FOREIGN KEY ', SchemaSmith_SafeBacktickWrap(ConstraintName));

    -- ================================================================================================
    -- 7. WHATIF -- print, execute nothing.
    -- ================================================================================================
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Would rebuild table ', v_Qualified));

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_CreateShadowSql);
        IF v_ZeroIdOnSql IS NOT NULL THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_ZeroIdOnSql);
        END IF;
        IF v_CopySql IS NOT NULL THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_CopySql);
        END IF;
        IF v_ZeroIdOffSql IS NOT NULL THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_ZeroIdOffSql);
        END IF;
        IF v_ReseedSql IS NOT NULL THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_ReseedSql);
        END IF;
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_SwapSql);
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), f.Stmt FROM _SchemaSmith_RebuildInboundFks f ORDER BY f.RowId;
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), v_DropOldSql);

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        VALUES (CONNECTION_ID(), 'table', v_TableRaw, 'wouldRebuild');

        -- WhatIf twin of the 'foreignKey'/'dropped' rows the real branch writes. Same source, same
        -- ObjectName shape the foreign-key quench uses (child table + constraint, unqualified -- the
        -- database is implicit for a whole MySQL deploy), so a preview's manifest lists the inbound keys
        -- a real run would take out.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(f.ChildTable, '.', f.ConstraintName), 'wouldDrop'
          FROM _SchemaSmith_RebuildInboundFks f;

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildInboundFks;
    ELSE
        -- ============================================================================================
        -- 8. THE DESTRUCTIVE SEQUENCE. See the ATOMICITY block at the top of this file for what a
        -- failure at each point below leaves behind -- there is no transaction here and there cannot be.
        -- ============================================================================================
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Rebuilding table ', v_Qualified));

        SET @ss_rebuild_rows = 0;
        SET @exec_sql = CONCAT('SELECT COUNT(*) INTO @ss_rebuild_rows FROM ', v_Qualified);
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
        SET v_RowsBefore = @ss_rebuild_rows;

        SET @exec_sql = v_CreateShadowSql;
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;

        IF v_CopySql IS NOT NULL THEN
            IF v_ZeroIdOnSql IS NOT NULL THEN
                SET @ss_rebuild_saved_sql_mode = @@SESSION.sql_mode;
                SET @exec_sql = v_ZeroIdOnSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END IF;

            SET @exec_sql = v_CopySql;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;

            IF v_ZeroIdOffSql IS NOT NULL THEN
                SET @exec_sql = v_ZeroIdOffSql;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END IF;

            -- This is the one operation in SchemaSmith that destroys user data, so it pays for a
            -- verification scan rather than trusting that INSERT ... SELECT moved everything.
            SET @ss_rebuild_rows = -1;
            SET @exec_sql = CONCAT('SELECT COUNT(*) INTO @ss_rebuild_rows FROM ', v_ShadowQualified);
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET v_RowsAfter = @ss_rebuild_rows;

            IF v_RowsAfter <> v_RowsBefore THEN
                INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
                VALUES (CONNECTION_ID(), CONCAT('  Table rebuild aborted for ', v_Qualified, ': the replacement holds ',
                        v_RowsAfter, ' rows but the original holds ', v_RowsBefore,
                        '. Nothing has been swapped -- the original table is untouched under its own name; the shadow ',
                        v_ShadowQualified, ' is left in place for inspection and must be dropped deliberately.'));
                SET @ss_msg = CONCAT('Table rebuild aborted for ', v_TableRaw, ': row count mismatch. See the deploy log.');
                SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
            END IF;
        END IF;

        IF v_ReseedSql IS NOT NULL THEN
            SET @exec_sql = v_ReseedSql;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
        END IF;

        -- LAST LOOK BEFORE THE POINT OF NO RETURN. DDL commits implicitly on this engine, so no lock
        -- taken during the copy is still held here and another session may have written to the original
        -- in between. Re-counting cannot PREVENT that -- see the header -- but it turns the common case
        -- (a row arriving mid-copy) from a silent loss into a loud abort with the original still intact.
        SET @ss_rebuild_rows = -1;
        SET @exec_sql = CONCAT('SELECT COUNT(*) INTO @ss_rebuild_rows FROM ', v_Qualified);
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
        SET v_RowsFinal = @ss_rebuild_rows;

        IF v_RowsFinal <> v_RowsBefore THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            VALUES (CONNECTION_ID(), CONCAT('  Table rebuild aborted for ', v_Qualified, ': the original held ',
                    v_RowsBefore, ' rows when the copy started and holds ', v_RowsFinal,
                    ' now, so another session wrote to it mid-rebuild and those rows are not in the replacement. Nothing has been swapped -- the original table is untouched under its own name; the shadow ',
                    v_ShadowQualified, ' is left in place for inspection and must be dropped deliberately. Re-run against a quiesced target.'));
            SET @ss_msg = CONCAT('Table rebuild aborted for ', v_TableRaw, ': the source changed mid-rebuild. See the deploy log.');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        END IF;

        SET @exec_sql = v_SwapSql;
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;

        -- Audited before the drops run, matching how the sibling MySQL quench passes record a fold of
        -- DDL they are about to issue. Past the swap the rebuild is committed regardless.
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Dropping inbound foreign key ', f.ChildTable, '.', f.ConstraintName)
          FROM _SchemaSmith_RebuildInboundFks f;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'foreignKey', CONCAT(f.ChildTable, '.', f.ConstraintName), 'dropped'
          FROM _SchemaSmith_RebuildInboundFks f;

        SET v_FkId = (SELECT MIN(RowId) FROM _SchemaSmith_RebuildInboundFks);
        WHILE v_FkId IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_RebuildInboundFks WHERE RowId = v_FkId;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET v_FkId = (SELECT MIN(RowId) FROM _SchemaSmith_RebuildInboundFks WHERE RowId > v_FkId);
        END WHILE;

        SET @exec_sql = v_DropOldSql;
        PREPARE stmt FROM @exec_sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        VALUES (CONNECTION_ID(), 'table', v_TableRaw, 'rebuilt');

        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RebuildInboundFks;
    END IF;
END//

DELIMITER ;
