-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_IsSystemTimePeriodColumn//

CREATE FUNCTION SchemaSmith_IsSystemTimePeriodColumn(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Column VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TINYINT
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    DECLARE v_ColumnKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_NameKeyCI(p_Column);
    -- MariaDb variant override of the shared MySQL function. See the MySQL base definition for why this
    -- is isolated in a function at all rather than inlined into GenerateTableJson.
    --
    -- Returns 1 for the row-start / row-end columns of a system-versioned table. Those columns are
    -- generated and maintained by the engine; SchemaSmith must not extract them as ordinary columns,
    -- because the apply path would then try to manage them and a re-deploy would attempt DDL the engine
    -- owns. SQL Server has the same exclusion for GENERATED ALWAYS AS ROW START/END (#369).
    --
    -- Only the EXPLICIT authoring form exposes them. Verified live on 11.4.12:
    --   CREATE TABLE t (...) WITH SYSTEM VERSIONING                  -> row_start/row_end are HIDDEN,
    --                                                                   absent from INFORMATION_SCHEMA.COLUMNS
    --   CREATE TABLE t (..., rs ... ROW START, re ... ROW END,
    --                   PERIOD FOR SYSTEM_TIME(rs, re)) WITH SYSTEM VERSIONING
    --                                                                -> rs/re present, EXTRA = 'STORED GENERATED',
    --                                                                   IS_SYSTEM_TIME_PERIOD_START/_END = 'YES'
    -- So the implicit form needs no exclusion and this simply returns 0 for it.
    --
    -- Read GENERATION_EXPRESSION, not IS_SYSTEM_TIME_PERIOD_*. The latter looks like the obvious column
    -- and is a trap: it ships with INFORMATION_SCHEMA.PERIODS in 11.4, NOT with system versioning in
    -- 10.3. Measured against live servers -- absent on 10.2 AND 10.6, present on 11.4 -- after an
    -- asserted 10.3 guard cost every 10.3-11.3 target an 'Unknown column' failure.
    --
    -- GENERATION_EXPRESSION carries the literal 'ROW START' / 'ROW END' for an explicitly-declared
    -- period column and is present on every supported version, so this needs no version gate at all and
    -- leaves no read gap between 10.3 and 11.3. Verified identical on 10.6 and 11.4; on 10.2 the column
    -- exists and simply matches nothing, which is correct there -- 10.2 has no system versioning.
    --
    -- The implicit form (row_start/row_end hidden) is absent from INFORMATION_SCHEMA.COLUMNS entirely,
    -- so it needs no exclusion and this returns 0 for it either way.
    RETURN COALESCE((
        SELECT CASE WHEN c.GENERATION_EXPRESSION IN ('ROW START', 'ROW END') THEN 1 ELSE 0 END
        FROM INFORMATION_SCHEMA.COLUMNS c
        WHERE c.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(c.TABLE_SCHEMA) = v_DbKey
          AND SchemaSmith_IdentifierKey(c.TABLE_NAME) = v_TableKey
          AND SchemaSmith_NameKeyCI(c.COLUMN_NAME) = v_ColumnKey
    ), 0);
END //

DELIMITER ;
