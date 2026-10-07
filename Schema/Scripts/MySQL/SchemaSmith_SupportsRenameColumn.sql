-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsRenameColumn`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsRenameColumn`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target supports the `ALTER TABLE ... RENAME COLUMN old TO new` syntax:
  --   * MySQL: since 8.0.0 -> major >= 8.
  --   * MariaDB: since 10.5.2, read through the patch-level comparable.
  -- Below this (MySQL 5.7 / MariaDB 10.2-10.5), the rename is emitted as a version-agnostic
  -- `CHANGE COLUMN old new <current-definition>` instead (see SchemaSmith_MissingTableAndColumnQuench).
  RETURN IF((VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 100502)
            OR (VERSION() NOT LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 800), 1, 0);
END //

DELIMITER ;
