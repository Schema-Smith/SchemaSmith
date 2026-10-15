-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

-- The period a key is declared WITHOUT OVERLAPS over, from INFORMATION_SCHEMA.KEY_PERIOD_USAGE joined to PERIODS for
-- its columns. Both views arrive in 11.4, so the read sits inside a /*M!110400 ... */ executable comment as
-- SchemaSmith_TablePeriodsJson's does: below 11.4 the engine sees a comment, the function returns NULL, and a temporal
-- key reads back as a plain unique key over the period's end and start columns -- the catalog lists exactly those.
-- The deploy compares a declared key by its expanded columns, so that blind spot does not churn.
DELIMITER //
DROP FUNCTION IF EXISTS SchemaSmith_IndexPeriodJson//
CREATE FUNCTION SchemaSmith_IndexPeriodJson(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Index VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_Json LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    /*M!110400
    SELECT JSON_OBJECT('Name', pd.PERIOD, 'StartColumn', pd.START_COLUMN_NAME, 'EndColumn', pd.END_COLUMN_NAME)
      INTO v_Json
      FROM INFORMATION_SCHEMA.KEY_PERIOD_USAGE kpu
      JOIN INFORMATION_SCHEMA.PERIODS pd
        ON pd.TABLE_SCHEMA = kpu.TABLE_SCHEMA AND pd.TABLE_NAME = kpu.TABLE_NAME AND pd.PERIOD = kpu.PERIOD_NAME
     WHERE kpu.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(kpu.TABLE_SCHEMA) = v_DbKey
       AND SchemaSmith_IdentifierKey(kpu.TABLE_NAME) = v_TableKey
       AND SchemaSmith_NameKeyCI(kpu.CONSTRAINT_NAME) = SchemaSmith_NameKeyCI(p_Index)
     LIMIT 1;
    */
    RETURN v_Json;
END //
DELIMITER ;
