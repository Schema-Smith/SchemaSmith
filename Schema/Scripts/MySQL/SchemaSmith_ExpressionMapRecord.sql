-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP PROCEDURE IF EXISTS SchemaSmith_ExpressionMapRecord//

CREATE PROCEDURE SchemaSmith_ExpressionMapRecord(
    IN p_DatabaseName VARCHAR(64),
    IN p_WhatIf TINYINT(1)
)
proc: BEGIN
    -- An index-only quench never builds the declared working set; a missing temp table means there is nothing
    -- to record, not a failure. 1146 is "table doesn't exist".
    DECLARE CONTINUE HANDLER FOR 1146 BEGIN END;

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
         WHERE BINARY em.ObjectSchema = BINARY p_DatabaseName
           AND BINARY em.EngineVersion != BINARY VERSION()
           AND ((em.ObjectKind = 'COLUMN' AND em.Slot = 'generated'
                 AND EXISTS (SELECT 1 FROM _SchemaSmith_Columns c
                              WHERE BINARY SchemaSmith_StripBacktickWrapping(c.TableName) = BINARY em.ObjectTable
                                AND BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName) = BINARY em.ObjectName
                                AND BINARY c.GeneratedExpression = BINARY em.AuthoredText))
             OR (em.ObjectKind = 'CHECK' AND em.Slot = 'expression'
                 AND EXISTS (SELECT 1 FROM _SchemaSmith_CheckConstraints k
                              WHERE BINARY SchemaSmith_StripBacktickWrapping(k.TableName) = BINARY em.ObjectTable
                                AND BINARY SchemaSmith_StripBacktickWrapping(k.ConstraintName) = BINARY em.ObjectName
                                AND BINARY k.Expression = BINARY em.AuthoredText))));
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
            ON BINARY tc.TABLE_SCHEMA = BINARY ''', p_DatabaseName, '''
           AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
           AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
           AND tc.CONSTRAINT_TYPE = ''CHECK''
          JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
            ON BINARY cc.CONSTRAINT_SCHEMA = BINARY ''', p_DatabaseName, '''
           AND BINARY cc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
         WHERE IFNULL(TRIM(c.Expression), '''') != ''''
            ON DUPLICATE KEY UPDATE
               AuthoredText = VALUES(AuthoredText),
               CanonicalText = VALUES(CanonicalText),
               EngineVersion = VALUES(EngineVersion),
               UpdatedUtc = VALUES(UpdatedUtc)');
        PREPARE stmt FROM @v_emSql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;

    -- Generated columns. Compared with a bare TRIM today, so any expression the engine reformats re-applies
    -- on every deploy -- and this surface had no idempotency coverage at all.
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
      JOIN INFORMATION_SCHEMA.COLUMNS isc
        ON BINARY isc.TABLE_SCHEMA = BINARY p_DatabaseName
       AND BINARY isc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
       AND BINARY isc.COLUMN_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ColumnName)
     WHERE IFNULL(TRIM(c.GeneratedExpression), '') != ''
        ON DUPLICATE KEY UPDATE
           AuthoredText = VALUES(AuthoredText),
           CanonicalText = VALUES(CanonicalText),
           EngineVersion = VALUES(EngineVersion),
           UpdatedUtc = VALUES(UpdatedUtc);
END//

DELIMITER ;
