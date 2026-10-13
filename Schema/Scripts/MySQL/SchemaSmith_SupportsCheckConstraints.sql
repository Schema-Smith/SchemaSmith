-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_SupportsCheckConstraints`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SupportsCheckConstraints`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when the target enforces CHECK constraints AND exposes INFORMATION_SCHEMA.CHECK_CONSTRAINTS:
  --   * MariaDB: enforcement since 10.2.1, the view since 10.2.22.
  --   * MySQL: both arrived at 8.0.16.
  -- Below this CHECK constraints are parsed and ignored (MySQL) or cannot be read back (MariaDB), so the CHECK
  -- catalog reads are version-gated and CHECK emit degrades via UnsupportedFeaturePolicy.
  RETURN IF((VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 100222)
            OR (VERSION() NOT LIKE '%MariaDB%' AND SchemaSmith_ServerVersionPatchNum() >= 80016), 1, 0);
END //

DELIMITER ;
