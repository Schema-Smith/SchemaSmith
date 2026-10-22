-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_CaseRespellKeepsOldSpelling`;
DELIMITER //
-- MariaDB from 10.3 until 10.6.26, 10.11.17 and 11.4.11 (and the series between, which never got the fix) runs
-- ALTER TABLE ... DROP INDEX `A`, ADD ... INDEX `a` on an index a foreign key needs without an error and keeps the old
-- spelling (probed on 10.3.39, 10.5.29, 10.6.8 to 10.6.25, 10.11.16 and 11.4.10; 10.2.44 and the fixed patches respell
-- it). The same window as MDEV-34951. MySQL renames an index's case by RENAME INDEX and is not affected.
CREATE FUNCTION `SchemaSmith_CaseRespellKeepsOldSpelling`()
RETURNS TINYINT
NOT DETERMINISTIC
NO SQL
BEGIN
  DECLARE v INT DEFAULT SchemaSmith_ServerVersionPatchNum();
  RETURN IF(VERSION() LIKE '%MariaDB%'
            AND v >= 100300
            AND (v < 100626 OR (v >= 100700 AND v < 101117) OR (v >= 101200 AND v < 110411)), 1, 0);
END //
DELIMITER ;
