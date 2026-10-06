-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- StripLeadingSelect: a component ShouldApplyExpression is embedded as a bare predicate inside
-- NOT (<expr>). Accept the folder-gate form too -- a projection-only SELECT -- by stripping a
-- leading SELECT keyword so the remainder is a usable predicate. Either form then works on any
-- component gate (#282). The match requires SELECT followed by whitespace, so an identifier like
-- "selected" is not mistaken for the keyword; a non-SELECT expression is returned unchanged.

DROP FUNCTION IF EXISTS `SchemaSmith_StripLeadingSelect`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_StripLeadingSelect`(p_text VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(4000) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE v_rest VARCHAR(4000) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    IF p_text IS NULL THEN
        RETURN p_text;
    END IF;
    -- The REGEXP *operator* exists on every supported version, but REGEXP_REPLACE is MySQL 8.0+ (MariaDB
    -- has it, MySQL 5.7 does not), so the strip is done with SUBSTRING + a whitespace scan to keep this
    -- function runnable on the MySQL 5.7 / MariaDB 10.2 floor. Output is byte-identical on every version to
    -- the former REGEXP_REPLACE(TRIM(p_text), '^[Ss][Ee][Ll][Ee][Cc][Tt][[:space:]]+', '').
    IF TRIM(p_text) REGEXP '^[Ss][Ee][Ll][Ee][Cc][Tt][[:space:]]' THEN
        SET v_rest = SUBSTRING(TRIM(p_text), 7);   -- drop the 6-char SELECT keyword
        -- consume the following run of whitespace (space, tab, LF, VT, FF, CR = the POSIX [:space:] class)
        WHILE LENGTH(v_rest) > 0
              AND LOCATE(LEFT(v_rest, 1), CONCAT(' ', CHAR(9), CHAR(10), CHAR(11), CHAR(12), CHAR(13))) > 0 DO
            SET v_rest = SUBSTRING(v_rest, 2);
        END WHILE;
        RETURN v_rest;
    END IF;
    RETURN p_text;
END //

DELIMITER ;
