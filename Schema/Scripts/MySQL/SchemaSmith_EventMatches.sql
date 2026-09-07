-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_EventMatches`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_EventMatches`(
    p_Schema VARCHAR(200), p_Name VARCHAR(64),
    p_ScheduleType VARCHAR(10), p_Interval VARCHAR(64), p_ExecuteAt VARCHAR(64),
    p_Starts VARCHAR(64), p_Ends VARCHAR(64), p_Status VARCHAR(20),
    p_Preserve TINYINT, p_Comment TEXT, p_Definition LONGTEXT
)
RETURNS TINYINT
NOT DETERMINISTIC
READS SQL DATA
BEGIN
  -- 1 when the deployed event already matches the declaration, 0 when it differs or is absent.
  --
  -- WHY A FUNCTION RATHER THAN AN INLINE PREDICATE: converging an event means DROP + CREATE (ALTER EVENT
  -- cannot change every attribute, and CREATE OR REPLACE EVENT is MariaDB-only). A drop-and-recreate that
  -- fires when nothing changed is not merely wasteful -- it resets the event's schedule, so a nightly job
  -- could be pushed past its window on every deploy. Getting "unchanged" exactly right is the point.
  --
  -- THE CATALOG AND THE DDL DISAGREE ON SPELLING, and every one of these was read off a live server
  -- rather than assumed:
  --   STATUS        catalog ENABLED / DISABLED / SLAVESIDE_DISABLED   vs DDL ENABLE / DISABLE / DISABLE ON SLAVE
  --   ON_COMPLETION catalog 'PRESERVE' / 'NOT PRESERVE'               vs a bool in the package
  --   interval      catalog INTERVAL_VALUE + INTERVAL_FIELD, separate vs one string, "1 DAY"
  --   EVENT_TYPE    catalog 'RECURRING' / 'ONE TIME'                  vs DDL EVERY / AT
  -- Comparing either side raw would report a difference on every deploy and rebuild the event forever.
  DECLARE v_type VARCHAR(20);
  DECLARE v_interval VARCHAR(64);
  DECLARE v_at VARCHAR(64);
  DECLARE v_starts VARCHAR(64);
  DECLARE v_ends VARCHAR(64);
  DECLARE v_status VARCHAR(20);
  DECLARE v_preserve TINYINT;
  DECLARE v_comment TEXT;
  DECLARE v_def LONGTEXT;

  SELECT CASE WHEN EVENT_TYPE = 'ONE TIME' THEN 'AT' ELSE 'EVERY' END,
         CASE WHEN INTERVAL_VALUE IS NULL THEN NULL
              ELSE CONCAT(INTERVAL_VALUE, ' ', INTERVAL_FIELD) END,
         CAST(EXECUTE_AT AS CHAR),
         CAST(STARTS AS CHAR),
         CAST(ENDS AS CHAR),
         CASE STATUS WHEN 'ENABLED' THEN 'ENABLE'
                     WHEN 'DISABLED' THEN 'DISABLE'
                     WHEN 'SLAVESIDE_DISABLED' THEN 'DISABLE ON SLAVE'
                     ELSE STATUS END,
         CASE WHEN ON_COMPLETION = 'PRESERVE' THEN 1 ELSE 0 END,
         EVENT_COMMENT,
         EVENT_DEFINITION
    INTO v_type, v_interval, v_at, v_starts, v_ends, v_status, v_preserve, v_comment, v_def
    FROM INFORMATION_SCHEMA.EVENTS
   WHERE EVENT_SCHEMA = p_Schema
     AND EVENT_NAME = p_Name;

  IF v_type IS NULL THEN
    RETURN 0;
  END IF;

  IF UPPER(COALESCE(p_ScheduleType, 'EVERY')) <> v_type THEN RETURN 0; END IF;

  IF v_type = 'AT' THEN
    -- Only the execution time matters for a one-shot; interval/starts/ends are meaningless there and
    -- the catalog reports them NULL, so comparing them would fail against any package that set them.
    IF COALESCE(p_ExecuteAt, '') <> COALESCE(v_at, '') THEN RETURN 0; END IF;
  ELSE
    -- The interval is compared case-insensitively and whitespace-normalised: "1 DAY", "1  day" and
    -- "1 Day" are the same schedule, and rebuilding an event over its capitalisation would be absurd.
    -- Collapse ANY run of spaces to one (the swap trick: space -> '<>', cancel adjacent '><', '<>' -> space),
    -- not just a single doubled-space pass -- a non-recursive REPLACE('  ',' ') leaves "1   DAY" (3 spaces)
    -- as "1  DAY" and spuriously reports the event changed. Works on MySQL 5.7 (no REGEXP_REPLACE needed).
    IF UPPER(REPLACE(REPLACE(REPLACE(COALESCE(p_Interval, ''), ' ', '<>'), '><', ''), '<>', ' ')) <> UPPER(COALESCE(v_interval, '')) THEN RETURN 0; END IF;
    -- STARTS AND ENDS ARE ONLY COMPARED WHEN THE PACKAGE DECLARES THEM, and that is not tidiness --
    -- it is the difference between working and rebuilding forever.
    --
    -- The server MATERIALISES an unspecified STARTS to the moment the event was created. Verified: an
    -- event created with no STARTS reports STARTS = its own creation timestamp. So a package that omits
    -- Starts can NEVER equal the catalog, the event reads as changed on every deploy, and converging
    -- means DROP + CREATE -- which RESETS THE SCHEDULE. A nightly job would be pushed forward every
    -- single deploy, forever, and nothing would look wrong.
    --
    -- An omitted Starts therefore means "not managed", the same convention placement and AccessMethod
    -- use elsewhere: the server's own value stands and is not drift.
    IF NULLIF(p_Starts, '') IS NOT NULL AND p_Starts <> COALESCE(v_starts, '') THEN RETURN 0; END IF;
    IF NULLIF(p_Ends, '') IS NOT NULL AND p_Ends <> COALESCE(v_ends, '') THEN RETURN 0; END IF;
  END IF;

  IF UPPER(COALESCE(p_Status, 'ENABLE')) <> v_status THEN RETURN 0; END IF;
  IF COALESCE(p_Preserve, 0) <> v_preserve THEN RETURN 0; END IF;
  IF COALESCE(p_Comment, '') <> COALESCE(v_comment, '') THEN RETURN 0; END IF;

  -- The body is compared with whitespace collapsed at the ends only. The server stores the body
  -- essentially verbatim, so anything more aggressive risks calling two genuinely different bodies equal.
  IF TRIM(COALESCE(p_Definition, '')) <> TRIM(COALESCE(v_def, '')) THEN RETURN 0; END IF;

  RETURN 1;
END //

DELIMITER ;
