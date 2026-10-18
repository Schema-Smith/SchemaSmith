-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- 1 when a declared SYSTEM_TIME partitioning can be created on this server: the table must be system-versioned, and
-- below MariaDB 10.5 only a list naming every partition, with no STARTS, is accepted. The create and the compare of a
-- deployed table both ask, so a table the server created without the clause is not refused on every redeploy.
DROP FUNCTION IF EXISTS `SchemaSmith_SystemTimePartitionable`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SystemTimePartitionable`(p_HasList TINYINT, p_HasStarts TINYINT)
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  RETURN IF(SchemaSmith_SupportsSystemVersioning() = 1
            AND (SchemaSmith_SupportsSystemTimePartitionCount() = 1 OR (p_HasList = 1 AND p_HasStarts = 0)), 1, 0);
END //
DELIMITER ;
