DROP FUNCTION IF EXISTS `ufnGetProductListPrice`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE FUNCTION `ufnGetProductListPrice`(p_ProductID int,p_OrderDate datetime) RETURNS decimal(19,4)
    READS SQL DATA
BEGIN
    DECLARE ListPrice DECIMAL(19,4);

    SELECT plph.`ListPrice` INTO ListPrice
    FROM `Production_Product` p
        INNER JOIN `Production_ProductListPriceHistory` plph
        ON p.`ProductID` = plph.`ProductID`
            AND p.`ProductID` = p_ProductID
            AND p_OrderDate BETWEEN plph.`StartDate` AND COALESCE(plph.`EndDate`, CAST('9999-12-31' AS DATETIME));

    RETURN ListPrice;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
