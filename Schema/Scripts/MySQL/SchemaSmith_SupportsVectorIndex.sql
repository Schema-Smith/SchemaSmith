-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- A VECTOR index (MariaDB 11.7). MySQL Community has none at any version. Below the floor a declared one is skipped
-- and a downgrade is recorded (the fail policy refuses it), in SchemaSmith_DegradeVectorIndexes.
DROP FUNCTION IF EXISTS `SchemaSmith_SupportsVectorIndex`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SupportsVectorIndex`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 1107, 1, 0);
END //
DELIMITER ;
