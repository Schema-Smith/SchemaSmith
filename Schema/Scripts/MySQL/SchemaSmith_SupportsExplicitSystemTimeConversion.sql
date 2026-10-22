-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsExplicitSystemTimeConversion`;
DELIMITER //
-- MariaDB 11.7 lets a system-versioned table with the implicit row_start/row_end pair take explicit ROW START / ROW END
-- columns (ALTER TABLE ... ADD COLUMN ... ROW START, ADD COLUMN ... ROW END, ADD PERIOD FOR SYSTEM_TIME); 11.4 refuses it
-- (4134, "Duplicate ROW START column"), probed on 10.3, 11.4, 11.8 and 13.0. MySQL has no system versioning.
CREATE FUNCTION `SchemaSmith_SupportsExplicitSystemTimeConversion`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 110700, 1, 0);
END //
DELIMITER ;
