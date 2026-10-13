-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ColumnSrid//

CREATE FUNCTION SchemaSmith_ColumnSrid(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS INT
READS SQL DATA
BEGIN
    -- MariaDb variant override of the shared MySQL function. MariaDB has no column-level SRID
    -- attribute and no INFORMATION_SCHEMA.COLUMNS.SRS_ID column at any version -- verified live
    -- against 11.4.12, the newest supported release. Unconditional NULL, not a version-gated read (this
    -- is not a threshold MariaDB will ever cross the way it crosses SchemaSmith_IndexIsVisible's
    -- IGNORED-column floor). Parameters are accepted only to keep the signature callers rely on
    -- identical to the MySQL base definition. See the MySQL base definition and
    -- SchemaSmith_SupportsColumnSrid for the full rationale.
    RETURN NULL;
END //

DELIMITER ;
