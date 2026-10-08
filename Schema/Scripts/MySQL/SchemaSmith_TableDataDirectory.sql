-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableDataDirectory//

CREATE PROCEDURE SchemaSmith_TableDataDirectory(
    IN p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    OUT p_DataDirectory VARCHAR(512) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
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
    -- 5.7 keeps the same facts under the SYS_-prefixed names, chosen by version inside the dynamic string so only the
    -- present ones are ever parsed; reading NULL there refused every redeploy of a table that declared a directory.
    -- InnoDB names the table, and its .ibd file, in the filename-encoded form (SchemaSmith_InnodbName).
    BEGIN
        DECLARE v_suffix VARCHAR(600) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
        DECLARE CONTINUE HANDLER FOR NOT FOUND SET @ss_tdd_out = NULL;
        SET @ss_tdd_name = CONCAT(SchemaSmith_InnodbName(p_Schema), '/', SchemaSmith_InnodbName(p_Table));
        SET @ss_tdd_out = NULL;
        SET @ss_tdd_sql = CONCAT('SELECT df.PATH INTO @ss_tdd_out
FROM INFORMATION_SCHEMA.', IF(SchemaSmith_ServerVersionNum() < 800, 'INNODB_SYS_DATAFILES', 'INNODB_DATAFILES'), ' df
JOIN INFORMATION_SCHEMA.', IF(SchemaSmith_ServerVersionNum() < 800, 'INNODB_SYS_TABLES', 'INNODB_TABLES'), ' it ON it.SPACE = df.SPACE
WHERE it.NAME = @ss_tdd_name
LIMIT 1');
        PREPARE ss_tdd_stmt FROM @ss_tdd_sql;
        EXECUTE ss_tdd_stmt;
        DEALLOCATE PREPARE ss_tdd_stmt;
        SET v_suffix = CONCAT('/', @ss_tdd_name, '.ibd');
        IF @ss_tdd_out IS NULL OR LEFT(@ss_tdd_out, 2) = './' THEN
            SET p_DataDirectory = NULL;
        ELSEIF RIGHT(@ss_tdd_out, CHAR_LENGTH(v_suffix)) = v_suffix THEN
            SET p_DataDirectory = TRIM(TRAILING '/' FROM LEFT(@ss_tdd_out, CHAR_LENGTH(@ss_tdd_out) - CHAR_LENGTH(v_suffix)));
        ELSE
            SET p_DataDirectory = NULL;
        END IF;
    END;
END //

DELIMITER ;
