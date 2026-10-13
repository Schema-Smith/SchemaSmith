-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsInvisibleColumn`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsInvisibleColumn`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target supports hiding a column from SELECT * / INSERT-without-column-list via the
  -- INVISIBLE keyword (`ALTER TABLE t ADD c INT INVISIBLE`). Unlike the invisible-INDEX feature
  -- (INVISIBLE on MySQL, IGNORED on MariaDB), the column keyword is the SAME on both engines --
  -- only the introduction version differs:
  --   * MySQL: since 8.0.23, which the patch-level version shows.
  --   * MariaDB: since 10.3.0 -> ServerVersionNum() >= 1003.
  -- Below this the keyword is a hard syntax error (unlike a DESC key part, which parses-and-ignores).
  -- So a declared invisible column degrades: the INVISIBLE clause is suppressed at ColumnScript build
  -- time (see ParseTableJson), the modified-column compare ignores the visibility difference so the
  -- deploy stays idempotent, and one 'downgraded' manifest row is recorded (or, under
  -- UnsupportedFeaturePolicy 'fail', the deploy aborts) -- mirrors SchemaSmith_SupportsInvisibleIndex.
  RETURN IF((VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 1003)
            OR (VERSION() NOT LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 80023), 1, 0);
END //

DELIMITER ;
