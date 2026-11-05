-- Routed to by SchemaSmith_MissingTableAndColumnQuench before it creates a table that is in
-- the product but missing from the target (the engine CALLs SchemaSmith_CustomTableRestore
-- (database, table) only when this proc exists). If a recycled copy of the table exists,
-- restore it (preserving its data) rather than letting the quench create an empty one;
-- otherwise do nothing and let creation proceed.
DROP PROCEDURE IF EXISTS `SchemaSmith_CustomTableRestore`;
DELIMITER //
CREATE PROCEDURE `SchemaSmith_CustomTableRestore`(IN p_DatabaseName VARCHAR(64), IN p_TableName VARCHAR(64))
  LANGUAGE SQL
  NOT DETERMINISTIC
  MODIFIES SQL DATA
  SQL SECURITY DEFINER
ctr: BEGIN
  DECLARE v_recycled    VARCHAR(128) DEFAULT NULL;
  DECLARE v_has_registry INT;
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_recycled = NULL;

  SELECT COUNT(*) INTO v_has_registry FROM information_schema.tables
    WHERE table_schema = p_DatabaseName AND table_name = 'recyclebin_Registry';
  IF v_has_registry = 0 THEN
    LEAVE ctr;
  END IF;

  -- Most recently recycled copy matching the original schema (database) and table name
  SELECT RecycledName INTO v_recycled
    FROM recyclebin_Registry
    WHERE OriginalSchema = p_DatabaseName AND OriginalName = p_TableName
    ORDER BY RecycledDate DESC
    LIMIT 1;

  IF v_recycled IS NULL THEN
    LEAVE ctr;
  END IF;

  CALL recyclebin_RestoreTable(v_recycled);
END //
DELIMITER ;
