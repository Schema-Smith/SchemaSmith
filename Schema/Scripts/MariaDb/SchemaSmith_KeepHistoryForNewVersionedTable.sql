-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_KeepHistoryForNewVersionedTable//

CREATE PROCEDURE SchemaSmith_KeepHistoryForNewVersionedTable(IN p_On TINYINT)
SQL SECURITY DEFINER
BEGIN
    -- MariaDb override of the shared MySQL no-op. MariaDB refuses a UNIQUE index or constraint on a system-versioned
    -- table unless system_versioning_alter_history is KEEP (4119, measured on 10.3, 10.6, 11.4 and 13.0), so a versioned
    -- table with a unique key could not be deployed fresh. KEEP exists to protect history from being rewritten; a table
    -- created in this run has none, so the index pass sets it around that table's index work alone. p_On = 1 saves the
    -- session value and sets KEEP; p_On = 0 puts the saved value back, so an operator's own setting is never lost and
    -- KEEP never reaches an existing table's work. Named only inside string literals, as in
    -- SchemaSmith_SetSystemVersioningAlterHistory, because the variable is resolved at CREATE time and is absent on 10.2.
    IF SchemaSmith_ServerVersionNum() >= 1003 THEN
        IF p_On = 1 THEN
            SET @ss_keep_sql = 'SET @ss_keep_prior = @@SESSION.system_versioning_alter_history';
            PREPARE ss_keep_stmt FROM @ss_keep_sql;
            EXECUTE ss_keep_stmt;
            DEALLOCATE PREPARE ss_keep_stmt;
            SET @ss_keep_sql = 'SET SESSION system_versioning_alter_history = 1';
        ELSE
            SET @ss_keep_sql = 'SET SESSION system_versioning_alter_history = @ss_keep_prior';
        END IF;
        PREPARE ss_keep_stmt FROM @ss_keep_sql;
        EXECUTE ss_keep_stmt;
        DEALLOCATE PREPARE ss_keep_stmt;
    END IF;
END //

DELIMITER ;
