-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_DestructiveChangeGate//

CREATE PROCEDURE SchemaSmith_DestructiveChangeGate(
    IN p_ProductName VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_DatabaseName VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT,
    IN p_DropTables TINYINT,
    IN p_DropColumns TINYINT
)
SQL SECURITY DEFINER
gate: BEGIN
    -- H3. What this step removes -- tables and columns dropped because the package no longer declares them -- with each
    -- table's rows from the catalog (TABLE_ROWS, never COUNT(*), which would scan exactly the big tables this is for),
    -- recorded in the change audit. Under 'fail' the step is refused before anything in it is dropped, unless the change is
    -- named in @schemasmith_accept_destructive: ';db.table;' accepts every change to the table, ';db.table.column;' one
    -- column, lower case and without backticks. The policy and the list arrive in session variables, as the rebuild policy
    -- does: a new parameter would break every direct caller, and an unset variable reads as allow. The same predicates as
    -- STEP 4 (columns) and STEP 8 (tables) of ModifiedTableQuench, which run after this.
    DECLARE v_DbCi VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci DEFAULT p_DatabaseName;
    DECLARE v_DbKey VARCHAR(260) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin DEFAULT SchemaSmith_IdentifierKey(p_DatabaseName);
    DECLARE v_Policy VARCHAR(5) DEFAULT COALESCE(@schemasmith_destructive_policy, 'allow');
    DECLARE v_Accept TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT LOWER(COALESCE(@schemasmith_accept_destructive, ''));

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Destructive;
    CREATE TEMPORARY TABLE _SchemaSmith_Destructive (
        RowId INT AUTO_INCREMENT PRIMARY KEY,
        Kind VARCHAR(16) NOT NULL,
        TableName VARCHAR(300) COLLATE utf8mb4_unicode_ci NOT NULL,
        ObjectName VARCHAR(512) COLLATE utf8mb4_unicode_ci NOT NULL,
        TableKey VARCHAR(260) COLLATE utf8mb4_bin NOT NULL,
        Detail VARCHAR(400) COLLATE utf8mb4_unicode_ci NULL,
        EstimatedRows BIGINT NULL,
        Disposition VARCHAR(16) NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

    IF p_DropTables = 1 THEN
        INSERT INTO _SchemaSmith_Destructive (Kind, TableName, ObjectName, TableKey)
        SELECT 'table drop', CONCAT(p_DatabaseName, '.', po.ObjectName), CONCAT(p_DatabaseName, '.', po.ObjectName),
               CONVERT(IF(@@lower_case_table_names = 0, po.ObjectName, LOWER(po.ObjectName)) USING utf8mb4) COLLATE utf8mb4_bin
        FROM SchemaSmith_ProductOwnership po
        WHERE CONVERT(po.ProductName USING utf8mb4) = CONVERT(p_ProductName USING utf8mb4)
          AND SchemaSmith_IdentifierKey(po.ObjectSchema) = SchemaSmith_IdentifierKey(p_DatabaseName)
          AND po.ObjectType = 'TABLE'
          AND COALESCE(po.PreventDrop, 0) = 0
          AND EXISTS (SELECT 1 FROM _SchemaSmith_ExistingTables ist
                       WHERE ist.TableKey = CONVERT(IF(@@lower_case_table_names = 0, po.ObjectName, LOWER(po.ObjectName)) USING utf8mb4) COLLATE utf8mb4_bin)
          AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_Tables t
                           WHERE SchemaSmith_IdentifierKey(SchemaSmith_StripBacktickWrapping(t.TableName)) = SchemaSmith_IdentifierKey(po.ObjectName));

        -- A partitioned table removed from the product is refused by name by STEP 8's guard; the gate stands aside so that
        -- is the message the deploy stops on.
        IF EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.PARTITIONS ip
                    WHERE ip.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(ip.TABLE_SCHEMA) = v_DbKey
                      AND ip.PARTITION_NAME IS NOT NULL
                      AND SchemaSmith_IdentifierKey(ip.TABLE_NAME) IN (SELECT TableKey FROM _SchemaSmith_Destructive)) THEN
            LEAVE gate;
        END IF;
    END IF;

    IF p_DropColumns = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateDefined;
        CREATE TEMPORARY TABLE _SchemaSmith_GateDefined (RowId INT AUTO_INCREMENT PRIMARY KEY) ENGINE=InnoDB
            SELECT c.TableKey, c.ColumnKey, c.OldNameKey FROM _SchemaSmith_Columns c;
        CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
        INSERT INTO _SchemaSmith_Destructive (Kind, TableName, ObjectName, TableKey)
        SELECT 'column drop', CONCAT(p_DatabaseName, '.', SchemaSmith_StripBacktickWrapping(t.TableName)),
               CONCAT(p_DatabaseName, '.', SchemaSmith_StripBacktickWrapping(t.TableName), '.', isc.COLUMN_NAME), t.TableKey
        FROM _SchemaSmith_Tables t
        INNER JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = t.TableKey
        LEFT JOIN _SchemaSmith_GateDefined dc
            ON dc.TableKey = t.TableKey
           AND (dc.ColumnKey = isc.ColumnKey OR (dc.OldNameKey IS NOT NULL AND dc.OldNameKey = isc.ColumnKey))
        WHERE t.NewTable = 0
          AND dc.TableKey IS NULL
          AND SchemaSmith_IsSystemTimePeriodColumn(p_DatabaseName, SchemaSmith_StripBacktickWrapping(t.TableName), isc.COLUMN_NAME) = 0
          AND SchemaSmith_IsGeneratedInvisiblePrimaryKey(p_DatabaseName, SchemaSmith_StripBacktickWrapping(t.TableName), isc.COLUMN_NAME) = 0
          AND COALESCE(t.DropColumnsRemovedFromProduct, 1) = 1;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateDefined;
    END IF;

    -- A column whose type change rounds or cuts values without an error (SchemaSmith_ColumnChangeLoss), and one whose
    -- generated status changes, which STEP 3.5 drops and adds again: a plain column's stored values are replaced by the
    -- expression's, a generated one comes back plain and empty. Without a strict sql_mode -- the one the kindled routines
    -- run under, which @@SESSION.sql_mode reads here -- a narrowing that would refuse clamps instead, and a NULL becomes
    -- the type's zero when the column turns NOT NULL, and a character a utf8 column moved to a narrower character set
    -- cannot hold becomes '?'. STRICT_TRANS_TABLES is strict only for a transactional table: on any
    -- other the rows after the first are converted with a warning.
    CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateEngines;
    CREATE TEMPORARY TABLE _SchemaSmith_GateEngines (RowId INT AUTO_INCREMENT PRIMARY KEY) ENGINE=InnoDB
        SELECT CONVERT(IF(@@lower_case_table_names = 0, it.TABLE_NAME, LOWER(it.TABLE_NAME)) USING utf8mb4) COLLATE utf8mb4_bin AS TableKey,
               IF(@@SESSION.sql_mode LIKE '%STRICT_ALL_TABLES%'
                  OR (@@SESSION.sql_mode LIKE '%STRICT_TRANS_TABLES%' AND it.ENGINE = 'InnoDB'), 1, 0) AS Strict
          FROM INFORMATION_SCHEMA.TABLES it
         WHERE it.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(it.TABLE_SCHEMA) = v_DbKey;
    INSERT INTO _SchemaSmith_Destructive (Kind, TableName, ObjectName, TableKey, Detail)
    SELECT IF(ch.Replaced = 1, 'column replaced', 'type change'), ch.TableName, ch.ObjectName, ch.TableKey,
           LEFT(IF(ch.Replaced = 1,
                   CONCAT(IF(ch.LiveGenerated = 1, 'generated', ch.LiveType), ' -> ', IF(ch.LiveGenerated = 1, ch.DeclaredType, 'generated')),
                   CONCAT(ch.LiveType, IF(ch.CharsetNarrowed = 1, CONCAT(' ', ch.LiveCharset), ''), IF(ch.NullsZeroed = 1, ' NULL', ''), ' -> ',
                          ch.DeclaredType, IF(ch.CharsetNarrowed = 1, CONCAT(' ', ch.DeclaredCharset), ''), IF(ch.NullsZeroed = 1, ' NOT NULL', ''))), 400)
      FROM (SELECT CONCAT(p_DatabaseName, '.', SchemaSmith_StripBacktickWrapping(t.TableName)) AS TableName,
                   CONCAT(p_DatabaseName, '.', SchemaSmith_StripBacktickWrapping(t.TableName), '.', isc.COLUMN_NAME) AS ObjectName,
                   t.TableKey, isc.COLUMN_TYPE AS LiveType, c.DataType AS DeclaredType,
                   isc.CHARACTER_SET_NAME AS LiveCharset,
                   LOWER(COALESCE(NULLIF(TRIM(c.CharacterSet), ''), NULLIF(SUBSTRING_INDEX(TRIM(c.Collation), '_', 1), ''))) AS DeclaredCharset,
                   IF(IFNULL(isc.GENERATION_EXPRESSION, '') != '', 1, 0) AS LiveGenerated,
                   IF((IFNULL(isc.GENERATION_EXPRESSION, '') != '') != (IFNULL(TRIM(c.GeneratedExpression), '') != ''), 1, 0) AS Replaced,
                   IF(IFNULL(isc.GENERATION_EXPRESSION, '') = '' AND IFNULL(TRIM(c.GeneratedExpression), '') = ''
                      AND SchemaSmith_ColumnTypeDiffers(isc.COLUMN_TYPE, c.CompareDataType) = 1
                      AND SchemaSmith_ColumnChangeLoss(isc.COLUMN_TYPE, c.CompareDataType, COALESCE(e.Strict, 0)) = 2, 1, 0) AS TypeLoses,
                   IF(IFNULL(isc.GENERATION_EXPRESSION, '') = '' AND IFNULL(TRIM(c.GeneratedExpression), '') = ''
                      AND COALESCE(e.Strict, 0) = 0 AND isc.IS_NULLABLE = 'YES' AND c.IsNullable = 0, 1, 0) AS NullsZeroed,
                   IF(COALESCE(e.Strict, 0) = 0 AND isc.CHARACTER_SET_NAME LIKE 'utf8%'
                      AND LOWER(COALESCE(NULLIF(TRIM(c.CharacterSet), ''), NULLIF(SUBSTRING_INDEX(TRIM(c.Collation), '_', 1), ''))) NOT LIKE 'utf8%', 1, 0) AS CharsetNarrowed
              FROM _SchemaSmith_Columns c
              JOIN _SchemaSmith_Tables t ON t.TableName = c.TableName
              JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey
              LEFT JOIN _SchemaSmith_GateEngines e ON e.TableKey = t.TableKey
             WHERE t.NewTable = 0 AND c.NewColumn = 0
               AND c.SystemTimeRole IS NULL
               AND SchemaSmith_IsSystemTimePeriodColumn(p_DatabaseName, SchemaSmith_StripBacktickWrapping(t.TableName), isc.COLUMN_NAME) = 0) ch
     WHERE ch.Replaced = 1 OR ch.TypeLoses = 1 OR ch.NullsZeroed = 1 OR ch.CharsetNarrowed = 1;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateEngines;

    IF NOT EXISTS (SELECT 1 FROM _SchemaSmith_Destructive) THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Destructive;
        LEAVE gate;
    END IF;

    -- MySQL 8 caches table statistics for a day by default; this read wants the current ones. MariaDB has no such cache.
    IF VERSION() NOT LIKE '%MariaDB%' AND SchemaSmith_ServerVersionNum() >= 800 THEN
        SET @ss_gate_sql = 'SET SESSION information_schema_stats_expiry = 0';
        PREPARE ss_gate_stmt FROM @ss_gate_sql;
        EXECUTE ss_gate_stmt;
        DEALLOCATE PREPARE ss_gate_stmt;
    END IF;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateRows;
    CREATE TEMPORARY TABLE _SchemaSmith_GateRows (RowId INT AUTO_INCREMENT PRIMARY KEY) ENGINE=InnoDB
        SELECT CONVERT(IF(@@lower_case_table_names = 0, it.TABLE_NAME, LOWER(it.TABLE_NAME)) USING utf8mb4) COLLATE utf8mb4_bin AS TableKey,
               CASE WHEN it.ENGINE IN ('InnoDB', 'MyISAM', 'Aria') THEN it.TABLE_ROWS END AS EstimatedRows
          FROM INFORMATION_SCHEMA.TABLES it
         WHERE it.TABLE_SCHEMA = v_DbCi AND SchemaSmith_IdentifierKey(it.TABLE_SCHEMA) = v_DbKey;
    UPDATE _SchemaSmith_Destructive d JOIN _SchemaSmith_GateRows r ON r.TableKey = d.TableKey SET d.EstimatedRows = r.EstimatedRows;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_GateRows;

    UPDATE _SchemaSmith_Destructive
       SET Disposition = CASE WHEN LOCATE(CONCAT(';', LOWER(ObjectName), ';'), v_Accept) > 0
                                OR LOCATE(CONCAT(';', LOWER(TableName), ';'), v_Accept) > 0 THEN 'accepted'
                              WHEN v_Policy = 'fail' THEN IF(p_WhatIf = 1, 'wouldRefuse', 'refused')
                              WHEN v_Policy = 'warn' THEN 'warned'
                              ELSE 'allowed' END;

    INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType, Mode, Detail, EstimatedRows)
    SELECT CONNECTION_ID(), Kind, ObjectName, 'destructive', Disposition, Detail, EstimatedRows FROM _SchemaSmith_Destructive;

    IF EXISTS (SELECT 1 FROM _SchemaSmith_Destructive WHERE Disposition = 'refused') THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        SELECT CONNECTION_ID(), CONCAT('  Destructive change refused: ',
                                       CASE Kind WHEN 'table drop' THEN CONCAT('table ', ObjectName, ' dropped')
                                                 WHEN 'column drop' THEN CONCAT('column ', ObjectName, ' dropped')
                                                 ELSE CONCAT(Kind, ' ', ObjectName, ' ', COALESCE(Detail, '')) END,
                                       IF(EstimatedRows IS NULL, '', CONCAT(' (about ', EstimatedRows, ' rows)')))
          FROM _SchemaSmith_Destructive WHERE Disposition = 'refused';
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Destructive;
        -- Keep < 128 chars: MySQL refuses an over-long MESSAGE_TEXT. SchemaQuench reports the full list from the audit.
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Destructive change refused (DestructiveChangePolicy is fail) -- see the run log.';
    END IF;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Destructive;
END //

DELIMITER ;
