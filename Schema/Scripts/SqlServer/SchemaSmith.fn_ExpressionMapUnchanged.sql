-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_ExpressionMapUnchanged') IS NOT NULL
  DROP FUNCTION SchemaSmith.fn_ExpressionMapUnchanged
GO

-- #242. Answers one question: does SchemaSmith already know that this expression is unchanged?
--
-- The engine rewrites an expression when it stores it, so comparing what the package authored against what the
-- catalog reports is never equal for anything non-trivial -- and the object is dropped and re-created on every
-- deploy, forever. Text normalisation cannot fix that soundly: anything blunt enough to absorb the engine's
-- reframing also hides a real change.
--
-- So the comparison stops being about text. SchemaSmith.ExpressionMap records, per expression slot, what was
-- authored and what the engine reported immediately after applying it. This function returns 1 -- "unchanged,
-- leave it alone" -- only when BOTH sides still agree with what was recorded:
--   * the authored text equals what the package declared when we applied it, AND
--   * the live text equals what the engine gave back at that moment.
-- Either side moving means something really changed: an edited declaration, or an out-of-band edit to the live
-- object. Both must re-apply, and the second is why this is not a metadata-only compare -- going quiet on a
-- hand-edited constraint would be a worse failure than the churn this removes.
--
-- CONTEXT, AND WHY A STALE ROW IS NOT A CHANGE. Canonical form is not a property of the engine alone: SQL
-- Server renders CONVERT differently at compatibility level 100 and 110+, and freezes the stored text at
-- CREATION-time compat -- so one database can hold both forms indefinitely. A row written under a different
-- engine version or compat level cannot vouch for today's canonical text. That makes it stale, not wrong: the
-- honest answer is to leave the object alone and let the recording pass re-baseline the row. Re-applying
-- instead would drop and re-create every expression-bearing object in the database on the first deploy after a
-- compat bump -- the exact churn this exists to remove, delivered all at once.
--
-- NO ROW MEANS NO OPINION: returns 0, and the caller's existing text comparison decides. The mapping is an
-- optimisation over a working comparison, never a prerequisite for it.
CREATE FUNCTION SchemaSmith.fn_ExpressionMapUnchanged(
  @p_ObjectSchema NVARCHAR(256),
  @p_ObjectTable NVARCHAR(256),
  @p_ObjectKind VARCHAR(32),
  @p_ObjectName NVARCHAR(256),
  @p_Slot VARCHAR(32),
  @p_Authored NVARCHAR(MAX),
  @p_LiveCanonical NVARCHAR(MAX))
RETURNS BIT
AS
BEGIN
  DECLARE @v_Version VARCHAR(50) = CONVERT(VARCHAR(50), SERVERPROPERTY('ProductVersion'))
  DECLARE @v_Compat INT = CONVERT(INT, DATABASEPROPERTYEX(DB_NAME(), 'CompatibilityLevel'))

  DECLARE @v_Authored NVARCHAR(MAX), @v_Canonical NVARCHAR(MAX), @v_RowVersion VARCHAR(50), @v_RowCompat INT
  SELECT @v_Authored = em.[AuthoredText], @v_Canonical = em.[CanonicalText],
         @v_RowVersion = em.[EngineVersion], @v_RowCompat = em.[CompatLevel]
    FROM SchemaSmith.ExpressionMap em WITH (NOLOCK)
   WHERE em.[ObjectSchema] = SchemaSmith.fn_StripBracketWrapping(@p_ObjectSchema)
     AND em.[ObjectTable] = SchemaSmith.fn_StripBracketWrapping(@p_ObjectTable)
     AND em.[ObjectKind] = @p_ObjectKind
     AND em.[ObjectName] = SchemaSmith.fn_StripBracketWrapping(@p_ObjectName)
     AND em.[Slot] = @p_Slot

  IF @v_Authored IS NULL RETURN 0                                   -- no row: no opinion
  IF @v_Authored <> ISNULL(@p_Authored, '') RETURN 0                -- the declaration changed
  IF @v_RowVersion <> @v_Version OR ISNULL(@v_RowCompat, -1) <> ISNULL(@v_Compat, -1) RETURN 1  -- stale: re-baseline
  IF @v_Canonical <> ISNULL(@p_LiveCanonical, '') RETURN 0          -- the live object was edited out of band

  RETURN 1
END
GO
