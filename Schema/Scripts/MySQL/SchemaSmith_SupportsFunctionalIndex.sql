-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsFunctionalIndex`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsFunctionalIndex`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target supports an index on an expression (a functional/expression key part): the key part
  -- has no COLUMN_NAME and instead reports its text via INFORMATION_SCHEMA.STATISTICS.EXPRESSION, which is
  -- also what SHOW CREATE TABLE renders as (( <expr> )).
  --   * MySQL: since 8.0.13, which the patch-level version shows.
  --   * MariaDB: no equivalent in this form -- a MariaDB "functional index" is a regular index on a
  --     generated/virtual column, which extraction already reports as a plain column. Always 0.
  --   * Multi-valued indexes (CAST(col->'$.path' AS type ARRAY)) are a functional key part too --
  --     same NULL COLUMN_NAME / populated EXPRESSION shape -- so they ride this same gate. Their own
  --     threshold is MySQL 8.0.17, so on 8.0.13-8.0.16 one is attempted and the engine refuses it.
  RETURN IF(VERSION() LIKE '%MariaDB%', 0, IF(SchemaSmith_ServerVersionPatchNum() >= 80013, 1, 0));
END //

DELIMITER ;
