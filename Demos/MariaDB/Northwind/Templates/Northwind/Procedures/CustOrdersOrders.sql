DROP PROCEDURE IF EXISTS `CustOrdersOrders`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `CustOrdersOrders`(IN p_CustomerID char(5))
BEGIN

SELECT `OrderID`,
	`OrderDate`,
	`RequiredDate`,
	`ShippedDate`
FROM `Orders`
WHERE `CustomerID` = p_CustomerID
ORDER BY `OrderID`;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
