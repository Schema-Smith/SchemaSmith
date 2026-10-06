-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizePartitionExpression//

CREATE FUNCTION SchemaSmith_NormalizePartitionExpression(
    p_Expression TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- Canonical form of a partition expression, for comparing the DECLARED one against what the catalog
    -- reports (#partitioning, K3). Backticks removed, all whitespace removed, lower-cased.
    --
    -- THE SUPPORTED FLOOR IS THE REASON THIS EXISTS, not tidiness. The engines do not agree about what
    -- INFORMATION_SCHEMA.PARTITIONS.PARTITION_EXPRESSION contains -- verified live on four servers:
    --
    --   MySQL 5.7      RANGE (YEAR(dt))  ->  YEAR(dt)        the text the user wrote, unchanged
    --   MySQL 8.0      RANGE (YEAR(dt))  ->  year(`dt`)      rewritten: lower-cased, identifiers quoted
    --   MariaDB 10.2   RANGE (YEAR(dt))  ->  year(`dt`)      same rewritten form
    --   MariaDB 11.4   RANGE (YEAR(dt))  ->  year(`dt`)      same rewritten form
    --
    -- So a literal string compare would refuse a package extracted on 5.7 and deployed to 8.0 -- a false
    -- alarm on a layout that is byte-identical in the engine, and one that would make a package
    -- engine-specific for no reason. It also lets a hand-authored "Id" match a catalog `Id`, which is what
    -- anyone writing a package by hand will type.
    --
    -- Deliberately NOT a general SQL normalizer: it does not reorder arguments, resolve synonyms, or
    -- understand precedence. Two genuinely different expressions still compare different, which is the
    -- safe direction -- the cost of a false NON-match is a refusal the user can read and correct, while a
    -- false match would silently accept a layout that is not what the package declares.
    IF p_Expression IS NULL THEN
        RETURN NULL;
    END IF;

    -- REPLACE handles every whitespace form the catalog can return; a single TRIM would only touch the ends.
    RETURN LOWER(
        REPLACE(
            REPLACE(
                REPLACE(
                    REPLACE(REPLACE(p_Expression, '`', ''), ' ', ''),
                    CHAR(9), ''),
                CHAR(13), ''),
            CHAR(10), ''));
END //

DELIMITER ;
