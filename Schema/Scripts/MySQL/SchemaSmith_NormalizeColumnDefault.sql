-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizeColumnDefault//

CREATE FUNCTION SchemaSmith_NormalizeColumnDefault(
    p_Default TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- Canonicalizes INFORMATION_SCHEMA.COLUMN_DEFAULT for desired-vs-live column comparison.
    -- MySQL already reports COLUMN_DEFAULT in the form SchemaSmith's comparison expects (SQL NULL
    -- for no default, unquoted string values, uppercase CURRENT_TIMESTAMP), so this base version is
    -- an identity. The Scripts/MariaDb variant overrides it to fold MariaDB's divergent reporting
    -- (literal 'NULL' marker, quoted string values, current_timestamp() with parens) back to this
    -- same canonical form, so an unchanged column is not phantom-modified every quench.
    RETURN p_Default;
END //

DELIMITER ;
