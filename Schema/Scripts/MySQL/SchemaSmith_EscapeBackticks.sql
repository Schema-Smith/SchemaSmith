-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- EscapeBackticks: doubles every backtick in a bare name, for generated SQL that writes the name between backticks
-- of its own. Unlike QuoteIdentifier it neither trims nor unwraps: the caller already holds the bare name.
--
-- A function rather than an inline REPLACE because the parameter's declared collation absorbs whatever collation the
-- argument arrives in. An inline REPLACE on MariaDB 11.x mixes the argument's collation with the routine literals'
-- (utf8mb4_uca1400_ai_ci) and fails with 'Illegal mix of collations'.

DROP FUNCTION IF EXISTS `SchemaSmith_EscapeBackticks`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_EscapeBackticks`(name TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    RETURN REPLACE(name, '`', '``');
END //

DELIMITER ;
