DROP PROCEDURE IF EXISTS `recyclebin_CleanupJob`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `recyclebin_CleanupJob`()
    MODIFIES SQL DATA
BEGIN
  DECLARE v_done     INT DEFAULT 0;
  DECLARE v_recycled VARCHAR(128);
  DECLARE v_db       VARCHAR(64);
  DECLARE v_cnt      INT;

  DECLARE purge_cur CURSOR FOR
    SELECT RecycledName, OriginalSchema FROM recyclebin_Registry WHERE ExpirationDate <= NOW();
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_done = 1;

  -- 1. Adopt untracked recyclebin_ tables in the current database (present but not in the
  --    registry), so a table renamed aside out-of-band still gets a retention window.
  INSERT INTO recyclebin_Registry (OriginalSchema, OriginalName, RecycledName, RecycledDate, RetentionDays, ExpirationDate)
    SELECT DATABASE(), t.TABLE_NAME, t.TABLE_NAME, NOW(), 90, DATE_ADD(NOW(), INTERVAL 90 DAY)
      FROM information_schema.tables t
      WHERE t.table_schema = DATABASE()
        AND t.table_name LIKE 'recyclebin\_%'
        AND t.table_name <> 'recyclebin_Registry'
        AND NOT EXISTS (SELECT 1 FROM recyclebin_Registry r WHERE r.RecycledName = t.TABLE_NAME);

  -- 2. Drop tables that have exceeded their retention period
  OPEN purge_cur;
  purge_loop: LOOP
    FETCH purge_cur INTO v_recycled, v_db;
    IF v_done = 1 THEN LEAVE purge_loop; END IF;
    SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
      WHERE table_schema = v_db AND table_name = v_recycled;
    IF v_cnt > 0 THEN
      SET @ddl = CONCAT('DROP TABLE `', v_db, '`.`', v_recycled, '`');
      PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;
    END IF;
    DELETE FROM recyclebin_Registry WHERE RecycledName = v_recycled;
  END LOOP;
  CLOSE purge_cur;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
