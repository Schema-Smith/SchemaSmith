-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_CheckClauseText//

-- MySQL's INFORMATION_SCHEMA.CHECK_CONSTRAINTS.CHECK_CLAUSE is not the clause's SQL: it is string-escaped (every quote and
-- backslash, inside identifiers too) and its non-ASCII text is UTF-8 read as latin1 and encoded again. This returns the
-- SQL itself. MariaDB reports the clause as written, so it passes through.
CREATE FUNCTION SchemaSmith_CheckClauseText(
    p_Clause TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE v_Text TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Bytes LONGBLOB;

    IF p_Clause IS NULL OR VERSION() LIKE '%MariaDB%' THEN
        RETURN p_Clause;
    END IF;

    SET v_Text = p_Clause;
    -- Undone only when the text is exactly such a double encoding: every character fits latin1 and the bytes they
    -- make are valid UTF-8. Plain ASCII passes both and is unchanged.
    SET v_Bytes = CAST(CONVERT(p_Clause USING latin1) AS BINARY);
    IF CAST(CONVERT(CONVERT(p_Clause USING latin1) USING utf8mb4) AS BINARY) = CAST(p_Clause AS BINARY)
       AND CAST(CONVERT(v_Bytes USING utf8mb4) AS BINARY) = v_Bytes THEN
        SET v_Text = CONVERT(v_Bytes USING utf8mb4);
    END IF;

    RETURN REPLACE(REPLACE(REPLACE(v_Text, '\\\\', CHAR(1 USING utf8mb4)), '\\''', ''''), CHAR(1 USING utf8mb4), '\\');
END //

DELIMITER ;
