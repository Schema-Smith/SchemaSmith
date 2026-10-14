DROP PROCEDURE IF EXISTS `Sales by Year`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `Sales by Year`(IN p_Beginning_Date datetime,IN p_Ending_Date datetime)
BEGIN

SELECT `Orders`.`ShippedDate`, `Orders`.`OrderID`, `Order Subtotals`.`Subtotal`, YEAR(`ShippedDate`) AS `Year`
FROM `Orders` INNER JOIN `Order Subtotals` ON `Orders`.`OrderID` = `Order Subtotals`.`OrderID`
WHERE `Orders`.`ShippedDate` BETWEEN p_Beginning_Date AND p_Ending_Date;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
