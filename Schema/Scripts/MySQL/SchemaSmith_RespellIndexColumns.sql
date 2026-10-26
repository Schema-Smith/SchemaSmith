-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_RespellIndexColumns`;

DELIMITER //

-- Column names match without regard to case on every server, so a key part may name its column in another case than
-- the column is declared (`val` on `Val`). The catalog reports the column's own spelling and the index comparison is
-- exact, so such an index was dropped and rebuilt on every deploy. Each key part's name is respelled as the declared
-- column (or, where the package declares no columns, the live one); what follows the name -- a prefix length, DESC --
-- is kept. Lists holding a functional key part are returned as they are: their commas are not all key-part separators.
CREATE FUNCTION `SchemaSmith_RespellIndexColumns`(
    p_IndexColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_TableName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin,
    p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
NOT DETERMINISTIC
READS SQL DATA
BEGIN
  DECLARE v_rest TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT p_IndexColumns;
  DECLARE v_part TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_out TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
  DECLARE v_nameEnd INT;
  DECLARE v_name VARCHAR(130) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
  DECLARE v_spelled VARCHAR(130) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

  IF p_IndexColumns IS NULL OR SchemaSmith_IndexHasFunctionalKeyPart(p_IndexColumns) = 1 THEN
    RETURN p_IndexColumns;
  END IF;

  WHILE v_rest <> '' DO
    SET v_part = TRIM(SUBSTRING_INDEX(v_rest, ',', 1));
    SET v_rest = IF(LOCATE(',', v_rest) > 0, SUBSTRING(v_rest, LOCATE(',', v_rest) + 1), '');
    -- The name ends at its closing backtick, or unquoted at the first ( or space.
    SET v_nameEnd = IF(LEFT(v_part, 1) = '`', LOCATE('`', v_part, 2),
                       LEAST(IF(LOCATE('(', v_part) > 0, LOCATE('(', v_part) - 1, CHAR_LENGTH(v_part)),
                             IF(LOCATE(' ', v_part) > 0, LOCATE(' ', v_part) - 1, CHAR_LENGTH(v_part))));
    SET v_name = SchemaSmith_StripBacktickWrapping(LEFT(v_part, v_nameEnd));
    SET v_spelled = (SELECT SchemaSmith_StripBacktickWrapping(c.ColumnName) FROM _SchemaSmith_Columns c
                      WHERE c.TableName = p_TableName
                        AND SchemaSmith_NameKeyCI(SchemaSmith_StripBacktickWrapping(c.ColumnName)) = SchemaSmith_NameKeyCI(v_name)
                      LIMIT 1);
    IF v_spelled IS NULL THEN
      SET v_spelled = (SELECT COLUMN_NAME FROM information_schema.COLUMNS
                        WHERE TABLE_SCHEMA = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_general_ci AND SchemaSmith_IdentifierKey(TABLE_SCHEMA) = SchemaSmith_IdentifierKey(p_DatabaseName)
                          AND SchemaSmith_IdentifierKey(TABLE_NAME) = SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(p_TableName))
                          AND SchemaSmith_NameKeyCI(COLUMN_NAME) = SchemaSmith_NameKeyCI(v_name)
                        LIMIT 1);
    END IF;
    IF v_spelled IS NOT NULL AND v_spelled <> v_name THEN
      SET v_part = CONCAT('`', REPLACE(v_spelled, '`', '``'), '`', SUBSTRING(v_part, v_nameEnd + 1));
    END IF;
    SET v_out = IF(v_out = '', v_part, CONCAT(v_out, ',', v_part));
  END WHILE;
  RETURN v_out;
END //

DELIMITER ;
