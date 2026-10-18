-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- SYSTEM_TIME partitioning with AUTO, where the engine adds history partitions as they fill: MariaDB 10.9.1 (refused
-- with 1064 on 10.6.28, accepted on 10.11.19). Below it the table is partitioned without AUTO and a downgrade recorded.
DROP FUNCTION IF EXISTS `SchemaSmith_SupportsSystemTimeAutoPartitions`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SupportsSystemTimeAutoPartitions`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 100901, 1, 0);
END //
DELIMITER ;
