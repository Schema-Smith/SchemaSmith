-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableTablespace//

CREATE PROCEDURE SchemaSmith_TableTablespace(
    IN p_Schema VARCHAR(64),
    IN p_Table VARCHAR(64),
    OUT p_Tablespace VARCHAR(64)
)
SQL SECURITY DEFINER
BEGIN
    -- Sets p_Tablespace to the NAMED InnoDB general tablespace a table is placed in, or NULL when the
    -- table lives in its own implicit (innodb_file_per_table) tablespace -- which is the overwhelming
    -- majority of tables and must read back as "no declared placement", not as a tablespace named after
    -- the table itself.
    --
    -- WHY A PROCEDURE WITH AN OUT PARAM, NOT A FUNCTION: this started as a FUNCTION RETURNS VARCHAR(64)
    -- with the INNODB_TABLES/INNODB_TABLESPACES SELECT written directly in its body. That FAILS TO CREATE
    -- on a genuine MySQL 5.7 target -- confirmed live -- with
    --   ERROR 1109 (42S02): Unknown table 'INNODB_TABLES' in information_schema
    -- because MySQL BINDS every INFORMATION_SCHEMA reference in a stored FUNCTION body at CREATE FUNCTION
    -- time, not at CALL time. This is DIFFERENT from the ordinary-table/column deferred-resolution rule
    -- SchemaSmith_ColumnSrid and SchemaSmith_IndexIsVisible rely on elsewhere in this codebase (an early
    -- version-gated RETURN there keeps an unreached branch's column reference unbound) -- an
    -- INFORMATION_SCHEMA reference does not get that deferral, so no version-gated IF/RETURN inside a
    -- FUNCTION can save it: 5.7 has only the deprecated INNODB_SYS_TABLES/INNODB_SYS_TABLESPACES names, so
    -- the unprefixed views this needs are simply absent from the 5.7 catalog at CREATE time, full stop.
    --
    -- The escape is dynamic SQL: build the SELECT as a STRING and PREPARE/EXECUTE it, so the view names
    -- live inside a string literal and are never parsed/bound until EXECUTE actually runs -- which the
    -- IF below keeps from ever happening on 5.7. But MySQL does not allow PREPARE/EXECUTE inside a stored
    -- FUNCTION (ERROR 1336, "Dynamic SQL is not allowed in stored function or trigger") -- only inside a
    -- PROCEDURE. Hence the OUT parameter shape: every caller (GenerateTableJson, ModifiedTableQuench)
    -- CALLs this and reads the result from a session/local variable instead of using it inline as an
    -- expression.
    --
    -- SPACE_TYPE = 'General' is what tells a NAMED general tablespace apart from the implicit per-table
    -- form: INNODB_TABLESPACES.NAME for a file-per-table space is the SCHEMA/TABLE name itself (SPACE_TYPE
    -- 'Single'), not a tablespace a user could have declared, so it must never read back as a placement.
    --
    -- Below MySQL 8.0 the unprefixed INNODB_TABLES/INNODB_TABLESPACES views do not exist at all (5.7 has
    -- only the SYS_-prefixed names), so the read degrades to NULL there -- general-tablespace placement is
    -- simply unreported below the floor. The dynamic-SQL string is never built or PREPAREd in that branch,
    -- so nothing below-floor-unsafe is ever parsed.
    IF SchemaSmith_ServerVersionNum() < 800 THEN
        SET p_Tablespace = NULL;
    ELSE
      -- Nested block so the NOT FOUND handler below is scoped to (and consumed by) the zero-row read,
      -- and can never escape to a caller. A no-match `SELECT ... INTO` raises SQLSTATE 02000 (NOT FOUND),
      -- NOT merely a warning, inside a stored program. Callers run this CALL with their OWN
      -- `CONTINUE HANDLER FOR NOT FOUND` active (ModifiedTableQuench STEP -0.4's refuse cursor;
      -- extraction cursors above GenerateTableJson): if this callee left 02000 unhandled it would
      -- propagate up and fire the CALLER's handler, prematurely tripping its loop-done flag and silently
      -- skipping every remaining table -- a false-negative refuse, or a truncated extraction. Handling it
      -- locally (leaving @ss_tts_out at its NULL seed) keeps the "no named tablespace" common case a
      -- non-event regardless of caller context.
      BEGIN
        DECLARE CONTINUE HANDLER FOR NOT FOUND SET @ss_tts_out = NULL;
        -- Session variables, not routine params, inside the dynamic SQL string: a prepared statement
        -- cannot reference IN/local routine variables directly, only session (@-prefixed) ones.
        SET @ss_tts_schema = p_Schema;
        SET @ss_tts_table = p_Table;
        SET @ss_tts_out = NULL;
        -- A single multi-line quoted string literal, NOT '...' || '...' concatenation -- MySQL treats ||
        -- as logical OR by default (PIPES_AS_CONCAT is an opt-in SQL mode, not assumable here). Matches
        -- the existing CHECK_CONSTRAINTS dynamic-SQL block in SchemaSmith_GenerateTableJson.sql: embedded
        -- literals are doubled single quotes ('' for a literal ').
        SET @ss_tts_sql = 'SELECT ts.NAME INTO @ss_tts_out
FROM INFORMATION_SCHEMA.INNODB_TABLES it
JOIN INFORMATION_SCHEMA.INNODB_TABLESPACES ts ON ts.SPACE = it.SPACE
WHERE it.NAME = CONCAT(@ss_tts_schema, ''/'', @ss_tts_table)
  AND ts.SPACE_TYPE = ''General''
LIMIT 1';
        PREPARE ss_tts_stmt FROM @ss_tts_sql;
        EXECUTE ss_tts_stmt;
        DEALLOCATE PREPARE ss_tts_stmt;
        -- No matching row leaves @ss_tts_out at the NULL seed above -- the common case (an
        -- implicit-tablespace table) -- with the 02000 consumed by this block's own handler.
        SET p_Tablespace = @ss_tts_out;
      END;
    END IF;
END //

DELIMITER ;
