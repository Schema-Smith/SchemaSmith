-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- IdentifierKey: the value two object names must be compared on to decide whether they name the
-- SAME object on THIS server.
--
-- MySQL and MariaDB do not agree with themselves about identifier case; the server decides:
--   lower_case_table_names = 0  (the Linux default, and what CI and the demo containers run)
--       `CaseProbe` and `caseprobe` are two different tables. Comparing them case-insensitively
--       conflates two real objects -- which is how a second product declaring the lowercase twin
--       was refused with "Table CaseProbe is already owned by another product", naming a table it
--       had not declared.
--   lower_case_table_names >= 1 (the Windows/macOS default)
--       The server folds identifiers itself, so the two names are the same table and MUST still
--       compare equal. Folding both sides here keeps that behaviour byte-for-byte what it was
--       before this function existed, so upgrading such a server changes nothing.
--
-- Returning utf8mb4_bin is the load-bearing half: it makes the comparison at every call site
-- binary regardless of how the underlying column was declared, so the answer comes from this
-- policy rather than from whatever collation a table happened to be created with.

DROP FUNCTION IF EXISTS `SchemaSmith_IdentifierKey`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_IdentifierKey`(identifier VARCHAR(260))
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
