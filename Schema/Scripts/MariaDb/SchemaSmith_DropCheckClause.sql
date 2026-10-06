-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_DropCheckClause//

CREATE FUNCTION SchemaSmith_DropCheckClause() RETURNS VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC NO SQL
BEGIN
    -- MariaDb variant override of the shared MySQL function. MariaDB rejects `ALTER TABLE ... DROP
    -- CHECK name` (a MySQL 8.0.16+ syntax); it drops a CHECK constraint via the generic
    -- `ALTER TABLE ... DROP CONSTRAINT name`. Isolating the keyword here keeps the large shared
    -- MissingIndexesAndConstraintsQuench proc unforked. See the MySQL base definition for the full
    -- rationale.
    RETURN 'DROP CONSTRAINT';
END //

DELIMITER ;
