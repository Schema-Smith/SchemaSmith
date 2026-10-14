DROP PROCEDURE IF EXISTS `film_in_stock`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `film_in_stock`(IN p_film_id int,IN p_store_id int,OUT p_film_count int)
    READS SQL DATA
BEGIN
     SELECT inventory_id
     FROM inventory
     WHERE film_id = p_film_id
     AND store_id = p_store_id
     AND inventory_in_stock(inventory_id);

     SELECT FOUND_ROWS() INTO p_film_count;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
