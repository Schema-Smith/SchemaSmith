-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableDataDirectory//

CREATE PROCEDURE SchemaSmith_TableDataDirectory(
    IN p_Schema VARCHAR(64),
    IN p_Table VARCHAR(64),
    OUT p_DataDirectory VARCHAR(512)
)
SQL SECURITY DEFINER
BEGIN
    -- MariaDb variant override of the shared MySQL procedure (same PROCEDURE-with-OUT-param signature --
    -- kept identical for caller symmetry, even though this body needs none of the dynamic-SQL machinery
    -- the MySQL base definition needs -- see that script for why it does).
    --
    -- Unlike MySQL, MariaDB DOES surface DATA DIRECTORY in INFORMATION_SCHEMA.TABLES.CREATE_OPTIONS --
    -- verified live 2026-09-04: `CREATE TABLE t (...) DATA DIRECTORY='/ddspace'` reports back as
    -- `DATA DIRECTORY='/ddspace/'` in that column -- a SPACED key (not KEY=VALUE like COMPRESSION or
    -- PAGE_COMPRESSED), a single-quoted value, and CANONICALIZED WITH A TRAILING SLASH. That single-quoted
    -- form is why this does NOT reuse SchemaSmith_CreateOption: that parser's SUBSTRING_INDEX(v_val, ' ', 1)
    -- takes the value up to the next SPACE, but "DATA DIRECTORY" itself contains a space before the '=', so
    -- reusing it would misparse the key. A safe read (no dynamic SQL needed at all -- CREATE_OPTIONS exists
    -- on every supported MariaDB version, no kindle-floor trap here) parses the key directly instead.
    DECLARE v_options TEXT;
    DECLARE v_pos INT;
    DECLARE v_val VARCHAR(512);
    -- The needle includes the opening quote so LOCATE lands exactly at the value's first character --
    -- no separate "skip past the quote" step needed.
    -- Single-quoted literal (with the embedded quote doubled), NOT a double-quoted one: under
    -- sql_mode=ANSI_QUOTES a double-quoted token is parsed as an IDENTIFIER, so `"DATA DIRECTORY='"`
    -- would fail to CREATE this PROCEDURE at kindle time on any server/session running that mode. Every
    -- other script in this tree uses the single-quote-with-doubling idiom for exactly this portability.
    DECLARE v_needle VARCHAR(20) DEFAULT 'DATA DIRECTORY=''';
    -- A non-aggregate `SELECT ... INTO` with zero matching rows raises SQLSTATE 02000 (NOT FOUND) inside a
    -- stored routine -- the same trap the MySQL base definition's dynamic-SQL read handles locally (see
    -- that script). Scoped to this procedure's own body, so it can never escape to a caller's handler: a
    -- callee consuming its own NOT FOUND is exactly what keeps it from propagating across the CALL
    -- boundary in the first place.
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_options = NULL;

    SELECT t.CREATE_OPTIONS INTO v_options
    FROM INFORMATION_SCHEMA.TABLES t
    WHERE t.TABLE_SCHEMA = p_Schema AND t.TABLE_NAME = p_Table
    LIMIT 1;

    IF v_options IS NULL OR v_options = '' THEN
        SET p_DataDirectory = NULL;
    ELSE
        SET v_pos = LOCATE(v_needle, v_options);
        IF v_pos = 0 THEN
            SET p_DataDirectory = NULL;
        ELSE
            SET v_val = SUBSTRING(v_options, v_pos + CHAR_LENGTH(v_needle));
            -- Value runs up to the next single quote -- SUBSTRING_INDEX with the closing delimiter, same
            -- idiom SchemaSmith_CreateOption uses for its own quote-stripping.
            SET v_val = SUBSTRING_INDEX(v_val, '''', 1);
            -- Strip the trailing slash MariaDB's own canonicalization adds, so a user who declared
            -- '/ddspace' round-trips as '/ddspace' -- matching the MySQL base definition's derivation,
            -- which never has one to strip.
            SET p_DataDirectory = NULLIF(TRIM(TRAILING '/' FROM v_val), '');
        END IF;
    END IF;
END //

DELIMITER ;
