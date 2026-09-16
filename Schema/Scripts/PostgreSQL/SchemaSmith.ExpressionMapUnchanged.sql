-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- #242, PostgreSQL. The SQL Server twin's doc comment carries the full reasoning; the short version:
-- PostgreSQL rewrites an expression when it stores it -- starts_with(tag, 'a') comes back as
-- starts_with(tag, 'a'::text) -- so comparing authored text against pg_get_constraintdef is never equal and
-- the object is dropped and re-created on every deploy. This answers "did anything actually change?" from
-- what was recorded when the expression was applied, instead of from the text.
--
--   authored moved -> the package changed. Apply.
--   live moved     -> someone edited the object out of band. Re-apply. (Without this half the fix would be a
--                     metadata-only compare, which silently accepts a hand-edited constraint -- worse than
--                     the churn.)
--   context moved  -> the row cannot vouch for today's canonical text. Stale, not wrong: leave the object
--                     alone and let the recording pass re-baseline it.
--   no row         -> no opinion; the caller's text comparison decides.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ExpressionMapUnchanged"(
  p_ObjectSchema TEXT,
  p_ObjectTable TEXT,
  p_ObjectKind TEXT,
  p_ObjectName TEXT,
  p_Slot TEXT,
  p_Authored TEXT,
  p_LiveCanonical TEXT)
RETURNS BOOLEAN
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
  v_version TEXT := current_setting('server_version');
  v_authored TEXT;
  v_canonical TEXT;
  v_rowversion TEXT;
BEGIN
  SELECT em."AuthoredText", em."CanonicalText", em."EngineVersion"
    INTO v_authored, v_canonical, v_rowversion
    FROM "SchemaSmith"."ExpressionMap" em
   WHERE em."ObjectSchema" = p_ObjectSchema
     AND em."ObjectTable" = p_ObjectTable
     AND em."ObjectKind" = p_ObjectKind
     AND em."ObjectName" = p_ObjectName
     AND em."Slot" = p_Slot;

  IF v_authored IS NULL THEN RETURN FALSE; END IF;               -- no row: no opinion
  IF v_authored != COALESCE(p_Authored, '') THEN RETURN FALSE; END IF;   -- the declaration changed
  IF v_rowversion != v_version THEN RETURN TRUE; END IF;         -- stale context: re-baseline, do not re-apply
  IF v_canonical != COALESCE(p_LiveCanonical, '') THEN RETURN FALSE; END IF;  -- edited out of band

  RETURN TRUE;
END;
$$;
