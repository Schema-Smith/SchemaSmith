-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsRenameIndex`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsRenameIndex`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target supports the `ALTER TABLE ... RENAME INDEX old TO new` syntax:
  --   * MySQL: since 5.7.0 -> at/above our 5.7 floor, always yes.
  --   * MariaDB: since 10.5.2, read through the patch-level comparable.
  -- Below this (MariaDB 10.2-10.5), an index rename is emitted as drop-then-recreate instead (see
  -- SchemaSmith_MissingIndexesAndConstraintsQuench / SchemaSmith_IndexOnlyQuench).
  RETURN IF(VERSION() LIKE '%MariaDB%', SchemaSmith_ServerVersionPatchNum() >= 100502, 1);
END //

DELIMITER ;
