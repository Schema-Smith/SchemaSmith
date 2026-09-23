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
     -- Explicit collation on every key comparison: a routine parameter carries the server default
     -- (utf8mb4_0900_ai_ci on MySQL 8, utf8mb4_uca1400_ai_ci on MariaDB 11), and mixing that with the
     -- column's own collation is an "Illegal mix of collations" error rather than a false comparison.
     -- The three key members that hold an identifier go through SchemaSmith_IdentifierKey instead of a
     -- fixed collation, because whether two names are the same object is the server's call
     -- (lower_case_table_names) -- and the sibling that WRITES these rows, ExpressionMapRecord, has
     -- always compared them with BINARY. Forcing utf8mb4_unicode_ci here meant the reader and the
     -- writer disagreed: on a case-sensitive server this could answer for the wrong table.
     -- ObjectKind and Slot are engine constants, not identifiers, so they keep the fixed collation.
     WHERE SchemaSmith_IdentifierKey(em.ObjectSchema) = SchemaSmith_IdentifierKey(p_ObjectSchema)
       AND SchemaSmith_IdentifierKey(em.ObjectTable) = SchemaSmith_IdentifierKey(p_ObjectTable)
       AND em.ObjectKind = CONVERT(p_ObjectKind USING utf8mb4) COLLATE utf8mb4_unicode_ci
       AND SchemaSmith_IdentifierKey(em.ObjectName) = SchemaSmith_IdentifierKey(p_ObjectName)
       AND em.Slot = CONVERT(p_Slot USING utf8mb4) COLLATE utf8mb4_unicode_ci
     LIMIT 1;

    IF v_authored IS NULL THEN RETURN 0; END IF;
    IF BINARY v_authored != BINARY IFNULL(p_Authored, '') THEN RETURN 0; END IF;
    -- ORDER MATTERS. The live object is checked BEFORE the context, because a stale context must never be an
    -- excuse to stop looking at the server. With the two the other way round, an engine version that moved since
    -- the last deploy -- a cumulative update, or on other engines a packaging rebuild -- made this function answer
    -- "unchanged" without reading the live text at all: a hand-edited constraint was left in place, and the
    -- recorder then wrote the DRIFTED text as the new baseline, so the drift became permanent and silent. Checking
    -- the live text first costs one re-apply in the rare case where an engine genuinely re-renders an existing
    -- object's stored text (SQL Server freezes it, so in practice this is PostgreSQL major upgrades), and that
    -- re-apply restores the declared text -- which is the right answer anyway.
    IF BINARY v_canonical != BINARY IFNULL(p_LiveCanonical, '') THEN RETURN 0; END IF;
    IF v_rowversion != v_version THEN RETURN 1; END IF;

    RETURN 1;
END//

DELIMITER ;
