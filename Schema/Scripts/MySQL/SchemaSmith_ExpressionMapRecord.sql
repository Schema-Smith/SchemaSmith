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

    -- Table-level check constraints. MySQL and MariaDB cannot attribute a check to a column, so table-level
    -- is the only form -- the column-level alias was retired in 2.7.0.
    INSERT INTO SchemaSmith_ExpressionMap
        (ObjectSchema, ObjectTable, ObjectKind, ObjectName, Slot, AuthoredText, CanonicalText,
         PlatformName, EngineVersion, CompatLevel, UpdatedUtc)
    SELECT p_DatabaseName,
           SchemaSmith_StripBacktickWrapping(c.TableName),
           'CHECK',
           SchemaSmith_StripBacktickWrapping(c.ConstraintName),
           'expression',
           c.Expression,
           SchemaSmith_NormalizeCheckExpression(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)),
           'MySQL', VERSION(), NULL, UTC_TIMESTAMP(3)
      FROM _SchemaSmith_CheckConstraints c
      JOIN INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        ON BINARY tc.TABLE_SCHEMA = BINARY p_DatabaseName
       AND BINARY tc.TABLE_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.TableName)
       AND BINARY tc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
       AND tc.CONSTRAINT_TYPE = 'CHECK'
      JOIN INFORMATION_SCHEMA.CHECK_CONSTRAINTS cc
        ON BINARY cc.CONSTRAINT_SCHEMA = BINARY p_DatabaseName
       AND BINARY cc.CONSTRAINT_NAME = BINARY SchemaSmith_StripBacktickWrapping(c.ConstraintName)
     WHERE IFNULL(TRIM(c.Expression), '') != ''
        ON DUPLICATE KEY UPDATE
           AuthoredText = VALUES(AuthoredText),
           CanonicalText = VALUES(CanonicalText),
           EngineVersion = VALUES(EngineVersion),
           UpdatedUtc = VALUES(UpdatedUtc);

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
