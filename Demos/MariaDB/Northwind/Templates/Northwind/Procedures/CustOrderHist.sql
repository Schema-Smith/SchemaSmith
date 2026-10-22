DROP PROCEDURE IF EXISTS `CustOrderHist`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `CustOrderHist`(IN p_CustomerID char(5))
BEGIN

SELECT `ProductName`, SUM(`Quantity`) AS `Total`
FROM `Products` P, `Order Details` OD, `Orders` O, `Customers` C
WHERE C.`CustomerID` = p_CustomerID
AND C.`CustomerID` = O.`CustomerID` AND O.`OrderID` = OD.`OrderID` AND OD.`ProductID` = P.`ProductID`
GROUP BY `ProductName`;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
