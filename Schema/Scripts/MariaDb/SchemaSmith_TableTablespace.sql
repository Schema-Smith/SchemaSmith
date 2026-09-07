-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_TableTablespace//

CREATE PROCEDURE SchemaSmith_TableTablespace(
    IN p_Schema VARCHAR(64),
    IN p_Table VARCHAR(64),
    OUT p_Tablespace VARCHAR(64)
)
SQL SECURITY DEFINER
BEGIN
    -- MariaDb variant override of the shared MySQL procedure (same PROCEDURE-with-OUT-param signature --
    -- see the MySQL base definition for why this is a procedure and not a function). MariaDB has NO
    -- general tablespaces at any version -- CREATE TABLESPACE ... ADD DATAFILE is a syntax error there
    -- (verified live) -- so there is never a placement to report. Unconditional NULL, not a version-gated
    -- read (this is not a threshold MariaDB will ever cross, unlike the MySQL base definition's own 8.0
    -- INNODB_TABLES/INNODB_TABLESPACES view-name floor). Parameters are accepted only to keep the
    -- signature callers rely on identical to the MySQL base definition. See the MySQL base definition
    -- (Scripts/MySQL/SchemaSmith_TableTablespace.sql) for the full rationale, and
    -- SchemaSmith_ColumnSrid / SchemaSmith_SetSystemVersioningAlterHistory for the same per-engine-override
    -- shape.
    SET p_Tablespace = NULL;
END //

DELIMITER ;
