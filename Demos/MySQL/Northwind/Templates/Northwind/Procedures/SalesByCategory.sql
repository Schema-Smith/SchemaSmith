DROP PROCEDURE IF EXISTS `SalesByCategory`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `SalesByCategory`(IN p_CategoryName varchar(15),IN p_OrdYear varchar(4))
BEGIN

IF p_OrdYear IS NULL THEN
	SET p_OrdYear = '1998';
END IF;

IF p_OrdYear != '1996' AND p_OrdYear != '1997' AND p_OrdYear != '1998' THEN
	SET p_OrdYear = '1998';
END IF;

SELECT `ProductName`,
	ROUND(SUM(CAST(OD.`Quantity` * (1 - OD.`Discount`) * OD.`UnitPrice` AS DECIMAL(14,2))), 0) AS `TotalPurchase`
FROM `Order Details` OD, `Orders` O, `Products` P, `Categories` C
WHERE OD.`OrderID` = O.`OrderID`
	AND OD.`ProductID` = P.`ProductID`
	AND P.`CategoryID` = C.`CategoryID`
	AND C.`CategoryName` = p_CategoryName
	AND SUBSTRING(DATE_FORMAT(O.`OrderDate`, '%Y/%m/%d'), 1, 4) = p_OrdYear
GROUP BY `ProductName`
ORDER BY `ProductName`;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
