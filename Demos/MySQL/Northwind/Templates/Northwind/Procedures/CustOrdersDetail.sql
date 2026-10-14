DROP PROCEDURE IF EXISTS `CustOrdersDetail`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `CustOrdersDetail`(IN p_OrderID int)
BEGIN

SELECT `ProductName`,
    ROUND(Od.`UnitPrice`, 2) AS `UnitPrice`,
    `Quantity`,
    CAST(`Discount` * 100 AS SIGNED) AS `Discount`,
    ROUND(`Quantity` * (1 - `Discount`) * Od.`UnitPrice`, 2) AS `ExtendedPrice`
FROM `Products` P, `Order Details` Od
WHERE Od.`ProductID` = P.`ProductID` AND Od.`OrderID` = p_OrderID;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
