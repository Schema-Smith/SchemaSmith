-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_KeepHistoryForNewVersionedTable//

CREATE PROCEDURE SchemaSmith_KeepHistoryForNewVersionedTable(IN p_On TINYINT)
SQL SECURITY DEFINER
BEGIN
    -- Sets system_versioning_alter_history to KEEP around the index work on a system-versioned table created in this
    -- run, and puts the session back after. No-op on MySQL, which has no system versioning; the MariaDb override does
    -- the work. A separate file for the reason SchemaSmith_SetSystemVersioningAlterHistory gives: MySQL will not CREATE
    -- a routine that mentions the variable.
    SET @ss_unused_keep_history = p_On;
END //

DELIMITER ;
