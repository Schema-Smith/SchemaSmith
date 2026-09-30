-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- QuoteIdentifier: wraps an identifier in backticks, escaping any embedded backtick.
--
-- Accepts either a raw name or an already-wrapped one, which is why it unwraps first: ~41 callers rely on
-- being able to hand it whichever they have, and re-wrapping an already-wrapped value must be idempotent.
--
-- The unwrap is ONE PAIR, via the same rule as SchemaSmith_StripBacktickWrapping. It used
-- TRIM(BOTH '`' FROM identifier), which strips EVERY backtick at each end, so a raw name beginning or
-- ending with a backtick was destroyed on the way in -- the name a` was written as `a`, i.e. the name a.
-- Ordinary names are unaffected either way, which is why it went unnoticed.
--
-- The residual, and it is unavoidable rather than an oversight: a RAW name that itself begins and ends with
-- a backtick is indistinguishable from the delimited form of the name between them, so `x` is read as x.
-- The SQL Server side carries the identical limit with [ ]. Both halves agree on that reading, so nothing
-- silently diverges -- it simply cannot be represented, and the end-user docs say so.

DROP FUNCTION IF EXISTS `SchemaSmith_QuoteIdentifier`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_QuoteIdentifier`(identifier VARCHAR(255))
RETURNS VARCHAR(260) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    DECLARE trimmed VARCHAR(255);
    DECLARE unquoted VARCHAR(255);

    IF identifier IS NULL THEN
        RETURN NULL;
    END IF;

    SET trimmed = TRIM(identifier);

    IF CHAR_LENGTH(trimmed) >= 2
       AND LEFT(trimmed, 1) = '`'
       AND RIGHT(trimmed, 1) = '`' THEN
        SET unquoted = REPLACE(SUBSTRING(trimmed, 2, CHAR_LENGTH(trimmed) - 2), '``', '`');
    ELSE
        SET unquoted = trimmed;
    END IF;

    RETURN CONCAT('`', REPLACE(unquoted, '`', '``'), '`');
END //

DELIMITER ;
