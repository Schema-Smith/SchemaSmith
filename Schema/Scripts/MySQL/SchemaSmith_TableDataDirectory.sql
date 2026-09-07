-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableDataDirectory//

CREATE PROCEDURE SchemaSmith_TableDataDirectory(
    IN p_Schema VARCHAR(64),
    IN p_Table VARCHAR(64),
    OUT p_DataDirectory VARCHAR(512)
)
SQL SECURITY DEFINER
BEGIN
    -- Sets p_DataDirectory to the filesystem directory an InnoDB table's data file is placed in (`DATA
    -- DIRECTORY='<path>'`), or NULL when the table lives in the default datadir -- the overwhelming
    -- majority of tables -- which must read back as "no declared placement".
    --
    -- WHY A PROCEDURE WITH AN OUT PARAM, NOT A FUNCTION, AND WHY DYNAMIC SQL: same reasoning as the
    -- sibling SchemaSmith_TableTablespace (F2b) -- see that script for the full derivation. In short:
    -- INFORMATION_SCHEMA.INNODB_DATAFILES is a MySQL-8.0+-only view (verified live), and MySQL BINDS every
    -- INFORMATION_SCHEMA reference in a stored FUNCTION body at CREATE FUNCTION time -- not at CALL time --
    -- so a static reference to it would fail to CREATE at all on a genuine MySQL 5.7 target with ERROR 1109
    -- (Unknown table 'INNODB_DATAFILES'). The escape is dynamic SQL (PREPARE/EXECUTE), which keeps the view
    -- name inside a string literal that is never parsed/bound until EXECUTE actually runs -- gated below
    -- 8.0 so that EXECUTE never happens on a floor server. But PREPARE/EXECUTE is disallowed inside a
    -- stored FUNCTION (ERROR 1336, "Dynamic SQL is not allowed in stored function or trigger"), hence the
    -- OUT-parameter PROCEDURE shape: callers (GenerateTableJson, ModifiedTableQuench) CALL this and read
    -- the result from a variable rather than using it inline as an expression.
    --
    -- THE READ, verified live 2026-09-04: DATA DIRECTORY is NOT surfaced in
    -- INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS on MySQL (unlike MariaDB below) -- it must be derived from
    -- INNODB_DATAFILES.PATH joined to INNODB_TABLES by SPACE. A placed table's PATH is ABSOLUTE, e.g.
    -- /ddspace/spike/dd_test.ibd; a plain table's PATH is relative to the datadir, ./spike/plain_t.ibd. So
    -- an absolute PATH minus its trailing /<schema>/<table>.ibd is the declared directory; a `./`-relative
    -- PATH means no DATA DIRECTORY was declared.
    --
    -- Below MySQL 8.0 the unprefixed INNODB_DATAFILES view does not exist at all, so the read degrades to
    -- NULL there -- DATA DIRECTORY placement is simply unreported below the floor, same posture as
    -- Tablespace. The dynamic-SQL string is never built or PREPAREd in that branch.
    --
    -- KNOWN LIMITATION (partitioned tables): INNODB_TABLES.NAME is per-PARTITION for a partitioned table
    -- ('schema/table#p#p0', not 'schema/table'), so the exact-name join below returns zero rows and this
    -- reads back NULL even when a table-level DATA DIRECTORY is deployed. The sibling
    -- SchemaSmith_TableTablespace (F2b) has the identical gap for the same reason. Table-level placement on
    -- a partitioned table therefore does not round-trip and is not refuse-guarded on redeploy -- per-partition
    -- placement is out of scope, and resolving the table-level default from the partition catalog is a
    -- deliberate follow-up decision, not silently papered over here.
    IF SchemaSmith_ServerVersionNum() < 800 THEN
        SET p_DataDirectory = NULL;
    ELSE
      -- Nested block so the NOT FOUND handler below is scoped to (and consumed by) the zero-row read, and
      -- can never escape to a caller. A no-match `SELECT ... INTO` raises SQLSTATE 02000 (NOT FOUND), NOT
      -- merely a warning, inside a stored program. Callers run this CALL with their OWN
      -- `CONTINUE HANDLER FOR NOT FOUND` active (ModifiedTableQuench's DATA DIRECTORY refuse cursor;
      -- extraction cursors above GenerateTableJson): if this callee left 02000 unhandled it would propagate
      -- up and fire the CALLER's handler, prematurely tripping its loop-done flag and silently skipping
      -- every remaining table -- a false-negative refuse, or a truncated extraction. Handling it locally
      -- (leaving @ss_tdd_out at its NULL seed) keeps the "no declared placement" common case a non-event
      -- regardless of caller context.
      BEGIN
        -- Computed OUTSIDE the dynamic-SQL string (plain SQL, using the routine's own IN params) so the
        -- string stays a single simple SELECT -- the suffix-stripping below runs after EXECUTE returns.
        DECLARE v_suffix VARCHAR(600);
        DECLARE CONTINUE HANDLER FOR NOT FOUND SET @ss_tdd_out = NULL;

        -- Session variables, not routine params, inside the dynamic SQL string: a prepared statement
        -- cannot reference IN/local routine variables directly, only session (@-prefixed) ones.
        SET @ss_tdd_schema = p_Schema;
        SET @ss_tdd_table = p_Table;
        SET @ss_tdd_out = NULL;
        -- A single multi-line quoted string literal, NOT '...' || '...' concatenation -- MySQL treats ||
        -- as logical OR by default (PIPES_AS_CONCAT is an opt-in SQL mode, not assumable here). Matches the
        -- existing CHECK_CONSTRAINTS dynamic-SQL block in SchemaSmith_GenerateTableJson.sql and the sibling
        -- SchemaSmith_TableTablespace: embedded literals are doubled single quotes ('' for a literal ').
        SET @ss_tdd_sql = 'SELECT df.PATH INTO @ss_tdd_out
FROM INFORMATION_SCHEMA.INNODB_DATAFILES df
JOIN INFORMATION_SCHEMA.INNODB_TABLES it ON it.SPACE = df.SPACE
WHERE it.NAME = CONCAT(@ss_tdd_schema, ''/'', @ss_tdd_table)
LIMIT 1';
        PREPARE ss_tdd_stmt FROM @ss_tdd_sql;
        EXECUTE ss_tdd_stmt;
        DEALLOCATE PREPARE ss_tdd_stmt;

        -- No matching row leaves @ss_tdd_out at the NULL seed above (should not happen -- the caller
        -- always names an existing table -- but the handler above keeps it harmless either way).
        --
        -- A `./`-relative PATH means the table lives in the default datadir -- no declared placement.
        -- An absolute PATH's declared directory is everything before its trailing /<schema>/<table>.ibd,
        -- which is what the data file is always named -- stripping that known suffix, rather than parsing
        -- forward, is robust to a directory path that itself contains slashes.
        SET v_suffix = CONCAT('/', p_Schema, '/', p_Table, '.ibd');
        IF @ss_tdd_out IS NULL OR LEFT(@ss_tdd_out, 2) = './' THEN
            SET p_DataDirectory = NULL;
        ELSEIF RIGHT(@ss_tdd_out, CHAR_LENGTH(v_suffix)) = v_suffix THEN
            -- TRIM(TRAILING '/' ...) is defensive, not load-bearing here (the suffix strip above already
            -- leaves no trailing slash) -- it keeps this derivation and the MariaDb CREATE_OPTIONS-parsed
            -- one below normalizing to the identical no-trailing-slash form.
            SET p_DataDirectory = TRIM(TRAILING '/' FROM LEFT(@ss_tdd_out, CHAR_LENGTH(@ss_tdd_out) - CHAR_LENGTH(v_suffix)));
        ELSE
            -- Defensive: PATH did not end with the expected <schema>/<table>.ibd suffix -- should not
            -- happen given the join predicate above pins it to this exact table -- report unplaced rather
            -- than emit a mangled path.
            SET p_DataDirectory = NULL;
        END IF;
      END;
    END IF;
END //

DELIMITER ;
