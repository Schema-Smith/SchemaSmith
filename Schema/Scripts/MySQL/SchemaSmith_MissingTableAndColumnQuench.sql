-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_MissingTableAndColumnQuench//

CREATE PROCEDURE SchemaSmith_MissingTableAndColumnQuench(
    IN p_DatabaseName VARCHAR(128),
    IN p_WhatIf TINYINT
)
SQL SECURITY DEFINER
BEGIN
    -- This procedure creates missing tables and adds missing columns.
    -- It reads from the _SchemaSmith_Tables and _SchemaSmith_Columns temp tables
    -- which are populated by the JSON parsing in SchemaSmith_ParseTableJson.
    --
    -- Column ordering:
    --   - Non-generated columns are ordered by OrdinalPosition
    --   - Generated columns are added after non-generated columns, ordered by
    --     DependencyLevel (to handle dependencies between generated columns)
    --     then by OrdinalPosition

    DECLARE v_Done INT DEFAULT FALSE;
    DECLARE v_Sql TEXT;
    DECLARE v_StatusTableName VARCHAR(128);
    DECLARE v_StatusVariant VARCHAR(128);

    -- Cursor for CREATE TABLE statements (non-generated columns only, ordered by OrdinalPosition).
    -- Still cursor-driven in create_tables_loop below: each new table is a distinct standalone
    -- CREATE TABLE statement (not an ALTER clause), so there is nothing to fold into a single
    -- multi-clause statement, and MySQL PREPARE only accepts one statement at a time.
    DECLARE cur_NewTables CURSOR FOR
        SELECT
            t.TableName,
            t.VariantName,
            CONCAT(
                'CREATE TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' (',
                GROUP_CONCAT(c.ColumnScript ORDER BY c.OrdinalPosition SEPARATOR ', '),
                COALESCE(t.AutoIncrementKeyClause, ''),
                COALESCE(
                    (SELECT CONCAT(', PRIMARY KEY (', i.IndexColumns, ')')
                     FROM _SchemaSmith_Indexes i
                     WHERE i.TableName = t.TableName AND i.IsPrimaryKey = 1),
                    ''
                ),
                -- Application-time periods (MariaDB 10.4.3+). Inside the CREATE rather than a follow-up
                -- ALTER because a period's columns must already exist when it is declared, and here they
                -- provably do. Suppressed below the threshold so the table still deploys -- the same
                -- degrade shape as a column SRID -- rather than failing the whole CREATE on a clause the
                -- engine cannot parse. MySQL never has rows here.
                COALESCE(
                    (SELECT GROUP_CONCAT(CONCAT(', PERIOD FOR ', pd.PeriodName, '(', pd.StartColumn, ', ', pd.EndColumn, ')')
                                         ORDER BY pd.PeriodName SEPARATOR '')
                     FROM _SchemaSmith_Periods pd
                     WHERE pd.TableName = t.TableName
                       AND SchemaSmith_SupportsApplicationTimePeriods() = 1),
                    ''
                ),
                ') ENGINE=', COALESCE(t.Engine, 'InnoDB'),
                CASE WHEN t.RowFormat IS NOT NULL AND t.RowFormat != ''
                     THEN CONCAT(' ROW_FORMAT=', t.RowFormat)
                     ELSE '' END,
                -- The CREATE_OPTIONS four. Engine-gated in SQL as well as by the domain's Platforms scoping,
                -- because a hand-authored package can still name a property its schema does not declare, and
                -- each of these is a hard syntax error on the other engine. Option names sit inside string
                -- literals, so nothing here resolves at CREATE PROCEDURE time.
                CASE WHEN t.Compression IS NOT NULL AND t.Compression != '' AND VERSION() NOT LIKE '%MariaDB%'
                     THEN CONCAT(' COMPRESSION=''', t.Compression, '''')
                     ELSE '' END,
                CASE WHEN t.KeyBlockSize IS NOT NULL
                     THEN CONCAT(' KEY_BLOCK_SIZE=', t.KeyBlockSize)
                     ELSE '' END,
                CASE WHEN t.PageCompressed = 1 AND VERSION() LIKE '%MariaDB%'
                     THEN ' PAGE_COMPRESSED=1'
                     ELSE '' END,
                CASE WHEN t.PageCompressed = 1 AND t.PageCompressionLevel IS NOT NULL AND VERSION() LIKE '%MariaDB%'
                     THEN CONCAT(' PAGE_COMPRESSION_LEVEL=', t.PageCompressionLevel)
                     ELSE '' END,
                -- At-rest encryption (F2a), same engine-gated shape as the compression pair above:
                -- MySQL's ENCRYPTION='Y'|'N' string vs MariaDB's ENCRYPTED=YES bool (+ ENCRYPTION_KEY_ID).
                CASE WHEN t.Encryption IS NOT NULL AND t.Encryption != '' AND VERSION() NOT LIKE '%MariaDB%'
                     THEN CONCAT(' ENCRYPTION=''', t.Encryption, '''')
                     ELSE '' END,
                CASE WHEN t.Encrypted = 1 AND VERSION() LIKE '%MariaDB%'
                     THEN ' ENCRYPTED=YES'
                     ELSE '' END,
                CASE WHEN t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL AND VERSION() LIKE '%MariaDB%'
                     THEN CONCAT(' ENCRYPTION_KEY_ID=', t.EncryptionKeyId)
                     ELSE '' END,
                CASE WHEN t.AutoIncrementValue IS NOT NULL
                     THEN CONCAT(' AUTO_INCREMENT=', t.AutoIncrementValue)
                     ELSE '' END,
                -- Escaping matches the established _SchemaSmith_FullTextIndexes.Comment form (double
                -- the embedded single quotes) -- see SchemaSmith_IndexOnlyQuench.sql.
                CASE WHEN t.Comment IS NOT NULL AND t.Comment != ''
                     THEN CONCAT(' COMMENT=''', REPLACE(t.Comment, '''', ''''''), '''')
                     ELSE '' END,
                -- General tablespace placement (F2b), MySQL only -- like the CREATE_OPTIONS four above,
                -- engine-gated in SQL as well as by the domain's Platforms scoping, because a
                -- hand-authored package can still name this on a MariaDB target and MariaDB has no general
                -- tablespaces at all. UNQUOTED (`TABLESPACE name`, not `TABLESPACE='name'`) -- MySQL's own
                -- grammar for this clause, unlike the KEY=VALUE CREATE_OPTIONS above. Applied only here, on
                -- CREATE: an existing table whose declared value disagrees with what is deployed is
                -- refused by ModifiedTableQuench (STEP -0.4), never converged -- a move is a full data-file
                -- relocation, the same posture partitioning and system versioning's DROP direction take.
                CASE WHEN t.Tablespace IS NOT NULL AND t.Tablespace != '' AND VERSION() NOT LIKE '%MariaDB%'
                     THEN CONCAT(' TABLESPACE ', t.Tablespace)
                     ELSE '' END,
                -- InnoDB DATA DIRECTORY placement (F2c), BOTH engines -- unlike Tablespace above, no
                -- VERSION() guard: both MySQL and MariaDB support this clause. Applied only here, on
                -- CREATE: an existing table whose declared value disagrees with what is deployed is
                -- refused by ModifiedTableQuench, never converged -- a move is a full data-file relocation,
                -- the same posture Tablespace above takes. MySQL requires the directory to already be
                -- listed in the server's innodb_directories or CREATE fails with its own ERROR 3121 -- that
                -- is user server configuration, like a missing filegroup, not something to gate here.
                CASE WHEN t.DataDirectory IS NOT NULL AND t.DataDirectory != ''
                     THEN CONCAT(' DATA DIRECTORY=''', REPLACE(t.DataDirectory, '''', ''''''), '''')
                     ELSE '' END,
                -- Partitioning (#partitioning, K3). LAST in the statement, which is where MySQL's own
                -- CREATE TABLE grammar puts it -- after every table option.
                --
                -- Inside the CREATE, never as a follow-up ALTER: ALTER TABLE ... PARTITION BY rewrites
                -- every row, so it is emitted only here, where the table is empty by construction. On an
                -- already-deployed table a mismatch is REFUSED in ModifiedTableQuench instead.
                --
                -- RANGE and LIST name each partition with its boundary and the ORDER matters (RANGE
                -- boundaries must ascend, and the engine rejects a definition where they do not), so the
                -- list is aggregated by Ordinal. HASH and KEY carry a count instead and no list at all.
                CASE WHEN t.PartitionMethod IS NULL THEN ''
                     WHEN t.PartitionMethod IN ('HASH', 'KEY')
                     THEN CONCAT(' PARTITION BY ', t.PartitionMethod, ' (', t.PartitionExpression, ')',
                                 CASE WHEN t.PartitionCount IS NOT NULL THEN CONCAT(' PARTITIONS ', t.PartitionCount) ELSE '' END)
                     ELSE CONCAT(' PARTITION BY ', t.PartitionMethod, ' (', t.PartitionExpression, ') (',
                                 COALESCE((SELECT GROUP_CONCAT(CONCAT('PARTITION ', pt.PartitionName,
                                                                      CASE WHEN t.PartitionMethod LIKE 'LIST%'
                                                                           THEN CONCAT(' VALUES IN (', pt.PartitionValues, ')')
                                                                           ELSE CONCAT(' VALUES LESS THAN (', pt.PartitionValues, ')') END)
                                                               ORDER BY pt.Ordinal SEPARATOR ', ')
                                             FROM _SchemaSmith_Partitions pt
                                            WHERE pt.TableName = t.TableName), ''),
                                 ')')
                     END,
                -- System versioning (MariaDB WITH SYSTEM VERSIONING, F1S1). A trailing table SUFFIX
                -- keyword, not a KEY=VALUE option, so it goes last -- after PARTITION BY, which is where
                -- MariaDB's own grammar places it. Gated on the EXISTING SchemaSmith_SupportsSystemVersioning()
                -- (MariaDB 10.3+; MySQL never): below the floor the clause is suppressed here so the table
                -- still deploys as an ordinary table -- see the degrade guard below, which reports the loss.
                CASE WHEN t.IsSystemVersioned = 1 AND SchemaSmith_SupportsSystemVersioning() = 1
                     THEN ' WITH SYSTEM VERSIONING' ELSE '' END
            ) AS CreateTableStatement
        FROM _SchemaSmith_Tables t
        INNER JOIN _SchemaSmith_Columns c ON c.TableName = t.TableName
        WHERE t.NewTable = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
        GROUP BY t.TableName, t.VariantName, t.Engine, t.RowFormat, t.Compression, t.KeyBlockSize,
                 t.PageCompressed, t.PageCompressionLevel, t.Encryption, t.Encrypted, t.EncryptionKeyId,
                 t.AutoIncrementValue, t.Comment, t.Tablespace, t.DataDirectory,
                 t.PartitionMethod, t.PartitionExpression, t.PartitionCount;

    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_Done = TRUE;

    SET SESSION group_concat_max_len = 1000000;

    INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'BEGIN MissingTableAndColumnQuench');

    -- A CustomTableRestore hook restores tables being added in case they were custom-dropped
    -- (recycled) previously; mirrors the SQL Server / PostgreSQL hook.
    SET @has_custom_restore = (SELECT COUNT(*) FROM INFORMATION_SCHEMA.ROUTINES
                               WHERE CONVERT(ROUTINE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                                 AND ROUTINE_NAME = 'SchemaSmith_CustomTableRestore'
                                 AND ROUTINE_TYPE = 'PROCEDURE');

    -- =========================================================================
    -- Degrade column DEFAULT expressions below MySQL 8.0.13 (extraction already recognises this stored
    -- form -- COLUMN_DEFAULT LIKE '(%' -- see GenerateTableJson). MariaDB has supported expression
    -- defaults since 10.2.1 (MDEV-10134), at/below our 10.2 floor, so SchemaSmith_SupportsDefaultExpression()
    -- never gates MariaDB -- this branch is MySQL-only. Below the threshold DEFAULT (<expr>) is a hard
    -- syntax error (unlike a parse-and-ignored clause), so there is no safe partial emit: 'fail' aborts
    -- naming the offending column(s); 'warn' (default) skips the column entirely -- the CREATE TABLE and
    -- ADD COLUMN emit sites below exclude it via the identical predicate -- and records a 'downgraded'
    -- manifest row per column so the run stays idempotent and visible.
    -- =========================================================================
    IF SchemaSmith_SupportsDefaultExpression() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE (t.NewTable = 1 OR c.NewColumn = 1)
                     AND c.IsAutoIncrement = 0
                     AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
                     AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%') THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column DEFAULT expression unsupported (requires MySQL 8.0.13): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
            SET @ss_msg = CONCAT('Column DEFAULT expressions require MySQL 8.0.13 (detected ',
                                 SchemaSmith_ServerVersionNum(), '); see the deploy log for the unsupported column(s).');
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Skipping column (DEFAULT expression requires MySQL 8.0.13 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (DEFAULT expression, MySQL 8.0.13)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsAutoIncrement = 0
              AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
              AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%';
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade invisible columns below MySQL 8.0.23 / MariaDB 10.3 (mirrors the invisible-index guard in
    -- MissingIndexesAndConstraintsQuench, one level down). Below the threshold the INVISIBLE keyword is a
    -- hard syntax error, so ColumnScript (built in ParseTableJson) never emits it there -- the column is
    -- created visible instead, which is the safe degrade; the CREATE TABLE and ADD COLUMN emit sites below
    -- need no exclusion for it. This block only adds the user-facing report: 'fail' aborts naming the
    -- offending column(s); 'warn' (default) records a 'downgraded' manifest row per column so a
    -- silently-visible column stays discoverable.
    -- =========================================================================
    IF SchemaSmith_SupportsInvisibleColumn() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE (t.NewTable = 1 OR c.NewColumn = 1)
                     AND c.IsInvisible = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible column requires MySQL 8.0.23 / MariaDB 10.3 (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsInvisible = 1;
            SET @ss_msg = 'Invisible column requires MySQL 8.0.23 / MariaDB 10.3 (UnsupportedFeaturePolicy=fail). See the run log for the full list.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Invisible column stored visible (requires MySQL 8.0.23 / MariaDB 10.3 - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsInvisible = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (invisible, MySQL 8.0.23 / MariaDB 10.3)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsInvisible = 1;
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade column SRID restriction below MySQL 8.0.3 (mirrors the invisible-column guard directly
    -- above). MariaDB has no equivalent attribute at any version, so SchemaSmith_SupportsColumnSrid()
    -- is 0 there unconditionally, not a floor it ever crosses -- this block fires for MariaDB the same
    -- way it fires for a genuinely old MySQL. Below the threshold the SRID clause is a hard syntax
    -- error, so ColumnScript (built in ParseTableJson) never emits it there -- the column is created
    -- unrestricted instead, which is the safe degrade; the CREATE TABLE and ADD COLUMN emit sites below
    -- need no exclusion for it. This block only adds the user-facing report: 'fail' aborts naming the
    -- offending column(s); 'warn' (default) records a 'downgraded' manifest row per column so a
    -- silently-unrestricted spatial column stays discoverable.
    -- =========================================================================
    IF SchemaSmith_SupportsColumnSrid() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE (t.NewTable = 1 OR c.NewColumn = 1)
                     AND c.Srid IS NOT NULL) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column SRID requires MySQL 8.0.3 (MariaDB unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.Srid IS NOT NULL;
            SET @ss_msg = 'Column SRID requires MySQL 8.0.3 (MariaDB unsupported) (UnsupportedFeaturePolicy=fail). See the run log for the full list.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column SRID stored unrestricted (requires MySQL 8.0.3, MariaDB unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.Srid IS NOT NULL;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column (SRID, MySQL 8.0.3)',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.Srid IS NOT NULL;
        END IF;
    END IF;

    -- =========================================================================
    -- APPLICATION-TIME PERIOD BELOW THE ENGINE THRESHOLD
    -- =========================================================================
    -- A declared PERIOD FOR needs MariaDB 10.4.3, and MySQL has no equivalent at any version. Below the
    -- threshold the clause is suppressed at CREATE-build time so the table still deploys -- what the user
    -- loses is the period, not the table, which is why the registry records this as Reduced rather than
    -- Skipped.
    --
    -- Suppressing it SILENTLY would be the failure this whole guard exists to prevent: the table would
    -- come out looking correct and quietly missing a declared part of its schema. 'fail' refuses the
    -- deploy naming the periods; 'warn' (default) records a 'downgraded' manifest row per period so the
    -- loss stays discoverable afterwards.
    IF SchemaSmith_SupportsApplicationTimePeriods() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Periods pd
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
                   WHERE t.NewTable = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Application-time period requires MariaDB 10.4.3 (MySQL unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(pd.TableName), '.', SchemaSmith_StripBacktickWrapping(pd.PeriodName))
            FROM _SchemaSmith_Periods pd
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
            WHERE t.NewTable = 1;
            SET @ss_msg = 'Application-time period needs MariaDB 10.4.3 (UnsupportedFeaturePolicy=fail). See the run log.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Application-time period not created (requires MariaDB 10.4.3, MySQL unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(pd.TableName), '.', SchemaSmith_StripBacktickWrapping(pd.PeriodName))
            FROM _SchemaSmith_Periods pd
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
            WHERE t.NewTable = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'table without its PERIOD FOR clause',
                   CONCAT(SchemaSmith_StripBacktickWrapping(pd.TableName), '.', SchemaSmith_StripBacktickWrapping(pd.PeriodName)), 'downgraded'
            FROM _SchemaSmith_Periods pd
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = pd.TableName
            WHERE t.NewTable = 1;
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade per-column history exclusion below MariaDB 10.3.4 / on MySQL at any version (#408).
    -- Mirrors the application-time-period guard directly above, and for the same reason: ParseTableJson
    -- suppresses the WITHOUT SYSTEM VERSIONING clause when the gate is 0 (below the floor the keyword is
    -- a hard syntax error), so the column is created WITHOUT the exclusion -- meaning it silently starts
    -- keeping history the package said not to keep. Suppressing that silently is the failure this guard
    -- exists to prevent. Reduced, not Skipped: the column is still created, only its exclusion is lost.
    -- =========================================================================
    IF SchemaSmith_SupportsSystemVersioning() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                   INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
                   WHERE (t.NewTable = 1 OR c.NewColumn = 1)
                     AND c.IsWithoutSystemVersioning = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Per-column history exclusion requires MariaDB 10.3.4 (MySQL unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsWithoutSystemVersioning = 1;
            SET @ss_msg = 'Per-column history exclusion needs MariaDB 10.3.4 (UnsupportedFeaturePolicy=fail). See the run log.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Column history exclusion not applied (requires MariaDB 10.3.4, MySQL unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName))
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsWithoutSystemVersioning = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'column without its WITHOUT SYSTEM VERSIONING clause',
                   CONCAT(SchemaSmith_StripBacktickWrapping(c.TableName), '.', SchemaSmith_StripBacktickWrapping(c.ColumnName)), 'downgraded'
            FROM _SchemaSmith_Columns c
            INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
            WHERE (t.NewTable = 1 OR c.NewColumn = 1)
              AND c.IsWithoutSystemVersioning = 1;
        END IF;
    END IF;

    -- =========================================================================
    -- Degrade table-level system versioning below MariaDB 10.3 / on MySQL at any version (F1S1). Mirrors
    -- the per-column history exclusion guard directly above, and for the same reason: the CREATE TABLE
    -- CONCAT above suppresses the trailing WITH SYSTEM VERSIONING clause when the gate is 0, so a new
    -- table declaring IsSystemVersioned would otherwise deploy as an ORDINARY table -- silently losing
    -- the versioning the package asked for. Suppressing that silently is the failure this guard exists
    -- to prevent.
    --
    -- Skip, not Reduced: the WHOLE table's versioning is dropped here, not merely a part of it (unlike
    -- the column-level exclusion above, which loses only the exclusion while the column itself survives).
    --
    -- Scope: t.NewTable = 1 only. Converging an EXISTING table's versioning (ALTER ADD/DROP SYSTEM
    -- VERSIONING) is a separate later task, not built here.
    -- =========================================================================
    IF SchemaSmith_SupportsSystemVersioning() = 0
       AND EXISTS (SELECT 1 FROM _SchemaSmith_Tables t
                   WHERE t.NewTable = 1
                     AND t.IsSystemVersioned = 1) THEN
        IF SchemaSmith_UnsupportedFeaturePolicy() = 'fail' THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  System versioning requires MariaDB 10.3 (MySQL unsupported) (UnsupportedFeaturePolicy=fail): ',
                   SchemaSmith_StripBacktickWrapping(t.TableName))
            FROM _SchemaSmith_Tables t
            WHERE t.NewTable = 1
              AND t.IsSystemVersioned = 1;
            SET @ss_msg = 'System versioning needs MariaDB 10.3 (UnsupportedFeaturePolicy=fail). See the run log.';
            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = @ss_msg;
        ELSE
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('  Table deployed without system versioning (requires MariaDB 10.3, MySQL unsupported - downgraded): ',
                   SchemaSmith_StripBacktickWrapping(t.TableName))
            FROM _SchemaSmith_Tables t
            WHERE t.NewTable = 1
              AND t.IsSystemVersioned = 1;
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'table without its WITH SYSTEM VERSIONING clause',
                   SchemaSmith_StripBacktickWrapping(t.TableName), 'downgraded'
            FROM _SchemaSmith_Tables t
            WHERE t.NewTable = 1
              AND t.IsSystemVersioned = 1;
        END IF;
    END IF;


    IF p_WhatIf = 1 THEN
        -- WhatIf mode: output the actual SQL that would be executed

        -- Declarative renames would run here (before add-columns). Log the statements only.
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('RENAME TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`',
                      SchemaSmith_StripBacktickWrapping(t.OldName), '` TO `',
                      CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_StripBacktickWrapping(t.TableName), '`')
        FROM _SchemaSmith_Tables t
        WHERE t.OldName IS NOT NULL
          AND t.NewTable = 0
          AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.OldName))
          AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName));

        -- Descriptive (version-agnostic) preview: the executed DDL is RENAME COLUMN or, below MySQL 8.0 /
        -- MariaDB 10.5.2, CHANGE COLUMN preserving the current definition (see the real-path block below).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Rename column `', SchemaSmith_StripBacktickWrapping(c.OldName),
                      '` to `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '` on `', c.TableName, '`')
        FROM _SchemaSmith_Columns c
        WHERE c.OldName IS NOT NULL
          AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS isc WHERE BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName) AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.OldName))
          AND NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS isc WHERE BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName) AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName));

        IF @has_custom_restore = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Attempt custom table restore for tables being added');
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
            SELECT CONNECTION_ID(), CONCAT('CALL SchemaSmith_CustomTableRestore(''', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, ''', ''', SchemaSmith_StripBacktickWrapping(t.TableName), ''')')
            FROM _SchemaSmith_Tables t
            WHERE t.NewTable = 1;
        END IF;

        -- Step 1: Show CREATE TABLE statements (set-based; same CONCAT shape as cur_NewTables).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing tables');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT(
                      'CREATE TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' (',
                      GROUP_CONCAT(c.ColumnScript ORDER BY c.OrdinalPosition SEPARATOR ', '),
                      COALESCE(t.AutoIncrementKeyClause, ''),
                      COALESCE(
                          (SELECT CONCAT(', PRIMARY KEY (', i.IndexColumns, ')')
                           FROM _SchemaSmith_Indexes i
                           WHERE i.TableName = t.TableName AND i.IsPrimaryKey = 1),
                          ''
                      ),
                      ') ENGINE=', COALESCE(t.Engine, 'InnoDB'),
                      CASE WHEN t.RowFormat IS NOT NULL AND t.RowFormat != ''
                           THEN CONCAT(' ROW_FORMAT=', t.RowFormat)
                           ELSE '' END,
                      -- The CREATE_OPTIONS four. Engine-gated in SQL as well as by the domain's Platforms scoping,
                      -- because a hand-authored package can still name a property its schema does not declare, and
                      -- each of these is a hard syntax error on the other engine. Option names sit inside string
                      -- literals, so nothing here resolves at CREATE PROCEDURE time.
                      CASE WHEN t.Compression IS NOT NULL AND t.Compression != '' AND VERSION() NOT LIKE '%MariaDB%'
                           THEN CONCAT(' COMPRESSION=''', t.Compression, '''')
                           ELSE '' END,
                      CASE WHEN t.KeyBlockSize IS NOT NULL
                           THEN CONCAT(' KEY_BLOCK_SIZE=', t.KeyBlockSize)
                           ELSE '' END,
                      CASE WHEN t.PageCompressed = 1 AND VERSION() LIKE '%MariaDB%'
                           THEN ' PAGE_COMPRESSED=1'
                           ELSE '' END,
                      CASE WHEN t.PageCompressed = 1 AND t.PageCompressionLevel IS NOT NULL AND VERSION() LIKE '%MariaDB%'
                           THEN CONCAT(' PAGE_COMPRESSION_LEVEL=', t.PageCompressionLevel)
                           ELSE '' END,
                      -- At-rest encryption (F2a) -- must match the real-path cur_NewTables CONCAT above exactly.
                      CASE WHEN t.Encryption IS NOT NULL AND t.Encryption != '' AND VERSION() NOT LIKE '%MariaDB%'
                           THEN CONCAT(' ENCRYPTION=''', t.Encryption, '''')
                           ELSE '' END,
                      CASE WHEN t.Encrypted = 1 AND VERSION() LIKE '%MariaDB%'
                           THEN ' ENCRYPTED=YES'
                           ELSE '' END,
                      CASE WHEN t.Encrypted = 1 AND t.EncryptionKeyId IS NOT NULL AND VERSION() LIKE '%MariaDB%'
                           THEN CONCAT(' ENCRYPTION_KEY_ID=', t.EncryptionKeyId)
                           ELSE '' END,
                      CASE WHEN t.AutoIncrementValue IS NOT NULL
                           THEN CONCAT(' AUTO_INCREMENT=', t.AutoIncrementValue)
                           ELSE '' END,
                      CASE WHEN t.Comment IS NOT NULL AND t.Comment != ''
                           THEN CONCAT(' COMMENT=''', REPLACE(t.Comment, '''', ''''''), '''')
                           ELSE '' END,
                      -- General tablespace placement (F2b) -- must match the real-path cur_NewTables CONCAT above exactly.
                      CASE WHEN t.Tablespace IS NOT NULL AND t.Tablespace != '' AND VERSION() NOT LIKE '%MariaDB%'
                           THEN CONCAT(' TABLESPACE ', t.Tablespace)
                           ELSE '' END,
                      -- DATA DIRECTORY placement (F2c) -- must match the real-path cur_NewTables CONCAT above exactly.
                      CASE WHEN t.DataDirectory IS NOT NULL AND t.DataDirectory != ''
                           THEN CONCAT(' DATA DIRECTORY=''', REPLACE(t.DataDirectory, '''', ''''''), '''')
                           ELSE '' END,
                      -- Partitioning (#partitioning, K3) -- must match the real-path emit above exactly,
                      -- or the WhatIf preview shows a statement the live run would not issue.
                      CASE WHEN t.PartitionMethod IS NULL THEN ''
                           WHEN t.PartitionMethod IN ('HASH', 'KEY')
                           THEN CONCAT(' PARTITION BY ', t.PartitionMethod, ' (', t.PartitionExpression, ')',
                                       CASE WHEN t.PartitionCount IS NOT NULL THEN CONCAT(' PARTITIONS ', t.PartitionCount) ELSE '' END)
                           ELSE CONCAT(' PARTITION BY ', t.PartitionMethod, ' (', t.PartitionExpression, ') (',
                                       COALESCE((SELECT GROUP_CONCAT(CONCAT('PARTITION ', pt.PartitionName,
                                                                            CASE WHEN t.PartitionMethod LIKE 'LIST%'
                                                                                 THEN CONCAT(' VALUES IN (', pt.PartitionValues, ')')
                                                                                 ELSE CONCAT(' VALUES LESS THAN (', pt.PartitionValues, ')') END)
                                                                     ORDER BY pt.Ordinal SEPARATOR ', ')
                                                   FROM _SchemaSmith_Partitions pt
                                                  WHERE pt.TableName = t.TableName), ''),
                                       ')')
                           END,
                      -- Must match the real-path emit above exactly, or the WhatIf preview shows a
                      -- statement the live run would not issue.
                      CASE WHEN t.IsSystemVersioned = 1 AND SchemaSmith_SupportsSystemVersioning() = 1
                           THEN ' WITH SYSTEM VERSIONING' ELSE '' END)
        FROM _SchemaSmith_Tables t
        INNER JOIN _SchemaSmith_Columns c ON c.TableName = t.TableName
        WHERE t.NewTable = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
        GROUP BY t.TableName, t.VariantName, t.Engine, t.RowFormat, t.Compression, t.KeyBlockSize,
                 t.PageCompressed, t.PageCompressionLevel, t.Encryption, t.Encrypted, t.EncryptionKeyId,
                 t.AutoIncrementValue, t.Comment, t.Tablespace, t.DataDirectory,
                 t.PartitionMethod, t.PartitionExpression, t.PartitionCount;

        -- Step 2: Show ALTER TABLE ADD COLUMN for new columns on existing tables (set-based;
        -- one row per column, matching the per-column statement the ELSE branch would issue
        -- one-at-a-time before it folds them into a single ALTER per table).
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add missing columns to existing tables');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                      ' ADD COLUMN ',
                      -- WhatIf preview must match the real run: strip WITHOUT SYSTEM VERSIONING on a
                      -- not-yet-versioned table exactly as the ELSE branch's exec does (MariaDB 4124).
                      CASE WHEN ist.TABLE_TYPE = 'SYSTEM VERSIONED' THEN c.ColumnScript
                           ELSE REPLACE(c.ColumnScript, ' WITHOUT SYSTEM VERSIONING', '') END)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        LEFT JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
        ORDER BY c.TableName, c.OrdinalPosition;

        -- #363: WhatIf twins of the ELSE-branch 'table'/'created' (cursor loop) and 'column'/'created'
        -- audits. Set-based over the same temp-table sources; the new-table's own columns are covered
        -- by the table row (NewTable = 0 only for the column twin), matching the real audits.
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'table', t.TableName, 'wouldCreate'
        FROM _SchemaSmith_Tables t
        WHERE t.NewTable = 1;

        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(c.TableName, '.', c.ColumnName), 'wouldCreate'
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        WHERE t.NewTable = 0
          AND c.NewColumn = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0);

    ELSE
        -- =======================
        -- DECLARATIVE RENAMES (OldName) — run BEFORE create/add-columns
        -- =======================
        -- Table + column renames execute here, ahead of add-columns, so a carried column (unchanged
        -- across the rename) or a newly-added column targets the post-rename table name — parity with
        -- SQL Server / PostgreSQL, which rename before adding columns. ProductOwnership is reconciled
        -- old->new later by ModifiedTableQuench (which has the product name). BINARY comparisons avoid
        -- collation clashes between INFORMATION_SCHEMA (utf8mb3), SchemaSmith functions
        -- (utf8mb4_unicode_ci), and connection params (utf8mb4_0900_ai_ci).

        -- Table renames: fold all old->new pairs into one multi-target RENAME TABLE, then drain.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TableRenameStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_TableRenameStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_TableRenameStmts (Stmt)
        SELECT CONCAT('RENAME TABLE ',
                      GROUP_CONCAT(
                          CONCAT('`', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_StripBacktickWrapping(t.OldName), '` TO `',
                                 CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.`', SchemaSmith_StripBacktickWrapping(t.TableName), '`')
                          ORDER BY t.TableName SEPARATOR ', '))
        FROM _SchemaSmith_Tables t
        WHERE t.OldName IS NOT NULL
          AND t.NewTable = 0
          AND EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist
              WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.OldName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist
              WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
          )
        HAVING COUNT(*) > 0;

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Rename table `', SchemaSmith_StripBacktickWrapping(t.OldName), '` to `', SchemaSmith_StripBacktickWrapping(t.TableName), '`')
        FROM _SchemaSmith_Tables t
        WHERE t.OldName IS NOT NULL
          AND t.NewTable = 0
          AND EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist
              WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.OldName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist
              WHERE BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
          );

        SET @v_tablerename_id := (SELECT MIN(RowId) FROM _SchemaSmith_TableRenameStmts);
        WHILE @v_tablerename_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_TableRenameStmts WHERE RowId = @v_tablerename_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_tablerename_id := (SELECT MIN(RowId) FROM _SchemaSmith_TableRenameStmts WHERE RowId > @v_tablerename_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_TableRenameStmts;

        -- Column renames: fold each table's column renames into one multi-clause ALTER, then drain.
        -- Catalog-based predicate (old column present, new column absent) so a column rename that
        -- rides a table rename also fires — its parse-time NewColumn flag is unreliable because the
        -- new table name did not exist when the flag was computed.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColRenameStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_ColRenameStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        -- `ALTER TABLE ... RENAME COLUMN old TO new` needs MySQL 8.0 / MariaDB 10.5.2+. Below that
        -- (MySQL 5.7 / MariaDB 10.2-10.5) reproduce the rename with `CHANGE COLUMN old new <def>`,
        -- reconstructing the OLD column's current definition from INFORMATION_SCHEMA (type / charset /
        -- collate / nullability / generated / auto_increment / comment). DEFAULT is intentionally OMITTED:
        -- the subsequent ModifiedTableQuench pass reconciles the column to its desired default in the same
        -- deploy, which sidesteps the cross-engine COLUMN_DEFAULT-quoting divergence (MySQL 5.7 returns
        -- string defaults unquoted, MariaDB/8.0 quoted) and keeps the rename data-preserving. INVISIBLE
        -- (MySQL 8.0.23 / MariaDB 10.3) is omitted for the same reason and reconciled the same way -- see
        -- InvisibleColumnGatingTests.RenameOfInvisibleColumnNullableNoDefault_InRenameColumnFallbackBand_VisibilityPredicateAloneRestoresInvisible,
        -- which isolates that predicate and reddens if it's removed. ON UPDATE CURRENT_TIMESTAMP[(n)] is
        -- omitted here too, for the same reason: it is unconditionally dropped by this CHANGE COLUMN and
        -- restored by ModifiedTableQuench's ON UPDATE compare against the new column name in the same deploy.
        INSERT INTO _SchemaSmith_ColRenameStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName, ' ',
                      GROUP_CONCAT(
                          IF(SchemaSmith_SupportsRenameColumn() = 1,
                             CONCAT('RENAME COLUMN `', SchemaSmith_StripBacktickWrapping(c.OldName),
                                    '` TO `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '`'),
                             CONCAT('CHANGE COLUMN `', SchemaSmith_StripBacktickWrapping(c.OldName),
                                    '` `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '` ',
                                    CONVERT(isc.COLUMN_TYPE USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                    IF(isc.CHARACTER_SET_NAME IS NOT NULL,
                                       CONCAT(' CHARACTER SET ', CONVERT(isc.CHARACTER_SET_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci,
                                              ' COLLATE ', CONVERT(isc.COLLATION_NAME USING utf8mb4) COLLATE utf8mb4_unicode_ci), ''),
                                    IF(CONVERT(isc.EXTRA USING utf8mb4) LIKE '%GENERATED%',
                                       CONCAT(' GENERATED ALWAYS AS (', CONVERT(isc.GENERATION_EXPRESSION USING utf8mb4) COLLATE utf8mb4_unicode_ci, ') ',
                                              IF(CONVERT(isc.EXTRA USING utf8mb4) LIKE '%STORED%', 'STORED', 'VIRTUAL')), ''),
                                    IF(CONVERT(isc.IS_NULLABLE USING utf8mb4) = 'NO', ' NOT NULL', ' NULL'),
                                    IF(CONVERT(isc.EXTRA USING utf8mb4) LIKE '%auto_increment%', ' AUTO_INCREMENT', ''),
                                    IF(COALESCE(CONVERT(isc.COLUMN_COMMENT USING utf8mb4), '') <> '',
                                       CONCAT(' COMMENT ', QUOTE(CONVERT(isc.COLUMN_COMMENT USING utf8mb4) COLLATE utf8mb4_unicode_ci)), '')))
                          ORDER BY c.ColumnName SEPARATOR ', '))
        FROM _SchemaSmith_Columns c
        JOIN INFORMATION_SCHEMA.COLUMNS isc
            ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
           AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
           AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.OldName)
        WHERE c.OldName IS NOT NULL
          AND NOT EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS isc2
              WHERE BINARY isc2.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY isc2.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                AND BINARY isc2.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
          )
        GROUP BY c.TableName;

        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Rename column `', SchemaSmith_StripBacktickWrapping(c.OldName),
                      '` to `', SchemaSmith_StripBacktickWrapping(c.ColumnName), '` on `', c.TableName, '`')
        FROM _SchemaSmith_Columns c
        WHERE c.OldName IS NOT NULL
          AND EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS isc
              WHERE BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.OldName)
          )
          AND NOT EXISTS (
              SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS isc
              WHERE BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
                AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
                AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
          );

        SET @v_colrename_id := (SELECT MIN(RowId) FROM _SchemaSmith_ColRenameStmts);
        WHILE @v_colrename_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_ColRenameStmts WHERE RowId = @v_colrename_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_colrename_id := (SELECT MIN(RowId) FROM _SchemaSmith_ColRenameStmts WHERE RowId > @v_colrename_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ColRenameStmts;

        -- After renames, clear NewColumn for any column now present under its (possibly renamed)
        -- table: parse flagged carried/renamed columns as new because it checked under the
        -- post-rename table name, which did not exist yet. Without this, add-columns below would try
        -- to re-add an existing column (duplicate-column error). Mirrors the CustomTableRestore fixup.
        UPDATE _SchemaSmith_Columns c
        SET c.NewColumn = 0
        WHERE c.NewColumn = 1
          AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS ic
                      WHERE CONVERT(ic.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                        AND CONVERT(ic.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.TableName) USING utf8mb4)
                        AND CONVERT(ic.COLUMN_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.ColumnName) USING utf8mb4));

        -- CustomTableRestore hook: attempt to restore tables being added in case they were
        -- custom-dropped (recycled) previously, then mark any that now exist as not-new so the
        -- create step below does not recreate them empty (preserving restored data).
        IF @has_custom_restore = 1 THEN
            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Attempt custom table restore for tables being added');

            -- Each restore is a standalone CALL statement, not an ALTER clause that can fold into
            -- another table's statement, so materialize one CALL per table and drain via WHILE
            -- instead of the per-row cursor this replaces.
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RestoreStmts;
            CREATE TEMPORARY TABLE _SchemaSmith_RestoreStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
            INSERT INTO _SchemaSmith_RestoreStmts (Stmt)
            SELECT CONCAT('CALL SchemaSmith_CustomTableRestore(''', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, ''', ''', SchemaSmith_StripBacktickWrapping(t.TableName), ''')')
            FROM _SchemaSmith_Tables t
            WHERE t.NewTable = 1;

            SET @v_restore_id := (SELECT MIN(RowId) FROM _SchemaSmith_RestoreStmts);
            WHILE @v_restore_id IS NOT NULL DO
                SELECT Stmt INTO @exec_sql FROM _SchemaSmith_RestoreStmts WHERE RowId = @v_restore_id;
                PREPARE stmt FROM @exec_sql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
                SET @v_restore_id := (SELECT MIN(RowId) FROM _SchemaSmith_RestoreStmts WHERE RowId > @v_restore_id);
            END WHILE;
            DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_RestoreStmts;

            UPDATE _SchemaSmith_Tables t
            SET t.NewTable = 0
            WHERE t.NewTable = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES ist
                          WHERE CONVERT(ist.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                            AND CONVERT(ist.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(t.TableName) USING utf8mb4));

            -- NewColumn was set at parse time, before the restore brought the table back, so the
            -- restored table's columns are still flagged as new. Clear the flag for any column that
            -- now exists so the add-columns step does not try to re-add it (duplicate column error).
            UPDATE _SchemaSmith_Columns c
            SET c.NewColumn = 0
            WHERE c.NewColumn = 1
              AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS ic
                          WHERE CONVERT(ic.TABLE_SCHEMA USING utf8mb4) = CONVERT(p_DatabaseName USING utf8mb4)
                            AND CONVERT(ic.TABLE_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.TableName) USING utf8mb4)
                            AND CONVERT(ic.COLUMN_NAME USING utf8mb4) = CONVERT(SchemaSmith_StripBacktickWrapping(c.ColumnName) USING utf8mb4));
        END IF;

        -- Step 1: Create new tables (with non-generated columns only)
        -- INTRINSIC — left as a cursor. Each new table is a distinct standalone CREATE TABLE
        -- statement (not an ALTER clause), so there is nothing to fold into a single multi-clause
        -- statement, and MySQL PREPARE only accepts one statement at a time.
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Create missing tables');
        SET v_Done = FALSE;
        OPEN cur_NewTables;

        create_tables_loop: LOOP
            FETCH cur_NewTables INTO v_StatusTableName, v_StatusVariant, v_Sql;
            IF v_Done THEN
                LEAVE create_tables_loop;
            END IF;

            INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), CONCAT('  Create table ', v_StatusTableName,
                CASE WHEN COALESCE(v_StatusVariant, '') <> '' THEN CONCAT(' (variant: ', v_StatusVariant, ')') ELSE '' END));
            SET @exec_sql = v_Sql;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            -- Object-change audit (#243 E5): after EXECUTE, before DEALLOCATE (crash-safe #337 point).
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) VALUES (CONNECTION_ID(), 'table', v_StatusTableName, 'created');
            DEALLOCATE PREPARE stmt;
        END LOOP;

        CLOSE cur_NewTables;

        -- Step 2: Add non-generated columns to existing tables
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (CONNECTION_ID(), 'Add missing columns to existing tables');
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Add column: ',
                      CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName,
                             ' ADD COLUMN ',
                             -- Match the exec below: strip WITHOUT SYSTEM VERSIONING on a not-yet-versioned table.
                             CASE WHEN ist.TABLE_TYPE = 'SYSTEM VERSIONED' THEN c.ColumnScript
                                  ELSE REPLACE(c.ColumnScript, ' WITHOUT SYSTEM VERSIONING', '') END),
                      CASE WHEN COALESCE(c.VariantName, '') <> '' THEN CONCAT(' (variant: ', c.VariantName, ')') ELSE '' END)
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        LEFT JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
        ORDER BY c.TableName, c.OrdinalPosition;

        -- Fold each table's missing-column adds into one multi-clause ALTER, materialize, execute.
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_AddColumnStmts;
        CREATE TEMPORARY TABLE _SchemaSmith_AddColumnStmts (RowId INT AUTO_INCREMENT PRIMARY KEY, Stmt TEXT)
            ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
        INSERT INTO _SchemaSmith_AddColumnStmts (Stmt)
        SELECT CONCAT('ALTER TABLE `', CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_unicode_ci, '`.', c.TableName, ' ',
                      -- Strip WITHOUT SYSTEM VERSIONING from the ADD COLUMN clause when the target table is
                      -- not (yet) system-versioned: MariaDB refuses that clause on an ALTER ADD COLUMN
                      -- against a non-versioned table (ERROR 4124), which aborts the whole deploy for a table
                      -- that is only CONVERGING to versioned in this same run (STEP 7.5 of ModifiedTableQuench
                      -- adds the versioning AFTER this pass). The clause is valid inline on CREATE (unchanged)
                      -- and on ADD COLUMN against an already-versioned table (kept). The column's exclusion is
                      -- (re)applied post-versioning by ModifiedTableQuench's exclusion pass.
                      GROUP_CONCAT(CONCAT('ADD COLUMN ',
                          CASE WHEN ist.TABLE_TYPE = 'SYSTEM VERSIONED' THEN c.ColumnScript
                               ELSE REPLACE(c.ColumnScript, ' WITHOUT SYSTEM VERSIONING', '') END)
                          ORDER BY c.OrdinalPosition SEPARATOR ', '))
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        LEFT JOIN INFORMATION_SCHEMA.TABLES ist
            ON BINARY ist.TABLE_SCHEMA = BINARY p_DatabaseName
            AND BINARY ist.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
        WHERE t.NewTable = 0
          AND c.NewColumn = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0)
        GROUP BY c.TableName, ist.TABLE_TYPE;

        SET @v_addcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_AddColumnStmts);
        WHILE @v_addcol_id IS NOT NULL DO
            SELECT Stmt INTO @exec_sql FROM _SchemaSmith_AddColumnStmts WHERE RowId = @v_addcol_id;
            PREPARE stmt FROM @exec_sql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_addcol_id := (SELECT MIN(RowId) FROM _SchemaSmith_AddColumnStmts WHERE RowId > @v_addcol_id);
        END WHILE;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_AddColumnStmts;

        -- Object-change audit (#243 E5): one row per physical column added to an existing table.
        -- Set-based over temp tables only (no INFORMATION_SCHEMA — not the #337 segfault shape).
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
        SELECT CONNECTION_ID(), 'column', CONCAT(c.TableName, '.', c.ColumnName), 'created'
        FROM _SchemaSmith_Columns c
        INNER JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
        WHERE t.NewTable = 0
          AND c.NewColumn = 1
          AND (c.GeneratedExpression IS NULL OR TRIM(c.GeneratedExpression) = '')
          AND NOT (c.IsAutoIncrement = 0 AND c.DefaultValue IS NOT NULL AND TRIM(c.DefaultValue) LIKE '(%' AND SchemaSmith_SupportsDefaultExpression() = 0);

    END IF;

END//

DELIMITER ;
