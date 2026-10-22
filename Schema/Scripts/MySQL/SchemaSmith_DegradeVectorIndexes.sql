-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_DegradeVectorIndexes`;

DELIMITER //

-- A declared VECTOR index where the engine has none (MySQL; MariaDB below 11.7) is skipped and the downgrade recorded,
-- or refused under the fail policy. Called after SchemaSmith_RefuseUnsupportedIndexes by MissingTableAndColumnQuench and
-- IndexOnlyQuench: the index is taken out of the working set, so nothing creates or compares it. Such an engine cannot hold one,
-- so there is no live vector index to leave alone.
CREATE PROCEDURE `SchemaSmith_DegradeVectorIndexes`()
BEGIN
  DECLARE v_List TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;

  IF SchemaSmith_SupportsVectorIndex() = 0 AND EXISTS (SELECT 1 FROM _SchemaSmith_Indexes WHERE UPPER(IndexType) = 'VECTOR') THEN
    SELECT GROUP_CONCAT(CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
                        ORDER BY i.TableName, i.IndexName SEPARATOR ', ')
      INTO v_List
      FROM _SchemaSmith_Indexes i
     WHERE UPPER(i.IndexType) = 'VECTOR';
    IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
      INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
        CONCAT('  A VECTOR index requires MariaDB 11.7 (MySQL unsupported) (UnsupportedFeaturePolicy=fail): ', v_List));
      SET @ss_msg = LEFT(CONCAT('A VECTOR index needs MariaDB 11.7 (UnsupportedFeaturePolicy=fail): ', v_List), 128);
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;
    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
      CONCAT('  Skipping VECTOR index (requires MariaDB 11.7, MySQL unsupported - downgraded): ', v_List));
    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
    SELECT CONNECTION_ID(), 'INDEX (VECTOR, MariaDB 11.7)',
           CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName)), 'downgraded'
      FROM _SchemaSmith_Indexes i
     WHERE UPPER(i.IndexType) = 'VECTOR';
    DELETE FROM _SchemaSmith_Indexes WHERE UPPER(IndexType) = 'VECTOR';
  END IF;
END //

DELIMITER ;
