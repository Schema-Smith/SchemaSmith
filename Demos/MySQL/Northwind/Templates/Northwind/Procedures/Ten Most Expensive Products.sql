DROP PROCEDURE IF EXISTS `Ten Most Expensive Products`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `Ten Most Expensive Products`()
BEGIN

SELECT `Products`.`ProductName` AS `TenMostExpensiveProducts`, `Products`.`UnitPrice`
FROM `Products`
ORDER BY `Products`.`UnitPrice` DESC
LIMIT 10;

END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
