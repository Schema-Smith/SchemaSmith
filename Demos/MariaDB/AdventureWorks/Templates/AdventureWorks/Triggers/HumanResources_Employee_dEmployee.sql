DROP TRIGGER IF EXISTS `HumanResources_Employee_dEmployee`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE TRIGGER `HumanResources_Employee_dEmployee`
  BEFORE DELETE
  ON `HumanResources_Employee` 
  FOR EACH ROW 
BEGIN
BEGIN
BEGIN
    SIGNAL SQLSTATE '45000'
        SET MESSAGE_TEXT = 'Employees cannot be deleted. They can only be marked as not current.';
END;
END;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
