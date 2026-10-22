DROP FUNCTION IF EXISTS `ufnGetProductStandardCost`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE FUNCTION `ufnGetProductStandardCost`(p_ProductID int,p_OrderDate datetime) RETURNS decimal(19,4)
    READS SQL DATA
BEGIN
    DECLARE StandardCost DECIMAL(19,4);

    SELECT pch.`StandardCost` INTO StandardCost
    FROM `Production_Product` p
        INNER JOIN `Production_ProductCostHistory` pch
        ON p.`ProductID` = pch.`ProductID`
            AND p.`ProductID` = p_ProductID
            AND p_OrderDate BETWEEN pch.`StartDate` AND COALESCE(pch.`EndDate`, CAST('9999-12-31' AS DATETIME));

    RETURN StandardCost;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
