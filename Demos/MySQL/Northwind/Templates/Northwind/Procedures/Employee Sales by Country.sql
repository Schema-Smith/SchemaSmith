DROP PROCEDURE IF EXISTS `Employee Sales by Country`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `Employee Sales by Country`(IN p_Beginning_Date datetime,IN p_Ending_Date datetime)
BEGIN

SELECT `Employees`.`Country`, `Employees`.`LastName`, `Employees`.`FirstName`, `Orders`.`ShippedDate`, `Orders`.`OrderID`, `Order Subtotals`.`Subtotal` AS `SaleAmount`
FROM `Employees` INNER JOIN
	(`Orders` INNER JOIN `Order Subtotals` ON `Orders`.`OrderID` = `Order Subtotals`.`OrderID`)
	ON `Employees`.`EmployeeID` = `Orders`.`EmployeeID`
WHERE `Orders`.`ShippedDate` BETWEEN p_Beginning_Date AND p_Ending_Date;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
