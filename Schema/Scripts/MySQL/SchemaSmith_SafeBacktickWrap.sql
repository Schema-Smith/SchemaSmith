-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- SafeBacktickWrap: Trims whitespace and safely wraps with backticks
-- Convenience function combining TRIM and QuoteIdentifier

DROP FUNCTION IF EXISTS `SchemaSmith_SafeBacktickWrap`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_SafeBacktickWrap`(identifier VARCHAR(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(260) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    RETURN `SchemaSmith_QuoteIdentifier`(TRIM(identifier));
END //

DELIMITER ;
