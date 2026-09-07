-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_EventQuench`;

DELIMITER //

CREATE PROCEDURE `SchemaSmith_EventQuench`(
    IN p_ProductName VARCHAR(50),
    IN p_DatabaseName VARCHAR(200),
    IN p_EventDefinitions LONGTEXT,
    IN p_WhatIf TINYINT,
    IN p_DropEventsRemovedFromProduct TINYINT,
    IN p_TemplateName VARCHAR(256)
)
BEGIN
    -- Converges declared scheduled events, and RETURNS THE STATEMENTS TO RUN rather than running them.
    --
    -- WHY THIS PROCEDURE IS SHAPED UNLIKE EVERY OTHER QUENCH. The others build DDL as a string and run it
    -- with PREPARE/EXECUTE. That is impossible for events: MySQL cannot prepare event DDL at all -- both
    -- CREATE EVENT and DROP EVENT fail with 1295, "This command is not supported in the prepared statement
    -- protocol yet". MariaDB CAN prepare them, so the limit is MySQL-only, and writing to the lower common
    -- denominator is what keeps ONE implementation for both engines instead of two.
    --
    -- The decision-making therefore stays here in SQL and the caller is a dumb executor: this returns an
    -- ORDERED list of statements and SchemaQuench runs them in sequence. The ownership and audit writes
    -- are IN that list rather than done here, which is what makes failure safe -- if a CREATE fails,
    -- execution stops and the ownership row that would have claimed it is never written.
    --
    -- WHAT CHANGES BY DECLARING AN EVENT. As a scripted object it was re-run every deploy (DROP then
    -- CREATE), never compared, and never removed when it left the package -- so a retired event kept
    -- firing until someone dropped it by hand. Declared, it is compared, converges, and can be dropped by
    -- absence.
    --
    -- SCRIPTED EVENTS ARE UNTOUCHED. A .sql file in Events/ still runs through the Objects slot exactly as
    -- before, and can never be dropped by absence here because it has no ownership row.
    --
    -- No version gate: events predate both floors (MySQL 5.7, MariaDB 10.2) and INFORMATION_SCHEMA.EVENTS
    -- is identical on both.
    DECLARE v_idx INT DEFAULT 0;
    DECLARE v_count INT DEFAULT 0;

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_Events;
    -- No ENGINE=MEMORY: it cannot hold BLOB/TEXT, and three of these columns are TEXT.
    CREATE TEMPORARY TABLE _SchemaSmith_Events (
        Id INT AUTO_INCREMENT PRIMARY KEY,
        Name VARCHAR(64) NOT NULL,
        Definition LONGTEXT,
        ScheduleType VARCHAR(10),
        `Interval` VARCHAR(64),
        ExecuteAt VARCHAR(64),
        Starts VARCHAR(64),
        Ends VARCHAR(64),
        Status VARCHAR(20),
        Preserve TINYINT,
        Comment TEXT,
        ShouldApply TINYINT DEFAULT 1,
        Changed TINYINT DEFAULT 0,
        DdlScript LONGTEXT
    );

    DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_EventStatements;
    -- Seq is assigned explicitly rather than auto-incremented, because the statements are inserted in
    -- FOUR passes (see below) and the order that matters is per-EVENT, not per-pass.
    CREATE TEMPORARY TABLE _SchemaSmith_EventStatements (
        Seq INT NOT NULL PRIMARY KEY,
        Statement LONGTEXT
    );

    SET v_count = COALESCE(JSON_LENGTH(p_EventDefinitions), 0);
    WHILE v_idx < v_count DO
        INSERT INTO _SchemaSmith_Events (Name, Definition, ScheduleType, `Interval`, ExecuteAt, Starts, Ends,
                                         Status, Preserve, Comment)
        SELECT SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Name'))),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Definition'))),
               UPPER(COALESCE(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].ScheduleType'))), 'EVERY')),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Interval'))),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].ExecuteAt'))),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Starts'))),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Ends'))),
               UPPER(COALESCE(SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Status'))), 'ENABLE')),
               COALESCE(SchemaSmith_JsonScalarInt(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Preserve'))), 0),
               SchemaSmith_JsonScalarStr(JSON_EXTRACT(p_EventDefinitions, CONCAT('$[', v_idx, '].Comment')));
        SET v_idx = v_idx + 1;
    END WHILE;

    -- Build the CREATE once, so the text that is COMPARED and the text that is EMITTED cannot drift apart.
    -- Rebuilding the string separately in two places is how an object ends up churning forever.
    UPDATE _SchemaSmith_Events
    SET DdlScript = CONCAT(
        'CREATE EVENT `', p_DatabaseName, '`.`', Name, '` ON SCHEDULE ',
        CASE WHEN ScheduleType = 'AT'
             THEN CONCAT('AT ''', COALESCE(ExecuteAt, ''), '''')
             ELSE CONCAT('EVERY ', COALESCE(`Interval`, '1 DAY'),
                         CASE WHEN NULLIF(Starts, '') IS NOT NULL THEN CONCAT(' STARTS ''', Starts, '''') ELSE '' END,
                         CASE WHEN NULLIF(Ends, '') IS NOT NULL THEN CONCAT(' ENDS ''', Ends, '''') ELSE '' END)
             END,
        ' ON COMPLETION ', CASE WHEN Preserve = 1 THEN 'PRESERVE' ELSE 'NOT PRESERVE' END,
        ' ', CASE Status WHEN 'DISABLE' THEN 'DISABLE'
                         WHEN 'DISABLE ON SLAVE' THEN 'DISABLE ON SLAVE'
                         ELSE 'ENABLE' END,
        CASE WHEN NULLIF(Comment, '') IS NOT NULL
             THEN CONCAT(' COMMENT ''', REPLACE(Comment, '''', ''''''), '''') ELSE '' END,
        ' DO ', COALESCE(Definition, ''));

    -- Decide "changed" ONCE and store it. Calling the comparison inline in four places would re-evaluate
    -- it against a catalog that the earlier statements have already altered.
    UPDATE _SchemaSmith_Events
    SET Changed = CASE WHEN SchemaSmith_EventMatches(p_DatabaseName, Name, ScheduleType, `Interval`,
                                                     ExecuteAt, Starts, Ends, Status, Preserve,
                                                     Comment, Definition) = 1 THEN 0 ELSE 1 END;

    -- ---- create or replace ------------------------------------------------------------------------
    -- Converging means DROP then CREATE: ALTER EVENT cannot change every attribute and CREATE OR REPLACE
    -- EVENT is MariaDB-only. Safe for an event, which carries no data -- but NOT free, because it resets
    -- the schedule. A nightly job would be pushed past its window on every deploy if this fired when
    -- nothing had actually changed, which is the whole reason SchemaSmith_EventMatches exists.
    IF p_WhatIf = 1 THEN
        INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
            SELECT CONNECTION_ID(), 'event', CONCAT(p_DatabaseName, '.', Name), 'wouldModify'
              FROM _SchemaSmith_Events WHERE ShouldApply = 1 AND Changed = 1;
    ELSE
        -- FOUR SEPARATE INSERTS, not one UNION ALL. MySQL cannot reference the same TEMPORARY table
        -- twice in a single statement -- error 1137, "Can't reopen table" -- so the obvious single
        -- query is not available here.
        --
        -- Seq is Id*10 + phase, which keeps each event's DROP, CREATE, ownership and audit ADJACENT.
        -- Ordering by phase across all events instead would be wrong in a specific and nasty way: if a
        -- CREATE failed partway, every later event would already have been DROPPED and never recreated.
        INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
            SELECT Id * 10 + 1, CONCAT('DROP EVENT IF EXISTS `', p_DatabaseName, '`.`', Name, '`')
              FROM _SchemaSmith_Events WHERE ShouldApply = 1 AND Changed = 1;

        INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
            SELECT Id * 10 + 2, DdlScript
              FROM _SchemaSmith_Events WHERE ShouldApply = 1 AND Changed = 1;

        -- Ownership AFTER the create, so a failed create never leaves a claim on an event that does not
        -- exist. Recorded for every declared event, not only changed ones, so adopting one that already
        -- matched still records who owns it.
        INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
            -- Every interpolated value is quote-escaped (doubled '') the same way the Comment field is
            -- above: a database, event, product or template name containing a single quote would
            -- otherwise break -- or inject into -- this generated INSERT.
            SELECT Id * 10 + 3,
                   CONCAT('INSERT INTO SchemaSmith_ProductOwnership (ObjectType, ObjectSchema, ObjectName, ProductName, TemplateName) ',
                          'SELECT ''EVENT'', ''', REPLACE(p_DatabaseName, '''', ''''''), ''', ''', REPLACE(Name, '''', ''''''), ''', ''', REPLACE(p_ProductName, '''', ''''''), ''', ''', REPLACE(COALESCE(p_TemplateName, ''), '''', ''''''), ''' ',
                          'FROM DUAL WHERE NOT EXISTS (SELECT 1 FROM SchemaSmith_ProductOwnership po WHERE po.ObjectType = ''EVENT'' ',
                          'AND CONVERT(po.ObjectSchema USING utf8mb4) COLLATE utf8mb4_general_ci = ''', REPLACE(p_DatabaseName, '''', ''''''), ''' ',
                          'AND CONVERT(po.ObjectName USING utf8mb4) COLLATE utf8mb4_general_ci = ''', REPLACE(Name, '''', ''''''), ''')')
              FROM _SchemaSmith_Events WHERE ShouldApply = 1;

        INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
            SELECT Id * 10 + 4,
                   CONCAT('INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) ',
                          'VALUES (CONNECTION_ID(), ''event'', ''', p_DatabaseName, '.', Name, ''', ''created'')')
              FROM _SchemaSmith_Events WHERE ShouldApply = 1 AND Changed = 1;
    END IF;

    -- ---- drop by absence --------------------------------------------------------------------------
    -- Opt-in, and scoped to events SchemaSmith OWNS. An event created by hand -- or by a scripted Events/
    -- .sql file, which is still fully supported -- has no ownership row and is invisible here. Without
    -- that scoping, turning the flag on would delete every event the package happens not to mention.
    --
    -- Every comparison over a stored string is CONVERT(x USING utf8mb4) COLLATE utf8mb4_general_ci, and it
    -- needs BOTH halves. Two different engines break it two different ways:
    --   * MariaDB 11.4: the temp table takes the DATABASE default (utf8mb4_uca1400_ai_ci) while the kindled
    --     tables are utf8mb4_unicode_ci, so a column-to-column compare fails 1267 -- the COLLATE unifies it.
    --   * MySQL 5.7: the default charset is latin1, so a bare `COLLATE utf8mb4_general_ci` over a latin1
    --     value is error 1253 ("not valid for CHARACTER SET 'latin1'") -- the CONVERT lifts it to utf8mb4
    --     first so the collation is legal. This was the original bug: the COLLATE alone shipped and the
    --     event drop path failed on the 5.7 floor.
    -- COLLATE without CONVERT fixes only 11.4; CONVERT without COLLATE fixes only the parameter case. The
    -- pair is what makes the drop path work on every supported MySQL and MariaDB.
    IF p_DropEventsRemovedFromProduct = 1 THEN
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_EventsToDrop;
        CREATE TEMPORARY TABLE _SchemaSmith_EventsToDrop (Id INT AUTO_INCREMENT PRIMARY KEY, Name VARCHAR(64) NOT NULL);
        INSERT INTO _SchemaSmith_EventsToDrop (Name)
            SELECT po.ObjectName
              FROM SchemaSmith_ProductOwnership po
             WHERE po.ObjectType = 'EVENT'
               AND CONVERT(po.ObjectSchema USING utf8mb4) COLLATE utf8mb4_general_ci = p_DatabaseName
               AND CONVERT(po.ProductName USING utf8mb4) COLLATE utf8mb4_general_ci = p_ProductName
               AND NOT EXISTS (SELECT 1 FROM _SchemaSmith_Events e
                                WHERE CONVERT(e.Name USING utf8mb4) COLLATE utf8mb4_general_ci = CONVERT(po.ObjectName USING utf8mb4) COLLATE utf8mb4_general_ci
                                  AND e.ShouldApply = 1);

        IF p_WhatIf = 1 THEN
            INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType)
                SELECT CONNECTION_ID(), 'event', CONCAT(p_DatabaseName, '.', Name), 'wouldDrop'
                  FROM _SchemaSmith_EventsToDrop;
        ELSE
            -- Same 1137 constraint as above: one reference per statement, explicit Seq. Offset well
            -- past the create range so drops always follow creates.
            SET @ss_ev_base = 1000000;
            INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
                SELECT @ss_ev_base + Id * 10 + 1, CONCAT('DROP EVENT IF EXISTS `', p_DatabaseName, '`.`', Name, '`')
                  FROM _SchemaSmith_EventsToDrop;

            INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
                SELECT @ss_ev_base + Id * 10 + 2,
                       CONCAT('DELETE FROM SchemaSmith_ProductOwnership WHERE ObjectType = ''EVENT'' ',
                              'AND CONVERT(ObjectSchema USING utf8mb4) COLLATE utf8mb4_general_ci = ''', p_DatabaseName, ''' ',
                              'AND CONVERT(ObjectName USING utf8mb4) COLLATE utf8mb4_general_ci = ''', Name, '''')
                  FROM _SchemaSmith_EventsToDrop;

            INSERT INTO _SchemaSmith_EventStatements (Seq, Statement)
                SELECT @ss_ev_base + Id * 10 + 3,
                       CONCAT('INSERT INTO SchemaSmith_ChangeAudit (SessionId, ObjectType, ObjectName, ActionType) ',
                              'VALUES (CONNECTION_ID(), ''event'', ''', p_DatabaseName, '.', Name, ''', ''dropped'')')
                  FROM _SchemaSmith_EventsToDrop;
        END IF;
        DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_EventsToDrop;
    END IF;

    -- The caller executes these in order. An empty set means nothing to do, the common case.
    SELECT Statement FROM _SchemaSmith_EventStatements ORDER BY Seq;
END //

DELIMITER ;
