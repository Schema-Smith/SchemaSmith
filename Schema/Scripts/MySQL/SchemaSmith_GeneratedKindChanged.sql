-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_GeneratedKindChanged//

-- 1 when a generated column's declared kind (STORED, or MariaDB's synonym PERSISTENT, against VIRTUAL, the default) is
-- not the live one, read from INFORMATION_SCHEMA.COLUMNS.EXTRA. Neither engine can MODIFY between the two, so such a
-- column joins the generated-status drop and re-add. A number, not a string: a string result meets the session's
-- collation in every caller.
CREATE FUNCTION SchemaSmith_GeneratedKindChanged(
    p_Extra TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_DeclaredType VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
DETERMINISTIC
NO SQL
BEGIN
    DECLARE v_LiveStored TINYINT DEFAULT IF(UPPER(IFNULL(p_Extra, '')) LIKE '%STORED GENERATED%' OR UPPER(IFNULL(p_Extra, '')) LIKE '%PERSISTENT%', 1, 0);
    DECLARE v_DeclaredStored TINYINT DEFAULT IF(UPPER(TRIM(IFNULL(p_DeclaredType, ''))) IN ('STORED', 'PERSISTENT'), 1, 0);
    RETURN IF(v_LiveStored <> v_DeclaredStored, 1, 0);
END//

DELIMITER ;
