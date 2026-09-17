-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Renders a DECLARED index key or INCLUDE list in the exact form the live snapshot reads back, for COMPARISON
-- only -- never for DDL (unquoting "Status" in a CREATE would fold it to status). The live side is built from
-- PG_GET_INDEXDEF per column with its surrounding double quotes trimmed and the sort options spelled from
-- indoption; so here each top-level item has its surrounding quotes removed, its modifiers upper-cased with
-- whitespace collapsed, and the defaults PostgreSQL does not keep (ASC; NULLS LAST after ASC; NULLS FIRST after
-- DESC) dropped. Without this an index authored "status" or status asc was dropped and rebuilt on every deploy.
-- Expressions (anything containing a parenthesis) are compared as authored, trimmed.
CREATE OR REPLACE FUNCTION "SchemaSmith"."NormalizeIndexColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE plpgsql
    IMMUTABLE
AS $$
DECLARE
    item TEXT;
    s TEXT;
    token TEXT;
    mods TEXT;
    col TEXT;
    res_items TEXT[] := ARRAY[]::TEXT[];
BEGIN
    FOREACH item IN ARRAY "SchemaSmith"."SplitTopLevelList"(p_List) LOOP
        s := TRIM(item);
        mods := '';
        LOOP
            EXIT WHEN s = '' OR NOT (
                s ~* '\s+(NULLS\s+FIRST)\s*$' OR
                s ~* '\s+(NULLS\s+LAST)\s*$' OR
                s ~* '\s+(ASC|DESC)\s*$'
            );
            token := UPPER(REGEXP_REPLACE(TRIM(SUBSTRING(s FROM '(?i)(NULLS\s+FIRST|NULLS\s+LAST|ASC|DESC)\s*$')), '\s+', ' ', 'g'));
            s := RTRIM(REGEXP_REPLACE(s, '(?i)\s*(NULLS\s+FIRST|NULLS\s+LAST|ASC|DESC)\s*$', ''));
            mods := CASE WHEN mods = '' THEN token ELSE token || ' ' || mods END;
        END LOOP;

        -- Defaults the catalog does not record, so the live side never shows them.
        mods := CASE mods
                  WHEN 'ASC' THEN ''
                  WHEN 'NULLS LAST' THEN ''
                  WHEN 'ASC NULLS LAST' THEN ''
                  WHEN 'ASC NULLS FIRST' THEN 'NULLS FIRST'
                  WHEN 'DESC NULLS FIRST' THEN 'DESC'
                  ELSE mods END;

        IF s LIKE '%(%' THEN
            col := s;
        ELSE
            -- The live side trims surrounding quotes and leaves any doubled inner quote alone; so does this.
            col := TRIM(BOTH '"' FROM s);
        END IF;
        res_items := ARRAY_APPEND(res_items, col || CASE WHEN mods <> '' THEN ' ' || mods ELSE '' END);
    END LOOP;

    RETURN ARRAY_TO_STRING(res_items, ',');
END $$;
