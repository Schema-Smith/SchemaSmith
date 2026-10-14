-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_ServerVersionNum`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_ServerVersionNum`()
RETURNS INT
NOT DETERMINISTIC
NO SQL
BEGIN
  DECLARE v_raw VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  -- Override (test affordance) wins; otherwise parse VERSION() to major*100+minor.
  IF @schemasmith_version_override IS NOT NULL THEN
    RETURN @schemasmith_version_override;
  END IF;
  SET v_raw = VERSION();  -- e.g. '8.0.36' / '8.4.1' (a build suffix on the patch part is ignored)
  RETURN CAST(SUBSTRING_INDEX(v_raw, '.', 1) AS UNSIGNED) * 100
       + CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(v_raw, '.', 2), '.', -1) AS UNSIGNED);
END //

DELIMITER ;
