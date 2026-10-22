DROP PROCEDURE IF EXISTS `recyclebin_RestoreTable`;
SET @ss_saved_sql_mode = @@SESSION.sql_mode;
SET SESSION sql_mode = 'ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';
DELIMITER //
CREATE PROCEDURE `recyclebin_RestoreTable`(IN p_RecycledName VARCHAR(128))
    MODIFIES SQL DATA
rt: BEGIN
  DECLARE v_orig_schema VARCHAR(64);
  DECLARE v_orig_name   VARCHAR(64);
  DECLARE v_cnt         INT;
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_orig_name = NULL;

  SELECT OriginalSchema, OriginalName INTO v_orig_schema, v_orig_name
    FROM recyclebin_Registry WHERE RecycledName = p_RecycledName;
  IF v_orig_name IS NULL THEN
    LEAVE rt;
  END IF;

  -- Recycled table gone (manually dropped or already restored)? Clear the stale entry.
  SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
    WHERE table_schema = v_orig_schema AND table_name = p_RecycledName;
  IF v_cnt = 0 THEN
    DELETE FROM recyclebin_Registry WHERE RecycledName = p_RecycledName;
    LEAVE rt;
  END IF;

  -- Refuse to clobber an existing table at the target
  SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
    WHERE table_schema = v_orig_schema AND table_name = v_orig_name;
  IF v_cnt > 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'recyclebin_RestoreTable: target table already exists';
  END IF;

  SET @ddl = CONCAT('RENAME TABLE `', v_orig_schema, '`.`', p_RecycledName,
                    '` TO `', v_orig_schema, '`.`', v_orig_name, '`');
  PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

  DELETE FROM recyclebin_Registry WHERE RecycledName = p_RecycledName;
END //
DELIMITER ;
SET SESSION sql_mode = @ss_saved_sql_mode;
