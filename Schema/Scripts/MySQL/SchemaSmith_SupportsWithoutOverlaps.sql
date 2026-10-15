-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- A unique or primary key declared WITHOUT OVERLAPS over an application-time period: MariaDB 10.5.3. MySQL has no
-- periods at any version. Below the floor the key is created over the period's end and start columns and a downgrade
-- is recorded (the fail policy refuses it in SchemaSmith_RefuseUnsupportedIndexes).
DROP FUNCTION IF EXISTS `SchemaSmith_SupportsWithoutOverlaps`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SupportsWithoutOverlaps`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 100503, 1, 0);
END //
DELIMITER ;
