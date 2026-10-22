-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ExpressionMapAdopt//

-- #508, MySQL and MariaDB. The expression map only knows what SchemaSmith applied, so a database another tool built --
-- or a declaration reworded without changing its meaning -- has every expression whose stored text differs from the
-- declared text re-applied once: a CHECK dropped and re-added, a generated column or an expression default rewritten.
-- Before that happens, the DECLARED expression is applied to an empty copy of the table, with the same MODIFY COLUMN /
-- ADD CHECK the deploy would run; if the engine stores it as exactly the live text, the live object IS that expression,
-- and the map row the recorder would write is written now. Anything uncertain -- a different rendering, a probe error
-- -- falls back to applying, as before. It runs before any table or column is created, so only objects that already
-- exist are candidates.
--
-- There is no transactional DDL here, and INFORMATION_SCHEMA does not describe a TEMPORARY table, so the copy is a
-- real table in the same database, dropped straight after. That is why it does not run under WhatIf, which has never
-- written DDL: a preview reports such an object as it always has, as one it would apply.
CREATE PROCEDURE SchemaSmith_ExpressionMapAdopt(
    IN p_DatabaseName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT(1)
)
proc: BEGIN
    DECLARE v_WorkingSetMissing TINYINT DEFAULT 0;
    DECLARE v_Probe INT;
    DECLARE v_Done TINYINT DEFAULT 0;
    DECLARE v_Table VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Kind VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Name VARCHAR(128) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Slot VARCHAR(32) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Authored TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Live TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Render TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_ReadKind VARCHAR(16) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Adopted INT DEFAULT 0;
    DECLARE v_ProbeTable VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE v_Qualified TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;
    DECLARE cur CURSOR FOR
        SELECT TableName, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render, ReadKind
          FROM _SchemaSmith_EmCandidates ORDER BY Id;
    DECLARE CONTINUE HANDLER FOR NOT FOUND SET v_Done = 1;

    IF p_WhatIf = 1 THEN
        LEAVE proc;
    END IF;
    BEGIN
        DECLARE CONTINUE HANDLER FOR 1146 SET v_WorkingSetMissing = 1;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_Columns;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_CheckConstraints;
    END;
    IF v_WorkingSetMissing = 1 THEN
        LEAVE proc;
    END IF;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_EmCandidates;
    CREATE TEMPORARY TABLE _SchemaSmith_EmCandidates (
        Id INT AUTO_INCREMENT PRIMARY KEY,
        TableName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        ObjectKind VARCHAR(32) COLLATE utf8mb4_bin NOT NULL,
        ObjectName VARCHAR(128) COLLATE utf8mb4_bin NOT NULL,
        Slot VARCHAR(32) COLLATE utf8mb4_bin NOT NULL,
        AuthoredText TEXT COLLATE utf8mb4_bin NOT NULL,
        LiveText TEXT COLLATE utf8mb4_bin NOT NULL,
        -- The DDL the deploy would run, against the copy ({probe}), and which catalog text to read back.
        Render TEXT COLLATE utf8mb4_bin NOT NULL,
        ReadKind VARCHAR(16) COLLATE utf8mb4_bin NOT NULL
    ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

    -- Each block takes the live text exactly as SchemaSmith_ExpressionMapRecord reads it, and only where the compare
    -- that follows would apply: no row vouches for it, and the texts differ.
    CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
    -- Names compare through their keys; the schema goes in through user variables (a case-insensitive prefilter, then
    -- the exact key), so the catalog's own spelling never has to match the configured one.
    SET @v_eaDbName = p_DatabaseName;
    SET @v_eaDbCi = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_general_ci;
    SET @v_eaDbKey = SchemaSmith_IdentifierKey(p_DatabaseName);

    -- Generated columns.
    INSERT INTO _SchemaSmith_EmCandidates (TableName, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render, ReadKind)
    SELECT SchemaSmith_StripBacktickWrapping(c.TableName), 'COLUMN', SchemaSmith_StripBacktickWrapping(c.ColumnName), 'generated',
           c.GeneratedExpression, isc.GENERATION_EXPRESSION, CONCAT('ALTER TABLE {probe} MODIFY COLUMN ', c.ColumnScript), 'generated'
      FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey
     WHERE IFNULL(TRIM(c.GeneratedExpression), '') != '' AND IFNULL(isc.GENERATION_EXPRESSION, '') != ''
       AND CAST(TRIM(isc.GENERATION_EXPRESSION) AS BINARY) != CAST(TRIM(c.GeneratedExpression) AS BINARY)
       AND SchemaSmith_ExpressionMapUnchanged(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), 'COLUMN',
             SchemaSmith_StripBacktickWrapping(c.ColumnName), 'generated', c.GeneratedExpression, isc.GENERATION_EXPRESSION) = 0;

    -- Expression defaults: the recorder's own test for which defaults are expressions.
    INSERT INTO _SchemaSmith_EmCandidates (TableName, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render, ReadKind)
    SELECT SchemaSmith_StripBacktickWrapping(c.TableName), 'COLUMN', SchemaSmith_StripBacktickWrapping(c.ColumnName), 'default',
           c.DefaultValue, isc.COLUMN_DEFAULT, CONCAT('ALTER TABLE {probe} MODIFY COLUMN ', c.ColumnScript), 'default'
      FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc ON isc.TableKey = c.TableKey AND isc.ColumnKey = c.ColumnKey
     WHERE IFNULL(TRIM(c.DefaultValue), '') != '' AND isc.COLUMN_DEFAULT IS NOT NULL
       AND LEFT(TRIM(c.DefaultValue), 1) NOT IN ('''', '"')
       AND NOT (TRIM(c.DefaultValue) REGEXP '^-?[0-9]')
       AND SchemaSmith_IsExpressionDefault(isc.COLUMN_DEFAULT, isc.EXTRA) = 1
       AND SchemaSmith_ExpressionDefaultsEqual(isc.COLUMN_DEFAULT, isc.EXTRA, c.DefaultValue) = 0
       AND SchemaSmith_ExpressionMapUnchanged(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), 'COLUMN',
             SchemaSmith_StripBacktickWrapping(c.ColumnName), 'default', c.DefaultValue, isc.COLUMN_DEFAULT) = 0;

    IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
        -- MariaDB column-level checks, recorded under the column's name, which is also the constraint's.
        IF VERSION() LIKE '%MariaDB%' THEN
            INSERT INTO _SchemaSmith_EmCandidates (TableName, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render, ReadKind)
            SELECT SchemaSmith_StripBacktickWrapping(c.TableName), 'CHECK', SchemaSmith_StripBacktickWrapping(c.ColumnName), 'expression',
                   c.CheckExpression,
                   SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName))),
                   CONCAT('ALTER TABLE {probe} MODIFY COLUMN ', c.ColumnScript), 'colcheck'
              FROM _SchemaSmith_Columns c
             WHERE IFNULL(TRIM(c.CheckExpression), '') != ''
               AND SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) IS NOT NULL
               AND CAST(SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName))) AS BINARY)
                   != CAST(SchemaSmith_NormalizeCheckExpression(c.CompareCheckExpression) AS BINARY)
               AND SchemaSmith_ExpressionMapUnchanged(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), 'CHECK',
                     SchemaSmith_StripBacktickWrapping(c.ColumnName), 'expression', c.CheckExpression,
                     SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)))) = 0;
        END IF;

        -- Table-level checks. CHECK_CONSTRAINTS is named only inside a PREPAREd string: on MySQL 5.7 the mention alone
        -- is fatal at CREATE PROCEDURE time.
        SET @v_eaSql = CONCAT('INSERT INTO _SchemaSmith_EmCandidates (TableName, ObjectKind, ObjectName, Slot, AuthoredText, LiveText, Render, ReadKind)
        SELECT SchemaSmith_StripBacktickWrapping(c.TableName), ''CHECK'', SchemaSmith_StripBacktickWrapping(c.ConstraintName), ''expression'',
               c.Expression, SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)),
               CONCAT(''ALTER TABLE {probe} ADD CONSTRAINT {check} CHECK ('', c.Expression, '')''), ''check''
          FROM _SchemaSmith_CheckConstraints c
          JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            ON tc.TABLE_SCHEMA = @v_eaDbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = @v_eaDbKey
           AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = c.TableKey
           AND SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME) = c.ConstraintKey
           AND tc.CONSTRAINT_TYPE = ''CHECK''
          JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
            ON cc.CONSTRAINT_SCHEMA = @v_eaDbCi AND SchemaSmith_IdentifierKey(cc.CONSTRAINT_SCHEMA) = @v_eaDbKey
           AND SchemaSmith_NameKeyCI(cc.CONSTRAINT_NAME) = c.ConstraintKey', SchemaSmith_CheckTableJoin(), '
         WHERE IFNULL(TRIM(c.Expression), '''') != ''''
           AND CAST(SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)) AS BINARY) != CAST(SchemaSmith_NormalizeCheckExpression(c.Expression) AS BINARY)
           AND SchemaSmith_ExpressionMapUnchanged(@v_eaDbName, SchemaSmith_StripBacktickWrapping(c.TableName), ''CHECK'',
                 SchemaSmith_StripBacktickWrapping(c.ConstraintName), ''expression'', c.Expression,
                 SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4))) = 0');
        PREPARE stmt FROM @v_eaSql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;

    SET v_ProbeTable = CONCAT('_schemasmith_probe_', CONNECTION_ID());
    SET @v_eaProbeCi = CONVERT(v_ProbeTable USING utf8mb4) COLLATE utf8mb4_general_ci;
    SET v_Qualified = CONCAT('`', REPLACE(p_DatabaseName, '`', '``'), '`.`', v_ProbeTable, '`');
    SET @v_eaSql = CONCAT('DROP TABLE IF EXISTS ', v_Qualified);
    PREPARE stmt FROM @v_eaSql;
    EXECUTE stmt;
    DEALLOCATE PREPARE stmt;

    OPEN cur;
    probe_loop: LOOP
        FETCH cur INTO v_Table, v_Kind, v_Name, v_Slot, v_Authored, v_Live, v_Render, v_ReadKind;
        IF v_Done = 1 THEN
            LEAVE probe_loop;
        END IF;

        SET @v_eaRendered = NULL;
        BEGIN
            DECLARE EXIT HANDLER FOR SQLEXCEPTION SET @v_eaRendered = NULL;
            SET @v_eaSql = CONCAT('CREATE TABLE ', v_Qualified, ' LIKE `', REPLACE(p_DatabaseName, '`', '``'), '`.`', REPLACE(v_Table, '`', '``'), '`');
            PREPARE stmt FROM @v_eaSql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
            SET @v_eaSql = REPLACE(REPLACE(v_Render, '{probe}', v_Qualified), '{check}', CONCAT('`', v_ProbeTable, '_ck`'));
            PREPARE stmt FROM @v_eaSql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;

            IF v_ReadKind = 'generated' OR v_ReadKind = 'default' THEN
                SELECT IF(v_ReadKind = 'generated', GENERATION_EXPRESSION, COLUMN_DEFAULT) INTO @v_eaRendered
                  FROM INFORMATION_SCHEMA.COLUMNS
                 WHERE TABLE_SCHEMA = @v_eaDbCi AND SchemaSmith_IdentifierKey(TABLE_SCHEMA) = @v_eaDbKey
                   AND TABLE_NAME = @v_eaProbeCi
                   AND SchemaSmith_NameKeyCI(COLUMN_NAME) = SchemaSmith_NameKeyCI(v_Name);
            ELSEIF v_ReadKind = 'colcheck' THEN
                SET @v_eaRendered = SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_DatabaseName, v_ProbeTable, v_Name));
            ELSE
                SET @v_eaProbeCheck = CONCAT(v_ProbeTable, '_ck');
                SET @v_eaSql = 'SELECT SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)) INTO @v_eaRendered
                                  FROM INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
                                 WHERE cc.CONSTRAINT_SCHEMA = @v_eaDbCi AND SchemaSmith_IdentifierKey(cc.CONSTRAINT_SCHEMA) = @v_eaDbKey
                                   AND SchemaSmith_NameKeyCI(cc.CONSTRAINT_NAME) = SchemaSmith_NameKeyCI(@v_eaProbeCheck)';
                PREPARE stmt FROM @v_eaSql;
                EXECUTE stmt;
                DEALLOCATE PREPARE stmt;
            END IF;
        END;
        BEGIN
            DECLARE CONTINUE HANDLER FOR SQLEXCEPTION BEGIN END;
            SET @v_eaSql = CONCAT('DROP TABLE IF EXISTS ', v_Qualified);
            PREPARE stmt FROM @v_eaSql;
            EXECUTE stmt;
            DEALLOCATE PREPARE stmt;
        END;
        -- An EXIT handler leaves the FETCH's NOT FOUND flag alone, but a failed read can set it; the cursor decides.
        SET v_Done = 0;

        -- Byte for byte.
        IF @v_eaRendered IS NOT NULL AND CAST(@v_eaRendered AS BINARY) = CAST(v_Live AS BINARY) THEN
            INSERT INTO SchemaSmith_ExpressionMap
                (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
                 PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
            VALUES (p_DatabaseName, v_Table, v_Kind, v_Name, v_Slot, v_Authored, v_Live, 'MySQL', VERSION(), NULL, UTC_TIMESTAMP(3))
                ON DUPLICATE KEY UPDATE
                   AuthoredText = VALUES(AuthoredText),
                   CanonicalText = VALUES(CanonicalText),
                   EngineVersion = VALUES(EngineVersion),
                   UpdatedUtc = VALUES(UpdatedUtc);
            SET v_Adopted = v_Adopted + 1;
        END IF;
    END LOOP;
    CLOSE cur;

    IF v_Adopted > 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Verified ', v_Adopted,
                ' expression(s) on a scratch copy of their table: the declared text renders to exactly what the database holds, so they are recorded rather than re-applied.'));
    END IF;
    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_EmCandidates;
END//

DELIMITER ;
