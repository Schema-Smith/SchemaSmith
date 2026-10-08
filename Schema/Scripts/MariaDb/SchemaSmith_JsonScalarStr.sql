-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_JsonScalarStr`;

DELIMITER //

-- MariaDB variant. MariaDB's JSON_UNQUOTE returns utf8 -- three bytes a character -- whatever it is given (probed on
-- 10.5), so a 4-byte character in a string (an emoji, a supplementary CJK character) came back as '?', silently,
-- in every value the recursive-CTE delivery path and the version-agnostic parse read through here. A string is
-- unquoted with JSON_VALUE instead, which returns utf8mb4 (10.2.3+); anything else keeps JSON_UNQUOTE, which is
-- ASCII there and keeps a boolean as true/false rather than JSON_VALUE's 1/0.
CREATE FUNCTION `SchemaSmith_JsonScalarStr`(p_val LONGTEXT CHARACTER SET utf8mb4)
-- CHARACTER SET is explicit on purpose. Without it the return value takes the *database's* default
-- collation, while JSON_UNQUOTE below and the string literals at every call site take the *connection*
-- collation. On a database whose collation differs from the server default (an extracted product often
-- has one) the two are both COERCIBLE and neither wins, so every NULLIF(TRIM(...), '') around this
-- function raises "Illegal mix of collations". Naming the charset lets each server use that charset's
-- own default collation -- the same one the literals get -- on 5.7 / 8.x / MariaDB alike.
RETURNS LONGTEXT CHARACTER SET utf8mb4
DETERMINISTIC
NO SQL
BEGIN
  -- Convert a JSON scalar (as returned by JSON_EXTRACT) into a SQL string for a text field, mapping an
  -- explicit JSON null to SQL NULL. A bare JSON_UNQUOTE of a JSON null yields the literal string 'null'
  -- (not SQL NULL, which JSON_TABLE produced), which would survive NULLIF(TRIM(...),'') and be treated as
  -- a real value (e.g. a bogus ShouldApplyExpression). Companion to SchemaSmith_JsonScalarInt for the
  -- version-agnostic parse. Pure computation on 10.2.3+ built-ins (JSON_VALUE, but no JSON_TABLE).
  IF p_val IS NULL OR JSON_TYPE(p_val) = 'NULL' THEN
    RETURN NULL;
  END IF;
  IF JSON_TYPE(p_val) = 'STRING' THEN
    RETURN JSON_VALUE(p_val, '$');
  END IF;
  RETURN JSON_UNQUOTE(p_val);
END //

DELIMITER ;
