-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IndexInvisibleClause//

CREATE FUNCTION SchemaSmith_IndexInvisibleClause() RETURNS VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    -- Returns the trailing index-definition clause that marks an index invisible to the optimizer.
    --
    -- WHY this exists: MySQL marks an index invisible with the `INVISIBLE` keyword
    -- (`CREATE INDEX ... INVISIBLE`); MariaDB has no INVISIBLE keyword — it marks an index
    -- "ignored" with `IGNORED` (reflected as INFORMATION_SCHEMA.STATISTICS.IGNORED='YES', the
    -- inverse of MySQL's IS_VISIBLE — see SchemaSmith_IndexIsVisible). The callers
    -- (MissingIndexesAndConstraintsQuench, IndexOnlyQuench) build the index DDL and are shared
    -- across the MySQL/MariaDb variant family, so the keyword divergence is isolated here: this MySQL
    -- definition returns ' INVISIBLE'; the Scripts/MariaDb override returns ' IGNORED'. The leading
    -- space lets the caller append it directly after the index column list.
    RETURN ' INVISIBLE';
END //

DELIMITER ;
