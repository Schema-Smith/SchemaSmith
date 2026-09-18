-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Splits a declared column / expression list on its TOP-LEVEL commas only: a comma inside parentheses, a
-- double-quoted identifier or a single-quoted literal belongs to its item. Items are returned untrimmed.
CREATE OR REPLACE FUNCTION "SchemaSmith"."SplitTopLevelList"(p_List TEXT)
    RETURNS TEXT[]
    LANGUAGE plpgsql
    IMMUTABLE
AS $$
DECLARE
    parts TEXT[] := ARRAY[]::TEXT[];
    len INT;
    i INT := 1;
    ch TEXT;
    cur TEXT := '';
    paren_level INT := 0;
    in_double BOOLEAN := FALSE;
    in_single BOOLEAN := FALSE;
BEGIN
    IF p_List IS NULL OR TRIM(p_List) = '' THEN
        RETURN parts;
    END IF;

    len := CHAR_LENGTH(p_List);
    WHILE i <= len LOOP
        ch := SUBSTR(p_List, i, 1);
        IF ch = '"' AND NOT in_single THEN
            IF in_double AND i < len AND SUBSTR(p_List, i + 1, 1) = '"' THEN
                cur := cur || '""';
                i := i + 2;
                CONTINUE;
            END IF;
            in_double := NOT in_double;
        ELSIF ch = '''' AND NOT in_double THEN
            IF in_single AND i < len AND SUBSTR(p_List, i + 1, 1) = '''' THEN
                cur := cur || '''''';
                i := i + 2;
                CONTINUE;
            END IF;
            in_single := NOT in_single;
        ELSIF ch = '(' AND NOT in_single AND NOT in_double THEN
            paren_level := paren_level + 1;
        ELSIF ch = ')' AND NOT in_single AND NOT in_double THEN
            IF paren_level > 0 THEN
                paren_level := paren_level - 1;
            END IF;
        ELSIF ch = ',' AND paren_level = 0 AND NOT in_double AND NOT in_single THEN
            parts := ARRAY_APPEND(parts, cur);
            cur := '';
            i := i + 1;
            CONTINUE;
        END IF;
        cur := cur || ch;
        i := i + 1;
    END LOOP;
    RETURN ARRAY_APPEND(parts, cur);
END $$;
