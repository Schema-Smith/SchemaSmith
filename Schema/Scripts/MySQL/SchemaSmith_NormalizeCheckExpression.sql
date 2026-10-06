-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizeCheckExpression//

CREATE FUNCTION SchemaSmith_NormalizeCheckExpression(
    p_Expression TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
DETERMINISTIC
NO SQL
BEGIN
    -- Normalizes a CHECK expression for desired-vs-live comparison so an unchanged check
    -- is not phantom-dropped/recreated on every run (the MySQL idempotency hazard).
    --
    -- WHY this is needed: MySQL reformats CHECK_CLAUSE on storage. An authored "`Id` > 100"
    -- comes back from INFORMATION_SCHEMA.CHECK_CONSTRAINTS as "(`Id` > 100)" — wrapped in a
    -- single outer paren pair and with normalized internal spacing. A raw text compare against
    -- the authored form would always differ. We collapse both sides to a canonical form by:
    --   1. removing ALL whitespace (kills MySQL's added spaces around operators), and
    --   2. peeling matched outer paren pairs that enclose the WHOLE expression
    --      (kills MySQL's added wrapper without corrupting "(a)>(b)"-style expressions,
    --       where the leading "(" does NOT match the trailing ")").
    -- This mirrors how PostgreSQL normalizes via pg_get_constraintdef and SQL Server via
    -- fn_StripParenWrapping.

    DECLARE v_Result TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Depth INT;
    DECLARE v_Pos INT;
    DECLARE v_Len INT;
    DECLARE v_Char CHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Enclosed INT;

    IF p_Expression IS NULL THEN
        RETURN NULL;
    END IF;

    -- Strip all whitespace (spaces, tabs, newlines)
    -- Note: strips all whitespace, so check expressions whose semantics depend on whitespace inside a string literal (e.g. 'New York') may falsely converge — same limitation as the PG/SQL Server expression normalization.
    SET v_Result = p_Expression;
    SET v_Result = REPLACE(v_Result, ' ', '');
    SET v_Result = REPLACE(v_Result, '\t', '');
    SET v_Result = REPLACE(v_Result, '\n', '');
    SET v_Result = REPLACE(v_Result, '\r', '');

    -- Peel outer paren pairs that enclose the entire expression.
    peel_loop: WHILE CHAR_LENGTH(v_Result) >= 2
                 AND LEFT(v_Result, 1) = '('
                 AND RIGHT(v_Result, 1) = ')' DO
        -- Verify the leading "(" matches the trailing ")" (depth returns to 0 only at the end).
        SET v_Depth = 0;
        SET v_Pos = 1;
        SET v_Len = CHAR_LENGTH(v_Result);
        SET v_Enclosed = 1;

        depth_loop: WHILE v_Pos <= v_Len DO
            SET v_Char = SUBSTRING(v_Result, v_Pos, 1);
            IF v_Char = '(' THEN
                SET v_Depth = v_Depth + 1;
            ELSEIF v_Char = ')' THEN
                SET v_Depth = v_Depth - 1;
            END IF;
            -- Depth hit 0 before the final character => the outer parens are NOT a single
            -- enclosing pair (e.g. "(a)>(b)"); do not peel.
            IF v_Depth = 0 AND v_Pos < v_Len THEN
                SET v_Enclosed = 0;
                LEAVE depth_loop;
            END IF;
            SET v_Pos = v_Pos + 1;
        END WHILE;

        IF v_Enclosed = 0 THEN
            LEAVE peel_loop;
        END IF;

        SET v_Result = SUBSTRING(v_Result, 2, CHAR_LENGTH(v_Result) - 2);
    END WHILE;

    RETURN v_Result;
END //

DELIMITER ;
