-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

IF OBJECT_ID('SchemaSmith.fn_StripBracketWrapping') IS NOT NULL DROP FUNCTION SchemaSmith.fn_StripBracketWrapping
GO
-- Removes ONE layer of [ ] wrapping and collapses ]] to ] on that strip, because inside a delimited SQL
-- Server identifier ]] DENOTES ONE ].
--
-- This is the exact inverse of delimiting, and it is deliberately a single strip rather than a loop.
-- Delimiting wraps a name once, so unwrapping removes one pair; stripping repeatedly over-strips a name
-- that legitimately CONTAINS brackets. The correctly delimited form of the column named [x] is [[x]]], and
-- the loop this replaced returned x where the name is [x] -- measured on SQL Server 2022. Ordinary names
-- are unaffected either way: [Orders] and Orders unwrap identically, which is what made it safe to change
-- on a function with 422 call sites across 17 files.
--
-- It MUST agree with Identifier.Unwrap, which has always been a single conditional strip. The deploy
-- unwraps the package side in C# and the catalog side here, so a disagreement is a rename that matches
-- nothing: the new object is created, the old one survives undeclared, and a later deploy carrying the
-- matching Drop...RemovedFromProduct flag removes the orphan WITH ITS DATA. That agreement is pinned by
-- StripBracketWrappingAgreesWithIdentifierUnwrapTests rather than left to inspection.
--
-- What this does NOT do, so nobody reads more into it: a BARE name that merely looks wrapped is still
-- unwrapped. An undelimited [x] returns x, because nothing in a stored string distinguishes it from the
-- delimited form of x. Only the caller knows whether a value was delimited, and both halves behave the
-- same way here, so the two still agree.
CREATE FUNCTION SchemaSmith.fn_StripBracketWrapping(@p_Input NVARCHAR(MAX))
  RETURNS NVARCHAR(MAX)
AS
BEGIN
  IF LEFT(RTRIM(@p_Input), 1) = '[' AND RIGHT(RTRIM(@p_Input), 1) = ']'
    SET @p_Input = REPLACE(SUBSTRING(RTRIM(@p_Input), 2, LEN(RTRIM(@p_Input)) - 2), ']]', ']')

  RETURN @p_Input
END
