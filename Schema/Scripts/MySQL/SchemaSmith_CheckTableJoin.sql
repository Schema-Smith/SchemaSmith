-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_CheckTableJoin`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_CheckTableJoin`()
RETURNS VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
NOT DETERMINISTIC
NO SQL
BEGIN
  -- The predicate that pairs a CHECK_CONSTRAINTS row (alias cc) with its TABLE_CONSTRAINTS row (alias tc) by table,
  -- spliced into the dynamic SQL that joins them. MariaDB names CHECK constraints per table, so schema and name alone
  -- match every table's same-named check (MA-009, MA-c14). MySQL names them per database and its CHECK_CONSTRAINTS has
  -- no TABLE_NAME, which is also why this is a string for dynamic SQL: naming the column in a static query would fail
  -- on MySQL.
  RETURN IF(VERSION() LIKE '%MariaDB%', ' AND cc.TABLE_NAME = tc.TABLE_NAME', '');
END //

DELIMITER ;
