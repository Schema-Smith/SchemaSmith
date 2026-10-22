-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- NameKeyCI: the value two column, index, constraint, routine, event or period names must be compared on to
-- decide whether they name the SAME object.
--
-- The engine compares these names case-insensitively whatever lower_case_table_names says: `CustomerName` and
-- `customername` are one column, and `IX_A` beside `ix_a` is a duplicate. A BINARY comparison sees two objects,
-- so a live column spelled differently from the package looked absent and was dropped with its data.
-- Database, table and view names follow the server setting instead; they use SchemaSmith_IdentifierKey.
--
-- utf8mb4_bin makes every call site compare the key itself, whatever collation the other operand carries.
-- Kindled before BootstrapTableQuench, which calls it.
--
-- SchemaSmith_OwnedNameMatches compares an ownership row's 'table.object' name with a table and an object name: the
-- table half by the server's table rule, the object half by this one. Matching on the length of the table name keeps a
-- table name that itself contains a dot whole.

DROP FUNCTION IF EXISTS `SchemaSmith_NameKeyCI`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_NameKeyCI`(identifier VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(260) CHARSET utf8mb4 COLLATE utf8mb4_bin
DETERMINISTIC
NO SQL
BEGIN
    RETURN LOWER(identifier);
END //

DELIMITER ;

DROP FUNCTION IF EXISTS `SchemaSmith_OwnedNameMatches`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_OwnedNameMatches`(ownedName VARCHAR(520) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                               tableName VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
                                               objectName VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS TINYINT
-- Not deterministic: the table half depends on lower_case_table_names (see SchemaSmith_IdentifierKey).
NOT DETERMINISTIC
NO SQL
BEGIN
    RETURN COALESCE(CHAR_LENGTH(ownedName) > CHAR_LENGTH(tableName) + 1
       AND SchemaSmith_IdentifierKey(LEFT(ownedName, CHAR_LENGTH(tableName))) = SchemaSmith_IdentifierKey(tableName)
       AND SUBSTRING(ownedName, CHAR_LENGTH(tableName) + 1, 1) = '.'
       AND SchemaSmith_NameKeyCI(SUBSTRING(ownedName, CHAR_LENGTH(tableName) + 2)) = SchemaSmith_NameKeyCI(objectName), 0);
END //

DELIMITER ;
