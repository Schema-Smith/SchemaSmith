-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_IndexHasFunctionalKeyPart`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_IndexHasFunctionalKeyPart`(
    p_IndexColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
  -- 1 when a DECLARED index's column list has at least one functional/expression key part -- the same
  -- discriminator SchemaSmith_NormalizeIndexColumns uses per key part: a key part starting with '(' rather
  -- than a backtick (extraction always backtick-wraps a plain column name, so this is unambiguous). Only
  -- the first non-space character of each TOP-LEVEL (paren depth 0, outside a backtick-quoted span) key
  -- part needs checking -- a lighter single-pass scan than NormalizeIndexColumns' full rebuild, since this
  -- only needs a yes/no answer to gate SchemaSmith_MissingIndexesAndConstraintsQuench /
  -- SchemaSmith_IndexOnlyQuench's create/modify emit sites below the SchemaSmith_SupportsFunctionalIndex()
  -- floor -- DETERMINISTIC (unlike NormalizeIndexColumns): pure string scan, no version read.
  DECLARE v_Len INT;
  DECLARE v_Pos INT DEFAULT 1;
  DECLARE v_Depth INT DEFAULT 0;
  DECLARE v_InBacktick TINYINT DEFAULT 0;
  DECLARE v_AtKeyPartStart TINYINT DEFAULT 1;
  DECLARE v_Char CHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;

  IF p_IndexColumns IS NULL OR TRIM(p_IndexColumns) = '' THEN
    RETURN 0;
  END IF;

  SET v_Len = CHAR_LENGTH(p_IndexColumns);
  WHILE v_Pos <= v_Len DO
    SET v_Char = SUBSTRING(p_IndexColumns, v_Pos, 1);
    IF v_AtKeyPartStart = 1 AND v_Char != ' ' THEN
      IF v_Char = '(' THEN
        RETURN 1;
      END IF;
      SET v_AtKeyPartStart = 0;
    END IF;
    IF v_Char = '`' THEN
      SET v_InBacktick = 1 - v_InBacktick;
    ELSEIF v_InBacktick = 0 AND v_Char = '(' THEN
      SET v_Depth = v_Depth + 1;
    ELSEIF v_InBacktick = 0 AND v_Char = ')' THEN
      SET v_Depth = v_Depth - 1;
    ELSEIF v_InBacktick = 0 AND v_Char = ',' AND v_Depth = 0 THEN
      SET v_AtKeyPartStart = 1;
    END IF;
    SET v_Pos = v_Pos + 1;
  END WHILE;

  RETURN 0;
END//

DELIMITER ;
