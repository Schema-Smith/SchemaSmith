-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_ServerVersionPatchNum`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_ServerVersionPatchNum`()
RETURNS INT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- major*10000+minor*100+patch, for the few gates that turn on a patch (MariaDB's RENAME COLUMN at 10.5.2).
  -- SchemaSmith_ServerVersionNum stays major*100+minor for everything else. A test that pins a version band with
  -- @schemasmith_version_override lands on that band's .0 here, unless it also sets the patch override.
  DECLARE v_raw VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_patch VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_digits INT DEFAULT 0;
  IF @schemasmith_version_patch_override IS NOT NULL THEN
    RETURN @schemasmith_version_patch_override;
  END IF;
  IF @schemasmith_version_override IS NOT NULL THEN
    RETURN @schemasmith_version_override * 100;
  END IF;
  SET v_raw = VERSION();  -- e.g. '8.0.36-log' / '10.5.2-MariaDB-1:10.5.2+maria~ubu2004'
  -- The patch part carries the build suffix ('12-MariaDB-ubu2404'), and a strict-mode CAST refuses the tail, so
  -- only its leading digits are cast. A loop rather than REGEXP_SUBSTR, which MySQL 5.7 does not have.
  SET v_patch = SUBSTRING_INDEX(SUBSTRING_INDEX(v_raw, '.', 3), '.', -1);
  WHILE v_digits < CHAR_LENGTH(v_patch) AND SUBSTRING(v_patch, v_digits + 1, 1) BETWEEN '0' AND '9' DO
    SET v_digits = v_digits + 1;
  END WHILE;
  RETURN CAST(SUBSTRING_INDEX(v_raw, '.', 1) AS UNSIGNED) * 10000
       + CAST(SUBSTRING_INDEX(SUBSTRING_INDEX(v_raw, '.', 2), '.', -1) AS UNSIGNED) * 100
       + IF(v_digits = 0, 0, CAST(LEFT(v_patch, v_digits) AS UNSIGNED));
END //

DELIMITER ;
