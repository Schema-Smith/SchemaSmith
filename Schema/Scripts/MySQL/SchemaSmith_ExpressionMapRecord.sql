-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ExpressionMapRecord//

CREATE PROCEDURE SchemaSmith_ExpressionMapRecord(
    IN p_DatabaseName VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    IN p_WhatIf TINYINT(1)
)
proc: BEGIN
    DECLARE v_WorkingSetMissing TINYINT DEFAULT 0;
    DECLARE v_Probe INT;

    -- An index-only quench, or a package with no tables, never builds the declared working set, so there is nothing
    -- to record. Probed up front rather than by a handler over the whole body: such a handler also swallowed the
    -- failed PREPARE below, and the EXECUTE after it then failed with "Unknown prepared statement handler".
    -- 1146 is "table doesn't exist".
    BEGIN
        DECLARE CONTINUE HANDLER FOR 1146 SET v_WorkingSetMissing = 1;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_Columns;
        SELECT COUNT(*) INTO v_Probe FROM _SchemaSmith_CheckConstraints;
    END;
    IF v_WorkingSetMissing = 1 THEN
        LEAVE proc;
    END IF;

    -- #242. Records what was applied for every expression-bearing object this run declared: the authored text,
    -- the canonical text the engine reports NOW, and the engine version that decides canonicalisation.
    -- SchemaSmith_ExpressionMapUnchanged reads it on the next deploy.
    --
    -- Runs after the apply passes, so an object created moments ago is recorded on this run rather than
    -- churning once more on the next. Re-baselining is the same statement: a row whose engine version no
    -- longer matches is overwritten with the current reading, without touching the object.
    --
    -- The live check text is read through the SAME normalisation the comparison uses. If those two ever
    -- diverge every constraint would look changed forever, which is the defect this exists to remove.
    IF p_WhatIf = 1 THEN
        LEAVE proc;
    END IF;

    -- Say so when a re-baseline happens (Paul, 2026-09-08: "re-baseline, and say so in the log -- log the count so
    -- it is visible rather than silent"). A re-baseline is a row whose DECLARATION is unchanged but whose engine
    -- context moved -- an engine upgrade or a compatibility-level change. A row whose declaration also changed was
    -- APPLIED, not re-baselined, and is not counted.
    -- BINARY on every comparison: the map is utf8mb4_unicode_ci, VERSION() and the parameter carry the server
    -- default, and mixing them is an error rather than a wrong answer.
    SET @v_emRebaselined := (
        SELECT COUNT(*) FROM SchemaSmith_ExpressionMap em
         WHERE CAST(em.ObjectSchema AS BINARY) = CAST(p_DatabaseName AS BINARY)
           AND CAST(em.EngineVersion AS BINARY) != CAST(VERSION() AS BINARY)
           -- One visit of _SchemaSmith_Columns: a TEMPORARY table cannot be opened twice in a statement (1137), so the
           -- generated-expression branch and the MariaDB column-level check branch share it.
           AND (EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                         WHERE CAST(SchemaSmith_StripBacktickWrapping(c.TableName) AS BINARY) = CAST(em.ObjectTable AS BINARY)
                           AND CAST(SchemaSmith_StripBacktickWrapping(c.ColumnName) AS BINARY) = CAST(em.ObjectName AS BINARY)
                           AND ((em.ObjectKind = 'COLUMN' AND em.Slot = 'generated'
                                 AND CAST(c.GeneratedExpression AS BINARY) = CAST(em.AuthoredText AS BINARY))
                                OR (em.ObjectKind = 'COLUMN' AND em.Slot = 'default'
                                    AND CAST(c.DefaultValue AS BINARY) = CAST(em.AuthoredText AS BINARY))
                                -- a MariaDB column-level check is recorded under its column's name
                                OR (em.ObjectKind = 'CHECK' AND em.Slot = 'expression'
                                    AND CAST(c.CheckExpression AS BINARY) = CAST(em.AuthoredText AS BINARY))))
             OR (em.ObjectKind = 'CHECK' AND em.Slot = 'expression'
                 AND EXISTS (SELECT 1 FROM _SchemaSmith_CheckConstraints k
                              WHERE CAST(SchemaSmith_StripBacktickWrapping(k.TableName) AS BINARY) = CAST(em.ObjectTable AS BINARY)
                                AND CAST(SchemaSmith_StripBacktickWrapping(k.ConstraintName) AS BINARY) = CAST(em.ObjectName AS BINARY)
                                AND CAST(k.Expression AS BINARY) = CAST(em.AuthoredText AS BINARY)))));
    IF IFNULL(@v_emRebaselined, 0) > 0 THEN
        INSERT INTO SchemaSmith_StatusMessages (SessionId, Message)
        VALUES (CONNECTION_ID(), CONCAT('  Re-baselined ', @v_emRebaselined,
                ' recorded expression(s): they were recorded under a different server version, and their stored canonical text is now refreshed for ',
                VERSION(), '. No object was changed.'));
    END IF;

    -- Table-level check constraints. MySQL and MariaDB cannot attribute a check to a column, so table-level
    -- is the only form -- the column-level alias was retired in 2.7.0.
    --
    -- INFORMATION_SCHEMA.CHECK_CONSTRAINTS ARRIVED IN MySQL 8.0.16. On MySQL 5.7 the mention alone is fatal at
    -- CREATE PROCEDURE time -- the whole kindle dies and every deploy with it -- so a runtime version guard
    -- around a static reference would not save it. The table is named only inside a string that is PREPAREd,
    -- which is the same shape SchemaSmith_MissingIndexesAndConstraintsQuench uses for the same reason.
    IF SchemaSmith_SupportsCheckConstraints() = 1 THEN
        -- Names compare through their keys; the schema goes in through user variables (a case-insensitive prefilter,
        -- then the exact key), so the catalog's own spelling never has to match the configured one.
        SET @v_emDbCi = CONVERT(p_DatabaseName USING utf8mb4) COLLATE utf8mb4_general_ci;
        SET @v_emDbKey = SchemaSmith_IdentifierKey(p_DatabaseName);
        SET @v_emSql = CONCAT('INSERT INTO SchemaSmith_ExpressionMap
            (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
             PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
        SELECT ''', p_DatabaseName, ''',
               SchemaSmith_StripBacktickWrapping(c.TableName),
               ''CHECK'',
               SchemaSmith_StripBacktickWrapping(c.ConstraintName),
               ''expression'',
               c.Expression,
               SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)),
               ''MySQL'', VERSION(), NULL, UTC_TIMESTAMP(3)
          FROM _SchemaSmith_CheckConstraints c
          JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            ON tc.TABLE_SCHEMA = @v_emDbCi AND SchemaSmith_IdentifierKey(tc.TABLE_SCHEMA) = @v_emDbKey
           AND SchemaSmith_IdentifierKey(tc.TABLE_NAME) = c.TableKey
           AND SchemaSmith_NameKeyCI(tc.CONSTRAINT_NAME) = c.ConstraintKey
           AND tc.CONSTRAINT_TYPE = ''CHECK''
          JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
            ON cc.CONSTRAINT_SCHEMA = @v_emDbCi AND SchemaSmith_IdentifierKey(cc.CONSTRAINT_SCHEMA) = @v_emDbKey
           AND SchemaSmith_NameKeyCI(cc.CONSTRAINT_NAME) = c.ConstraintKey', SchemaSmith_CheckTableJoin(), '
         WHERE IFNULL(TRIM(c.Expression), '''') != ''''
            ON DUPLICATE KEY UPDATE
               AuthoredText = VALUES(AuthoredText),
               CanonicalText = VALUES(CanonicalText),
               EngineVersion = VALUES(EngineVersion),
               UpdatedUtc = VALUES(UpdatedUtc)');
        PREPARE stmt FROM @v_emSql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;

        -- MariaDB column-level checks, recorded under the column's name, which is also the constraint's.
        IF VERSION() LIKE '%MariaDB%' THEN
            INSERT INTO SchemaSmith_ExpressionMap
                (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
                 PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
            SELECT p_DatabaseName,
                   SchemaSmith_StripBacktickWrapping(c.TableName),
                   'CHECK',
                   SchemaSmith_StripBacktickWrapping(c.ColumnName),
                   'expression',
                   c.CheckExpression,
                   SchemaSmith_NormalizeCheckExpression(SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName))),
                   'MySQL', VERSION(), NULL, UTC_TIMESTAMP(3)
              FROM _SchemaSmith_Columns c
             WHERE IFNULL(TRIM(c.CheckExpression), '') != ''
               AND SchemaSmith_ColumnLevelCheck(p_DatabaseName, SchemaSmith_StripBacktickWrapping(c.TableName), SchemaSmith_StripBacktickWrapping(c.ColumnName)) IS NOT NULL
                ON DUPLICATE KEY UPDATE
                   AuthoredText = VALUES(AuthoredText),
                   CanonicalText = VALUES(CanonicalText),
                   EngineVersion = VALUES(EngineVersion),
                   UpdatedUtc = VALUES(UpdatedUtc);
        END IF;
        SET @v_emSql = 'SELECT 1';
        PREPARE stmt FROM @v_emSql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;

    -- Generated columns. Compared with a bare TRIM today, so any expression the engine reformats re-applies
    -- on every deploy -- and this surface had no idempotency coverage at all.
    CALL SchemaSmith_SnapshotCatalogColumns(p_DatabaseName);
    INSERT INTO SchemaSmith_ExpressionMap
        (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
         PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
    SELECT p_DatabaseName,
           SchemaSmith_StripBacktickWrapping(c.TableName),
           'COLUMN',
           SchemaSmith_StripBacktickWrapping(c.ColumnName),
           'generated',
           c.GeneratedExpression,
           IFNULL(isc.GENERATION_EXPRESSION, ''),
           'MySQL', VERSION(), NULL, UTC_TIMESTAMP(3)
      FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc
        ON isc.TableKey = c.TableKey
       AND isc.ColumnKey = c.ColumnKey
     WHERE IFNULL(TRIM(c.GeneratedExpression), '') != ''
        ON DUPLICATE KEY UPDATE
           AuthoredText = VALUES(AuthoredText),
           CanonicalText = VALUES(CanonicalText),
           EngineVersion = VALUES(EngineVersion),
           UpdatedUtc = VALUES(UpdatedUtc);

    -- Expression defaults (#520). A declaration carries the expression as written; the engine stores its own
    -- rendering -- a charset introducer and escaped quotes on MySQL, lower case and LOWER as lcase on MariaDB -- so
    -- the two texts never agree by themselves once the column exists. The catalog says which live defaults are
    -- expressions; a declared quoted literal or number is never recorded, since the plain comparison decides those.
    INSERT INTO SchemaSmith_ExpressionMap
        (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
         PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
    SELECT p_DatabaseName,
           SchemaSmith_StripBacktickWrapping(c.TableName),
           'COLUMN',
           SchemaSmith_StripBacktickWrapping(c.ColumnName),
           'default',
           c.DefaultValue,
           IFNULL(isc.COLUMN_DEFAULT, ''),
           'MySQL', VERSION(), NULL, UTC_TIMESTAMP(3)
      FROM _SchemaSmith_Columns c
      JOIN _SchemaSmith_CatColumns isc
        ON isc.TableKey = c.TableKey
       AND isc.ColumnKey = c.ColumnKey
     WHERE IFNULL(TRIM(c.DefaultValue), '') != ''
       AND LEFT(TRIM(c.DefaultValue), 1) NOT IN ('''', '"')
       AND NOT (TRIM(c.DefaultValue) REGEXP '^-?[0-9]')
       AND SchemaSmith_IsExpressionDefault(isc.COLUMN_DEFAULT, isc.EXTRA) = 1
        ON DUPLICATE KEY UPDATE
           AuthoredText = VALUES(AuthoredText),
           CanonicalText = VALUES(CanonicalText),
           EngineVersion = VALUES(EngineVersion),
           UpdatedUtc = VALUES(UpdatedUtc);
END//

DELIMITER ;
