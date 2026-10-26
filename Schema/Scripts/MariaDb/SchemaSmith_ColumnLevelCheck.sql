-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- MariaDB names a column-level CHECK after its column, on every version, and from 10.11.19 / 11.4.13 / 13.0.2 refuses
-- a table-level check that shares a column's name (MDEV-24598). That name is the rule here rather than
-- CHECK_CONSTRAINTS.LEVEL, which is 10.5+ and would be fatal to mention in a function body below it. The predicate is
-- returned as the package declares it: without the charset introducers and backslash-escaped quotes the catalog adds,
-- the same cleaning the table-level extraction applies. An introducer is an underscore word that opens a quoted literal
-- and is not the tail of a longer word: `raw_binary` and 'a_latin1' are names and text, not introducers.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_ColumnLevelCheck//
CREATE FUNCTION SchemaSmith_ColumnLevelCheck(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    DECLARE v_Clause TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    IF SchemaSmith_SupportsCheckConstraints() = 0 THEN
        RETURN NULL;
    END IF;
    SELECT CONVERT(cc.CHECK_CLAUSE USING utf8mb4) INTO v_Clause
      FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
      JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
       AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
       AND tc.TABLE_NAME = cc.TABLE_NAME
       AND tc.CONSTRAINT_TYPE = 'CHECK'
     WHERE tc.TABLE_SCHEMA = CONVERT(p_Schema USING utf8mb4) COLLATE utf8mb4_general_ci AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = SchemaSmith_IdentifierKey(p_Schema)
       AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = SchemaSmith_IdentifierKey(p_Table)
       AND SchemaSmith_NameKeyCI(cc.CONSTRAINT_NAME) = SchemaSmith_NameKeyCI(p_Column)
     LIMIT 1;
    IF v_Clause IS NULL THEN
        RETURN NULL;
    END IF;
    RETURN SchemaSmith_CleanCheckClause(v_Clause);
END //
DELIMITER ;
