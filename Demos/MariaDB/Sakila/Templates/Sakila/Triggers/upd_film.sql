DROP TRIGGER IF EXISTS `upd_film`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE TRIGGER `upd_film`
  AFTER UPDATE
  ON `film` 
  FOR EACH ROW 
BEGIN
BEGIN
BEGIN
    IF (old.title != new.title) or (old.description != new.description)
    THEN
        UPDATE film_text
            SET title=new.title,
                description=new.description,
                film_id=new.film_id
        WHERE film_id=old.film_id;
    END IF;
  END;
END;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
