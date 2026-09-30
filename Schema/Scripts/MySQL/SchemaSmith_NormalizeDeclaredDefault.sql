-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizeDeclaredDefault//

CREATE FUNCTION SchemaSmith_NormalizeDeclaredDefault(
    p_Default TEXT
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- The DESIRED-side twin of SchemaSmith_NormalizeColumnDefault, so both sides of the default
    -- comparison are folded the same way.
    --
    -- WHY THIS EXISTS. The comparison normalised only the LIVE value and took the package's text almost
    -- raw -- outer quotes stripped and nothing else. On MySQL that is fine, because the live normaliser
    -- is an identity there and both sides already agree. On MariaDB it is not: the MariaDb override folds
    -- a function default by upper-casing it and dropping an empty argument list, so a live `uuid()` became
    -- `UUID` while the declared `uuid()` stayed `uuid()`. BINARY 'UUID' <> BINARY 'uuid()' on every
    -- deploy, so the column was re-ALTERed forever. Measured on the shipped Demos/MariaDB/AdventureWorks
    -- package: 29 `rowguid CHAR(36) ... DEFAULT (uuid())` columns re-altered on every run, over four
    -- consecutive runs, and MySQL clean -- the asymmetry is what named the cause.
    --
    -- CURRENT_TIMESTAMP survived only by luck: packages declare it already upper-cased and without
    -- parentheses, which is the folded form, so the two sides happened to meet. That is why this went
    -- unnoticed rather than being caught by the temporal defaults.
    --
    -- ORDER MATTERS, AND IT IS NORMALISE-THEN-STRIP. The reverse breaks MariaDB string defaults: strip
    -- `'web'` to `web` first and the fold then sees an UNQUOTED token and upper-cases it to `WEB`, while
    -- the live side folds `'web'` to `web`. Folding first lets the quoted-string rule claim the value and
    -- return it unchanged, and the strip below is then a no-op. Verified against all three shapes on both
    -- engines: quoted string, function, number.
    --
    -- The CONVERT ... COLLATE on the way in is not decoration. This function's RETURNS clause pins
    -- utf8mb4_unicode_ci while a bare quote literal takes the database's collation, and on a MySQL 8
    -- database (utf8mb4_0900_ai_ci) the LIKE below then fails outright: "Illegal mix of collations
    -- (utf8mb4_unicode_ci,COERCIBLE) and (utf8mb4_0900_ai_ci,COERCIBLE) for operation 'like'". Both
    -- operands are forced to one collation, which is the same idiom the quench procedures use on every
    -- p_DatabaseName comparison.
    DECLARE v_Normalized TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci;
    SET v_Normalized = CONVERT(SchemaSmith_NormalizeColumnDefault(p_Default) USING utf8mb4) COLLATE utf8mb4_unicode_ci;

    IF v_Normalized IS NULL THEN
        RETURN NULL;
    END IF;

    IF v_Normalized LIKE CONVERT('''%''' USING utf8mb4) COLLATE utf8mb4_unicode_ci THEN
        RETURN REPLACE(SUBSTRING(v_Normalized, 2, CHAR_LENGTH(v_Normalized) - 2), '''''', '''');
    END IF;

    RETURN v_Normalized;
END //

DELIMITER ;
