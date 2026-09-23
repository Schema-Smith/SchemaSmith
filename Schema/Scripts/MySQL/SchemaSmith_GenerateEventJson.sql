-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP PROCEDURE IF EXISTS `SchemaSmith_GenerateEventJSON`;

DELIMITER //

CREATE PROCEDURE `SchemaSmith_GenerateEventJSON`(
    IN p_Schema VARCHAR(200),
    IN p_Event VARCHAR(64)
)
BEGIN
    -- Extracts one scheduled event as the DECLARATIVE package form.
    --
    -- A kindled script rather than inline SQL in SchemaTongs, matching GenerateTableJson and
    -- GenerateMaterializedViewJson -- which also means the translation below can be certified against a
    -- live server instead of only exercised through the whole cast pipeline.
    --
    -- THE TRANSLATION IS THE POINT. The catalog reports these in a different vocabulary from the DDL an
    -- author writes, and the package uses the DDL spelling:
    --   STATUS        ENABLED / DISABLED / SLAVESIDE_DISABLED  ->  ENABLE / DISABLE / DISABLE ON SLAVE
    --   ON_COMPLETION 'PRESERVE' / 'NOT PRESERVE'              ->  a bool
    --   interval      INTERVAL_VALUE + INTERVAL_FIELD          ->  one string, "1 DAY"
    --   EVENT_TYPE    RECURRING / ONE TIME                     ->  EVERY / AT
    --
    -- STARTS IS DELIBERATELY NOT EMITTED. The server MATERIALISES it to the creation time when it was
    -- not specified, so capturing it would pin the event to whenever it happened to be created -- and
    -- every later deploy would then see drift, drop the event and recreate it, RESETTING ITS SCHEDULE
    -- each time. A nightly job would walk forward on every deploy and nothing would look wrong. An
    -- author who genuinely wants a fixed start writes Starts themselves.
    SELECT JSON_REMOVE(
        JSON_OBJECT(
            'Name', e.EVENT_NAME,
            'Definition', e.EVENT_DEFINITION,
            'ScheduleType', CASE WHEN e.EVENT_TYPE = 'ONE TIME' THEN 'AT' ELSE 'EVERY' END,
            'Interval', CASE WHEN e.INTERVAL_VALUE IS NULL THEN NULL
                             ELSE CONCAT(e.INTERVAL_VALUE, ' ', e.INTERVAL_FIELD) END,
            'ExecuteAt', CAST(e.EXECUTE_AT AS CHAR),
            'Ends', CAST(e.ENDS AS CHAR),
            'Status', CASE e.STATUS WHEN 'ENABLED' THEN 'ENABLE'
                                    WHEN 'DISABLED' THEN 'DISABLE'
                                    WHEN 'SLAVESIDE_DISABLED' THEN 'DISABLE ON SLAVE'
                                    -- MySQL 8.4 renamed this catalog value as part of its SLAVE ->
                                    -- REPLICA terminology change. Both spellings mean the same state and
                                    -- both map to the same DDL, which 8.4 still accepts, so the package
                                    -- keeps one portable spelling and an extract taken on either version
                                    -- deploys to the other. MariaDB still reports the older value.
                                    WHEN 'REPLICA_SIDE_DISABLED' THEN 'DISABLE ON SLAVE'
                                    ELSE e.STATUS END,
            -- A BARE COMPARISON, not CASE ... THEN TRUE. The engines disagree on what that produces:
            -- MySQL emits 1 for it, which is not a JSON boolean, and the generated .json-schema declares
            -- this property as "type": "boolean" -- so --Validate would reject the package extraction
            -- just wrote. CAST(... AS JSON) fixes it on MySQL but does not exist on MariaDB. The
            -- comparison form yields a real boolean on both. Verified on 8.0 and 11.4.
            'Preserve', e.ON_COMPLETION = 'PRESERVE',
            'Comment', CASE WHEN e.EVENT_COMMENT = '' THEN NULL ELSE e.EVENT_COMMENT END
        ),
        -- Strip what does not apply, so a recurring event carries no ExecuteAt and a one-shot no
        -- Interval. Leaving them as null would put keys in the package that its schema does not want,
        -- and Interval/Ends are non-nullable-free strings the author never wrote.
        CASE WHEN e.INTERVAL_VALUE IS NULL THEN '$.Interval' ELSE '$.___dummy___' END,
        CASE WHEN e.EXECUTE_AT IS NULL THEN '$.ExecuteAt' ELSE '$.___dummy___' END,
        CASE WHEN e.ENDS IS NULL THEN '$.Ends' ELSE '$.___dummy___' END,
        CASE WHEN COALESCE(e.EVENT_COMMENT, '') = '' THEN '$.Comment' ELSE '$.___dummy___' END
    ) AS EventJson
    FROM INFORMATION_SCHEMA.EVENTS e
    WHERE e.EVENT_SCHEMA = p_Schema
      AND e.EVENT_NAME = p_Event;
END //

DELIMITER ;
