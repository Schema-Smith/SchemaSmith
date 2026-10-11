-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_TrimEnumMembers`;

DELIMITER //

-- MySQL and MariaDB trim trailing spaces from ENUM and SET members when the column is created, so ENUM('a ','b') is
-- stored, and reported, as enum('a','b'). Compared as written it was re-applied on every deploy. This trims each quoted
-- member of a declared ENUM/SET type the same way; '' inside a member is an escaped quote, not its end.
CREATE FUNCTION `SchemaSmith_TrimEnumMembers`(
    p_DataType VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
) RETURNS VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
DETERMINISTIC
NO SQL
BEGIN
  DECLARE v_out VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT '';
  DECLARE v_member VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT '';
  DECLARE v_i INT DEFAULT 1;
  DECLARE v_len INT;
  DECLARE v_ch VARCHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
  DECLARE v_inQuote TINYINT DEFAULT 0;

  IF p_DataType IS NULL OR (UPPER(TRIM(p_DataType)) NOT LIKE 'ENUM%' AND UPPER(TRIM(p_DataType)) NOT LIKE 'SET%') THEN
    RETURN p_DataType;
  END IF;

  SET v_len = CHAR_LENGTH(p_DataType);
  WHILE v_i <= v_len DO
    SET v_ch = SUBSTRING(p_DataType, v_i, 1);
    IF v_inQuote = 0 THEN
      SET v_out = CONCAT(v_out, v_ch);
      IF v_ch = '''' THEN SET v_inQuote = 1, v_member = ''; END IF;
    ELSEIF v_ch = '''' AND SUBSTRING(p_DataType, v_i + 1, 1) = '''' THEN
      SET v_member = CONCAT(v_member, '''''');
      SET v_i = v_i + 1;
    ELSEIF v_ch = '''' THEN
      SET v_out = CONCAT(v_out, TRIM(TRAILING ' ' FROM v_member), '''');
      SET v_inQuote = 0;
    ELSE
      SET v_member = CONCAT(v_member, v_ch);
    END IF;
    SET v_i = v_i + 1;
  END WHILE;
  RETURN v_out;
END //

DELIMITER ;
