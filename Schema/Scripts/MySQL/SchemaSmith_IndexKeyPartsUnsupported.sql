-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_IndexKeyPartsUnsupported`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_IndexKeyPartsUnsupported`(
    p_IndexColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  -- 1 when a declared index has a key part this target cannot build: a functional key part where functional indexes
  -- are not supported, or a multi-valued one (CAST(... AS ... ARRAY), MySQL 8.0.17) below 8.0.17 -- it is a functional
  -- key part too, so it passes the 8.0.13 functional-index gate and the engine would refuse it.
  IF SchemaSmith_IndexHasFunctionalKeyPart(p_IndexColumns) = 0 THEN
    RETURN 0;
  END IF;
  IF SchemaSmith_SupportsFunctionalIndex() = 0 THEN
    RETURN 1;
  END IF;
  RETURN IF(UPPER(p_IndexColumns) REGEXP '[[:space:]]ARRAY[[:space:]]*[)]' AND SchemaSmith_ServerVersionPatchNum() < 80017, 1, 0);
END//

DELIMITER ;
