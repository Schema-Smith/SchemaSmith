-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."ExecuteOrDebug"(p_Script TEXT, p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql. Every procedure carries this, not just
  -- the ones that look like entry points -- the product CALLs ModifiedTableQuench and its siblings
  -- directly (SchemaQuench/DatabaseQuench.cs), so "nested" is not a safe assumption to plan around.
  SET jit = 'off'
AS $$
DECLARE
    code_block TEXT;
    -- The block's dollar-quote tag, chosen so the script cannot end it early: a description, a default or a check
    -- carrying the empty dollar-quote tag (a dollar-quoted literal) would otherwise close the block mid-statement.
    tag TEXT := '$ss$';
    n INT := 0;
BEGIN
    IF NULLIF(p_Script, '') IS NOT NULL THEN
        IF p_WhatIf THEN
            -- The script's own change rows are not written under WhatIf, so they are not shown either.
            RAISE NOTICE '%', regexp_replace(p_Script, 'INSERT INTO "SchemaSmith"\."ChangeAudit".*?''\);', '', 'g');
        ELSE
            WHILE POSITION(tag IN p_Script) > 0 LOOP
                n := n + 1;
                tag := '$ss' || n || '$';
            END LOOP;
            code_block := CHR(10) || 'DO ' || tag || CHR(10) || 'BEGIN' || CHR(10) || p_Script || CHR(10) || 'END ' || tag || CHR(10);
            EXECUTE code_block;
        END IF;
    END IF;
END $$