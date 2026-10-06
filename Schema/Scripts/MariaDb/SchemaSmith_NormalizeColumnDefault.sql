-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizeColumnDefault//

CREATE FUNCTION SchemaSmith_NormalizeColumnDefault(
    p_Default TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- MariaDb variant override. MariaDB reports INFORMATION_SCHEMA.COLUMN_DEFAULT differently from
    -- MySQL in three ways that each phantom-modify an unchanged column; fold them to the MySQL
    -- canonical form the shared comparison (and the desired-side processing) expects:
    --   1. nullable column, no default -> MariaDB reports the literal string 'NULL'  => SQL NULL
    --   2. string/enum default value   -> MariaDB quotes it ('web')                  => unquoted (web)
    --   3. function default            -> MariaDB adds parens/case (current_timestamp()) => CURRENT_TIMESTAMP
    -- The engine reporting is unambiguous: MariaDB quotes literal string values, so a *quoted* token
    -- is a string value and an *unquoted* token is a keyword/function/number (safe to upper-case).
    IF p_Default IS NULL THEN
        RETURN NULL;
    END IF;

    -- 1. MariaDB's no-default marker (unquoted literal NULL). A real string default of "NULL"
    --    would arrive quoted ('NULL'), so this cannot swallow an intentional value.
    IF p_Default = 'NULL' THEN
        RETURN NULL;
    END IF;

    -- 2. Quoted string literal: strip the surrounding quotes and unescape doubled quotes, matching
    --    MySQL's unquoted COLUMN_DEFAULT and the comparison's desired-side quote stripping.
    IF CHAR_LENGTH(p_Default) >= 2 AND LEFT(p_Default, 1) = '''' AND RIGHT(p_Default, 1) = '''' THEN
        RETURN REPLACE(SUBSTRING(p_Default, 2, CHAR_LENGTH(p_Default) - 2), '''''', '''');
    END IF;

    -- 3. Unquoted keyword/function/number: upper-case and drop an empty () argument list so
    --    current_timestamp() folds to CURRENT_TIMESTAMP (a precision arg like (6) is preserved).
    RETURN UPPER(REPLACE(p_Default, '()', ''));
END //

DELIMITER ;
