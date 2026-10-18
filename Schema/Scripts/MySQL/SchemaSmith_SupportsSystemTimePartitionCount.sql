-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- SYSTEM_TIME partitioning by a count (PARTITIONS n, or no partition list at all) or with an INTERVAL ... STARTS:
-- MariaDB 10.5. MariaDB 10.3 and 10.4 accept only a list that names every partition (probed: 10.3.39 and 10.4.34 refuse
-- the count form with 1492 and STARTS with 1064; 10.5.29 accepts both). MySQL has no system versioning at any version.
DROP FUNCTION IF EXISTS `SchemaSmith_SupportsSystemTimePartitionCount`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SupportsSystemTimePartitionCount`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 1005, 1, 0);
END //
DELIMITER ;
