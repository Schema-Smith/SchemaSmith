-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_DropCheckClause//

CREATE FUNCTION SchemaSmith_DropCheckClause() RETURNS VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    -- Returns the ALTER TABLE clause used to drop a named CHECK constraint.
    --
    -- WHY this exists: MySQL 8.0.16+ drops a CHECK constraint with `ALTER TABLE ... DROP CHECK name`.
    -- MariaDB does not accept DROP CHECK; it drops a CHECK via the generic `DROP CONSTRAINT name`.
    -- (MySQL only gained the generic DROP CONSTRAINT form in 8.0.19, below our support floor's earliest
    -- CHECK-capable point, so we keep DROP CHECK on MySQL and override to DROP CONSTRAINT for MariaDb.)
    -- The caller (MissingIndexesAndConstraintsQuench) is shared across the MySQL/MariaDb variant
    -- family, so the keyword divergence is isolated here: this MySQL definition returns 'DROP CHECK';
    -- the Scripts/MariaDb override returns 'DROP CONSTRAINT'.
    RETURN 'DROP CHECK';
END //

DELIMITER ;
