-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ParseTableJson//

CREATE PROCEDURE SchemaSmith_ParseTableJson(
    IN p_DatabaseName VARCHAR(128),
    IN p_TableDefinitions LONGTEXT
)
SQL SECURITY DEFINER
BEGIN
    -- Loop counters for the JSON_EXTRACT-based shred of p_TableDefinitions (MySQL 5.7 / MariaDB 10.2
    -- compatible replacement for JSON_TABLE, which does not exist on those versions). One outer/inner
    -- counter pair per JSON_TABLE call this procedure used to make; grouped here because MySQL requires
    -- all DECLAREs at the top of the BEGIN block, before any executable statement.
    DECLARE v_TblCnt INT;
    DECLARE v_TblIdx INT;
    DECLARE v_ColOuterCnt INT;
    DECLARE v_ColOuterIdx INT;
    DECLARE v_ColInnerCnt INT;
    DECLARE v_ColInnerIdx INT;
    DECLARE v_IxOuterCnt INT;
    DECLARE v_IxOuterIdx INT;
    DECLARE v_IxInnerCnt INT;
    DECLARE v_IxInnerIdx INT;
    DECLARE v_FkOuterCnt INT;
    DECLARE v_FkOuterIdx INT;
    DECLARE v_FkInnerCnt INT;
    DECLARE v_FkInnerIdx INT;
    DECLARE v_ChkOuterCnt INT;
    DECLARE v_ChkOuterIdx INT;
    DECLARE v_ChkInnerCnt INT;
    DECLARE v_ChkInnerIdx INT;
    DECLARE v_FtOuterCnt INT;
    DECLARE v_FtOuterIdx INT;
    DECLARE v_FtInnerCnt INT;
    DECLARE v_FtInnerIdx INT;
    DECLARE v_PdOuterCnt INT;
    DECLARE v_PdOuterIdx INT;
    DECLARE v_PdInnerCnt INT;
    DECLARE v_PdInnerIdx INT;

    -- Parse JSON table definitions into temporary tables for MySQL
    -- These temp tables persist in the session and are used by
    -- SchemaSmith_MissingTableAndColumnQuench, SchemaSmith_ModifiedTableQuench, and SchemaSmith_MissingIndexesAndConstraintsQuench

    -- Drop existing temp tables if they exist
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Tables;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Columns;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Indexes;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ForeignKeys;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_CheckConstraints;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FullTextIndexes;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Partitions;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Periods;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse table definitions');

    -- Parse Tables from JSON
    -- RowId is the synthetic primary key so the per-row ShouldApply UPDATE below can target
    -- exactly the source row whose expression evaluated false. Without it, two table entries
    -- sharing a Name with mutually exclusive ShouldApply would either collide on the natural-key
    -- PRIMARY KEY (TableName) at INSERT time or silently mark both rows ShouldApply=0 at UPDATE
    -- time. TableName remains an indexed lookup column but is no longer the uniqueness constraint.
    CREATE TEMPORARY TABLE _SchemaSmith_Tables (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        Engine VARCHAR(50) DEFAULT 'InnoDB',
        Collation VARCHAR(100) DEFAULT NULL,
        OldName VARCHAR(128) DEFAULT NULL,
        RowFormat VARCHAR(20) DEFAULT NULL,
        Compression VARCHAR(20) DEFAULT NULL,
        KeyBlockSize INT DEFAULT NULL,
        PageCompressed TINYINT DEFAULT 0,
        PageCompressionLevel INT DEFAULT NULL,
        Encryption VARCHAR(1) DEFAULT NULL,
        Encrypted TINYINT DEFAULT 0,
        EncryptionKeyId INT DEFAULT NULL,
        -- The InnoDB general tablespace this table is placed in (F2b), MySQL only. Placement, applied only
        -- at CREATE -- an existing table whose declared value disagrees with what is deployed is refused
        -- by ModifiedTableQuench (STEP -0.4), never converged. 64 chars matches MySQL's own identifier
        -- ceiling, which a general tablespace name is subject to.
        Tablespace VARCHAR(64) DEFAULT NULL,
        -- The filesystem directory this table's InnoDB data file is placed in (F2c), both engines.
        -- Placement, applied only at CREATE -- an existing table whose declared value disagrees with what
        -- is deployed is refused by ModifiedTableQuench, never converged. 512 chars matches the OUT param
        -- width on SchemaSmith_TableDataDirectory, itself sized to a comfortable filesystem-path ceiling.
        DataDirectory VARCHAR(512) DEFAULT NULL,
        -- Partitioning (#partitioning, K3). Flat on the table rather than a child table because there is at
        -- most ONE of each per table; the per-partition list lives in _SchemaSmith_Partitions below.
        PartitionMethod VARCHAR(20) DEFAULT NULL,
        PartitionExpression TEXT DEFAULT NULL,
        PartitionCount INT DEFAULT NULL,
        AutoIncrementValue BIGINT UNSIGNED DEFAULT NULL,
        -- MySQL's table comment ceiling is 2048 characters (COLUMN_COMMENT/COLUMN varies -- see the
        -- Columns/Indexes temp tables below). No pre-validation against that limit: an over-long
        -- comment is left for the engine's own error at CREATE/ALTER time (matching this codebase's
        -- existing convention for engine-enforced limits elsewhere).
        Comment VARCHAR(2048) DEFAULT NULL,
        NewTable TINYINT DEFAULT 0,
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        AutoIncrementKeyClause VARCHAR(500) DEFAULT '',
        DropColumnsRemovedFromProduct TINYINT DEFAULT NULL,
        DropForeignKeysRemovedFromProduct TINYINT DEFAULT NULL,
        DropCheckConstraintsRemovedFromProduct TINYINT DEFAULT NULL,
        DropPeriodsRemovedFromProduct TINYINT DEFAULT NULL,
        DropIndexesRemovedFromProduct TINYINT DEFAULT NULL,
        -- RebuildPolicy resolves MOST-SPECIFIC-WINS on the WHOLE object (ProductQuench.ResolveCascadedPolicy),
        -- so the apply side needs the SENTINEL -- did this table declare a policy at all? -- and not just
        -- the field values. A table declaring only { "Mode": "ALWAYS" } must not inherit a product-level
        -- Threshold, which a per-field COALESCE against the passed-in tier would graft on.
        RebuildPolicyMode VARCHAR(20) DEFAULT NULL,
        RebuildPolicyThreshold INT DEFAULT NULL,
        RebuildPolicyOnOrderMismatch TINYINT DEFAULT NULL,
        RebuildPolicySpecified TINYINT DEFAULT 0,
        PreventDrop TINYINT DEFAULT 0,
        -- F1S1: table-level system versioning (MariaDB WITH SYSTEM VERSIONING). Read here so a new
        -- table's CREATE can emit the clause; converging an EXISTING table's versioning is a separate
        -- later task, so this flag is only consulted where t.NewTable = 1.
        IsSystemVersioned TINYINT DEFAULT 0,
        KEY ix_tables_name (TableName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- First, insert all tables from JSON with NewTable = 0 (assume existing)
    -- The NOT EXISTS check against INFORMATION_SCHEMA is done separately via UPDATE
    -- to avoid a MySQL optimizer issue where correlated subqueries with function calls
    -- inside JSON_TABLE context don't re-evaluate correctly for all rows.
    SET v_TblCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_TblIdx = 0;
    WHILE v_TblIdx < v_TblCnt DO
        IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Name'))) IS NOT NULL THEN
            INSERT INTO _SchemaSmith_Tables (TableName, Engine, Collation, OldName, RowFormat, Compression, KeyBlockSize, PageCompressed, PageCompressionLevel, Encryption, Encrypted, EncryptionKeyId, Tablespace, DataDirectory, PartitionMethod, PartitionExpression, PartitionCount, AutoIncrementValue, Comment, NewTable, ShouldApply, ShouldApplyExpression, VariantName, DropColumnsRemovedFromProduct, DropForeignKeysRemovedFromProduct, DropCheckConstraintsRemovedFromProduct, DropPeriodsRemovedFromProduct, DropIndexesRemovedFromProduct, RebuildPolicyMode, RebuildPolicyThreshold, RebuildPolicyOnOrderMismatch, RebuildPolicySpecified, PreventDrop, IsSystemVersioned)
            SELECT
                SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Name')))) AS TableName,
                COALESCE(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Engine')))), ''), 'InnoDB') AS Engine,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Collation')))), '') AS Collation,
                -- #375: a blank/whitespace OldName means "no rename" -> NULL, not a manufactured `` backtick pair.
                -- Otherwise the OldName IS NOT NULL rename guards fire and two empty-OldName tables collide on
                -- _SchemaSmith_TableRenames.PRIMARY (OldTableName = '') on the second deploy.
                SchemaSmith_SafeBacktickWrap(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].OldName')))), '')) AS OldName,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].RowFormat')))), '') AS RowFormat,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Compression')))), '') AS Compression,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].KeyBlockSize'))) AS KeyBlockSize,
                COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].PageCompressed'))), 0) AS PageCompressed,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].PageCompressionLevel'))) AS PageCompressionLevel,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Encryption')))), '') AS Encryption,
                COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Encrypted'))), 0) AS Encrypted,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].EncryptionKeyId'))) AS EncryptionKeyId,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Tablespace')))), '') AS Tablespace,
                -- Strip a trailing '/' on the DECLARED value so it matches SchemaSmith_TableDataDirectory's
                -- deployed read, which normalizes it off (both engines). Without this, a declared '/x/'
                -- compares unequal to the deployed '/x' and the move-refuse fires on every redeploy forever.
                NULLIF(TRIM(TRAILING '/' FROM TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DataDirectory'))))), '') AS DataDirectory,
                UPPER(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Partitioning.Method')))), '')) AS PartitionMethod,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Partitioning.Expression')))), '') AS PartitionExpression,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Partitioning.PartitionCount'))) AS PartitionCount,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].AutoIncrementValue'))) AS AutoIncrementValue,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].Comment')))), '') AS Comment,
                0 AS NewTable,
                1 AS ShouldApply,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].VariantName')))), '') AS VariantName,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DropColumnsRemovedFromProduct'))) AS DropColumnsRemovedFromProduct,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DropForeignKeysRemovedFromProduct'))) AS DropForeignKeysRemovedFromProduct,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DropCheckConstraintsRemovedFromProduct'))) AS DropCheckConstraintsRemovedFromProduct,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DropPeriodsRemovedFromProduct'))) AS DropPeriodsRemovedFromProduct,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].DropIndexesRemovedFromProduct'))) AS DropIndexesRemovedFromProduct,
                NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].RebuildPolicy.Mode')))), '') AS RebuildPolicyMode,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].RebuildPolicy.Threshold'))) AS RebuildPolicyThreshold,
                SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].RebuildPolicy.OnOrderMismatch'))) AS RebuildPolicyOnOrderMismatch,
                -- The sentinel tests the value's TYPE, not mere path presence. JSON_CONTAINS_PATH would
                -- answer 1 for '"RebuildPolicy": null' -- which is exactly what an UNDECLARED policy
                -- serializes to -- and that would stop the product- or environment-level policy from
                -- applying. JSON_TYPE returns 'NULL' there and SQL NULL when the path is absent entirely,
                -- so both fall out as 0.
                COALESCE(JSON_TYPE(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].RebuildPolicy'))) = 'OBJECT', 0) AS RebuildPolicySpecified,
                COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].PreventDrop'))), 0) AS PreventDrop,
                COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_TblIdx, '].IsSystemVersioned'))), 0) AS IsSystemVersioned;
        END IF;
        SET v_TblIdx = v_TblIdx + 1;
    END WHILE;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Identify new tables');

    -- Snapshot existing tables into a temp table to avoid MySQL optimizer issues
    -- with correlated NOT EXISTS subqueries against INFORMATION_SCHEMA.
    -- The optimizer can cache/materialize INFORMATION_SCHEMA results incorrectly
    -- when used in correlated subqueries (both in JSON_TABLE and UPDATE contexts).
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingTables (
        TableName VARCHAR(128) NOT NULL PRIMARY KEY
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_ExistingTables (TableName)
    SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
    WHERE BINARY TABLE_SCHEMA = BINARY p_DatabaseName
    -- MariaDB reports a system-versioned table as 'SYSTEM VERSIONED', not 'BASE TABLE'. This list is
    -- what sets NewTable = 1, so filtering on 'BASE TABLE' alone made such a table look new and the
    -- deploy issued a CREATE for a table that already exists. Managing the versioning attribute is a
    -- separate feature; being able to see the table at all is not.
    AND TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED');

    -- Now set NewTable = 1 for tables not found in snapshot
    UPDATE _SchemaSmith_Tables t
    SET t.NewTable = 1
    WHERE NOT EXISTS (
        SELECT 1 FROM _SchemaSmith_ExistingTables et
        WHERE BINARY et.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
        OR (t.OldName IS NOT NULL AND BINARY et.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.OldName))
    );

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse columns');

    -- Parse Columns from JSON
    -- RowId is the synthetic primary key (see _SchemaSmith_Tables above). The natural key
    -- (TableName, ColumnName) becomes a regular index for lookup performance. Two same-named
    -- column entries with mutually exclusive ShouldApply expressions can now coexist in
    -- _SchemaSmith_Columns until ShouldApply DELETE removes the one whose expression evaluates false.
    CREATE TEMPORARY TABLE _SchemaSmith_Columns (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        OrdinalPosition INT NOT NULL DEFAULT 0,
        DataType VARCHAR(100) NOT NULL,
        IsNullable TINYINT DEFAULT 1,
        DefaultValue TEXT DEFAULT NULL,
        -- Independent of DefaultValue: DEFAULT governs INSERT-time initialization, this governs
        -- UPDATE-time refresh. 30 chars comfortably covers 'CURRENT_TIMESTAMP(6)' (the max fractional
        -- precision) with room to spare.
        OnUpdateCurrentTimestamp VARCHAR(30) DEFAULT NULL,
        IsAutoIncrement TINYINT DEFAULT 0,
        GeneratedExpression TEXT DEFAULT NULL,
        GeneratedType VARCHAR(10) DEFAULT NULL,
        CharacterSet VARCHAR(50) DEFAULT NULL,
        Collation VARCHAR(100) DEFAULT NULL,
        CheckExpression TEXT DEFAULT NULL,
        IsInvisible TINYINT DEFAULT 0,
        IsWithoutSystemVersioning TINYINT DEFAULT 0,
        Srid INT DEFAULT NULL,
        -- MySQL's column comment ceiling is 1024 characters. No pre-validation -- see the Tables
        -- temp table Comment column above for the same convention.
        Comment VARCHAR(1024) DEFAULT NULL,
        OldName VARCHAR(128) DEFAULT NULL,
        NewColumn TINYINT DEFAULT 0,
        ColumnScript TEXT DEFAULT NULL,
        DependencyLevel INT DEFAULT 0,
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_columns_table_name (TableName, ColumnName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_ColOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_ColOuterIdx = 0;
    WHILE v_ColOuterIdx < v_ColOuterCnt DO
        SET v_ColInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns'))), 0);
        SET v_ColInnerIdx = 0;
        WHILE v_ColInnerIdx < v_ColInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_Columns (
                    TableName, ColumnName, OrdinalPosition, DataType, IsNullable, DefaultValue, OnUpdateCurrentTimestamp,
                    IsAutoIncrement, GeneratedExpression, GeneratedType,
                    CharacterSet, Collation, CheckExpression, IsInvisible, IsWithoutSystemVersioning, Srid, Comment, OldName, NewColumn, ShouldApply, ShouldApplyExpression, VariantName
                )
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Name')))) AS ColumnName,
                    v_ColInnerIdx + 1 AS OrdinalPosition,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].DataType'))) AS DataType,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Nullable'))), 1) AS IsNullable,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Default'))) AS DefaultValue,
                    -- UPPER so a hand-authored lower-case 'current_timestamp(3)' compares equal to the
                    -- canonical form SchemaSmith_ColumnOnUpdateClause extracts from a live target.
                    UPPER(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].OnUpdateCurrentTimestamp')))), '')) AS OnUpdateCurrentTimestamp,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].AutoIncrement'))), 0) AS IsAutoIncrement,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].GenerationExpression'))) AS GeneratedExpression,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Generated'))) AS GeneratedType,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].CharacterSet'))) AS CharacterSet,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Collation'))) AS Collation,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].CheckExpression'))) AS CheckExpression,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Invisible'))), 0) AS IsInvisible,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].WithoutSystemVersioning'))), 0) AS IsWithoutSystemVersioning,
                    SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Srid'))) AS Srid,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].Comment')))), '') AS Comment,
                    -- #375: blank/whitespace OldName -> NULL (no rename), same as the table-level OldName above.
                    SchemaSmith_SafeBacktickWrap(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].OldName')))), '')) AS OldName,
                    0 AS NewColumn,
                    1 AS ShouldApply,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ColOuterIdx, '].Columns[', v_ColInnerIdx, '].VariantName')))), '') AS VariantName;
            END IF;
            SET v_ColInnerIdx = v_ColInnerIdx + 1;
        END WHILE;
        SET v_ColOuterIdx = v_ColOuterIdx + 1;
    END WHILE;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Identify new columns');

    -- Snapshot existing columns into a temp table (same optimizer workaround as tables)
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingColumns;
    CREATE TEMPORARY TABLE _SchemaSmith_ExistingColumns (
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        PRIMARY KEY (TableName, ColumnName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    INSERT INTO _SchemaSmith_ExistingColumns (TableName, ColumnName)
    SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
    WHERE BINARY TABLE_SCHEMA = BINARY p_DatabaseName;

    -- Now set NewColumn = 1 for columns not found in snapshot
    UPDATE _SchemaSmith_Columns c
    SET c.NewColumn = 1
    WHERE NOT EXISTS (
        SELECT 1 FROM _SchemaSmith_ExistingColumns ec
        WHERE BINARY ec.TableName = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        AND (
            BINARY ec.ColumnName = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
            OR (c.OldName IS NOT NULL AND BINARY ec.ColumnName = BINARY SchemaSmith_StripBacktickWrapping(c.OldName))
        )
    );

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingColumns;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Build column scripts');

    -- Build ColumnScript for each column
    -- The trailing SRID / INVISIBLE clauses are appended OUTSIDE the generated/regular CASE so they apply
    -- to either form uniformly, mirroring how SchemaSmith_IndexInvisibleClause is appended after an
    -- index's column list rather than folded into it. SRID is gated behind SchemaSmith_SupportsColumnSrid()
    -- (MySQL 8.0.3+; MariaDB never), INVISIBLE behind SchemaSmith_SupportsInvisibleColumn() (MySQL 8.0.23 /
    -- MariaDB 10.3): below its floor each keyword is a hard syntax error, so suppressing it here (rather
    -- than at each of the CREATE TABLE / ADD COLUMN / MODIFY COLUMN emit sites that consume ColumnScript)
    -- is the single point that keeps a declared SRID / invisible column safely degrading everywhere. SRID
    -- is placed immediately after the type/nullability/default block, matching MySQL's own reference-manual
    -- rendering ("g GEOMETRY NOT NULL SRID 4326") -- the form a user hand-authoring the JSON would recognize.
    UPDATE _SchemaSmith_Columns
    SET ColumnScript = CONCAT(
        ColumnName, ' ',
        CASE
            WHEN GeneratedExpression IS NOT NULL AND TRIM(GeneratedExpression) != '' THEN
                CONCAT(
                    SchemaSmith_UpperDataType(DataType), ' ',
                    'GENERATED ALWAYS AS (', GeneratedExpression, ') ',
                    COALESCE(UPPER(GeneratedType), 'VIRTUAL')
                )
            ELSE
                CONCAT(
                    SchemaSmith_UpperDataType(DataType),
                    CASE WHEN CharacterSet IS NOT NULL AND TRIM(CharacterSet) != ''
                         THEN CONCAT(' CHARACTER SET ', CharacterSet) ELSE '' END,
                    CASE WHEN Collation IS NOT NULL AND TRIM(Collation) != ''
                         THEN CONCAT(' COLLATE ', Collation) ELSE '' END,
                    CASE WHEN IsNullable = 1 THEN ' NULL' ELSE ' NOT NULL' END,
                    CASE WHEN IsAutoIncrement = 1 THEN ' AUTO_INCREMENT' ELSE '' END,
                    CASE WHEN DefaultValue IS NOT NULL AND TRIM(DefaultValue) != '' AND IsAutoIncrement = 0
                         THEN CONCAT(' DEFAULT ',
                              -- A default containing '(' is wrapped so a function default (UUID()) emits as
                              -- MySQL 8.0.13's expression-default form. The temporal defaults must NOT be:
                              -- CURRENT_TIMESTAMP(3) and its synonyms are ordinary column defaults that
                              -- predate expression defaults entirely, and wrapping them turns a clause every
                              -- version accepts into a hard syntax error below 8.0.13.
                              CASE WHEN DefaultValue REGEXP '\\(' AND LEFT(DefaultValue, 1) != '('
                                    AND UPPER(TRIM(DefaultValue)) NOT REGEXP
                                        '^(CURRENT_TIMESTAMP|NOW|LOCALTIME|LOCALTIMESTAMP)[[:space:]]*\\([0-9]*\\)$'
                                   THEN CONCAT('(', DefaultValue, ')')
                                   ELSE DefaultValue END)
                         ELSE '' END,
                    -- ON UPDATE must immediately follow the DEFAULT clause per MySQL/MariaDB's own
                    -- column_definition grammar (unlike SRID/INVISIBLE/COMMENT below, which are trailing
                    -- options appended after this whole block) -- placed inside this CONCAT rather than
                    -- with those. No SchemaSmith_Supports... gate: the clause predates both floors.
                    CASE WHEN OnUpdateCurrentTimestamp IS NOT NULL AND TRIM(OnUpdateCurrentTimestamp) != ''
                         THEN CONCAT(' ON UPDATE ', OnUpdateCurrentTimestamp) ELSE '' END
                )
        END,
        CASE WHEN Srid IS NOT NULL AND SchemaSmith_SupportsColumnSrid() = 1 THEN CONCAT(' SRID ', Srid) ELSE '' END,
        CASE WHEN IsInvisible = 1 AND SchemaSmith_SupportsInvisibleColumn() = 1 THEN ' INVISIBLE' ELSE '' END,
        -- COMMENT is placed last, after SRID/INVISIBLE, matching MySQL's own trailing-option
        -- placement and the identical pattern already used for FULLTEXT index comments below.
        -- Escaping (double the embedded single quotes) mirrors that same established form -- see
        -- _SchemaSmith_FullTextIndexes.Comment's CREATE/ADD FULLTEXT INDEX emit in
        -- SchemaSmith_IndexOnlyQuench.sql.
        CASE WHEN Comment IS NOT NULL AND Comment != '' THEN CONCAT(' COMMENT ''', REPLACE(Comment, '''', ''''''), '''') ELSE '' END,
        -- Truly last: MariaDB accepts this clause either side of COMMENT (verified on 11.4), so it goes
        -- where the grammar documents it rather than interleaved. Gated the same way SRID and INVISIBLE
        -- are -- below MariaDB 10.3, and on MySQL at any version, the keyword is a hard syntax error, so
        -- suppressing it at this single build point degrades a declared column safely everywhere rather
        -- than failing the statement. #408
        CASE WHEN IsWithoutSystemVersioning = 1 AND SchemaSmith_SupportsSystemVersioning() = 1
             THEN ' WITHOUT SYSTEM VERSIONING' ELSE '' END
    );

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Calculate generated column dependencies');

    -- Calculate DependencyLevel for generated columns
    -- Level 0: Regular columns (default)
    -- Level 1+: Generated columns, ordered by dependencies on other generated columns
    --
    -- Note: MySQL doesn't allow reopening a temp table in any self-referential context,
    -- so we use TWO separate temp tables and copy between them.

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps2;

    CREATE TEMPORARY TABLE _SchemaSmith_GenColDeps (
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        ColumnNameStripped VARCHAR(128) NOT NULL,
        GeneratedExpression TEXT NOT NULL,
        DependencyLevel INT DEFAULT 0,
        PRIMARY KEY (TableName, ColumnName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    CREATE TEMPORARY TABLE _SchemaSmith_GenColDeps2 (
        TableName VARCHAR(128) NOT NULL,
        ColumnName VARCHAR(128) NOT NULL,
        ColumnNameStripped VARCHAR(128) NOT NULL,
        GeneratedExpression TEXT NOT NULL,
        DependencyLevel INT DEFAULT 0,
        PRIMARY KEY (TableName, ColumnName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    -- Copy generated columns to first helper table
    INSERT INTO _SchemaSmith_GenColDeps (TableName, ColumnName, ColumnNameStripped, GeneratedExpression)
    SELECT TableName, ColumnName, SchemaSmith_StripBacktickWrapping(ColumnName), GeneratedExpression
    FROM _SchemaSmith_Columns
    WHERE GeneratedExpression IS NOT NULL
      AND TRIM(GeneratedExpression) != '';

    -- Make a copy for self-reference queries
    INSERT INTO _SchemaSmith_GenColDeps2 SELECT * FROM _SchemaSmith_GenColDeps;

    -- Iteratively assign dependency levels
    SET @_ssc_curr_level = 0;
    SET @_ssc_prev_unresolved = -1;

    dep_loop: WHILE @_ssc_curr_level < 10 DO
        SET @_ssc_curr_level = @_ssc_curr_level + 1;

        -- Count unresolved generated columns
        SELECT COUNT(*) INTO @_ssc_curr_unresolved
        FROM _SchemaSmith_GenColDeps
        WHERE DependencyLevel = 0;

        -- If no change from last iteration, we're either done or have a cycle
        IF @_ssc_curr_unresolved = @_ssc_prev_unresolved THEN
            LEAVE dep_loop;
        END IF;
        SET @_ssc_prev_unresolved = @_ssc_curr_unresolved;

        -- If nothing left to resolve, we're done
        IF @_ssc_curr_unresolved = 0 THEN
            LEAVE dep_loop;
        END IF;

        -- Update columns in GenColDeps whose dependencies (checked in GenColDeps2) are all resolved
        -- A column can be resolved if no other unresolved generated column is referenced in its expression
        UPDATE _SchemaSmith_GenColDeps g1
        SET g1.DependencyLevel = @_ssc_curr_level
        WHERE g1.DependencyLevel = 0
          AND NOT EXISTS (
              SELECT 1 FROM _SchemaSmith_GenColDeps2 g2
              WHERE g2.TableName = g1.TableName
                AND g2.DependencyLevel = 0
                AND g2.ColumnName != g1.ColumnName
                AND (g1.GeneratedExpression LIKE CONCAT('%', g2.ColumnNameStripped, '%')
                     OR g1.GeneratedExpression LIKE CONCAT('%', g2.ColumnName, '%'))
          );

        -- Sync the changes back to GenColDeps2
        UPDATE _SchemaSmith_GenColDeps2 g2
        INNER JOIN _SchemaSmith_GenColDeps g1
            ON g2.TableName = g1.TableName AND g2.ColumnName = g1.ColumnName
        SET g2.DependencyLevel = g1.DependencyLevel;
    END WHILE;

    -- Check for circular dependencies (generated columns still at level 0 after all iterations)
    IF EXISTS (
        SELECT 1 FROM _SchemaSmith_GenColDeps
        WHERE DependencyLevel = 0
    ) THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps2;
        SIGNAL SQLSTATE '45000'
        SET MESSAGE_TEXT = 'Circular dependency detected in generated columns. Cannot determine creation order.';
    END IF;

    -- Copy dependency levels back to main columns table
    UPDATE _SchemaSmith_Columns c
    INNER JOIN _SchemaSmith_GenColDeps g
        ON c.TableName = g.TableName AND c.ColumnName = g.ColumnName
    SET c.DependencyLevel = g.DependencyLevel;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GenColDeps2;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse indexes');

    -- Parse Indexes from JSON
    -- RowId is the synthetic primary key (see _SchemaSmith_Tables above for rationale).
    CREATE TEMPORARY TABLE _SchemaSmith_Indexes (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
        IsPrimaryKey TINYINT DEFAULT 0,
        IsUnique TINYINT DEFAULT 0,
        IndexType VARCHAR(20) DEFAULT 'BTREE',
        IndexColumns TEXT NOT NULL,
        IsVisible TINYINT DEFAULT 1,
        -- MySQL's index comment ceiling is 1024 characters. No pre-validation -- see the Tables
        -- temp table Comment column above for the same convention.
        Comment VARCHAR(1024) DEFAULT NULL,
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_indexes_table_name (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_IxOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_IxOuterIdx = 0;
    WHILE v_IxOuterIdx < v_IxOuterCnt DO
        SET v_IxInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes'))), 0);
        SET v_IxInnerIdx = 0;
        WHILE v_IxInnerIdx < v_IxInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_Indexes (
                    TableName, IndexName, IsPrimaryKey, IsUnique, IndexType, IndexColumns, IsVisible, Comment, ShouldApply, ShouldApplyExpression, VariantName
                )
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].Name')))) AS IndexName,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].PrimaryKey'))), 0) AS IsPrimaryKey,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].Unique'))), SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].PrimaryKey'))), 0) AS IsUnique,
                    COALESCE(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].IndexType')))), ''), 'BTREE') AS IndexType,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].IndexColumns'))) AS IndexColumns,
                    COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].Visible'))), 1) AS IsVisible,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].Comment')))), '') AS Comment,
                    1 AS ShouldApply,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_IxOuterIdx, '].Indexes[', v_IxInnerIdx, '].VariantName')))), '') AS VariantName;
            END IF;
            SET v_IxInnerIdx = v_IxInnerIdx + 1;
        END WHILE;
        SET v_IxOuterIdx = v_IxOuterIdx + 1;
    END WHILE;

    -- MySQL: AUTO_INCREMENT column must be indexed. When it's not the first column
    -- in a composite PK, we need a separate KEY clause in the CREATE TABLE statement.
    -- Uses JOIN syntax instead of correlated subquery to avoid MySQL temp table re-open restriction.
    UPDATE _SchemaSmith_Tables t
      JOIN _SchemaSmith_Columns c ON c.TableName = t.TableName AND c.IsAutoIncrement = 1
      JOIN _SchemaSmith_Indexes i ON i.TableName = t.TableName AND i.IsPrimaryKey = 1
       SET t.AutoIncrementKeyClause = CONCAT(', KEY (`', SchemaSmith_StripBacktickWrapping(c.ColumnName), '`)')
     WHERE LOCATE(CONCAT('`', SchemaSmith_StripBacktickWrapping(c.ColumnName), '`') COLLATE utf8mb4_unicode_ci, i.IndexColumns COLLATE utf8mb4_unicode_ci) > 1;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse foreign keys');

    -- Parse Foreign Keys from JSON
    -- RowId is the synthetic primary key (see _SchemaSmith_Tables above for rationale).
    CREATE TEMPORARY TABLE _SchemaSmith_ForeignKeys (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        KeyName VARCHAR(128) NOT NULL,
        Columns TEXT NOT NULL,
        RelatedTableSchema VARCHAR(128) DEFAULT NULL,
        RelatedTable VARCHAR(128) NOT NULL,
        RelatedColumns TEXT NOT NULL,
        DeleteAction VARCHAR(20) DEFAULT 'NO ACTION',
        UpdateAction VARCHAR(20) DEFAULT 'NO ACTION',
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_fks_table_name (TableName, KeyName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_FkOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_FkOuterIdx = 0;
    WHILE v_FkOuterIdx < v_FkOuterCnt DO
        SET v_FkInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys'))), 0);
        SET v_FkInnerIdx = 0;
        WHILE v_FkInnerIdx < v_FkInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_ForeignKeys (
                    TableName, KeyName, Columns, RelatedTableSchema, RelatedTable, RelatedColumns, DeleteAction, UpdateAction, ShouldApply, ShouldApplyExpression, VariantName
                )
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].Name')))) AS KeyName,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].Columns'))) AS Columns,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].RelatedTableSchema')))), '') AS RelatedTableSchema,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].RelatedTable')))) AS RelatedTable,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].RelatedColumns'))) AS RelatedColumns,
                    COALESCE(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].DeleteAction')))), ''), 'NO ACTION') AS DeleteAction,
                    COALESCE(NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].UpdateAction')))), ''), 'NO ACTION') AS UpdateAction,
                    1 AS ShouldApply,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FkOuterIdx, '].ForeignKeys[', v_FkInnerIdx, '].VariantName')))), '') AS VariantName;
            END IF;
            SET v_FkInnerIdx = v_FkInnerIdx + 1;
        END WHILE;
        SET v_FkOuterIdx = v_FkOuterIdx + 1;
    END WHILE;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse check constraints');

    -- Parse Check Constraints from JSON (MySQL 8.0.16+)
    -- RowId is the synthetic primary key (see _SchemaSmith_Tables above for rationale).
    CREATE TEMPORARY TABLE _SchemaSmith_CheckConstraints (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        ConstraintName VARCHAR(128) NOT NULL,
        Expression TEXT NOT NULL,
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_checks_table_name (TableName, ConstraintName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_ChkOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_ChkOuterIdx = 0;
    WHILE v_ChkOuterIdx < v_ChkOuterCnt DO
        SET v_ChkInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints'))), 0);
        SET v_ChkInnerIdx = 0;
        WHILE v_ChkInnerIdx < v_ChkInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints[', v_ChkInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_CheckConstraints (TableName, ConstraintName, Expression, ShouldApply, ShouldApplyExpression, VariantName)
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints[', v_ChkInnerIdx, '].Name')))) AS ConstraintName,
                    -- Strip MySQL charset introducers (_utf8mb4, _utf8, etc.) and backslash-escaped quotes
                    -- from CHECK_CLAUSE expressions. MySQL's INFORMATION_SCHEMA stores these internally
                    -- but they're not needed in DDL and cause PREPARE failures. Nested REPLACE (not
                    -- REGEXP_REPLACE, which is MySQL 8.0+) keeps this version-agnostic down to the 5.7 floor;
                    -- the longer introducers must be stripped before their prefixes (_utf8mb4/_utf8mb3 before
                    -- _utf8), so they are the innermost. (_utf8mb3 is emitted by MySQL 8.0.30+ for legacy
                    -- 3-byte utf8; kept in sync with GenerateTableJson's regex list.)
                    REPLACE(
                        REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                            SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints[', v_ChkInnerIdx, '].Expression'))),
                            '_utf8mb4', ''), '_utf8mb3', ''), '_utf8', ''), '_latin1', ''), '_binary', ''),
                        '\\''', '''') AS Expression,
                    1 AS ShouldApply,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints[', v_ChkInnerIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_ChkOuterIdx, '].CheckConstraints[', v_ChkInnerIdx, '].VariantName')))), '') AS VariantName;
            END IF;
            SET v_ChkInnerIdx = v_ChkInnerIdx + 1;
        END WHILE;
        SET v_ChkOuterIdx = v_ChkOuterIdx + 1;
    END WHILE;

    -- Note: Tables with just a Name and no columns/indexes are valid for IndexOnlyQuench
    -- scenarios where we want to track that the table is "in the definition" for the purpose
    -- of managing product-owned indexes. All tables in _SchemaSmith_Tables came from the JSON
    -- so they were explicitly included by the user.

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Parse fulltext indexes');

    -- Parse FullText Indexes from JSON
    -- The temp table schema MUST match IndexOnlyQuench's fallback definition exactly
    -- (IndexOnlyQuench.sql has the same CREATE TEMPORARY TABLE IF NOT EXISTS as a fallback;
    -- keep them in lockstep when changing either definition). The earlier helper-temp-table
    -- approach (_SchemaSmith_FTShouldApply) for ShouldApply evaluation was retired so this
    -- table follows the same RowId + ShouldApply+ShouldApplyExpression pattern as the others.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_FullTextIndexes;
    CREATE TEMPORARY TABLE IF NOT EXISTS _SchemaSmith_FullTextIndexes (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        IndexName VARCHAR(128) NOT NULL,
        Columns TEXT NOT NULL,
        Parser VARCHAR(128) DEFAULT NULL,
        -- Was VARCHAR(255) -- narrower than MySQL's actual 1024-char index-comment ceiling (the same
        -- ceiling used for _SchemaSmith_Indexes.Comment above). Under the default strict SQL mode (the
        -- default since MySQL 5.7 / MariaDB 10.2, this codebase's own floor), inserting a live
        -- FULLTEXT index's comment longer than 255 chars into this staging column raised a hard
        -- 1406 "Data too long" error naming this temp table -- a confusing deploy failure for a
        -- comment the engine itself accepted and stored.
        Comment VARCHAR(1024) DEFAULT NULL,
        ShouldApply TINYINT DEFAULT 1,
        ShouldApplyExpression VARCHAR(4000) DEFAULT NULL,
        VariantName VARCHAR(128) DEFAULT NULL,
        KEY ix_ft_table_name (TableName, IndexName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_FtOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_FtOuterIdx = 0;
    WHILE v_FtOuterIdx < v_FtOuterCnt DO
        SET v_FtInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes'))), 0);
        SET v_FtInnerIdx = 0;
        WHILE v_FtInnerIdx < v_FtInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_FullTextIndexes (TableName, IndexName, Columns, Parser, Comment, ShouldApply, ShouldApplyExpression, VariantName)
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].Name')))) AS IndexName,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].Columns'))) AS Columns,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].Parser')))), '') AS Parser,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].Comment')))), '') AS Comment,
                    1 AS ShouldApply,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].ShouldApplyExpression')))), '') AS ShouldApplyExpression,
                    NULLIF(TRIM(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_FtOuterIdx, '].FullTextIndexes[', v_FtInnerIdx, '].VariantName')))), '') AS VariantName;
            END IF;
            SET v_FtInnerIdx = v_FtInnerIdx + 1;
        END WHILE;
        SET v_FtOuterIdx = v_FtOuterIdx + 1;
    END WHILE;

    -- Application-time periods (MariaDB `PERIOD FOR <name>(start, end)`, 10.4.3+). MariaDB-only by
    -- nature -- MySQL has no equivalent at any version -- but staged by the shared parser because a
    -- MySQL package simply never carries the key, so the loop finds nothing and costs nothing.
    --
    -- SYSTEM_TIME never appears here: extraction excludes it deliberately (the table already declares
    -- that state through IsSystemVersioned), so a package cannot ask for it through this door either.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Periods;
    CREATE TEMPORARY TABLE IF NOT EXISTS _SchemaSmith_Periods (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        PeriodName VARCHAR(128) NOT NULL,
        StartColumn VARCHAR(128) NOT NULL,
        EndColumn VARCHAR(128) NOT NULL,
        KEY ix_pd_table_name (TableName, PeriodName)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_PdOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_PdOuterIdx = 0;
    WHILE v_PdOuterIdx < v_PdOuterCnt DO
        SET v_PdInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods'))), 0);
        SET v_PdInnerIdx = 0;
        WHILE v_PdInnerIdx < v_PdInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].Name'))) IS NOT NULL
               AND SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].StartColumn'))) IS NOT NULL
               AND SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].EndColumn'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_Periods (TableName, PeriodName, StartColumn, EndColumn)
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Name')))) AS TableName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].Name')))) AS PeriodName,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].StartColumn')))) AS StartColumn,
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Periods[', v_PdInnerIdx, '].EndColumn')))) AS EndColumn;
            END IF;
            SET v_PdInnerIdx = v_PdInnerIdx + 1;
        END WHILE;
        SET v_PdOuterIdx = v_PdOuterIdx + 1;
    END WHILE;


    -- Partitioning (#partitioning, K3): the ORDERED per-partition list. Order is load-bearing -- RANGE
    -- boundaries must ascend and the engine rejects a definition where they do not -- so Ordinal is carried
    -- explicitly rather than relying on insertion order.
    --
    -- Values is NULL for HASH and KEY, which have no boundary: those declare PartitionCount instead and
    -- normally carry no Partitions array at all.
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Partitions;
    CREATE TEMPORARY TABLE IF NOT EXISTS _SchemaSmith_Partitions (
        RowId INT AUTO_INCREMENT NOT NULL PRIMARY KEY,
        TableName VARCHAR(128) NOT NULL,
        Ordinal INT NOT NULL,
        PartitionName VARCHAR(128) NOT NULL,
        PartitionValues TEXT DEFAULT NULL,
        KEY ix_pt_table_name (TableName, Ordinal)
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    SET v_PdOuterCnt = JSON_LENGTH(p_TableDefinitions);
    SET v_PdOuterIdx = 0;
    WHILE v_PdOuterIdx < v_PdOuterCnt DO
        SET v_PdInnerCnt = COALESCE(JSON_LENGTH(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Partitioning.Partitions'))), 0);
        SET v_PdInnerIdx = 0;
        WHILE v_PdInnerIdx < v_PdInnerCnt DO
            IF SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Partitioning.Partitions[', v_PdInnerIdx, '].Name'))) IS NOT NULL
               AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables st WHERE st.TableName = SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Name'))))) THEN
                INSERT INTO _SchemaSmith_Partitions (TableName, Ordinal, PartitionName, PartitionValues)
                SELECT
                    SchemaSmith_SafeBacktickWrap(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Name')))) AS TableName,
                    v_PdInnerIdx AS Ordinal,
                    SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Partitioning.Partitions[', v_PdInnerIdx, '].Name'))) AS PartitionName,
                    NULLIF(TRIM(COALESCE(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_TableDefinitions, CONCAT('$[', v_PdOuterIdx, '].Partitioning.Partitions[', v_PdInnerIdx, '].Values'))), '')), '') AS PartitionValues;
            END IF;
            SET v_PdInnerIdx = v_PdInnerIdx + 1;
        END WHILE;
        SET v_PdOuterIdx = v_PdOuterIdx + 1;
    END WHILE;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Evaluate ShouldApplyExpression');

    -- Evaluate ShouldApplyExpression using dynamic SQL (matching SQL Server/PostgreSQL pattern)
    -- Build UPDATE statements into a helper table, then PREPARE/EXECUTE each one
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ShouldApplyEval;
    CREATE TEMPORARY TABLE _SchemaSmith_ShouldApplyEval (
        EvalId INT AUTO_INCREMENT PRIMARY KEY,
        EvalSql TEXT NOT NULL
    ) ENGINE=InnoDB;

    -- Tables: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression)
    -- Scoped by RowId so each generated UPDATE targets exactly the source row whose expression
    -- evaluated false (no collateral damage to siblings sharing the natural-key name).
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_Tables SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_Tables
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- Columns: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression) (scoped by RowId)
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_Columns SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_Columns
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- Indexes: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression) (scoped by RowId)
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_Indexes SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_Indexes
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- ForeignKeys: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression) (scoped by RowId)
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_ForeignKeys SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_ForeignKeys
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- CheckConstraints: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression) (scoped by RowId)
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_CheckConstraints SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_CheckConstraints
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- FullTextIndexes: UPDATE ... SET ShouldApply = 0 WHERE NOT (expression) (scoped by RowId)
    -- ShouldApply / ShouldApplyExpression now live directly on _SchemaSmith_FullTextIndexes
    -- (the IndexOnlyQuench fallback schema was updated in lockstep), matching the pattern of
    -- the other parser temp tables. The earlier _SchemaSmith_FTShouldApply helper table has been
    -- retired -- it was a workaround for the missing columns and is no longer needed.
    INSERT INTO _SchemaSmith_ShouldApplyEval (EvalSql)
    SELECT CONCAT('UPDATE _SchemaSmith_FullTextIndexes SET ShouldApply = 0 WHERE RowId = ',
                  RowId,
                  ' AND NOT (', SchemaSmith_StripLeadingSelect(ShouldApplyExpression), ')')
    FROM _SchemaSmith_FullTextIndexes
    WHERE ShouldApplyExpression IS NOT NULL AND TRIM(ShouldApplyExpression) <> '';

    -- Execute each statement via PREPARE/EXECUTE loop
    SET @_ssa_eval_id = 0;
    ssa_eval_loop: LOOP
        SELECT EvalId, EvalSql INTO @_ssa_eval_id, @_ssa_eval_sql
        FROM _SchemaSmith_ShouldApplyEval WHERE EvalId > @_ssa_eval_id
        ORDER BY EvalId LIMIT 1;
        IF ROW_COUNT() = 0 THEN LEAVE ssa_eval_loop; END IF;
        PREPARE _ssa_stmt FROM @_ssa_eval_sql;
        EXECUTE _ssa_stmt;
        DEALLOCATE PREPARE _ssa_stmt;
    END LOOP;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ShouldApplyEval;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'ParseTableJson: Apply conditional filters');

    -- Filter out objects where ShouldApply = 0 (conditional deployment)
    -- Note: Table-level ShouldApply=0 removes the entire table and all its objects
    DELETE FROM _SchemaSmith_CheckConstraints WHERE ShouldApply = 0;
    DELETE FROM _SchemaSmith_ForeignKeys WHERE ShouldApply = 0;
    DELETE FROM _SchemaSmith_Indexes WHERE ShouldApply = 0;
    DELETE FROM _SchemaSmith_FullTextIndexes WHERE ShouldApply = 0;
    DELETE FROM _SchemaSmith_Columns WHERE ShouldApply = 0;

    -- Now that ShouldApply has filtered each temp table down to at most one row per natural
    -- key, promote the natural-key indexes to UNIQUE so MySQL's optimizer can infer functional
    -- dependency in downstream GROUP BY queries (ONLY_FULL_GROUP_BY mode is the default in 8.0+).
    -- These indexes existed as regular KEYs during the INSERT/UPDATE phases so two same-named
    -- rows with mutually exclusive ShouldApplyExpression could coexist until the DELETE pass.
    ALTER TABLE _SchemaSmith_Tables DROP KEY ix_tables_name, ADD UNIQUE KEY uq_tables_name (TableName);
    ALTER TABLE _SchemaSmith_Columns DROP KEY ix_columns_table_name, ADD UNIQUE KEY uq_columns_table_name (TableName, ColumnName);
    ALTER TABLE _SchemaSmith_Indexes DROP KEY ix_indexes_table_name, ADD UNIQUE KEY uq_indexes_table_name (TableName, IndexName);
    ALTER TABLE _SchemaSmith_ForeignKeys DROP KEY ix_fks_table_name, ADD UNIQUE KEY uq_fks_table_name (TableName, KeyName);
    ALTER TABLE _SchemaSmith_CheckConstraints DROP KEY ix_checks_table_name, ADD UNIQUE KEY uq_checks_table_name (TableName, ConstraintName);
    ALTER TABLE _SchemaSmith_FullTextIndexes DROP KEY ix_ft_table_name, ADD UNIQUE KEY uq_ft_table_name (TableName, IndexName);

    -- Delete objects belonging to tables that should not apply
    DELETE cc FROM _SchemaSmith_CheckConstraints cc
    INNER JOIN _SchemaSmith_Tables t ON cc.TableName = t.TableName
    WHERE t.ShouldApply = 0;

    DELETE fk FROM _SchemaSmith_ForeignKeys fk
    INNER JOIN _SchemaSmith_Tables t ON fk.TableName = t.TableName
    WHERE t.ShouldApply = 0;

    DELETE i FROM _SchemaSmith_Indexes i
    INNER JOIN _SchemaSmith_Tables t ON i.TableName = t.TableName
    WHERE t.ShouldApply = 0;

    DELETE ft FROM _SchemaSmith_FullTextIndexes ft
    INNER JOIN _SchemaSmith_Tables t ON ft.TableName = t.TableName
    WHERE t.ShouldApply = 0;

    DELETE c FROM _SchemaSmith_Columns c
    INNER JOIN _SchemaSmith_Tables t ON c.TableName = t.TableName
    WHERE t.ShouldApply = 0;

    DELETE FROM _SchemaSmith_Tables WHERE ShouldApply = 0;

END//

DELIMITER ;
