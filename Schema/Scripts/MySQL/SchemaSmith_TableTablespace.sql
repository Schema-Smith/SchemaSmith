-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableTablespace//

CREATE PROCEDURE SchemaSmith_TableTablespace(
    IN p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    OUT p_Tablespace VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
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
    -- MySQL 5.7 has the same facts under the SYS_-prefixed names (INNODB_SYS_TABLES, INNODB_SYS_TABLESPACES, with the
    -- same columns), so the view names are chosen by version inside the dynamic string and only the present ones are
    -- ever parsed. Reading NULL there used to refuse every redeploy of a table that declared a tablespace.
    --
    -- InnoDB names a table by the filename-encoded form (SchemaSmith_InnodbName), so a name like `a-b` is matched as
    -- `a@002db`. A table placed explicitly in the system tablespace is space 0, which has no tablespace row; it reads
    -- back as innodb_system.
    -- Nested block so the NOT FOUND handler below is scoped to (and consumed by) the zero-row read, and can never
    -- escape to a caller. A no-match `SELECT ... INTO` raises SQLSTATE 02000 (NOT FOUND), NOT merely a warning, inside
    -- a stored program. Callers run this CALL with their OWN `CONTINUE HANDLER FOR NOT FOUND` active (ModifiedTableQuench
    -- STEP -0.4's refuse cursor; extraction cursors above GenerateTableJson): if this callee left 02000 unhandled it would
    -- propagate up and fire the CALLER's handler, prematurely tripping its loop-done flag and silently skipping every
    -- remaining table. Handling it locally keeps the "no named tablespace" common case a non-event.
    BEGIN
        DECLARE CONTINUE HANDLER FOR NOT FOUND SET @ss_tts_out = NULL;
        -- Session variables, not routine params, inside the dynamic SQL string: a prepared statement cannot reference
        -- IN/local routine variables directly, only session (@-prefixed) ones.
        SET @ss_tts_name = CONCAT(SchemaSmith_InnodbName(p_Schema), '/', SchemaSmith_InnodbName(p_Table));
        SET @ss_tts_out = NULL;
        -- CONCAT, not ||, which is logical OR unless PIPES_AS_CONCAT is set. Embedded literals are doubled quotes.
        SET @ss_tts_sql = CONCAT('SELECT CASE WHEN it.SPACE = 0 THEN ''innodb_system'' ELSE ts.NAME END INTO @ss_tts_out
FROM INFORMATION_SCHEMA.', IF(SchemaSmith_ServerVersionNum() < 800, 'INNODB_SYS_TABLES', 'INNODB_TABLES'), ' it
LEFT JOIN INFORMATION_SCHEMA.', IF(SchemaSmith_ServerVersionNum() < 800, 'INNODB_SYS_TABLESPACES', 'INNODB_TABLESPACES'), ' ts ON ts.SPACE = it.SPACE
WHERE it.NAME = @ss_tts_name
  AND (it.SPACE = 0 OR ts.SPACE_TYPE = ''General'')
LIMIT 1');
        PREPARE ss_tts_stmt FROM @ss_tts_sql;
        EXECUTE ss_tts_stmt;
        DEALLOCATE PREPARE ss_tts_stmt;
        -- No matching row leaves @ss_tts_out at the NULL seed above -- the common case (an implicit-tablespace
        -- table) -- with the 02000 consumed by this block's own handler.
        SET p_Tablespace = @ss_tts_out;
    END;
END //

DELIMITER ;
