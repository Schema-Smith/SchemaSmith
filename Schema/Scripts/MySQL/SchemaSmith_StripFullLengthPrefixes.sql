-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_StripFullLengthPrefixes`;

DELIMITER //

-- A key part declared with a prefix as long as its column -- `name`(10) on varchar(10) -- indexes the whole column, and
-- the catalog reports it with no prefix (SUB_PART NULL). Compared as written, the index was dropped and rebuilt on
-- every deploy. This drops such a prefix from a declared key-part list, judged against the DECLARED column in
-- _SchemaSmith_Columns, so a length change in the same deploy is measured against the new length. Lists holding a
-- functional key part are returned as they are: their commas are not all key-part separators.
CREATE FUNCTION `SchemaSmith_StripFullLengthPrefixes`(
    p_IndexColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_TableName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
  DECLARE v_rest TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT p_IndexColumns;
  DECLARE v_part TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_out TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
  DECLARE v_name VARCHAR(130) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
  DECLARE v_len VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_open INT;

  IF p_IndexColumns IS NULL OR LOCATE('(', p_IndexColumns) = 0 OR SchemaSmith_IndexHasFunctionalKeyPart(p_IndexColumns) = 1 THEN
    RETURN p_IndexColumns;
  END IF;

  WHILE v_rest <> '' DO
    SET v_part = TRIM(SUBSTRING_INDEX(v_rest, ',', 1));
    SET v_rest = IF(LOCATE(',', v_rest) > 0, SUBSTRING(v_rest, LOCATE(',', v_rest) + 1), '');
    SET v_open = LOCATE('(', v_part);
    IF v_open > 0 THEN
      SET v_name = SchemaSmith_StripBacktickWrapping(TRIM(LEFT(v_part, v_open - 1)));
      SET v_len = TRIM(SUBSTRING_INDEX(SUBSTRING(v_part, v_open + 1), ')', 1));
      IF EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                  WHERE c.TableName = p_TableName
                    AND SchemaSmith_StripBacktickWrapping(c.ColumnName) = v_name
                    AND (UPPER(REPLACE(c.DataType, ' ', '')) LIKE CONCAT('VARCHAR(', v_len, ')%')
                      OR UPPER(REPLACE(c.DataType, ' ', '')) LIKE CONCAT('CHAR(', v_len, ')%')
                      OR UPPER(REPLACE(c.DataType, ' ', '')) LIKE CONCAT('VARBINARY(', v_len, ')%')
                      OR UPPER(REPLACE(c.DataType, ' ', '')) LIKE CONCAT('BINARY(', v_len, ')%'))) THEN
        SET v_part = CONCAT(TRIM(LEFT(v_part, v_open - 1)), SUBSTRING(v_part, LOCATE(')', v_part, v_open) + 1));
      END IF;
    END IF;
    SET v_out = IF(v_out = '', v_part, CONCAT(v_out, ',', v_part));
  END WHILE;
  RETURN v_out;
END //

DELIMITER ;
