DROP PROCEDURE IF EXISTS `uspLogError`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `uspLogError`(OUT p_ErrorLogID int)
proc_body: BEGIN
    DECLARE v_errno INT;
    DECLARE v_severity INT DEFAULT 0;
    DECLARE v_state INT DEFAULT 0;
    DECLARE v_procedure VARCHAR(126) DEFAULT NULL;
    DECLARE v_lineno INT DEFAULT 0;
    DECLARE v_msg TEXT;
    DECLARE v_sqlstate CHAR(5);
    DECLARE v_errmsg TEXT;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        
        CALL `uspPrintError`();
        SET p_ErrorLogID = -1;
    END;

    
    
    SET p_ErrorLogID = 0;

    GET DIAGNOSTICS CONDITION 1
        v_errno = MYSQL_ERRNO,
        v_msg = MESSAGE_TEXT,
        v_sqlstate = RETURNED_SQLSTATE;

    
    IF v_errno IS NULL OR v_errno = 0 THEN
        LEAVE proc_body;
    END IF;

    INSERT INTO `ErrorLog`
        (
        `UserName`,
        `ErrorNumber`,
        `ErrorSeverity`,
        `ErrorState`,
        `ErrorProcedure`,
        `ErrorLine`,
        `ErrorMessage`
        )
    VALUES
        (
        CURRENT_USER(),
        v_errno,
        v_severity,
        v_state,
        v_procedure,
        v_lineno,
        v_msg
        );

    
    SET p_ErrorLogID = LAST_INSERT_ID();
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
