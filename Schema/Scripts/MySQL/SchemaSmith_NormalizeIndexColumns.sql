-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

DELIMITER //

DROP FUNCTION IF EXISTS SchemaSmith_NormalizeIndexColumns//

CREATE FUNCTION SchemaSmith_NormalizeIndexColumns(
    p_IndexColumns TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci
) RETURNS TEXT CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci
NOT DETERMINISTIC
READS SQL DATA
SQL SECURITY DEFINER
BEGIN
    -- Normalizes an index column list for comparison. Canonical per-key-part form, comma-separated:
    -- `ColumnName`(SubPart) DESC for a plain column -- backtick-wrapped name, then (n) only when a prefix
    -- length was declared, then DESC only when the target actually stores descending key parts -- or
    -- (expression) DESC for a functional/expression key part (MySQL 8.0.13+, including a multi-valued
    -- CAST(col->'$.path' AS type ARRAY) index -- MySQL 8.0.17+, same NULL-COLUMN_NAME/EXPRESSION shape,
    -- no dedicated handling needed), carrying the parenthesized expression verbatim: exactly what
    -- INFORMATION_SCHEMA.STATISTICS.EXPRESSION reports (with its charset-introducer noise -- e.g.
    -- _latin1'...' or _utf8mb4'...', varying with the connection charset in effect when the index
    -- was CREATEd -- already stripped, see GenerateTableJson.sql), which is also what SHOW CREATE
    -- TABLE renders. Both the declared side (here) and the catalog-snapshot side
    -- (SchemaSmith_IndexOnlyQuench.sql / SchemaSmith_MissingIndexesAndConstraintsQuench.sql
    -- _SchemaSmith_IdxDetectSnap builds) must produce this exact form or the comparison never converges.
    --
    -- When the target does NOT store descending index key parts (MySQL 5.7 / MariaDB 10.2-10.7), the DESC
    -- suffix is dropped: the engine silently stores such indexes ascending, so the live catalog always
    -- reports ascending. Dropping DESC here makes the desired list match that ascending catalog, keeping the
    -- deploy idempotent (without this, a declared DESC index would be seen as modified and rebuilt every run).
    -- The 'downgraded' visibility is recorded by the index-apply procs. NOT DETERMINISTIC: reads the version.

    DECLARE v_Result TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_Column TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Pos INT DEFAULT 1;
    DECLARE v_Len INT;
    DECLARE v_Comma INT;
    DECLARE v_Trimmed TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_IsDesc INT DEFAULT 0;
    DECLARE v_ColName TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_Prefix TEXT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci DEFAULT '';
    DECLARE v_ParenPos INT DEFAULT 0;
    DECLARE v_SupportsDesc TINYINT DEFAULT SchemaSmith_SupportsDescendingIndex();
    DECLARE v_Depth INT DEFAULT 0;
    DECLARE v_Scan INT;
    DECLARE v_Char CHAR(1) CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
    DECLARE v_InBacktick TINYINT DEFAULT 0;

    IF p_IndexColumns IS NULL OR TRIM(p_IndexColumns) = '' THEN
        RETURN '';
    END IF;

    SET v_Len = CHAR_LENGTH(p_IndexColumns);

    -- Process each key part
    WHILE v_Pos <= v_Len DO
        -- Find the next TOP-LEVEL comma (paren depth 0, outside a backtick-quoted span) -- not just the
        -- next comma. A functional/expression key part is wrapped in its own parens and can itself
        -- contain a comma (e.g. `(concat(\`a\`,\`b\`))`); a plain LOCATE(',', ...) would treat that
        -- internal comma as a key-part boundary and split the expression in two.
        SET v_Depth = 0;
        SET v_InBacktick = 0;
        SET v_Scan = v_Pos;
        SET v_Comma = 0;
        scan_loop: WHILE v_Scan <= v_Len DO
            SET v_Char = SUBSTRING(p_IndexColumns, v_Scan, 1);
            IF v_Char = '`' THEN
                SET v_InBacktick = 1 - v_InBacktick;
            ELSEIF v_InBacktick = 0 AND v_Char = '(' THEN
                SET v_Depth = v_Depth + 1;
            ELSEIF v_InBacktick = 0 AND v_Char = ')' THEN
                SET v_Depth = v_Depth - 1;
            ELSEIF v_InBacktick = 0 AND v_Char = ',' AND v_Depth = 0 THEN
                SET v_Comma = v_Scan;
                LEAVE scan_loop;
            END IF;
            SET v_Scan = v_Scan + 1;
        END WHILE scan_loop;
        IF v_Comma = 0 THEN
            SET v_Comma = v_Len + 1;
        END IF;

        -- Extract key part
        SET v_Column = TRIM(SUBSTRING(p_IndexColumns, v_Pos, v_Comma - v_Pos));

        -- Check for DESC suffix
        SET v_IsDesc = 0;
        IF UPPER(v_Column) LIKE '% DESC' THEN
            SET v_IsDesc = 1;
            SET v_Column = TRIM(SUBSTRING(v_Column, 1, CHAR_LENGTH(v_Column) - 5));
        ELSEIF UPPER(v_Column) LIKE '% ASC' THEN
            SET v_Column = TRIM(SUBSTRING(v_Column, 1, CHAR_LENGTH(v_Column) - 4));
        END IF;

        IF v_Result != '' THEN
            SET v_Result = CONCAT(v_Result, ',');
        END IF;

        -- A functional/expression key part starts with '(' rather than a backtick -- extraction always
        -- backtick-wraps a plain column name, so this is an unambiguous discriminator. Carried verbatim:
        -- unlike a column, it has no name to strip/re-wrap and no prefix length to split off.
        IF LEFT(v_Column, 1) = '(' THEN
            SET v_Result = CONCAT(v_Result, v_Column);
        ELSE
            -- Split off a trailing prefix length, e.g. `code`(5) -> `code` + (5), BEFORE stripping
            -- backticks -- SchemaSmith_StripBacktickWrapping expects a bare/backtick-wrapped identifier
            -- and would otherwise mangle the whole `code`(5) token.
            SET v_Prefix = '';
            SET v_ParenPos = 0;
            IF RIGHT(v_Column, 1) = ')' THEN
                SET v_ParenPos = CHAR_LENGTH(v_Column) - LOCATE('(', REVERSE(v_Column)) + 1;
            END IF;
            IF v_ParenPos > 0 THEN
                SET v_Prefix = SUBSTRING(v_Column, v_ParenPos);
                SET v_Column = TRIM(SUBSTRING(v_Column, 1, v_ParenPos - 1));
            END IF;

            -- Strip existing backticks and re-wrap
            SET v_ColName = SchemaSmith_StripBacktickWrapping(v_Column);
            SET v_Result = CONCAT(v_Result, '`', v_ColName, '`', v_Prefix);
        END IF;

        IF v_IsDesc = 1 AND v_SupportsDesc = 1 THEN
            SET v_Result = CONCAT(v_Result, ' DESC');
        END IF;

        -- Move to next key part
        SET v_Pos = v_Comma + 1;
    END WHILE;

    RETURN v_Result;
END//

DELIMITER ;
