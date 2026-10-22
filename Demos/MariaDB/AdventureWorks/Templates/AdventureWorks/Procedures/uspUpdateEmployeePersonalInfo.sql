DROP PROCEDURE IF EXISTS `uspUpdateEmployeePersonalInfo`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `uspUpdateEmployeePersonalInfo`(IN p_BusinessEntityID int,IN p_NationalIDNumber varchar(15),IN p_BirthDate datetime,IN p_MaritalStatus char(1),IN p_Gender char(1))
BEGIN
    DECLARE v_ErrorLogID INT;

    DECLARE EXIT HANDLER FOR SQLEXCEPTION
    BEGIN
        CALL `uspLogError`(v_ErrorLogID);
    END;

    UPDATE `HumanResources_Employee`
    SET `NationalIDNumber` = p_NationalIDNumber
        ,`BirthDate` = p_BirthDate
        ,`MaritalStatus` = p_MaritalStatus
        ,`Gender` = p_Gender
    WHERE `BusinessEntityID` = p_BusinessEntityID;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
