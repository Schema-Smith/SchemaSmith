-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_RefuseUnsupportedIndexes`;

DELIMITER //

CREATE PROCEDURE `SchemaSmith_RefuseUnsupportedIndexes`()
BEGIN
  -- Index declarations no supported degrade can rescue, refused by name before anything is created, under either
  -- unsupported-feature policy. Reads the caller's _SchemaSmith_Indexes and _SchemaSmith_Tables; called first by
  -- MissingTableAndColumnQuench and IndexOnlyQuench.
  --
  -- A long unique key: MariaDB 10.4+ enforces UNIQUE over a column too wide for a B-tree key through a hidden
  -- hash, and extracts it as IndexType HASH. On 10.2 and 10.3 InnoDB turns USING HASH into a B-tree and refuses the key
  -- length (1071 / 1170). Dropping the key would drop the uniqueness, so there is no degrade. MEMORY tables have real
  -- hash indexes on every version and are excluded.
  DECLARE v_List TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;

  IF VERSION() LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() < 1004 THEN
    SELECT GROUP_CONCAT(CONCAT(SchemaSmith_StripBacktickWrapping(i.TableName), '.', SchemaSmith_StripBacktickWrapping(i.IndexName))
                        ORDER BY i.TableName, i.IndexName SEPARATOR ', ')
      INTO v_List
      FROM _SchemaSmith_Indexes i
      LEFT JOIN _SchemaSmith_Tables t ON t.TableKey = i.TableKey
     WHERE i.IsUnique = 1 AND UPPER(i.IndexType) = 'HASH' AND i.ShouldApply = 1
       AND UPPER(COALESCE(t.Engine, 'InnoDB')) NOT IN ('MEMORY', 'HEAP');

    IF v_List IS NOT NULL THEN
      INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(),
        CONCAT('  A long unique key (UNIQUE ... USING HASH) needs MariaDB 10.4; below it InnoDB cannot enforce it, and dropping it would drop the uniqueness (refused): ', v_List));
      SET @ss_msg = LEFT(CONCAT('A long unique key needs MariaDB 10.4 (refused): ', v_List), 128);
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
    END IF;
  END IF;
END //

DELIMITER ;
