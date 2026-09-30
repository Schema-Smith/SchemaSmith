-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- StripBacktickWrapping: removes ONE layer of backtick wrapping and unescapes doubled backticks.
--
-- Must agree exactly with MySqlReservedWords.Unquote, which Identifier.Unwrap routes to on this platform:
-- the deploy unwraps the package side in C# and the catalog side here, so a disagreement is a rename that
-- matches nothing -- the new object is created, the old one survives undeclared, and a later deploy with the
-- matching Drop...RemovedFromProduct flag removes the orphan WITH ITS ROWS. Pinned by
-- BacktickWrappingAgreesWithIdentifierUnwrapSharedTests rather than left to inspection.
--
-- This used TRIM(BOTH '`' FROM identifier), which removes EVERY leading and trailing backtick rather than
-- one pair. Ordinary names were unaffected, so it went unnoticed; it diverged on a name that begins or ends
-- with a backtick, where the correctly delimited form carries two at that end. Measured: the stored form
-- `a`` (the name a`) came back as a, losing the character entirely.
--
-- What this does NOT do, the same limit its SQL Server sibling carries: a BARE name that merely looks
-- wrapped is still unwrapped, since nothing in a stored string distinguishes an undelimited `x` from the
-- delimited form of x. Both halves behave the same way there, so they still agree.

DROP FUNCTION IF EXISTS `SchemaSmith_StripBacktickWrapping`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_StripBacktickWrapping`(identifier VARCHAR(260))
RETURNS VARCHAR(255) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE trimmed VARCHAR(260);

    IF identifier IS NULL THEN
        RETURN NULL;
    END IF;

    SET trimmed = TRIM(identifier);

    -- One pair only, then unescape -- the inverse of wrapping once.
    IF CHAR_LENGTH(trimmed) >= 2
       AND LEFT(trimmed, 1) = '`'
       AND RIGHT(trimmed, 1) = '`' THEN
        RETURN REPLACE(SUBSTRING(trimmed, 2, CHAR_LENGTH(trimmed) - 2), '``', '`');
    END IF;

    RETURN trimmed;
END //

DELIMITER ;
