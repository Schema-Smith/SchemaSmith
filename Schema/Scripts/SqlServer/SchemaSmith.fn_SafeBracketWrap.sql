-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('[SchemaSmith].[fn_SafeBracketWrap]') IS NOT NULL DROP FUNCTION [SchemaSmith].[fn_SafeBracketWrap]
GO
-- Delimits an identifier: strips one layer of existing wrapping so an already-delimited input is idempotent,
-- then RE-ESCAPES on the way out. The re-escape is the half that was missing.
--
-- fn_StripBracketWrapping collapses ]] to ] because inside a delimited identifier ]] denotes one ]. This
-- function is its inverse, so it has to double a ] back. Without that it produced DDL the engine cannot
-- parse -- and because it runs in the NORMALIZE step of ParseTableJsonIntoTempTables, the damage is on
-- CREATE, not just on rename: an object whose name contains a ] could not be deployed at all.
--
-- Two input classes were broken, and only the first was a regression:
--   * ALREADY ESCAPED -- [a]]b], which is what extraction now writes and downstream consumers store.
--     Correct until the strip gained its ]] collapse, then normalised to the invalid [a]b]. That regression
--     arrived with the collapse and was reported from the consumer side, measured across three packages
--     with one variable.
--   * RAW -- a]b. Broken before and after: nothing stripped, and the wrap never escaped.
-- One re-escape fixes both.
--
-- REPLACE rather than QUOTENAME on purpose. QUOTENAME returns NULL for input longer than 128 characters,
-- and this function is called from 124 sites across eight scripts; swapping a silent NULL in for a
-- previously-working value at any of them would be a worse failure than the one being fixed. The explicit
-- REPLACE has no length ceiling. Agreement with QUOTENAME on real identifiers is asserted by
-- SafeBracketWrapMatchesQuoteName, which uses the engine as the oracle rather than trusting a round trip --
-- strip(wrap(x)) == x passes whenever both halves are consistently wrong.
CREATE FUNCTION [SchemaSmith].[fn_SafeBracketWrap](@p_Input NVARCHAR(MAX))
  RETURNS NVARCHAR(MAX)
AS
BEGIN
  RETURN '[' + REPLACE(SchemaSmith.fn_StripBracketWrapping(LTRIM(RTRIM(@p_Input))), ']', ']]') + ']'
END
