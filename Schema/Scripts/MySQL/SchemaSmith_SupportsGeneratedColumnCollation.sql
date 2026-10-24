-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- A COLLATE clause on a generated column. MariaDB rejects it with 1064 before 10.2.37, 10.3.28, 10.4.18 and 10.5.9 (the
-- same fix on each line: refused on 10.2.36, 10.3.27, 10.4.17 and 10.5.8, accepted on the next patch of each); 10.6 and
-- MySQL always accept it. Below it the column is created in its character set's default collation and a downgrade recorded.
DROP FUNCTION IF EXISTS `SchemaSmith_SupportsGeneratedColumnCollation`;
DELIMITER //
CREATE FUNCTION `SchemaSmith_SupportsGeneratedColumnCollation`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  DECLARE v_Patch INT DEFAULT SchemaSmith_ServerVersionPatchNum();
  IF VERSION() NOT LIKE '%MariaDB%' THEN
    RETURN 1;
  END IF;
  RETURN CASE FLOOR(v_Patch / 100)
           WHEN 1002 THEN v_Patch >= 100237
           WHEN 1003 THEN v_Patch >= 100328
           WHEN 1004 THEN v_Patch >= 100418
           WHEN 1005 THEN v_Patch >= 100509
           ELSE v_Patch >= 100600
         END;
END //
DELIMITER ;
