-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- IdentifierKey: the value two database, table or view names must be compared on to decide whether they
-- name the SAME object on THIS server.
--
-- MySQL and MariaDB fold these names according to lower_case_table_names:
--   0  (the Linux default, and what CI and the demo containers run)
--       `CaseProbe` and `caseprobe` are two different tables. Comparing them case-insensitively
--       conflates two real objects.
--   1  (the Windows default, and Azure Database for MySQL) the catalog stores the lowercased name.
--   2  (the macOS default) the catalog keeps the declared spelling for tables but lowercases views, and
--       MariaDB spells the same table differently in TABLES and COLUMNS.
--   On 1 and 2 no single spelling can be assumed on either side, so both sides fold.
--
-- Column, index and constraint names are case-insensitive on every setting; they use SchemaSmith_NameKeyCI.
--
-- Returning utf8mb4_bin is the load-bearing half: it makes the comparison at every call site
-- binary regardless of how the underlying column was declared, so the answer comes from this
-- policy rather than from whatever collation a table happened to be created with.
-- Kindled before BootstrapTableQuench, which calls it.

DROP FUNCTION IF EXISTS `SchemaSmith_IdentifierKey`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_IdentifierKey`(identifier VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci)
RETURNS VARCHAR(260) CHARSET utf8mb4 COLLATE utf8mb4_bin
-- NOT DETERMINISTIC for the same reason SchemaSmith_ServerVersionNum is: the answer depends on a
-- server variable, not on the argument alone, so declaring otherwise would license the optimizer to
-- fold one call's result across rows read on a differently configured connection.
NOT DETERMINISTIC
NO SQL
BEGIN
    RETURN IF(@@lower_case_table_names = 0, identifier, LOWER(identifier));
END //

DELIMITER ;
