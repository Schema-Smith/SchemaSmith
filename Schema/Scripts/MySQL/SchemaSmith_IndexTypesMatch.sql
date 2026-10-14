-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_IndexTypesMatch`;

DELIMITER //

-- Whether a declared index type is what the live index already is. The one rule both index-change detectors use, so
-- a real change of kind is applied and a spelling the engine stores differently is not rebuilt on every deploy:
--  * an omitted type is BTREE, as the model declares it;
--  * outside MEMORY, BTREE and HASH name the same structure: InnoDB stores a declared HASH as BTREE (MySQL, MariaDB
--    through 11.4), while MariaDB reports a long unique key (10.4+) and, from 13.0, a non-unique HASH as HASH;
--  * MySQL 8.0.12+ makes any index on a geometry column SPATIAL, so a declared BTREE there is SPATIAL.
-- Anything else -- BTREE against HASH on MEMORY, a SPATIAL added or removed on MariaDB -- is a real difference.
CREATE FUNCTION `SchemaSmith_IndexTypesMatch`(
    p_Declared VARCHAR(32) CHARACTER SET utf8mb4,
    p_Live VARCHAR(32) CHARACTER SET utf8mb4,
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4)
RETURNS TINYINT
READS SQL DATA
BEGIN
  DECLARE v_engine VARCHAR(64) CHARACTER SET utf8mb4 DEFAULT NULL;
  DECLARE v_declared VARCHAR(32) CHARACTER SET utf8mb4;
  DECLARE v_live VARCHAR(32) CHARACTER SET utf8mb4 DEFAULT UPPER(TRIM(COALESCE(p_Live, '')));

  SELECT UPPER(ENGINE) INTO v_engine FROM information_schema.TABLES
   WHERE TABLE_SCHEMA = p_Schema AND TABLE_NAME = SchemaSmith_StripBacktickWrapping(p_Table) LIMIT 1;
  SET v_declared = UPPER(TRIM(COALESCE(NULLIF(TRIM(p_Declared), ''), 'BTREE')));

  IF v_declared = v_live THEN RETURN 1; END IF;
  IF COALESCE(v_engine, '') <> 'MEMORY' AND v_declared IN ('BTREE', 'HASH') AND v_live IN ('BTREE', 'HASH') THEN RETURN 1; END IF;
  IF VERSION() NOT LIKE '%MariaDB%' AND v_declared = 'BTREE' AND v_live = 'SPATIAL' THEN RETURN 1; END IF;
  RETURN 0;
END //

DELIMITER ;
