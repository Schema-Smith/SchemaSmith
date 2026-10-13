-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_TablePeriodsJson//

CREATE FUNCTION SchemaSmith_TablePeriodsJson(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_Json LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '[]';

    -- MariaDb variant override of the shared MySQL '[]' stub. See the MySQL base definition for why the
    -- read is isolated in a function at all.
    --
    -- TWO version thresholds, and they are not the same one -- this is the trap in the whole feature:
    --   * application-time periods themselves arrive in 10.4.3
    --   * INFORMATION_SCHEMA.PERIODS, the only catalog that reports them, arrives in 11.4
    -- So on 10.4.3 - 11.3 a period can exist on a table and nothing can be asked about it. This returns
    -- '[]' there, which is a genuine blind spot rather than an answer, and it is documented on
    -- MariaDbTable.Periods and in the reference so a user is not left to infer it from silence.
    --
    -- The read therefore sits inside a /*M!110400 ... */ version-prefixed executable comment: MariaDB
    -- runs the contents only from 11.4, and every earlier version -- including the 10.2 floor and the
    -- 10.6 leg CI runs -- sees a comment and leaves v_Json at its '[]' default. A plain runtime IF would
    -- not do: an unknown TABLE is rejected when the routine is CREATED, so the reference has to be
    -- invisible to the parser rather than merely unreached. SchemaSmith_RebuildBlockedReason uses the
    -- same construct for the same catalog and is the working precedent.
    --
    -- SYSTEM_TIME is excluded deliberately. MariaDB lists it in PERIODS alongside application periods on
    -- an explicitly-versioned table, but that state is already described by IsSystemVersioned and its
    -- columns are already excluded from extraction -- emitting it here as well would have a package
    -- declare the same thing twice, in two shapes that can then disagree.
    /*M!110400
    SET v_Json = COALESCE((
        SELECT CONCAT('[', GROUP_CONCAT(
                   JSON_OBJECT('Name', pd.PERIOD,
                               'StartColumn', pd.START_COLUMN_NAME,
                               'EndColumn', pd.END_COLUMN_NAME)
                   ORDER BY pd.PERIOD SEPARATOR ','), ']')
        FROM INFORMATION_SCHEMA.PERIODS pd
        WHERE pd.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(pd.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(pd.TABLE_NAME) = v_TableKey
          AND pd.PERIOD <> 'SYSTEM_TIME'
    ), '[]');
    */

    RETURN v_Json;
END //

DELIMITER ;
