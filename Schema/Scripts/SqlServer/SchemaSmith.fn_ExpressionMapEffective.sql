-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_ExpressionMapEffective') IS NOT NULL
  DROP FUNCTION SchemaSmith.fn_ExpressionMapEffective
GO

-- #242. For comparisons that build a WHOLE object script and compare it as one string -- CREATE INDEX,
-- CREATE STATISTICS -- the mapping cannot simply veto a difference: that would also suppress a real change to
-- the columns or options in the same string. So instead of answering yes/no, this returns the TEXT to put in
-- the declared script:
--
--   the engine's own rendering (@p_LiveCanonical), when fn_ExpressionMapUnchanged vouches that the declared
--     expression is what produced it -- so an unchanged expression compares equal;
--   the authored text otherwise -- so a real change to the expression still shows as a difference.
--
-- Every other part of the script keeps comparing on its own terms. Used identically by ModifiedTableQuench
-- and both IndexOnlyQuench variants; one definition, so the full and index-only paths cannot drift apart.
CREATE FUNCTION SchemaSmith.fn_ExpressionMapEffective(
  @p_ObjectSchema NVARCHAR(256),
  @p_ObjectTable NVARCHAR(256),
  @p_ObjectKind VARCHAR(32),
  @p_ObjectName NVARCHAR(256),
  @p_Slot VARCHAR(32),
  @p_Authored NVARCHAR(MAX),
  @p_LiveCanonical NVARCHAR(MAX))
RETURNS NVARCHAR(MAX)
AS
BEGIN
  IF @p_LiveCanonical IS NOT NULL
     AND SchemaSmith.fn_ExpressionMapUnchanged(@p_ObjectSchema, @p_ObjectTable, @p_ObjectKind, @p_ObjectName,
                                               @p_Slot, @p_Authored, @p_LiveCanonical) = 1
    RETURN @p_LiveCanonical
  RETURN @p_Authored
END
GO
