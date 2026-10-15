-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_CapturePermissions//

-- Renders the grants a DROP takes off a routine, as the GRANT statements that put them back, one per grantee, as a
-- single-row result. The quench calls this before a script that drops its routine and
-- SchemaSmith_ReapplyPermissions with the result once the routine exists again. A table's grants need none of this:
-- MySQL keys them by name, so they survive a rename swap on their own.
--
-- Routine grants live only in mysql.procs_priv (INFORMATION_SCHEMA has no view of them). When the deploy user may
-- not read that table the result is NULL and the run says why, rather than failing a deploy over something it
-- cannot see.
CREATE PROCEDURE SchemaSmith_CapturePermissions(
    IN p_Db VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_Kind VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
)
READS SQL DATA
BEGIN
    DECLARE v_Script TEXT DEFAULT NULL;
    DECLARE v_Unreadable TINYINT DEFAULT 0;
    DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
    BEGIN
        SET v_Unreadable = 1;
        SET v_Script = NULL;
    END;

    SELECT GROUP_CONCAT(
             CONCAT('GRANT ',
                    CONCAT_WS(', ',
                      IF(FIND_IN_SET('Execute', pp.Proc_priv) > 0, 'EXECUTE', NULL),
                      IF(FIND_IN_SET('Alter Routine', pp.Proc_priv) > 0, 'ALTER ROUTINE', NULL)),
                    ' ON ', p_Kind, ' `', REPLACE(p_Db, '`', '``'), '`.`', REPLACE(p_Name, '`', '``'), '` TO ''',
                    REPLACE(CONVERT(pp.User USING utf8mb4), '''', ''''''), '''@''', REPLACE(CONVERT(pp.Host USING utf8mb4), '''', ''''''), '''',
                    IF(FIND_IN_SET('Grant', pp.Proc_priv) > 0, ' WITH GRANT OPTION', ''), ';')
             ORDER BY pp.User, pp.Host SEPARATOR '\n')
      INTO v_Script
      FROM mysql.procs_priv pp
     WHERE CONVERT(pp.Db USING utf8mb4) COLLATE utf8mb4_general_ci = p_Db COLLATE utf8mb4_general_ci
       AND CONVERT(pp.Routine_name USING utf8mb4) COLLATE utf8mb4_general_ci = p_Name COLLATE utf8mb4_general_ci
       AND CONVERT(pp.Routine_type USING utf8mb4) COLLATE utf8mb4_general_ci = p_Kind COLLATE utf8mb4_general_ci
       AND (FIND_IN_SET('Execute', pp.Proc_priv) > 0 OR FIND_IN_SET('Alter Routine', pp.Proc_priv) > 0);

    IF v_Unreadable = 1 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  The grants on ', p_Kind, ' ', p_Name, ' could not be read (SELECT on mysql.procs_priv is needed), so they are not carried across its re-create.'));
    END IF;

    SELECT v_Script AS Script;
END//

DELIMITER ;
