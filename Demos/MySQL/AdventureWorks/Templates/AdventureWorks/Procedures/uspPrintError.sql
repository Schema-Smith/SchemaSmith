DROP PROCEDURE IF EXISTS `uspPrintError`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `uspPrintError`()
BEGIN
    DECLARE v_errno INT;
    DECLARE v_msg TEXT;
    DECLARE v_sqlstate CHAR(5);

    GET DIAGNOSTICS CONDITION 1
        v_errno = MYSQL_ERRNO,
        v_msg = MESSAGE_TEXT,
        v_sqlstate = RETURNED_SQLSTATE;

    SELECT CONCAT('Error ', v_errno,
                  ', SQLState ', v_sqlstate,
                  ', Message: ', v_msg) AS ErrorInfo;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
