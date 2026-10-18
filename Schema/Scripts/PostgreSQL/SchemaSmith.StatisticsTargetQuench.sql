-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The target of every declared extended-statistics object (ALTER STATISTICS ... SET STATISTICS n, PostgreSQL 13),
-- converged once the object exists: a declared target that differs from the live one is set in place. An undeclared
-- target (NULL) is unmanaged and leaves whatever is set alone. Below 13 the statement does not exist, so a declared
-- target follows the unsupported-feature policy: 'fail' aborts naming the objects, 'warn' (default) records a
-- downgrade per object and the object keeps the engine's default. Called by MissingIndexesAndConstraintsQuench and
-- IndexOnlyQuench after their create pass, from the temp_statistics they both build.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."StatisticsTargetQuench"(p_WhatIf BOOLEAN)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
DECLARE
  sql_script TEXT;
BEGIN
  IF "SchemaSmith"."ServerVersionNum"() < 13 THEN
    IF "SchemaSmith"."UnsupportedFeaturePolicy"() = 'fail'
       AND EXISTS (SELECT 1 FROM temp_statistics WHERE "StatisticsTarget" IS NOT NULL) THEN
      RAISE EXCEPTION 'A statistics target requires PostgreSQL 13 (detected major %); statistic(s): %',
        "SchemaSmith"."ServerVersionNum"(),
        (SELECT STRING_AGG("TableSchema" || '.' || "TableName" || '.' || "Name", ', ')
           FROM temp_statistics WHERE "StatisticsTarget" IS NOT NULL);
    ELSE
      INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType")
        SELECT pg_backend_pid(), 'statistics target (PG13)',
               "TableSchema" || '.' || "TableName" || '.' || "Name", 'downgraded'
          FROM temp_statistics
          WHERE "StatisticsTarget" IS NOT NULL;
    END IF;
    RETURN;
  END IF;

  RAISE NOTICE 'Converge Statistics Targets';
  -- stxstattarget is read through to_jsonb so this procedure also compiles below 13, where the column is absent;
  -- -1 (before 17) and NULL (17+) both mean the default.
  SELECT STRING_AGG('RAISE NOTICE ''  Set statistics target of ' || ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name" || ' to ' || ts."StatisticsTarget" || ''';' || CHR(10) ||
                    'ALTER STATISTICS ' || "SchemaSmith"."QuoteName"(COALESCE(ts."StatisticsSchema", ts."TableSchema")) || '.' || "SchemaSmith"."QuoteName"(ts."Name") || ' SET STATISTICS ' || ts."StatisticsTarget" || ';' || CHR(10) ||
                    'INSERT INTO "SchemaSmith"."ChangeAudit" ("SessionId", "ObjectType", "ObjectName", "ActionType") VALUES (pg_backend_pid(), ''statistic'', ''' || ts."TableSchema" || '.' || ts."TableName" || '.' || ts."Name" || ''', ''modified'');', CHR(10))
    INTO sql_script
    FROM temp_statistics ts
    JOIN pg_namespace sn ON sn.nspname = COALESCE(ts."StatisticsSchema", ts."TableSchema")
    JOIN pg_statistic_ext se ON se.stxnamespace = sn.oid AND se.stxname = ts."Name"
   WHERE ts."StatisticsTarget" IS NOT NULL
     AND ts."StatisticsTarget" IS DISTINCT FROM NULLIF((to_jsonb(se) ->> 'stxstattarget')::int, -1);
  CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
  IF p_WhatIf THEN CALL "SchemaSmith"."RecordWouldChanges"(sql_script); END IF;
END $$;
