DROP PROCEDURE IF EXISTS `uspGetWhereUsedProductID`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `uspGetWhereUsedProductID`(IN p_StartProductID int,IN p_CheckDate datetime)
BEGIN
    
    WITH RECURSIVE `BOM_cte` (`ProductAssemblyID`, `ComponentID`, `ComponentDesc`, `PerAssemblyQty`, `StandardCost`, `ListPrice`, `BOMLevel`, `RecursionLevel`)
    AS (
        
        SELECT b.`ProductAssemblyID`, b.`ComponentID`, p.`Name`, b.`PerAssemblyQty`, p.`StandardCost`, p.`ListPrice`, b.`BOMLevel`, 0
        FROM `Production_BillOfMaterials` b
            INNER JOIN `Production_Product` p
            ON b.`ProductAssemblyID` = p.`ProductID`
        WHERE b.`ComponentID` = p_StartProductID
            AND p_CheckDate >= b.`StartDate`
            AND p_CheckDate <= COALESCE(b.`EndDate`, p_CheckDate)
        UNION ALL
        
        SELECT b.`ProductAssemblyID`, b.`ComponentID`, p.`Name`, b.`PerAssemblyQty`, p.`StandardCost`, p.`ListPrice`, b.`BOMLevel`, cte.`RecursionLevel` + 1
        FROM `BOM_cte` cte
            INNER JOIN `Production_BillOfMaterials` b
            ON cte.`ProductAssemblyID` = b.`ComponentID`
            INNER JOIN `Production_Product` p
            ON b.`ProductAssemblyID` = p.`ProductID`
        WHERE p_CheckDate >= b.`StartDate`
            AND p_CheckDate <= COALESCE(b.`EndDate`, p_CheckDate)
    )
    
    SELECT b.`ProductAssemblyID`, b.`ComponentID`, b.`ComponentDesc`, SUM(b.`PerAssemblyQty`) AS `TotalQuantity`, b.`StandardCost`, b.`ListPrice`, b.`BOMLevel`, b.`RecursionLevel`
    FROM `BOM_cte` b
    GROUP BY b.`ComponentID`, b.`ComponentDesc`, b.`ProductAssemblyID`, b.`BOMLevel`, b.`RecursionLevel`, b.`StandardCost`, b.`ListPrice`
    ORDER BY b.`BOMLevel`, b.`ProductAssemblyID`, b.`ComponentID`;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
