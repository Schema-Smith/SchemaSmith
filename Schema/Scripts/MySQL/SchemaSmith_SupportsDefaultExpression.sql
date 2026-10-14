-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsDefaultExpression`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsDefaultExpression`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target accepts a non-literal expression in a column DEFAULT (extraction already
  -- recognises this stored form -- COLUMN_DEFAULT LIKE '(%' -- see GenerateTableJson):
  --   * MariaDB: MDEV-10134 landed in 10.2.1, the very first point release of the 10.2 series -- at/below
  --     our 10.2 floor, so MariaDB always supports it and this branch never triggers.
  --   * MySQL: since 8.0.13, which the patch-level version shows.
  RETURN IF(VERSION() LIKE '%MariaDB%' OR SchemaSmith_ServerVersionPatchNum() >= 80013, 1, 0);
END //

DELIMITER ;
