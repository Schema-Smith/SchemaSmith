-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IndexInvisibleClause//

CREATE FUNCTION SchemaSmith_IndexInvisibleClause() RETURNS VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    -- MariaDb variant override of the shared MySQL function. MariaDB has no INVISIBLE keyword;
    -- an index is hidden from the optimizer with `IGNORED` (`CREATE INDEX ... IGNORED`, reflected as
    -- INFORMATION_SCHEMA.STATISTICS.IGNORED='YES'). Isolating the keyword here keeps the large shared
    -- MissingIndexesAndConstraintsQuench / IndexOnlyQuench procs unforked. See the MySQL base
    -- definition for the full rationale.
    RETURN ' IGNORED';
END //

DELIMITER ;
