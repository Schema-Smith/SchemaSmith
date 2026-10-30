-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DROP FUNCTION IF EXISTS `SchemaSmith_EventMatches`;

DELIMITER //

CREATE FUNCTION `SchemaSmith_EventMatches`(
    p_Schema VARCHAR(200) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_Name VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_ScheduleType VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_Interval VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_ExecuteAt VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Starts VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_Ends VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_Status VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_Preserve TINYINT, p_Comment TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_Definition LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci,
    p_TimeZone VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci, p_SqlMode VARCHAR(1024) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
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
  --                 MySQL 8.4 renamed the third one REPLICA_SIDE_DISABLED; the DDL spelling is unchanged
  --                 and 8.4 still accepts it, so the package stays portable across both versions.
  --   ON_COMPLETION catalog 'PRESERVE' / 'NOT PRESERVE'               vs a bool in the package
  --   interval      catalog INTERVAL_VALUE + INTERVAL_FIELD, separate vs one string, "1 DAY"
  --   EVENT_TYPE    catalog 'RECURRING' / 'ONE TIME'                  vs DDL EVERY / AT
  -- Comparing either side raw would report a difference on every deploy and rebuild the event forever.
  DECLARE v_type VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_interval VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_at VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_starts VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_ends VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_status VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_preserve TINYINT;
  DECLARE v_comment TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_def LONGTEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_zone VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_mode VARCHAR(1024) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_flag VARCHAR(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
  DECLARE v_comment_kept INT;
  DECLARE v_i INT;
  DECLARE v_n INT;

  SELECT CASE WHEN EVENT_TYPE = 'ONE TIME' THEN 'AT' ELSE 'EVERY' END,
         CASE WHEN INTERVAL_VALUE IS NULL THEN NULL
              ELSE CONCAT(INTERVAL_VALUE, ' ', INTERVAL_FIELD) END,
         CAST(EXECUTE_AT AS CHAR),
         CAST(STARTS AS CHAR),
         CAST(ENDS AS CHAR),
         CASE STATUS WHEN 'ENABLED' THEN 'ENABLE'
                     WHEN 'DISABLED' THEN 'DISABLE'
                     WHEN 'SLAVESIDE_DISABLED' THEN 'DISABLE ON SLAVE'
                     -- MySQL 8.4's spelling of the same state (see GenerateEventJson). Without it the
                     -- comparison reads the raw catalog value against the package's DDL spelling, so a
                     -- slave-disabled event never matches and is re-applied on every single deploy.
                     WHEN 'REPLICA_SIDE_DISABLED' THEN 'DISABLE ON SLAVE'
                     ELSE STATUS END,
         CASE WHEN ON_COMPLETION = 'PRESERVE' THEN 1 ELSE 0 END,
         EVENT_COMMENT,
         EVENT_DEFINITION,
         TIME_ZONE,
         SQL_MODE
    INTO v_type, v_interval, v_at, v_starts, v_ends, v_status, v_preserve, v_comment, v_def, v_zone, v_mode
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
  -- The comment and body are the user's text, so they compare byte for byte: a change only in case or accent is a
  -- change, whatever the database's collation. The engine keeps as much of a comment as its catalog column holds
  -- (64 characters on MariaDB and MySQL 5.7, 2048 on 8.0) and drops the rest without a word, so the declaration is
  -- compared as it is stored; otherwise a longer one reads as changed, and re-creating the event resets its STARTS.
  SELECT CHARACTER_MAXIMUM_LENGTH INTO v_comment_kept FROM INFORMATION_SCHEMA.COLUMNS
   WHERE TABLE_SCHEMA = 'information_schema' AND TABLE_NAME = 'EVENTS' AND COLUMN_NAME = 'EVENT_COMMENT';
  IF CAST(LEFT(COALESCE(p_Comment, ''), COALESCE(v_comment_kept, 2048)) AS BINARY) <> CAST(COALESCE(v_comment, '') AS BINARY) THEN RETURN 0; END IF;

  -- The zone and the mode follow the Starts convention: omitted means not managed. The mode is compared as a
  -- SET of flags, because the engine canonicalises the order it stores ('ANSI_QUOTES,PIPES_AS_CONCAT' is stored
  -- as 'PIPES_AS_CONCAT,ANSI_QUOTES'), and an exact compare would rebuild the event on every deploy.
  IF NULLIF(p_TimeZone, '') IS NOT NULL AND CAST(p_TimeZone AS BINARY) <> CAST(COALESCE(v_zone, '') AS BINARY) THEN RETURN 0; END IF;
  IF p_SqlMode IS NOT NULL THEN
    SET v_mode = COALESCE(v_mode, '');
    SET v_n = CASE WHEN p_SqlMode = '' THEN 0 ELSE LENGTH(p_SqlMode) - LENGTH(REPLACE(p_SqlMode, ',', '')) + 1 END;
    IF v_n <> CASE WHEN v_mode = '' THEN 0 ELSE LENGTH(v_mode) - LENGTH(REPLACE(v_mode, ',', '')) + 1 END THEN RETURN 0; END IF;
    SET v_i = 1;
    WHILE v_i <= v_n DO
      SET v_flag = TRIM(SUBSTRING_INDEX(SUBSTRING_INDEX(p_SqlMode, ',', v_i), ',', -1));
      IF FIND_IN_SET(v_flag, v_mode) = 0 THEN RETURN 0; END IF;
      SET v_i = v_i + 1;
    END WHILE;
  END IF;

  -- The body is compared with whitespace collapsed at the ends only. The server stores the body
  -- essentially verbatim, so anything more aggressive risks calling two genuinely different bodies equal.
  IF CAST(TRIM(COALESCE(p_Definition, '')) AS BINARY) <> CAST(TRIM(COALESCE(v_def, '')) AS BINARY) THEN RETURN 0; END IF;

  RETURN 1;
END //

DELIMITER ;
