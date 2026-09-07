-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."EnumTypeQuench"(
    p_ProductName VARCHAR(50),
    p_EnumTypes TEXT,
    p_WhatIf BOOLEAN DEFAULT FALSE)
    LANGUAGE plpgsql
AS $$
DECLARE
  sql_script TEXT;
  bad RECORD;
BEGIN
  -- Converges declared enum types.
  --
  -- WHY THIS EXISTS AT ALL. As a scripted object an enum is created by a guarded CREATE TYPE. Once the
  -- type exists the guard skips, so EDITING THE VALUE LIST IN THE .sql FILE DOES NOTHING -- forever, and
  -- silently. Verified: re-running a guarded create carrying a third value left the type with its
  -- original two and reported success. That is the failure this replaces, and it is a no-op rather than
  -- an error, which is the worst kind.
  --
  -- WHAT CAN CONVERGE IS THE ENGINE'S LIMIT, NOT A CHOICE. PostgreSQL can ADD a value and place it
  -- (BEFORE/AFTER), but it cannot REMOVE or REORDER one without dropping and recreating the type -- which
  -- means dropping every column that uses it. So:
  --   * a value the package adds is added, in the right position
  --   * a value the package no longer lists is REPORTED, never dropped
  -- Reporting rather than attempting is the same posture placement takes: refuse the destructive thing by
  -- name instead of doing it because a string changed in a file.
  --
  -- ALTER TYPE ... ADD VALUE inside a transaction needs PostgreSQL 12, which is the supported floor
  -- (verified there), so no version gate is needed.
  DROP TABLE IF EXISTS temp_enum_types;
  CREATE TEMPORARY TABLE temp_enum_types AS
  WITH src(arr) AS (VALUES(p_EnumTypes::JSON))
  SELECT COALESCE(elem ->> 'Schema', 'public') AS "Schema",
         elem ->> 'Name' AS "Name",
         COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
         ARRAY(SELECT JSON_ARRAY_ELEMENTS_TEXT(elem -> 'Values')) AS "Values"
    FROM src, JSON_ARRAY_ELEMENTS(arr) AS elem;

  -- Evaluate ShouldApplyExpression: drop enum types whose condition is false so a conditional variant is
  -- SKIPPED rather than always applied (the value was parsed but never acted on). Always executes, even
  -- under --WhatIf -- this filters the internal working set, it is not user-visible DDL.
  SELECT STRING_AGG('DELETE FROM temp_enum_types WHERE "Schema" = ''' || "Schema" || ''' AND "Name" = ''' || "Name" || ''' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
    INTO sql_script
    FROM temp_enum_types
    WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

  -- Create the ones that do not exist yet, values and order intact.
  RAISE NOTICE 'Add Missing Enum Types';
  SELECT STRING_AGG('RAISE NOTICE ''  Create enum type ' || t."Schema" || '.' || t."Name" || ''';' || CHR(10) ||
                    'CREATE TYPE "' || t."Schema" || '"."' || t."Name" || '" AS ENUM (' ||
                    (SELECT STRING_AGG('''' || REPLACE(v, '''', '''''') || '''', ', ')
                       FROM UNNEST(t."Values") WITH ORDINALITY AS u(v, ord)) || ');' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''enum type'', ''' || t."Schema" || '.' || t."Name" || ''', ''created'');', CHR(10))
    INTO sql_script
    FROM temp_enum_types t
   WHERE NOT EXISTS (SELECT 1 FROM pg_type ty
                       JOIN pg_namespace n ON n.oid = ty.typnamespace
                      WHERE ty.typname = t."Name" AND n.nspname = t."Schema" AND ty.typtype = 'e');
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- Add values the package has and the type does not, positioned so the declared ORDER is honoured.
  -- Order is not cosmetic: PostgreSQL sorts and compares enum values by declared position, so appending
  -- a value the package puts in the middle would give the type a different meaning from the one declared.
  RAISE NOTICE 'Add Missing Enum Values';
  SELECT STRING_AGG('RAISE NOTICE ''  Add enum value ' || REPLACE(m.val, '''', '''''') || ' to ' || m."Schema" || '.' || m."Name" || ''';' || CHR(10) ||
                    'ALTER TYPE "' || m."Schema" || '"."' || m."Name" || '" ADD VALUE IF NOT EXISTS ''' ||
                    REPLACE(m.val, '''', '''''') || '''' ||
                    CASE WHEN m.next_existing IS NOT NULL
                         THEN ' BEFORE ''' || REPLACE(m.next_existing, '''', '''''') || ''''
                         ELSE '' END || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") ' ||
                    'VALUES (pg_backend_pid(), ''enum type'', ''' || m."Schema" || '.' || m."Name" || ''', ''modified'');', CHR(10) ORDER BY m."Schema", m."Name", m.ord)
    INTO sql_script
    FROM (SELECT t."Schema", t."Name", u.val, u.ord,
                 -- The first DECLARED value after this one that ALREADY exists on the server. Inserting
                 -- BEFORE that lands the new value in its declared position. NULL means nothing declared
                 -- after it exists yet, so appending is correct.
                 (SELECT u2.val
                    FROM UNNEST(t."Values") WITH ORDINALITY AS u2(val, ord)
                   WHERE u2.ord > u.ord
                     AND EXISTS (SELECT 1 FROM pg_enum e2
                                   JOIN pg_type ty2 ON ty2.oid = e2.enumtypid
                                   JOIN pg_namespace n2 ON n2.oid = ty2.typnamespace
                                  WHERE ty2.typname = t."Name" AND n2.nspname = t."Schema"
                                    AND e2.enumlabel = u2.val)
                   ORDER BY u2.ord LIMIT 1) AS next_existing
            FROM temp_enum_types t
            CROSS JOIN LATERAL UNNEST(t."Values") WITH ORDINALITY AS u(val, ord)
           WHERE EXISTS (SELECT 1 FROM pg_type ty
                           JOIN pg_namespace n ON n.oid = ty.typnamespace
                          WHERE ty.typname = t."Name" AND n.nspname = t."Schema" AND ty.typtype = 'e')
             AND NOT EXISTS (SELECT 1 FROM pg_enum e
                               JOIN pg_type ty ON ty.oid = e.enumtypid
                               JOIN pg_namespace n ON n.oid = ty.typnamespace
                              WHERE ty.typname = t."Name" AND n.nspname = t."Schema"
                                AND e.enumlabel = u.val)) m;
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);

  -- A value the server has and the package no longer lists. PostgreSQL cannot remove one without
  -- recreating the type, which would mean dropping every column using it -- so this is REPORTED, loudly
  -- and by name, and never attempted. A 'wouldDrop' row makes it visible in the manifest either way.
  FOR bad IN
    SELECT t."Schema", t."Name", e.enumlabel AS val
      FROM temp_enum_types t
      JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'e'
      JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
      JOIN pg_enum e ON e.enumtypid = ty.oid
     WHERE NOT (e.enumlabel = ANY(t."Values"))
     ORDER BY t."Schema", t."Name", e.enumsortorder
  LOOP
    RAISE WARNING 'Enum type %.% has value ''%'' which the package no longer declares. PostgreSQL cannot remove an enum value without recreating the type (and dropping every column that uses it), so it is left in place — remove it by hand, or restore it to the package.',
      bad."Schema", bad."Name", bad.val;
    IF NOT p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        VALUES (pg_backend_pid(), 'enum value', bad."Schema" || '.' || bad."Name" || '.' || bad.val, 'wouldDrop');
    END IF;
  END LOOP;

  -- A pure REORDER of existing values: the values present on BOTH sides appear in a different relative order
  -- than the package declares. PostgreSQL sorts and compares enum values by declared position
  -- (enumsortorder), so order is behavioural (ORDER BY / < > / MIN/MAX on an enum column all depend on it) --
  -- but it cannot be changed without recreating the type (dropping every dependent column), exactly like a
  -- value removal. So it is REPORTED by name, never performed. Compares only the COMMON values' relative
  -- order, so a value legitimately added mid-list (handled above) is not mistaken for a reorder.
  FOR bad IN
    SELECT t."Schema", t."Name"
      FROM temp_enum_types t
      JOIN pg_type ty ON ty.typname = t."Name" AND ty.typtype = 'e'
      JOIN pg_namespace n ON n.oid = ty.typnamespace AND n.nspname = t."Schema"
     WHERE (SELECT ARRAY_AGG(d.v ORDER BY d.ord)
              FROM UNNEST(t."Values") WITH ORDINALITY AS d(v, ord)
             WHERE EXISTS (SELECT 1 FROM pg_enum e WHERE e.enumtypid = ty.oid AND e.enumlabel = d.v))
           IS DISTINCT FROM
           (SELECT ARRAY_AGG(e.enumlabel::TEXT ORDER BY e.enumsortorder)
              FROM pg_enum e
             WHERE e.enumtypid = ty.oid AND e.enumlabel = ANY(t."Values"))
     ORDER BY t."Schema", t."Name"
  LOOP
    RAISE WARNING 'Enum type %.% declares its values in a different order than the server has. PostgreSQL sorts and compares enum values by declared position, so the order is behavioural — but it cannot be changed without recreating the type (and dropping every column that uses it), so it is left as-is. Recreate the type by hand to reorder.',
      bad."Schema", bad."Name";
    IF NOT p_WhatIf THEN
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        VALUES (pg_backend_pid(), 'enum type', bad."Schema" || '.' || bad."Name", 'wouldModify');
    END IF;
  END LOOP;
END $$;
