-- Routed to by SchemaSmith_ModifiedTableQuench when a table is removed from the product
-- (the engine CALLs SchemaSmith_CustomTableDrop(database, table) only when this proc
-- exists). Instead of dropping the table outright, soft-drop it: strip the constraints
-- whose names are database-scoped, rename the table aside under a recyclebin_ prefix, and
-- register it so it can be restored until its retention window expires.
--
-- MySQL stored procedures cannot declare default parameter values and the engine always
-- CALLs with exactly two arguments, so the retention window is fixed at 90 days here.
DROP PROCEDURE IF EXISTS `SchemaSmith_CustomTableDrop`;
DELIMITER //
CREATE PROCEDURE `SchemaSmith_CustomTableDrop`(IN p_DatabaseName VARCHAR(64), IN p_TableName VARCHAR(64))
  LANGUAGE SQL
  NOT DETERMINISTIC
  MODIFIES SQL DATA
  SQL SECURITY DEFINER
BEGIN
  DECLARE v_done      INT DEFAULT 0;
  DECLARE v_stmt      LONGTEXT;
  DECLARE v_recycled  VARCHAR(128);
  DECLARE v_modifier  INT DEFAULT 0;
  DECLARE v_cnt       INT;

  -- All constraints whose names are unique per-database (so they would collide across
  -- recycled copies) and must be dropped before the rename: foreign keys that reference
  -- this table, this table's own foreign keys, and this table's CHECK constraints. Indexes
  -- and the primary key are per-table scoped, so they ride along with the rename harmlessly
  -- and a later quench reconciles them.
  DECLARE drop_cur CURSOR FOR
    SELECT CONCAT('ALTER TABLE `', rc.CONSTRAINT_SCHEMA, '`.`', rc.TABLE_NAME,
                  '` DROP FOREIGN KEY `', rc.CONSTRAINT_NAME, '`')
      FROM information_schema.REFERENTIAL_CONSTRAINTS rc
      WHERE rc.CONSTRAINT_SCHEMA = p_DatabaseName
        AND rc.REFERENCED_TABLE_NAME = p_TableName
        AND rc.TABLE_NAME <> p_TableName
    UNION ALL
    SELECT CONCAT('ALTER TABLE `', p_DatabaseName, '`.`', p_TableName,
                  '` DROP FOREIGN KEY `', rc.CONSTRAINT_NAME, '`')
      FROM information_schema.REFERENTIAL_CONSTRAINTS rc
      WHERE rc.CONSTRAINT_SCHEMA = p_DatabaseName
        AND rc.TABLE_NAME = p_TableName
    UNION ALL
    SELECT CONCAT('ALTER TABLE `', p_DatabaseName, '`.`', p_TableName,
                  '` DROP CHECK `', tc.CONSTRAINT_NAME, '`')
      FROM information_schema.TABLE_CONSTRAINTS tc
      WHERE tc.TABLE_SCHEMA = p_DatabaseName
        AND tc.TABLE_NAME = p_TableName
        AND tc.CONSTRAINT_TYPE = 'CHECK';
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_done = 1;

  -- Validate the table exists
  SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
    WHERE table_schema = p_DatabaseName AND table_name = p_TableName;
  IF v_cnt = 0 THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SchemaSmith_CustomTableDrop: table does not exist';
  END IF;

  -- Generate a unique recycled name: recyclebin_<table>, suffixed _N on collision
  SET v_recycled = CONCAT('recyclebin_', p_TableName);
  SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
    WHERE table_schema = p_DatabaseName AND table_name = v_recycled;
  WHILE v_cnt > 0 DO
    SET v_modifier = v_modifier + 1;
    SET v_recycled = CONCAT('recyclebin_', p_TableName, '_', v_modifier);
    SELECT COUNT(*) INTO v_cnt FROM information_schema.tables
      WHERE table_schema = p_DatabaseName AND table_name = v_recycled;
  END WHILE;

  -- Drop the database-scoped constraints (inbound FKs are also cleared by the engine before
  -- the hook is called; dropping them here keeps the proc self-sufficient if called directly)
  OPEN drop_cur;
  drop_loop: LOOP
    FETCH drop_cur INTO v_stmt;
    IF v_done = 1 THEN LEAVE drop_loop; END IF;
    SET @ddl = v_stmt;
    PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;
  END LOOP;
  CLOSE drop_cur;

  -- Rename the table aside under the recyclebin_ prefix
  SET @ddl = CONCAT('RENAME TABLE `', p_DatabaseName, '`.`', p_TableName,
                    '` TO `', p_DatabaseName, '`.`', v_recycled, '`');
  PREPARE s FROM @ddl; EXECUTE s; DEALLOCATE PREPARE s;

  -- Register the recycled copy (retention fixed at 90 days; see header)
  INSERT INTO recyclebin_Registry (OriginalSchema, OriginalName, RecycledName, RecycledDate, RetentionDays, ExpirationDate)
    VALUES (p_DatabaseName, p_TableName, v_recycled, NOW(), 90, DATE_ADD(NOW(), INTERVAL 90 DAY));
END //
DELIMITER ;
