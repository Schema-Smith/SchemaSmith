DROP PROCEDURE IF EXISTS `Ten Most Expensive Products`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
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
