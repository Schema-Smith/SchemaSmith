-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[fn_SafeBracketWrapFullTextColumn]') IS NOT NULL DROP FUNCTION [SchemaSmith].[fn_SafeBracketWrapFullTextColumn]
GO
-- Delimits a full-text index column spec, which is not always a single identifier.
--
-- A full-text column can be written  [Doc] TYPE COLUMN [DocType]  -- TWO identifiers and a keyword. Handing
-- that whole string to fn_SafeBracketWrap round-tripped it only because the wrap did not escape: the strip
-- took the outer brackets off and the wrap put them back, byte for byte. Once the wrap re-escapes -- which
-- it must, or an object whose name contains a ] cannot be created at all -- the interior ] is doubled and
-- the column name becomes  Doc] TYPE COLUMN [DocType , which the engine rejects with
-- "Column name '...' does not exist in the target table or view."
--
-- WHY THIS IS A FUNCTION rather than another CASE branch beside the LANGUAGE and STATISTICAL_SEMANTICS ones:
-- those suffixes can appear TOGETHER with TYPE COLUMN, and they are peeled first. A sibling branch is
-- therefore unreachable for  [Doc] TYPE COLUMN [DocType] LANGUAGE 1033  -- the LANGUAGE branch claims it and
-- then wraps the remaining composite itself. The TYPE COLUMN split has to happen INSIDE whatever wraps the
-- column part, so every branch calls this instead of fn_SafeBracketWrap and composes correctly.
--
-- The declared side and the live-side render in ModifiedTableQuench are compared AS STRINGS for drift, so
-- both must produce the same text. For an ordinary name they do: this emits [Doc] TYPE COLUMN [DocType],
-- identical to the live side's  ' TYPE COLUMN ' + QUOTENAME(COL_NAME(...)) + ''  concatenation. A name containing a
-- ] still differs, because the live side does not escape either -- that is the unescaped-emission gap
-- tracked for 2.8.0, not something this function can close alone.
CREATE FUNCTION [SchemaSmith].[fn_SafeBracketWrapFullTextColumn](@p_Input NVARCHAR(MAX))
  RETURNS NVARCHAR(MAX)
AS
BEGIN
  DECLARE @v_Pos INT = CHARINDEX(' TYPE COLUMN ', @p_Input)

  IF @v_Pos = 0
    RETURN SchemaSmith.fn_SafeBracketWrap(@p_Input)

  RETURN SchemaSmith.fn_SafeBracketWrap(LEFT(@p_Input, @v_Pos - 1))
       + ' TYPE COLUMN '
       + SchemaSmith.fn_SafeBracketWrap(SUBSTRING(@p_Input, @v_Pos + 13, LEN(@p_Input)))
END
