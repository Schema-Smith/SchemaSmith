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

DROP FUNCTION IF EXISTS `SchemaSmith_NameKeyCI`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_NameKeyCI`(identifier VARCHAR(260))
RETURNS VARCHAR(260) CHARSET utf8mb4 COLLATE utf8mb4_bin
DETERMINISTIC
NO SQL
BEGIN
    RETURN LOWER(identifier);
END //

DELIMITER ;
