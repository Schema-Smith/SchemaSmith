-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_ExpressionMapUnchanged//

CREATE FUNCTION SchemaSmith_ExpressionMapUnchanged(
    p_ObjectSchema VARCHAR(64),
    p_ObjectTable VARCHAR(64),
    p_ObjectKind VARCHAR(32),
    p_ObjectName VARCHAR(64),
    p_Slot VARCHAR(32),
    p_Authored TEXT,
    p_LiveCanonical TEXT
) RETURNS TINYINT(1)
READS SQL DATA
BEGIN
    -- #242. Answers one question: does SchemaSmith already know that this expression is unchanged?
    --
    -- MySQL and MariaDB rewrite an expression when they store it, so comparing what a package authored with
    -- what INFORMATION_SCHEMA reports is never equal for anything non-trivial -- and the object is re-applied
    -- on every deploy. Normalising the text cannot fix that soundly: anything blunt enough to absorb the
    -- engine's reframing also hides a real change.
    --
    -- SchemaSmith_ExpressionMap records what was authored and what the engine gave back immediately after
    -- applying it. This returns 1 -- "unchanged, leave it alone" -- only when both still agree:
    --   authored moved -> the declaration changed. Apply.
    --   live moved     -> the object was edited out of band. Re-apply. Without this the fix would be a
    --                     metadata-only compare, which silently accepts a hand-edited constraint.
    --   version moved  -> the row cannot vouch for today's canonical text. Stale, not wrong: leave the object
    --                     alone and let the recording pass re-baseline it.
    --   no row         -> no opinion; the caller's text comparison decides, exactly as before.
    DECLARE v_authored TEXT DEFAULT NULL;
    DECLARE v_canonical TEXT DEFAULT NULL;
    DECLARE v_rowversion VARCHAR(50) DEFAULT NULL;
    DECLARE v_version VARCHAR(50) DEFAULT VERSION();

    SELECT em.AuthoredText, em.CanonicalText, em.EngineVersion
      INTO v_authored, v_canonical, v_rowversion
      FROM SchemaSmith_ExpressionMap em
     -- Explicit collation on every key comparison: the table is utf8mb4_unicode_ci while a routine parameter
     -- carries the server default (utf8mb4_0900_ai_ci on MySQL 8, utf8mb4_uca1400_ai_ci on MariaDB 11), and
     -- mixing them is an "Illegal mix of collations" error rather than a false comparison.
     WHERE em.ObjectSchema = CONVERT(p_ObjectSchema USING utf8mb4) COLLATE utf8mb4_unicode_ci
       AND em.ObjectTable = CONVERT(p_ObjectTable USING utf8mb4) COLLATE utf8mb4_unicode_ci
       AND em.ObjectKind = CONVERT(p_ObjectKind USING utf8mb4) COLLATE utf8mb4_unicode_ci
       AND em.ObjectName = CONVERT(p_ObjectName USING utf8mb4) COLLATE utf8mb4_unicode_ci
       AND em.Slot = CONVERT(p_Slot USING utf8mb4) COLLATE utf8mb4_unicode_ci
     LIMIT 1;

    IF v_authored IS NULL THEN RETURN 0; END IF;
    IF BINARY v_authored != BINARY IFNULL(p_Authored, '') THEN RETURN 0; END IF;
    IF v_rowversion != v_version THEN RETURN 1; END IF;
    IF BINARY v_canonical != BINARY IFNULL(p_LiveCanonical, '') THEN RETURN 0; END IF;

    RETURN 1;
END//

DELIMITER ;
