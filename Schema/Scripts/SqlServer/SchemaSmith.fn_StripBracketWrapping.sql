-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_StripBracketWrapping') IS NOT NULL DROP FUNCTION SchemaSmith.fn_StripBracketWrapping
GO
-- Collapses ]] to ] on each strip, because inside a delimited SQL Server identifier ]] DENOTES ONE ].
--
-- Without it this function disagreed with the catalog about any name containing a ]: the correctly
-- delimited form of the column named a]b is [a]]b], and stripping the wrapper alone returned a]]b --
-- four characters against the catalog's three. Measured on SQL Server 2022. Everywhere that matters,
-- that mismatch is SILENT: a rename whose OldName carries an escaped ] matches nothing, so the new
-- column is created, the old one is orphaned, and a later deploy with a Drop...RemovedFromProduct flag
-- removes the orphan WITH ITS DATA. The same defect was fixed on PostgreSQL ("" -> ") in this release;
-- this is the SQL Server half, found by comparing this function against Identifier.Unwrap, which has
-- always collapsed correctly.
--
-- The WHILE loop is deliberately LEFT ALONE here and is tracked separately: it strips repeatedly, so it
-- disagrees with Identifier.Unwrap on bracket-shaped input (this returns x for [[x]], the helper returns
-- [x]) and it rewrites a bare name that merely LOOKS wrapped. Changing it moves 422 call sites at once
-- and is a product decision, not a bug fix.
CREATE FUNCTION SchemaSmith.fn_StripBracketWrapping(@p_Input NVARCHAR(MAX))
  RETURNS NVARCHAR(MAX)
AS
BEGIN
  WHILE LEFT(RTRIM(@p_Input), 1) = '[' AND RIGHT(RTRIM(@p_Input), 1) = ']'
    SET @p_Input = REPLACE(SUBSTRING(RTRIM(@p_Input), 2, LEN(RTRIM(@p_Input)) - 2), ']]', ']')

  RETURN @p_Input
END