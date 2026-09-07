-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- GenerateTableJSON: Extracts comprehensive table metadata as JSON
-- Usage: CALL SchemaSmith_GenerateTableJSON('database_name', 'table_name');

DROP PROCEDURE IF EXISTS `SchemaSmith_GenerateTableJSON`;

DELIMITER //

CREATE PROCEDURE `SchemaSmith_GenerateTableJSON`(
    IN p_Schema VARCHAR(200),
    IN p_Table VARCHAR(200)
)
BEGIN
    DECLARE v_json LONGTEXT;
    DECLARE v_columns LONGTEXT;
    DECLARE v_indexes LONGTEXT;
    DECLARE v_foreign_keys LONGTEXT;
    DECLARE v_check_constraints LONGTEXT;
    DECLARE v_fulltext_indexes LONGTEXT;
    DECLARE v_tablespace VARCHAR(64);
    DECLARE v_datadirectory VARCHAR(512);

    -- Set session variables for proper GROUP_CONCAT handling
    SET SESSION group_concat_max_len = 1000000;

    -- SchemaSmith_TableTablespace is a PROCEDURE (OUT param), not a function -- it needs dynamic SQL to
    -- keep INFORMATION_SCHEMA.INNODB_TABLES/INNODB_TABLESPACES out of anything bound at CREATE time (see
    -- that script), and MySQL does not allow PREPARE/EXECUTE inside a stored FUNCTION (ERROR 1336). CALLed
    -- once here, ahead of the JSON_OBJECT SELECT below, which references the OUT result by variable.
    CALL SchemaSmith_TableTablespace(p_Schema, p_Table, v_tablespace);

    -- SchemaSmith_TableDataDirectory (F2c), both engines: MySQL's body needs the same PROCEDURE/dynamic-SQL
    -- shape as SchemaSmith_TableTablespace above (INNODB_DATAFILES is 8.0+ and MySQL disallows PREPARE
    -- inside a FUNCTION), so it is CALLed the same way -- once here, ahead of the JSON_OBJECT SELECT.
    CALL SchemaSmith_TableDataDirectory(p_Schema, p_Table, v_datadirectory);

    -- NULLIF(x, '') is NOT used inside JSON_OBJECT here. On MySQL 5.7 it collapses to a BOOLEAN --
    -- JSON_OBJECT emits `false` in place of the value -- so a table/column/index comment and a
    -- generated column's expression all extracted as `false` at the floor while 8.0 was correct.
    -- The CASE form evaluates identically on both. Verified live against 5.7 and 8.0.
    -- Get table metadata
    SELECT JSON_OBJECT(
        'Name', CONCAT('`', t.TABLE_NAME, '`'),
        'Engine', t.ENGINE,
        'RowFormat', t.ROW_FORMAT,
        'CharacterSet', SUBSTRING_INDEX(t.TABLE_COLLATION, '_', 1),
        'Collation', t.TABLE_COLLATION,
        'Comment', CASE WHEN t.TABLE_COMMENT = '' THEN NULL ELSE t.TABLE_COMMENT END,
        'AutoIncrementValue', t.AUTO_INCREMENT,
        -- These four live ONLY in CREATE_OPTIONS, a single free-text blob, which is why they share one
        -- parser and are read together. NULL (stripped by the JSON_REMOVE pass) when absent, so a table
        -- declaring none extracts exactly as it did before this shipped. #scope-boundary
        'Compression', CASE WHEN VERSION() LIKE '%MariaDB%' THEN NULL
                            ELSE SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'COMPRESSION') END,
        'KeyBlockSize', SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'KEY_BLOCK_SIZE'),
        -- MariaDB only, like IsSystemVersioned below.
        'PageCompressed', CASE WHEN SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'PAGE_COMPRESSED') = '1'
                               THEN TRUE ELSE NULL END,
        'PageCompressionLevel', SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'PAGE_COMPRESSION_LEVEL'),
        -- At-rest encryption, same CREATE_OPTIONS family as the compression four above. MySQL records
        -- ENCRYPTION in CREATE_OPTIONS only when ='Y' -- ='N'/default is ABSENT -- so absent means
        -- unencrypted default, same convention as every other property in this block. MySQL only,
        -- like Compression above.
        'Encryption', CASE WHEN VERSION() LIKE '%MariaDB%' THEN NULL
                           ELSE SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'ENCRYPTION') END,
        -- MariaDB only, like PageCompressed above. Verified live 2026-09-04: ENCRYPTED=YES surfaces as
        -- `ENCRYPTED`=YES in CREATE_OPTIONS.
        'Encrypted', CASE WHEN SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'ENCRYPTED') = 'YES'
                          THEN TRUE ELSE NULL END,
        'EncryptionKeyId', SchemaSmith_CreateOption(t.CREATE_OPTIONS, 'ENCRYPTION_KEY_ID'),
        -- The InnoDB general tablespace this table is placed in (F2b), MySQL only. NOT in CREATE_OPTIONS
        -- (unlike the block above) -- read via the per-engine SchemaSmith_TableTablespace CALL above (a
        -- PROCEDURE, not a function -- see its script for why), whose MySQL body reads
        -- INFORMATION_SCHEMA.INNODB_TABLES/INNODB_TABLESPACES through dynamic SQL and whose MariaDb
        -- override always sets NULL, keeping this shared proc kindle-safe on MariaDB (no bare reference to
        -- either MySQL-only view here). NULL for a table in the implicit per-table tablespace -- the
        -- overwhelming majority -- so it needs the same JSON_REMOVE strip below as the CREATE_OPTIONS four.
        'Tablespace', v_tablespace,
        -- The filesystem directory this table's InnoDB data file is placed in (F2c), both engines. NULL
        -- for the overwhelming majority of tables (no declared placement), so it needs the same
        -- JSON_REMOVE strip below as Tablespace above -- a table in no directory extracts exactly as it
        -- did before this shipped.
        'DataDirectory', v_datadirectory,
        -- MariaDB only, and NULL (stripped by the JSON_REMOVE pass) everywhere else, so a MySQL package
        -- never carries a property its schema does not declare.
        'IsSystemVersioned', CASE WHEN t.TABLE_TYPE = 'SYSTEM VERSIONED' THEN TRUE ELSE NULL END,
        -- Emit the sticky drop-protection marker first-class. Emitted as NULL when unset and stripped by the
        -- JSON_REMOVE pass below, so only protected tables carry "PreventDrop": true. Read from ProductOwnership. #270
        'PreventDrop', CASE WHEN EXISTS (SELECT 1 FROM SchemaSmith_ProductOwnership po
                                          WHERE po.ObjectType = 'TABLE'
                                            AND CONVERT(po.ObjectSchema USING utf8mb4) = CONVERT(p_Schema USING utf8mb4)
                                            AND CONVERT(po.ObjectName USING utf8mb4) = CONVERT(p_Table USING utf8mb4)
                                            AND COALESCE(po.PreventDrop, 0) = 1)
                            THEN TRUE ELSE NULL END
    ) INTO v_json
    FROM INFORMATION_SCHEMA.TABLES t
    WHERE t.TABLE_SCHEMA = p_Schema
      AND t.TABLE_NAME = p_Table
      -- MariaDB reports a system-versioned table as 'SYSTEM VERSIONED', not 'BASE TABLE'. Filtering on
      -- BASE TABLE alone silently omitted such a table from the extracted package -- no error, no warning,
      -- and the deploy-side twin of this filter was fixed separately. MySQL never reports this type.
      AND t.TABLE_TYPE IN ('BASE TABLE', 'SYSTEM VERSIONED');

    -- Get columns
    SELECT CONCAT('[', GROUP_CONCAT(
        -- JSON_REMOVE wrapper: WithoutSystemVersioning is stripped unless the column actually carries
        -- the exclusion. The table-level strip pass at the end of this procedure operates on the
        -- top-level object only and cannot reach inside a column, and leaving the key as null would be
        -- worse than noisy -- it is a NON-NULLABLE bool, so it fails deserialisation outright, the same
        -- trap IsSystemVersioned documents in that pass. It also keeps a MariaDB-only property out of
        -- every MySQL package, whose schema does not declare it and would reject it. #408
        JSON_REMOVE(JSON_OBJECT(
            'Name', CONCAT('`', c.COLUMN_NAME, '`'),
            'DataType', c.COLUMN_TYPE,
            'Nullable', CASE WHEN c.IS_NULLABLE = 'YES' THEN TRUE ELSE FALSE END,
            -- SchemaSmith_NormalizeColumnDefault folds MariaDB's divergent COLUMN_DEFAULT reporting
            -- (literal 'NULL' marker, quoted string values, current_timestamp() with parens) to the
            -- MySQL form so extraction output — and thus a round-tripped product — matches MySQL and
            -- does not phantom-churn on re-quench. It is an identity on MySQL. Shape detection
            -- (parens, function-call, binary/hex) runs on the RAW value so it isn't disturbed.
            -- Every branch is CONVERT(... USING utf8mb4) so the CASE has a single collation: the
            -- SchemaSmith_NormalizeColumnDefault result carries utf8mb4_unicode_ci while raw
            -- COLUMN_DEFAULT / string literals carry the information_schema/connection collation, and
            -- MariaDB refuses to aggregate the mix without an explicit coercion.
            'Default', CASE
                WHEN SchemaSmith_NormalizeColumnDefault(c.COLUMN_DEFAULT) IS NULL THEN NULL
                -- Numeric types: value is always a valid literal
                WHEN c.DATA_TYPE IN ('tinyint', 'smallint', 'mediumint', 'int', 'integer', 'bigint',
                                     'float', 'double', 'decimal', 'numeric', 'bit', 'year') THEN CONVERT(c.COLUMN_DEFAULT USING utf8mb4)
                -- Expression defaults (MySQL 8.0.13+): wrapped in parentheses
                WHEN c.COLUMN_DEFAULT LIKE '(%' THEN CONVERT(c.COLUMN_DEFAULT USING utf8mb4)
                -- Function/keyword defaults (CURRENT_TIMESTAMP, CURRENT_DATE, etc.): normalize so
                -- MariaDB's current_timestamp() folds to the MySQL CURRENT_TIMESTAMP form.
                WHEN UPPER(TRIM(c.COLUMN_DEFAULT)) LIKE 'CURRENT\_%' ESCAPE '\\' THEN CONVERT(SchemaSmith_NormalizeColumnDefault(c.COLUMN_DEFAULT) USING utf8mb4)
                -- Function calls like NOW(), UUID()
                WHEN UPPER(TRIM(c.COLUMN_DEFAULT)) LIKE '%()' THEN CONVERT(c.COLUMN_DEFAULT USING utf8mb4)
                -- Binary/hex literals
                WHEN c.COLUMN_DEFAULT LIKE 'b''%' THEN CONVERT(c.COLUMN_DEFAULT USING utf8mb4)
                WHEN c.COLUMN_DEFAULT LIKE '0x%' THEN CONVERT(c.COLUMN_DEFAULT USING utf8mb4)
                -- String literals: normalize (strips MariaDB's outer quotes) then wrap consistently
                ELSE CONVERT(CONCAT('''', REPLACE(CONVERT(SchemaSmith_NormalizeColumnDefault(c.COLUMN_DEFAULT) USING utf8mb4), '''', ''''''), '''') USING utf8mb4)
            END,
            -- ON UPDATE CURRENT_TIMESTAMP[(n)] -- deliberately independent of Default above: a column's
            -- DEFAULT CURRENT_TIMESTAMP governs INSERT-time initialization, this governs UPDATE-time
            -- refresh, and a column can carry either, both, or neither. Predates both engines' hard
            -- floors (MySQL 5.6.5; MariaDB inherited it), unlike Invisible/Srid below, so no
            -- SchemaSmith_Supports... gate is needed here or anywhere else in this feature.
            -- SchemaSmith_ColumnOnUpdateClause isolates the EXTRA parsing (EXTRA can carry several flags
            -- at once, e.g. 'DEFAULT_GENERATED on update CURRENT_TIMESTAMP') and preserves a declared
            -- precision (CURRENT_TIMESTAMP(3)) rather than collapsing it to the bare form.
            'OnUpdateCurrentTimestamp', SchemaSmith_ColumnOnUpdateClause(c.EXTRA),
            'AutoIncrement', CASE WHEN c.EXTRA LIKE '%auto_increment%' THEN TRUE ELSE FALSE END,
            -- EXTRA is a single column shared by both engines and predates the invisible-column feature,
            -- so (unlike SchemaSmith_IndexIsVisible's IS_VISIBLE/IGNORED divergence, which required a
            -- per-engine wrapper to avoid binding a column that doesn't exist below its floor) reading it
            -- here is safe on every version: below MySQL 8.0.23 / MariaDB 10.3 the INVISIBLE marker simply
            -- never appears, so the LIKE is always false there -- no version gate needed at extraction time.
            'Invisible', CASE WHEN c.EXTRA LIKE '%INVISIBLE%' THEN TRUE ELSE FALSE END,
            -- MariaDB only, and NULL (stripped by the JSON_REMOVE pass) everywhere else, so a MySQL
            -- package never carries a property its schema does not declare -- the IsSystemVersioned
            -- convention above. EXTRA is safe to read at every supported floor; below MariaDB 10.3 the
            -- marker simply never appears. #408
            'WithoutSystemVersioning', CASE WHEN c.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%' THEN TRUE ELSE NULL END,
            -- SRS_ID does not exist on MariaDB's INFORMATION_SCHEMA.COLUMNS at all (unlike EXTRA above,
            -- which both engines carry), so it cannot be read as a plain c.SRS_ID here without breaking
            -- extraction for every table on MariaDB (ER_BAD_FIELD_ERROR). SchemaSmith_ColumnSrid isolates
            -- the divergence: its MySQL body reads SRS_ID (gated below MySQL 8.0.3), its MariaDb override
            -- (Scripts/MariaDb/SchemaSmith_ColumnSrid.sql) always returns NULL. See
            -- SchemaSmith_SupportsColumnSrid for the full per-engine rationale.
            'Srid', SchemaSmith_ColumnSrid(p_Schema, p_Table, c.COLUMN_NAME),
            'Generated', CASE
                WHEN c.EXTRA LIKE '%VIRTUAL GENERATED%' THEN 'VIRTUAL'
                WHEN c.EXTRA LIKE '%STORED GENERATED%' THEN 'STORED'
                ELSE NULL
            END,
            'GenerationExpression', CASE WHEN c.GENERATION_EXPRESSION = '' THEN NULL ELSE c.GENERATION_EXPRESSION END,
            'CharacterSet', c.CHARACTER_SET_NAME,
            'Collation', CASE
                WHEN c.COLLATION_NAME = (SELECT TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES
                                         WHERE TABLE_SCHEMA = p_Schema AND TABLE_NAME = p_Table)
                THEN NULL  -- Don't include if same as table default
                ELSE c.COLLATION_NAME
            END,
            'Comment', CASE WHEN c.COLUMN_COMMENT = '' THEN NULL ELSE c.COLUMN_COMMENT END
        ), CASE WHEN c.EXTRA LIKE '%WITHOUT SYSTEM VERSIONING%'
                THEN '$.___dummy___' ELSE '$.WithoutSystemVersioning' END)
        -- Alphabetical, matching SQL Server and PostgreSQL. Ordinal order made the same table extract
        -- differently depending on which engine it came from, so a package re-extracted elsewhere showed
        -- a whole-file diff that was pure noise. Name order is also stable against a source table whose
        -- ordinal order changes, which is the determinism the sort exists for.
        -- Column sequence: 'Name' (default) or 'Physical', the table's own order. COLUMNS ONLY here --
        -- the same Product:ObjectOrder setting also orders indexes, foreign keys and check
        -- constraints, but the caller sequences those after this proc returns. MySQL stored procedures
        -- cannot carry default parameter values, so adding a parameter would break every existing caller --
        -- including the hand-written CALL this proc exists to serve. A session variable keeps those working
        -- unchanged; SQL Server and PostgreSQL take a defaulted parameter instead, which they support.
        --   SET @SchemaSmith_ObjectOrder = 'Physical';
        ORDER BY CASE WHEN LOWER(COALESCE(@SchemaSmith_ObjectOrder, 'Name')) = 'physical'
                      THEN c.ORDINAL_POSITION END,
                 CASE WHEN LOWER(COALESCE(@SchemaSmith_ObjectOrder, 'Name')) = 'physical'
                      THEN NULL ELSE c.COLUMN_NAME END
        SEPARATOR ','
    ), ']') INTO v_columns
    FROM INFORMATION_SCHEMA.COLUMNS c
    WHERE c.TABLE_SCHEMA = p_Schema
      AND c.TABLE_NAME = p_Table
      -- A system-versioned table's row-start/row-end columns are generated and maintained by the engine.
      -- Extracting them as ordinary columns would have the apply path try to manage them on re-deploy.
      -- Only the explicit authoring form exposes them at all; the implicit form hides them. Isolated in a
      -- function because the catalog columns behind it do not exist on MySQL -- see that function.
      AND SchemaSmith_IsSystemTimePeriodColumn(p_Schema, p_Table, c.COLUMN_NAME) = 0;

    -- Get indexes (excluding FULLTEXT which are handled separately). A functional/expression key part
    -- (MySQL 8.0.13+) has NULL COLUMN_NAME and reports its text via EXPRESSION instead; that column does
    -- not exist below the floor or on MariaDB, so the branch that reads it is gated behind
    -- SchemaSmith_SupportsFunctionalIndex() as two whole statements rather than one CASE inside a single
    -- statement -- column resolution is deferred to the execution of whichever statement actually runs (see
    -- SchemaSmith_IndexIsVisible / SchemaSmith_SnapshotIndexVisibility for the same IS_VISIBLE-below-8.0
    -- shape), so the unreached branch's EXPRESSION reference is never bound on an engine that lacks it.
    -- The expression is wrapped in one extra paren pair, matching what MySQL's own SHOW CREATE TABLE
    -- renders for a functional key part -- the form a user hand-authoring the JSON would recognize.
    -- EXPRESSION also carries a charset-introducer prefix (e.g. _latin1'...', _utf8mb4'...' -- it
    -- reflects the connection charset in effect when the index was CREATEd, so it is not a fixed
    -- value) on any string literal -- the same MySQL re-serialization quirk already stripped from
    -- CHECK_CLAUSE below, generalized here to any introducer rather than an enumerated list. AND,
    -- confirmed live (unlike SHOW CREATE TABLE, which does not do this): EXPRESSION additionally
    -- backslash-escapes that literal's quotes, e.g. _latin1\'$.tags\' -- so a two-pass clean is
    -- needed: (1) turn every backslash-escaped quote into a plain quote (a targeted `\'`-to-`'`
    -- substitution via CHAR(92)/CHAR(39), NOT a blanket backslash strip -- a JSON path or literal
    -- may legitimately contain an unrelated backslash, and destroying it would corrupt the compare
    -- more subtly than the bug being fixed here), THEN (2) strip the now-plainly-quoted introducer.
    -- Stripped so a multi-valued key part's mandatory JSON-path literal (CAST(col->'$.path' AS ...
    -- ARRAY) always carries one) round-trips clean instead of leaving internal charset/escaping
    -- noise the user never typed, which would otherwise never match the declared side and rebuild
    -- forever. Applies to any functional index whose expression contains a string literal, not just
    -- a multi-valued one -- lower(`name`) (C1-1's test case) has none, which is why it never surfaced
    -- this. Applied identically at both _SchemaSmith_IdxDetectSnap builds so drift comparison converges.
    -- REGEXP_REPLACE (MySQL 8.0.4+) IS safe here despite the 5.7 floor: this whole branch only
    -- executes when SchemaSmith_SupportsFunctionalIndex() = 1 (8.0.13+), and MySQL stored-routine
    -- bodies are not semantically validated at CREATE time -- function-name resolution, like the
    -- EXPRESSION column reference above, is deferred to the execution of whichever branch actually
    -- runs, so REGEXP_REPLACE compiles fine into an unreached branch on 5.7/MariaDB. This differs
    -- from SchemaSmith_StripLeadingSelect.sql and the CHECK_CLAUSE case in ParseTableJson.sql: both
    -- of those run unconditionally on every target regardless of version, so REGEXP_REPLACE there
    -- would actually be invoked on 5.7 and fail -- an execution-time constraint, not a compile-time
    -- one, and not the situation here.
    IF SchemaSmith_SupportsFunctionalIndex() = 1 THEN
        SELECT CONCAT('[', IFNULL(GROUP_CONCAT(idx_json SEPARATOR ','), ''), ']') INTO v_indexes
        FROM (
            SELECT JSON_OBJECT(
                'Name', s.INDEX_NAME,
                'PrimaryKey', CASE WHEN s.INDEX_NAME = 'PRIMARY' THEN TRUE ELSE FALSE END,
                'Unique', CASE WHEN s.NON_UNIQUE = 0 THEN TRUE ELSE FALSE END,
                'UniqueConstraint', CASE WHEN s.INDEX_NAME = 'PRIMARY' OR s.NON_UNIQUE = 0 THEN TRUE ELSE FALSE END,
                'IndexType', s.INDEX_TYPE,
                'IndexColumns', GROUP_CONCAT(
                    CASE WHEN s.COLUMN_NAME IS NOT NULL THEN
                        CONCAT('`', s.COLUMN_NAME, '`',
                            CASE WHEN s.SUB_PART IS NOT NULL AND s.INDEX_TYPE != 'SPATIAL' THEN CONCAT('(', s.SUB_PART, ')') ELSE '' END,
                            CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END
                        )
                    ELSE
                        CONCAT('(', REGEXP_REPLACE(
                            REPLACE(s.EXPRESSION, CONCAT(CHAR(92), CHAR(39)), CHAR(39)),
                            '_[A-Za-z0-9]+''', ''''), ')',
                            CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END
                        )
                    END
                    ORDER BY s.SEQ_IN_INDEX
                    SEPARATOR ','
                ),
                'Visible', CASE WHEN SchemaSmith_IndexIsVisible(p_Schema, p_Table, s.INDEX_NAME) = 1 THEN TRUE ELSE FALSE END,
                'Comment', CASE WHEN s.INDEX_COMMENT = '' THEN NULL ELSE s.INDEX_COMMENT END
            ) AS idx_json
            FROM INFORMATION_SCHEMA.STATISTICS s
            WHERE s.TABLE_SCHEMA = p_Schema
              AND s.TABLE_NAME = p_Table
              AND s.INDEX_TYPE != 'FULLTEXT'
            GROUP BY s.INDEX_NAME, s.NON_UNIQUE, s.INDEX_TYPE, s.INDEX_COMMENT
        ) idx_subquery;
    ELSE
        SELECT CONCAT('[', IFNULL(GROUP_CONCAT(idx_json SEPARATOR ','), ''), ']') INTO v_indexes
        FROM (
            SELECT JSON_OBJECT(
                'Name', s.INDEX_NAME,
                'PrimaryKey', CASE WHEN s.INDEX_NAME = 'PRIMARY' THEN TRUE ELSE FALSE END,
                'Unique', CASE WHEN s.NON_UNIQUE = 0 THEN TRUE ELSE FALSE END,
                'UniqueConstraint', CASE WHEN s.INDEX_NAME = 'PRIMARY' OR s.NON_UNIQUE = 0 THEN TRUE ELSE FALSE END,
                'IndexType', s.INDEX_TYPE,
                'IndexColumns', GROUP_CONCAT(
                    CONCAT('`', s.COLUMN_NAME, '`',
                        CASE WHEN s.SUB_PART IS NOT NULL AND s.INDEX_TYPE != 'SPATIAL' THEN CONCAT('(', s.SUB_PART, ')') ELSE '' END,
                        CASE WHEN s.COLLATION = 'D' THEN ' DESC' ELSE '' END
                    )
                    ORDER BY s.SEQ_IN_INDEX
                    SEPARATOR ','
                ),
                'Visible', CASE WHEN SchemaSmith_IndexIsVisible(p_Schema, p_Table, s.INDEX_NAME) = 1 THEN TRUE ELSE FALSE END,
                'Comment', CASE WHEN s.INDEX_COMMENT = '' THEN NULL ELSE s.INDEX_COMMENT END
            ) AS idx_json
            FROM INFORMATION_SCHEMA.STATISTICS s
            WHERE s.TABLE_SCHEMA = p_Schema
              AND s.TABLE_NAME = p_Table
              AND s.INDEX_TYPE != 'FULLTEXT'
            GROUP BY s.INDEX_NAME, s.NON_UNIQUE, s.INDEX_TYPE, s.INDEX_COMMENT
        ) idx_subquery;
    END IF;

    -- Get foreign keys
    SELECT CONCAT('[', IFNULL(GROUP_CONCAT(fk_json SEPARATOR ','), ''), ']') INTO v_foreign_keys
    FROM (
        SELECT JSON_OBJECT(
            'Name', tc.CONSTRAINT_NAME,
            'Columns', (
                SELECT GROUP_CONCAT(CONCAT('`', kcu2.COLUMN_NAME, '`') ORDER BY kcu2.ORDINAL_POSITION SEPARATOR ',')
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu2
                WHERE kcu2.CONSTRAINT_SCHEMA = p_Schema
                  AND kcu2.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                  AND kcu2.TABLE_NAME = p_Table
            ),
            'RelatedTableSchema', CASE
                WHEN rc.UNIQUE_CONSTRAINT_SCHEMA = p_Schema THEN ''
                ELSE rc.UNIQUE_CONSTRAINT_SCHEMA
            END,
            'RelatedTable', CONCAT('`', rc.REFERENCED_TABLE_NAME, '`'),
            'RelatedColumns', (
                SELECT GROUP_CONCAT(CONCAT('`', kcu3.REFERENCED_COLUMN_NAME, '`') ORDER BY kcu3.ORDINAL_POSITION SEPARATOR ',')
                FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu3
                WHERE kcu3.CONSTRAINT_SCHEMA = p_Schema
                  AND kcu3.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                  AND kcu3.TABLE_NAME = p_Table
            ),
            'DeleteAction', rc.DELETE_RULE,
            'UpdateAction', rc.UPDATE_RULE
        ) AS fk_json
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        JOIN INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS rc
          ON tc.CONSTRAINT_SCHEMA = rc.CONSTRAINT_SCHEMA
          AND tc.CONSTRAINT_NAME = rc.CONSTRAINT_NAME
        WHERE tc.TABLE_SCHEMA = p_Schema
          AND tc.TABLE_NAME = p_Table
          AND tc.CONSTRAINT_TYPE = 'FOREIGN KEY'
    ) fk_subquery;

    -- Get check constraints (MySQL 8.0.16+ / MariaDB; INFORMATION_SCHEMA.CHECK_CONSTRAINTS does not
    -- exist on MySQL 5.7). MySQL binds INFORMATION_SCHEMA references at CREATE time, so the read must
    -- live only inside this dynamically-built string, executed under the SupportsCheckConstraints guard.
    IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
        SET @v_ccSchema = p_Schema;
        SET @v_ccTable = p_Table;
        SET @v_ccSql = 'SELECT CONCAT(''['', IFNULL(GROUP_CONCAT(
    JSON_OBJECT(
        ''Name'', cc.CONSTRAINT_NAME,
        ''Expression'', REPLACE(REGEXP_REPLACE(cc.CHECK_CLAUSE, ''_utf8mb4|_utf8mb3|_utf8|_latin1|_binary'', ''''), ''\\\\'''''', '''''''')
    )
    SEPARATOR '',''
), ''''), '']'') INTO @v_ccResult
FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
  ON cc.CONSTRAINT_SCHEMA = tc.CONSTRAINT_SCHEMA
  AND cc.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
WHERE tc.TABLE_SCHEMA = @v_ccSchema
  AND tc.TABLE_NAME = @v_ccTable
  AND tc.CONSTRAINT_TYPE = ''CHECK''';
        PREPARE stmt FROM @v_ccSql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
        SET v_check_constraints = @v_ccResult;
    ELSE
        SET v_check_constraints = '[]';
    END IF;

    -- Get fulltext indexes
    SELECT CONCAT('[', IFNULL(GROUP_CONCAT(ft_json SEPARATOR ','), ''), ']') INTO v_fulltext_indexes
    FROM (
        SELECT JSON_OBJECT(
            'Name', s.INDEX_NAME,
            'Columns', GROUP_CONCAT(CONCAT('`', s.COLUMN_NAME, '`') ORDER BY s.SEQ_IN_INDEX SEPARATOR ','),
            'Comment', CASE WHEN MAX(s.INDEX_COMMENT) = '' THEN NULL ELSE MAX(s.INDEX_COMMENT) END
        ) AS ft_json
        FROM INFORMATION_SCHEMA.STATISTICS s
        WHERE s.TABLE_SCHEMA = p_Schema
          AND s.TABLE_NAME = p_Table
          AND s.INDEX_TYPE = 'FULLTEXT'
        GROUP BY s.INDEX_NAME
    ) ft_subquery;

    -- Combine all into final JSON. Nest the JSON-text vars as JSON values via JSON_EXTRACT(x,'$')
    -- rather than CAST(x AS JSON): MariaDB has no native JSON type and rejects the CAST syntax,
    -- while JSON_EXTRACT(x,'$') nests identically on both engines (and yields JSON null for NULL).
    SET v_json = JSON_SET(v_json,
        '$.Columns', JSON_EXTRACT(v_columns, '$'),
        '$.Indexes', JSON_EXTRACT(v_indexes, '$'),
        '$.ForeignKeys', JSON_EXTRACT(v_foreign_keys, '$'),
        '$.CheckConstraints', JSON_EXTRACT(v_check_constraints, '$'),
        '$.FullTextIndexes', JSON_EXTRACT(v_fulltext_indexes, '$'),
        -- Application-time periods, MariaDB only. Nested the same way as every array above, and for the
        -- same reason the comment there gives: MariaDB rejects CAST(x AS JSON).
        '$.Periods', JSON_EXTRACT(SchemaSmith_TablePeriodsJson(p_Schema, p_Table), '$'),
        -- Partitioning (#partitioning, K3): an OBJECT rather than an array, nested the same way and for
        -- the same reason -- MariaDB rejects CAST(x AS JSON). 'null' for an unpartitioned table, stripped
        -- by the JSON_REMOVE pass below so every existing package extracts byte-identically.
        '$.Partitioning', JSON_EXTRACT(SchemaSmith_TablePartitioningJson(p_Schema, p_Table), '$')
    );

    -- Remove null values for cleaner output
    SET v_json = JSON_REMOVE(v_json,
        CASE WHEN JSON_EXTRACT(v_json, '$.Comment') IS NULL THEN '$.Comment' ELSE '$.___dummy___' END,
        CASE WHEN JSON_EXTRACT(v_json, '$.AutoIncrementValue') IS NULL THEN '$.AutoIncrementValue' ELSE '$.___dummy___' END,
        CASE WHEN JSON_EXTRACT(v_json, '$.RowFormat') IS NULL THEN '$.RowFormat' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.PreventDrop')), 'NULL') = 'NULL' THEN '$.PreventDrop' ELSE '$.___dummy___' END,
        -- Same treatment as PreventDrop: a bool the package only carries when true. Without this the
        -- property serialises as null and deserialisation of the non-nullable bool fails outright.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.IsSystemVersioned')), 'NULL') = 'NULL' THEN '$.IsSystemVersioned' ELSE '$.___dummy___' END,
        -- The CREATE_OPTIONS four, same treatment: absent means the table declares none, and a MySQL
        -- package must not carry PageCompressed* (nor a MariaDB one Compression) at all.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.Compression')), 'NULL') = 'NULL' THEN '$.Compression' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.KeyBlockSize')), 'NULL') = 'NULL' THEN '$.KeyBlockSize' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.PageCompressed')), 'NULL') = 'NULL' THEN '$.PageCompressed' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.PageCompressionLevel')), 'NULL') = 'NULL' THEN '$.PageCompressionLevel' ELSE '$.___dummy___' END,
        -- The encryption three, same treatment: absent means the table declares none, and a MariaDB
        -- package must not carry Encryption (nor a MySQL one Encrypted/EncryptionKeyId) at all.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.Encryption')), 'NULL') = 'NULL' THEN '$.Encryption' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.Encrypted')), 'NULL') = 'NULL' THEN '$.Encrypted' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.EncryptionKeyId')), 'NULL') = 'NULL' THEN '$.EncryptionKeyId' ELSE '$.___dummy___' END,
        -- Tablespace (F2b): absent means the table lives in its own implicit per-table tablespace, and a
        -- package must not carry the key at all -- the no-churn contract every property in this block shares.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.Tablespace')), 'NULL') = 'NULL' THEN '$.Tablespace' ELSE '$.___dummy___' END,
        -- DataDirectory (F2c): absent means no declared placement, same no-churn contract as Tablespace.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.DataDirectory')), 'NULL') = 'NULL' THEN '$.DataDirectory' ELSE '$.___dummy___' END,
        -- Empty array, not null: the function returns '[]' for a table with no periods, and an
        -- ordinary table's package must not carry the key at all -- nor must any MySQL package,
        -- whose schema does not declare this property.
        CASE WHEN JSON_LENGTH(JSON_EXTRACT(v_json, '$.Periods')) = 0 THEN '$.Periods' ELSE '$.___dummy___' END,
        -- JSON null, not SQL NULL: the helper returns the literal 'null' for an unpartitioned table, which
        -- JSON_EXTRACT yields as a JSON null value -- so JSON_TYPE, not IS NULL, is what detects it.
        CASE WHEN COALESCE(JSON_TYPE(JSON_EXTRACT(v_json, '$.Partitioning')), 'NULL') = 'NULL' THEN '$.Partitioning' ELSE '$.___dummy___' END
    );

    SELECT v_json AS TableJson;
END //

DELIMITER ;
