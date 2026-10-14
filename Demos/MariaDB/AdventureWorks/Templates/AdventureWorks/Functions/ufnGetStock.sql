DROP FUNCTION IF EXISTS `ufnGetStock`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE FUNCTION `ufnGetStock`(p_ProductID int) RETURNS int(11)
    READS SQL DATA
BEGIN
    DECLARE ret INT;

    SELECT SUM(p.`Quantity`) INTO ret
    FROM `Production_ProductInventory` p
    WHERE p.`ProductID` = p_ProductID
        AND p.`LocationID` = 6;

    IF (ret IS NULL) THEN
        SET ret = 0;
    END IF;

    RETURN ret;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
