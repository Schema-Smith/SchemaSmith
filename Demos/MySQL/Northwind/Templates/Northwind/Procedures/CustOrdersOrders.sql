DROP PROCEDURE IF EXISTS `CustOrdersOrders`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
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
