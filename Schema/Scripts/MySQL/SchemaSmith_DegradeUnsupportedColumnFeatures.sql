-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_DegradeUnsupportedColumnFeatures`;

DELIMITER //

-- Column declarations the server cannot take, reported once the working set is final (after ShouldApply) and before
-- anything is created: called by MissingTableAndColumnQuench, which an index-only template does not run, so a column
-- such a template never creates is not reported.
-- - A generated column's COLLATE (MariaDB before 10.2.37, 10.3.28, 10.4.18, 10.5.9). ParseTableJson has already dropped
--   the collation from the column script and marked the column; here it is recorded, or refused under the fail policy.
-- - Under the fail policy, any CHECK where the server cannot read one back (MySQL below 8.0.16, MariaDB below 10.2.22):
--   refused here rather than at the constraints step, which runs after tables and columns have been created without it.
--   Under warn the constraints step records the skip.
CREATE PROCEDURE `SchemaSmith_DegradeUnsupportedColumnFeatures`()
BEGIN
  DECLARE v_List TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;

  IF EXISTS (SELECT 1 FROM _SchemaSmith_Columns WHERE CollateSkipped = 1) THEN
    SELECT GROUP_CONCAT(CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
                        ORDER BY c.TableName, c.ColumnName SEPARATOR ', ')
      INTO v_List
      FROM _SchemaSmith_Columns c
     WHERE c.CollateSkipped = 1;
    IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
      INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
        CONCAT('  Generated column COLLATE this server cannot create (UnsupportedFeaturePolicy=fail): ', v_List));
      SET @ss_msg = 'Generated column COLLATE needs MariaDB 10.2.37/10.3.28/10.4.18/10.5.9 (UnsupportedFeaturePolicy=fail); see the log.';
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;
    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
      CONCAT('  Generated column created in its character set''s default collation (COLLATE requires MariaDB 10.2.37, 10.3.28, 10.4.18 or 10.5.9 - downgraded): ', v_List));
    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT CONNECTION_ID(), 'column (generated COLLATE, MariaDB 10.2.37)',
           CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
      FROM _SchemaSmith_Columns c
     WHERE c.CollateSkipped = 1;
  END IF;

  IF SchemaSmith_SupportsCheckConstraints() = 0 AND SchemaSmith_UnsupportedFeaturePolicy() = 'fail'
     AND (EXISTS (SELECT 1 FROM _SchemaSmith_CheckConstraints)
          OR (VERSION() LIKE '%MariaDB%' AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns WHERE IFNULL(TRIM(CheckExpression), '') != ''))) THEN
    SET @ss_check_floor = IF(VERSION() LIKE '%MariaDB%', 'MariaDB 10.2.22', 'MySQL 8.0.16');
    SELECT GROUP_CONCAT(n ORDER BY n SEPARATOR ', ') INTO v_List
      -- One collation for both branches: the two temp tables' name columns are not collated alike, and UNION refuses the mix.
      FROM (SELECT CONVERT(CONCAT(SchemaSmith_StripBacktickWrapping(cc.TableName), '.', SchemaSmith_StripBacktickWrapping(cc.ConstraintName)) USING utf8mb4) COLLATE utf8mb4_unicode_ci AS n
              FROM _SchemaSmith_CheckConstraints cc
            UNION ALL
            SELECT CONVERT(CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)) USING utf8mb4) COLLATE utf8mb4_unicode_ci
              FROM _SchemaSmith_Columns c
             WHERE VERSION() LIKE '%MariaDB%' AND IFNULL(TRIM(c.CheckExpression), '') != '') checks;
    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
      CONCAT('  CHECK constraint unsupported (requires ', @ss_check_floor, '): ', v_List));
    -- Only the number: a packaged MariaDB's VERSION() ('10.2.21-MariaDB-1:10.2.21+maria~bionic') overruns 128 chars.
    SET @ss_msg = CONCAT('CHECK constraints require ', @ss_check_floor, ' (detected ',
                         SUBSTRING_INDEX(VERSION(), '-', 1), '); see the deploy log for the unsupported check(s).');
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
  END IF;
END //

DELIMITER ;
