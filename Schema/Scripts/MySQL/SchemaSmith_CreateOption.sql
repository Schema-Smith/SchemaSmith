-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_CreateOption`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_CreateOption`(p_Options TEXT, p_Key VARCHAR(64))
-- CHARACTER SET DECLARED, and it is load-bearing rather than tidiness. Without it the return takes the
-- DATABASE's default charset and collation, while a string literal it is compared against carries the
-- collation_connection captured when the calling routine was created -- the SERVER default. Against a
-- database made with an explicit COLLATE those differ, both sides are COERCIBLE so neither outranks the
-- other, and the comparison dies with 'Illegal mix of collations ... for operation =' -- which aborted
-- SchemaTongs on every table of such a database. This was the ONLY helper here missing the declaration;
-- every sibling already carries CHARSET utf8mb4, which is why only this one's callers ever broke.
RETURNS VARCHAR(256) CHARACTER SET utf8mb4
DETERMINISTIC
NO SQL
BEGIN
  -- Reads one option out of INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS.
  --
  -- WHY THIS EXISTS. Several table options surface in exactly one place and nowhere else: that column,
  -- a single free-text blob. COMPRESSION, KEY_BLOCK_SIZE and MariaDB's PAGE_COMPRESSED family all live
  -- there, so they share one parser -- which is the reason they are built together rather than one at a
  -- time.
  --
  -- NO REGEX, deliberately. REGEXP_SUBSTR does not exist on MySQL 5.7 (verified: "FUNCTION
  -- mysql.REGEXP_SUBSTR does not exist"), and 5.7 is the supported floor. Unlike a missing catalog
  -- column, a missing FUNCTION is resolved at call time rather than at CREATE time, so this would kindle
  -- fine and then fail at the floor on a real deploy -- the worse failure of the two, because nothing
  -- catches it earlier. LOCATE/SUBSTRING works identically on every supported version.
  --
  -- THE THREE SHAPES, all verified live rather than read:
  --   MySQL 8.0 / 5.7 : COMPRESSION="zlib"                    -- value double-quoted
  --   MySQL 8.0 / 5.7 : row_format=COMPRESSED KEY_BLOCK_SIZE=8 -- unquoted, mixed key case
  --   MariaDB 11.4    : `PAGE_COMPRESSED`=1                    -- KEY backtick-quoted, not the value
  -- Hence: strip backticks first, match the key case-insensitively, take the value up to the next
  -- space, then strip whichever quote style wraps it.
  --
  -- Returns NULL when the option is absent, which is what an unset option looks like -- CREATE_OPTIONS
  -- is an empty string for a table that declares none.
  DECLARE v_norm TEXT;
  DECLARE v_pos INT;
  DECLARE v_val VARCHAR(256);

  IF p_Options IS NULL OR p_Options = '' THEN
    RETURN NULL;
  END IF;

  -- Leading space so the needle can anchor on a word boundary: without it, looking for KEY_BLOCK_SIZE
  -- would also match a hypothetical option ending in that name.
  SET v_norm = CONCAT(' ', REPLACE(p_Options, '`', ''));
  SET v_pos = LOCATE(CONCAT(' ', UPPER(p_Key), '='), UPPER(v_norm));

  IF v_pos = 0 THEN
    RETURN NULL;
  END IF;

  -- UPPER does not change length for the ASCII these option names use, so the position found in the
  -- uppercased copy indexes the original -- which is what preserves the VALUE's own case.
  SET v_val = SUBSTRING(v_norm, v_pos + CHAR_LENGTH(p_Key) + 2);
  SET v_val = SUBSTRING_INDEX(v_val, ' ', 1);

  IF CHAR_LENGTH(v_val) >= 2 THEN
    IF (LEFT(v_val, 1) = '"' AND RIGHT(v_val, 1) = '"')
       OR (LEFT(v_val, 1) = '''' AND RIGHT(v_val, 1) = '''') THEN
      SET v_val = SUBSTRING(v_val, 2, CHAR_LENGTH(v_val) - 2);
    END IF;
  END IF;

  RETURN NULLIF(v_val, '');
END //

DELIMITER ;
