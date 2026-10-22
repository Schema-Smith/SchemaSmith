-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_TablePartitioningJson//

CREATE FUNCTION SchemaSmith_TablePartitioningJson(
    p_Schema VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Table VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
READS SQL DATA
BEGIN
    -- Name keys: the catalog may spell the schema and table differently from the caller (lower_case_table_names).
    DECLARE v_DbCi VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_Schema;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Schema);
    DECLARE v_TableKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_Table);
    -- The table's partitioning as a JSON object (#partitioning, K3), or 'null' when it has none.
    --
    -- Before this, extraction said nothing about partitioning at all: a partitioned table extracted
    -- cleanly, reported success, and produced a package describing an ordinary unpartitioned table.
    -- Redeploying that package elsewhere builds the wrong physical layout with no error anywhere.
    --
    -- A NON-partitioned table yields ONE row in INFORMATION_SCHEMA.PARTITIONS with every partition column
    -- NULL rather than no rows at all, so the test throughout is PARTITION_NAME IS NOT NULL -- a row count
    -- would report every table as partitioned. Verified on MySQL 5.7 and 8.0 and MariaDB 10.2 and 11.4.
    --
    -- Unlike its Periods sibling this is ONE shared definition with no MariaDb override:
    -- INFORMATION_SCHEMA.PARTITIONS exists on every supported version of both engines, and all four
    -- servers above were probed returning the same columns with the same meanings.
    DECLARE v_Method VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_Expression LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_SubMethod VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_SubExpression LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_Count INT DEFAULT 0;
    DECLARE v_SubCount INT DEFAULT 0;
    DECLARE v_Named INT DEFAULT 0;
    DECLARE v_Partitions LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT NULL;
    DECLARE v_Sub LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';

    -- A subpartitioned table has one row per SUBPARTITION, so every count and list below reads the first
    -- subpartition of each partition (or the partition's own row when there are none).
    SELECT p.PARTITION_METHOD, p.PARTITION_EXPRESSION, p.SUBPARTITION_METHOD, p.SUBPARTITION_EXPRESSION
      INTO v_Method, v_Expression, v_SubMethod, v_SubExpression
      FROM INFORMATION_SCHEMA.PARTITIONS p
     WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
       AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
       AND p.PARTITION_NAME IS NOT NULL
     ORDER BY p.PARTITION_ORDINAL_POSITION, p.SUBPARTITION_ORDINAL_POSITION
     LIMIT 1;

    IF v_Method IS NULL THEN
        RETURN 'null';
    END IF;

    -- v_Named counts partitions that carry a comment or a name other than the engine's own p0, p1, ...: a HASH or
    -- KEY table with none of either is written as a count, as the engine would create it from one.
    SELECT COUNT(*),
           SUM(p.PARTITION_NAME <> CONCAT('p', p.PARTITION_ORDINAL_POSITION - 1) OR COALESCE(p.PARTITION_COMMENT, '') <> '')
      INTO v_Count, v_Named
      FROM INFORMATION_SCHEMA.PARTITIONS p
     WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
       AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
       AND p.PARTITION_NAME IS NOT NULL
       AND COALESCE(p.SUBPARTITION_ORDINAL_POSITION, 1) = 1;

    IF v_SubMethod IS NOT NULL THEN
        SELECT COUNT(*) INTO v_SubCount
          FROM INFORMATION_SCHEMA.PARTITIONS p
         WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
           AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
           AND p.PARTITION_ORDINAL_POSITION = 1
           AND p.SUBPARTITION_NAME IS NOT NULL;
        SET v_Sub = CONCAT(',"Subpartitioning":{"Method":', JSON_QUOTE(v_SubMethod),
                           ',"Expression":', JSON_QUOTE(v_SubExpression),
                           ',"Count":', v_SubCount, '}');
    END IF;

    -- SYSTEM_TIME (MariaDB): the engine's expression is its own (over the row-end column) and is not declared, and
    -- INTERVAL, STARTS, LIMIT and AUTO appear only in SHOW CREATE TABLE, which SchemaTongs reads. Partitions the
    -- engine named (history p0, p1, ... and the current pn) are written as a count; any others as a list.
    IF v_Method = 'SYSTEM_TIME' THEN
        SELECT SUM(CASE WHEN p.PARTITION_DESCRIPTION = 'CURRENT' THEN p.PARTITION_NAME <> 'pn'
                        ELSE p.PARTITION_NAME <> CONCAT('p', p.PARTITION_ORDINAL_POSITION - 1) END)
          INTO v_Named
          FROM INFORMATION_SCHEMA.PARTITIONS p
         WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
           AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
           AND p.PARTITION_NAME IS NOT NULL;
        IF COALESCE(v_Named, 0) = 0 THEN
            RETURN JSON_OBJECT('Method', v_Method, 'PartitionCount', CAST(v_Count AS UNSIGNED));
        END IF;
        SELECT GROUP_CONCAT(
                   CONCAT('{"Name":', JSON_QUOTE(p.PARTITION_NAME),
                          ',"Values":', JSON_QUOTE(IF(p.PARTITION_DESCRIPTION = 'CURRENT', 'CURRENT', 'HISTORY')), '}')
                   ORDER BY p.PARTITION_ORDINAL_POSITION SEPARATOR ',')
          INTO v_Partitions
          FROM INFORMATION_SCHEMA.PARTITIONS p
         WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
           AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
           AND p.PARTITION_NAME IS NOT NULL;
        RETURN CONCAT('{"Method":', JSON_QUOTE(v_Method), ',"Partitions":[', COALESCE(v_Partitions, ''), ']}');
    END IF;

    -- HASH and KEY have no per-partition boundary -- the engine assigns rows by hashing -- so they carry a
    -- COUNT and no partition list, unless a partition has a name of its own or a comment.
    IF v_Method IN ('HASH', 'KEY', 'LINEAR HASH', 'LINEAR KEY') AND COALESCE(v_Named, 0) = 0 THEN
        -- CAST: MariaDB writes a local INT filled by SELECT ... INTO as a JSON string ("3"), which the package's schema
        -- refuses as an integer. MySQL writes the number either way.
        RETURN JSON_OBJECT('Method', v_Method, 'Expression', v_Expression, 'PartitionCount', CAST(v_Count AS UNSIGNED));
    END IF;

    -- ORDER IS PART OF THE DEFINITION: RANGE boundaries must ascend and the engine rejects a definition
    -- where they do not, so this reads by PARTITION_ORDINAL_POSITION rather than by name.
    --
    -- Assembled through CONCAT + JSON_EXTRACT rather than JSON_ARRAYAGG: JSON_ARRAYAGG is MySQL 5.7.22+
    -- and absent from the MariaDB floor, and CAST(x AS JSON) does not exist on MariaDB at all -- the same
    -- workaround every other nested array in GenerateTableJson uses, for the same reason.
    SELECT GROUP_CONCAT(
               CONCAT('{"Name":', JSON_QUOTE(p.PARTITION_NAME),
                      CASE WHEN v_Method IN ('HASH', 'KEY', 'LINEAR HASH', 'LINEAR KEY') THEN ''
                           ELSE CONCAT(',"Values":', JSON_QUOTE(p.PARTITION_DESCRIPTION)) END,
                      CASE WHEN COALESCE(p.PARTITION_COMMENT, '') = '' THEN ''
                           ELSE CONCAT(',"Comment":', JSON_QUOTE(p.PARTITION_COMMENT)) END,
                      '}')
               ORDER BY p.PARTITION_ORDINAL_POSITION SEPARATOR ',')
      INTO v_Partitions
      FROM INFORMATION_SCHEMA.PARTITIONS p
     WHERE p.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(p.TABLE_SCHEMA) = v_DbKey
       AND SchemaSmith_IdentifierKey(p.TABLE_NAME) = v_TableKey
       AND p.PARTITION_NAME IS NOT NULL
       AND COALESCE(p.SUBPARTITION_ORDINAL_POSITION, 1) = 1;

    RETURN CONCAT('{"Method":', JSON_QUOTE(v_Method),
                  ',"Expression":', JSON_QUOTE(v_Expression),
                  v_Sub,
                  ',"Partitions":[', COALESCE(v_Partitions, ''), ']}');
END //

DELIMITER ;
