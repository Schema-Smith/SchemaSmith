-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

    DROP TABLE IF EXISTS temp_tables;

    -- "_RowId" gives each parsed row a unique identifier so the per-row ShouldApply DELETE
    -- below targets exactly the source row whose expression evaluated false. Without it,
    -- the DELETE matched on ("Schema", "Name") and would silently wipe both rows when two
    -- entries shared a name with mutually exclusive ShouldApply expressions.
    CREATE TEMPORARY TABLE temp_tables AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "Schema",
           elem ->> 'Name' AS "Name",
           COALESCE(elem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(elem ->> 'VariantName', '') AS "VariantName",
           COALESCE(elem ->> 'OldName', '') AS "OldName",
           COALESCE((elem ->> 'RowLevelSecurity')::BOOLEAN, false) AS "RowLevelSecurity",
           COALESCE((elem ->> 'ForceRowLevelSecurity')::BOOLEAN, false) AS "ForceRowLevelSecurity",
           COALESCE(elem ->> 'AccessMethod', '') AS "AccessMethod",
           -- Empty means "placement not managed here", NOT "the database default" -- the FileGroup
           -- contract. Treating unset as the default fails every object a DBA placed elsewhere on its
           -- SECOND deploy, in packages that never mentioned placement.
           COALESCE(elem ->> 'Tablespace', '') AS "Tablespace",
           COALESCE(elem ->> 'PersistenceType', '') AS "PersistenceType",
           -- Empty string means "not declared, leave the server alone", the AccessMethod convention. #407
           COALESCE(UPPER(elem ->> 'ReplicaIdentity'), '') AS "ReplicaIdentity",
           COALESCE(elem ->> 'ReplicaIdentityIndex', '') AS "ReplicaIdentityIndex",
           CASE WHEN p_UpdateFillFactor THEN true ELSE COALESCE((elem ->> 'UpdateFillFactor')::BOOLEAN, false) END AS "UpdateFillFactor",
           COALESCE(NULLIF((elem ->> 'FillFactor')::INT2, 0), 100) AS "FillFactor",
           COALESCE((elem ->> 'PreventDrop')::BOOLEAN, FALSE) AS "PreventDrop",
           (elem ->> 'DropColumnsRemovedFromProduct')::BOOLEAN AS "DropColumnsRemovedFromProduct",
           (elem ->> 'DropForeignKeysRemovedFromProduct')::BOOLEAN AS "DropForeignKeysRemovedFromProduct",
           (elem ->> 'DropCheckConstraintsRemovedFromProduct')::BOOLEAN AS "DropCheckConstraintsRemovedFromProduct",
           (elem ->> 'DropExcludeConstraintsRemovedFromProduct')::BOOLEAN AS "DropExcludeConstraintsRemovedFromProduct",
           (elem ->> 'DropStatisticsRemovedFromProduct')::BOOLEAN AS "DropStatisticsRemovedFromProduct",
           (elem ->> 'DropIndexesRemovedFromProduct')::BOOLEAN AS "DropIndexesRemovedFromProduct",
           -- RebuildPolicy resolves MOST-SPECIFIC-WINS on the WHOLE object (ProductQuench.ResolveCascadedPolicy),
           -- so the apply side needs to know whether this table declared one AT ALL -- not just what its
           -- fields say. "RebuildPolicySpecified" is that sentinel. It tests the value's TYPE rather than
           -- mere key presence: an undeclared policy serializes as '"RebuildPolicy": null', and a key-
           -- containment test (jsonb ?) would read that null as a declaration and stop the product- or
           -- environment-level policy from applying. JSON_TYPEOF returns 'null' there and NULL when the key
           -- is absent entirely, so both fall out as FALSE.
           elem #>> '{RebuildPolicy,Mode}' AS "RebuildPolicyMode",
           (elem #>> '{RebuildPolicy,Threshold}')::INT AS "RebuildPolicyThreshold",
           (elem #>> '{RebuildPolicy,OnOrderMismatch}')::BOOLEAN AS "RebuildPolicyOnOrderMismatch",
           COALESCE(JSON_TYPEOF(elem -> 'RebuildPolicy') = 'object', FALSE) AS "RebuildPolicySpecified"
    FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem;

    -- ShouldApply scoped by "_RowId" so each generated DELETE targets exactly the source row.
    SELECT STRING_AGG('DELETE FROM temp_tables WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_tables
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_columns;
    CREATE TEMPORARY TABLE temp_columns AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           COALESCE(celem ->> 'DataType', '') AS "DataType",
           COALESCE((celem ->> 'Nullable')::BOOLEAN, false) AS "Nullable",
           COALESCE(celem ->> 'Default', '') AS "Default",
           COALESCE(celem ->> 'Collation', '') AS "Collation",
           COALESCE(celem ->> 'Generated', 'NEVER') AS "Generated",
           COALESCE(celem ->> 'GenerationExpression', '') AS "GenerationExpression",
           COALESCE((celem ->> 'Virtual')::BOOLEAN, false) AS "Virtual",
           COALESCE(celem ->> 'Storage', '') AS "Storage",
           COALESCE(celem ->> 'Compression', '') AS "Compression",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName",
           COALESCE(celem ->> 'OldName', '') AS "OldName",
           COALESCE(celem ->> 'CheckExpression', '') AS "CheckExpression"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Columns')::JSON) AS celem(value);

    -- PostgreSQL names an array type _element in the catalog, and that is what extraction used to emit, so
    -- packages in the wild carry both spellings: "_text" and "text[]". They mean the same column. Fold the
    -- catalog spelling to the SQL one FIRST, so the synonym mapping below sees a normal element name and
    -- whichever spelling a package happens to use stops re-modifying the column on every deploy.
    UPDATE temp_columns
       SET "DataType" = REGEXP_REPLACE("DataType", '^_(.+)$', '\1[]')
     WHERE "DataType" ~ '^_';

    -- Synonym mapping
    UPDATE temp_columns
       SET "DataType" = CASE WHEN TRIM(UPPER("DataType")) = 'BIGINT' THEN 'INT8'
                             WHEN TRIM(UPPER("DataType")) = 'BIGINT[]' THEN 'INT8[]'
                             WHEN TRIM(UPPER("DataType")) = 'BOOLEAN[]' THEN 'BOOL[]'
                             WHEN TRIM(UPPER("DataType")) = 'DOUBLE PRECISION[]' THEN 'FLOAT8[]'
                             WHEN TRIM(UPPER("DataType")) = 'FLOAT[]' THEN 'FLOAT8[]'
                             WHEN TRIM(UPPER("DataType")) = 'INTEGER[]' THEN 'INT4[]'
                             WHEN TRIM(UPPER("DataType")) = 'INT[]' THEN 'INT4[]'
                             WHEN TRIM(UPPER("DataType")) = 'REAL[]' THEN 'FLOAT4[]'
                             WHEN TRIM(UPPER("DataType")) = 'SMALLINT[]' THEN 'INT2[]'
                             WHEN TRIM(UPPER("DataType")) = 'BIGSERIAL' THEN 'SERIAL8'
                             WHEN TRIM(UPPER("DataType")) = 'BOOLEAN' THEN 'BOOL'
                             WHEN TRIM(UPPER("DataType")) = 'DOUBLE PRECISION' THEN 'FLOAT8'
                             WHEN TRIM(UPPER("DataType")) = 'FLOAT' THEN 'FLOAT8'
                             WHEN TRIM(UPPER("DataType")) = 'INTEGER' THEN 'INT4'
                             WHEN TRIM(UPPER("DataType")) = 'INT' THEN 'INT4'
                             WHEN TRIM(UPPER("DataType")) = 'REAL' THEN 'FLOAT4'
                             WHEN TRIM(UPPER("DataType")) = 'SMALLINT' THEN 'INT2'
                             WHEN TRIM(UPPER("DataType")) = 'SMALLSERIAL' THEN 'SERIAL2'
                             WHEN TRIM(UPPER("DataType")) = 'SERIAL' THEN 'SERIAL4'
                             WHEN "DataType" ILIKE 'bit varying%' THEN REGEXP_REPLACE("DataType", 'bit varying', 'VARBIT', 'i')
                             WHEN "DataType" ILIKE 'character varying%' THEN REGEXP_REPLACE("DataType", 'character varying', 'VARCHAR', 'i')
                             WHEN "DataType" ILIKE 'character%' THEN REGEXP_REPLACE("DataType", 'character', 'CHAR', 'i')
                             WHEN "DataType" ILIKE 'decimal%' THEN REGEXP_REPLACE("DataType", 'decimal', 'NUMERIC', 'i')
                             WHEN "DataType" ILIKE 'bpchar%' THEN REGEXP_REPLACE("DataType", 'bpchar', 'CHAR', 'i')
                             -- The four SQL-standard datetime spellings. The catalog reports udt_name --
                             -- timestamptz, timetz, timestamp, time -- and these were the gap this list's
                             -- own note predicted: it "was written against reported cases, not derived from
                             -- the engine's synonym table". Auditing it against the engine found all four
                             -- churning, and they are not exotic spellings: they are what the SQL standard
                             -- says, what pg_dump writes, and what most ORMs generate.
                             --
                             -- Matched by regex rather than by literal, because the precision sits in the
                             -- MIDDLE -- timestamp(3) with time zone -- so no whole-string equality or
                             -- prefix rule reaches them. The precision is carried across verbatim and an
                             -- array suffix is preserved, since _timestamptz has already been folded to
                             -- timestamptz[] by the pass above.
                             WHEN "DataType" ~* '^\s*timestamp\s*(\([0-9]+\))?\s*with\s+time\s+zone\s*(\[\])?\s*$'
                                  THEN 'TIMESTAMPTZ' || COALESCE(SUBSTRING("DataType" FROM '\([0-9]+\)'), '')
                                       || CASE WHEN "DataType" LIKE '%[]%' THEN '[]' ELSE '' END
                             WHEN "DataType" ~* '^\s*timestamp\s*(\([0-9]+\))?\s*without\s+time\s+zone\s*(\[\])?\s*$'
                                  THEN 'TIMESTAMP' || COALESCE(SUBSTRING("DataType" FROM '\([0-9]+\)'), '')
                                       || CASE WHEN "DataType" LIKE '%[]%' THEN '[]' ELSE '' END
                             WHEN "DataType" ~* '^\s*time\s*(\([0-9]+\))?\s*with\s+time\s+zone\s*(\[\])?\s*$'
                                  THEN 'TIMETZ' || COALESCE(SUBSTRING("DataType" FROM '\([0-9]+\)'), '')
                                       || CASE WHEN "DataType" LIKE '%[]%' THEN '[]' ELSE '' END
                             WHEN "DataType" ~* '^\s*time\s*(\([0-9]+\))?\s*without\s+time\s+zone\s*(\[\])?\s*$'
                                  THEN 'TIME' || COALESCE(SUBSTRING("DataType" FROM '\([0-9]+\)'), '')
                                       || CASE WHEN "DataType" LIKE '%[]%' THEN '[]' ELSE '' END
                             ELSE "DataType" END;

    -- Family defaults, applied AFTER the synonym fold so both spellings of a type reach one rule.
    -- ColumnTypeArguments renders the catalog side bare when the value equals the family default, so an
    -- explicitly-declared default has to be dropped here or it compares unequal to its own deployment --
    -- and the column is re-altered on every deploy for declaring what it already is.
    --   6 is the datetime family's default precision; 1 is BIT's default length ('bit' IS 'bit(1)').
    -- BIT VARYING is deliberately excluded: bare 'bit varying' is UNLIMITED, so 'bit varying(1)' is a
    -- different type, not a verbose spelling of the same one.
    UPDATE temp_columns
       SET "DataType" = REGEXP_REPLACE("DataType", '\(6\)', '')
     WHERE "DataType" ~* '^(TIMESTAMPTZ|TIMESTAMP|TIMETZ|TIME)\(6\)';

    UPDATE temp_columns
       SET "DataType" = REGEXP_REPLACE("DataType", '\(1\)', '')
     WHERE "DataType" ~* '^BIT\(1\)';

    SELECT STRING_AGG('DELETE FROM temp_columns WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_columns
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_indexes;
    CREATE TEMPORARY TABLE temp_indexes AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           COALESCE((celem ->> 'PrimaryKey')::BOOLEAN, false) AS "PrimaryKey",
           COALESCE((celem ->> 'Unique')::BOOLEAN, false) AS "Unique",
           COALESCE((celem ->> 'UniqueConstraint')::BOOLEAN, false) AS "UniqueConstraint",
           COALESCE((celem ->> 'Clustered')::BOOLEAN, false) AS "Clustered",
           REGEXP_REPLACE(COALESCE(celem ->> 'IndexColumns', ''), '\s*,\s*', ',', 'g') AS "IndexColumns",
           COALESCE(celem ->> 'IncludeColumns', '') AS "IncludeColumns",
           COALESCE(celem ->> 'AccessMethod', 'btree') AS "AccessMethod",
           COALESCE(celem ->> 'Tablespace', '') AS "Tablespace",
           COALESCE(celem ->> 'FilterExpression', '') AS "FilterExpression",
           COALESCE((celem ->> 'Deferrable')::BOOLEAN, false) AS "Deferrable",
           COALESCE((celem ->> 'InitiallyDeferred')::BOOLEAN, false) AS "InitiallyDeferred",
           -- Below PG15 NULLS NOT DISTINCT does not exist: the effective column drives compare + emit
           -- (coerced false so an old target neither churns nor emits an unsupported clause); the raw
           -- declared value drives the unsupported-feature policy (fail | warn-with-downgrade).
           CASE WHEN "SchemaSmith"."ServerVersionNum"() >= 15 THEN COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) ELSE false END AS "NullsNotDistinct",
           COALESCE((celem ->> 'NullsNotDistinct')::BOOLEAN, false) AS "NullsNotDistinctDeclared",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName",
           CASE WHEN p_UpdateFillFactor THEN true ELSE COALESCE((celem ->> 'UpdateFillFactor')::BOOLEAN, false) END AS "UpdateFillFactor",
           COALESCE(NULLIF((celem ->> 'FillFactor')::INT2, 0), 90) AS "FillFactor",
           -- Index storage parameters (the WITH clause), canonicalised as key=value pairs sorted by key so
           -- reloptions' own ordering does not matter, with fillfactor excluded (FillFactor owns it). This is
           -- what carries a vector index's m / ef_construction / lists. Same shape as IndexOnlyQuench's own
           -- temp_indexes -- both must declare it because they build the table independently.
           COALESCE((SELECT STRING_AGG(sp.k || '=' || sp.v, ',' ORDER BY sp.k)
                       FROM JSON_EACH_TEXT(COALESCE(celem -> 'StorageParameters', '{}'::JSON)) AS sp(k, v)
                      WHERE sp.k <> 'fillfactor'), '') AS "StorageParameters"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Indexes')::JSON) AS celem(value);

    UPDATE temp_indexes -- Table-level setting overrides index-level setting when true
      SET "UpdateFillFactor" = true
      WHERE NOT "UpdateFillFactor"
        AND EXISTS (SELECT *
                      FROM temp_tables t
                      WHERE t."Schema" = "TableSchema"
                        AND t."Name" = "TableName"
                        AND t."UpdateFillFactor" = true);

    SELECT STRING_AGG('DELETE FROM temp_indexes WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_indexes
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_checks;
    CREATE TEMPORARY TABLE temp_checks AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           COALESCE(celem ->> 'Expression', '') AS "Expression",
           COALESCE((celem ->> 'Deferrable')::BOOLEAN, false) AS "Deferrable",
           COALESCE((celem ->> 'InitiallyDeferred')::BOOLEAN, false) AS "InitiallyDeferred",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'CheckConstraints')::JSON) AS celem(value);

    SELECT STRING_AGG('DELETE FROM temp_checks WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_checks
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_fks;
    CREATE TEMPORARY TABLE temp_fks AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           celem ->> 'Columns' AS "Columns",
           celem ->> 'RelatedTableSchema' AS "RelatedTableSchema",
           celem ->> 'RelatedTable' AS "RelatedTable",
           celem ->> 'RelatedColumns' AS "RelatedColumns",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName",
           -- 'NO ACTION' is a legal literal per the domain pattern, but extraction (confdeltype/confupdtype
           -- code 'a') always renders the default action as '' -- normalize the alias here, on the declared
           -- side only, so a package can spell it either way without churning against every '' package already
           -- on disk.
           COALESCE(NULLIF(celem ->> 'DeleteAction', 'NO ACTION'), '') AS "DeleteAction",
           COALESCE(NULLIF(celem ->> 'UpdateAction', 'NO ACTION'), '') AS "UpdateAction",
           COALESCE((celem ->> 'Deferrable')::BOOLEAN, false) AS "Deferrable",
           COALESCE((celem ->> 'InitiallyDeferred')::BOOLEAN, false) AS "InitiallyDeferred",
           celem ->> 'MatchType' AS "MatchType"
    FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'ForeignKeys')::JSON) AS celem(value);

    SELECT STRING_AGG('DELETE FROM temp_fks WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_fks
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_statistics;
    CREATE TEMPORARY TABLE temp_statistics AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           COALESCE(celem ->> 'Kind', '') AS "Kind",
           COALESCE(celem ->> 'StatisticsColumns', '') AS "StatisticsColumns",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Statistics')::JSON) AS celem(value);

    SELECT STRING_AGG('DELETE FROM temp_statistics WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_statistics
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_excludes;
    CREATE TEMPORARY TABLE temp_excludes AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           celem ->> 'Name' AS "Name",
           (celem ->> 'ExcludeColumns')::JSON AS "ExcludeColumns",
           COALESCE(celem ->> 'AccessMethod', '') AS "AccessMethod",
           COALESCE(celem ->> 'FilterExpression', '') AS "FilterExpression",
           COALESCE((celem ->> 'Deferrable')::BOOLEAN, false) AS "Deferrable",
           COALESCE((celem ->> 'InitiallyDeferred')::BOOLEAN, false) AS "InitiallyDeferred",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'ExcludeConstraints')::JSON) AS celem(value);

    SELECT STRING_AGG('DELETE FROM temp_excludes WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_excludes
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

    DROP TABLE IF EXISTS temp_policies;
    CREATE TEMPORARY TABLE temp_policies AS
    WITH my_tables(arr) AS (VALUES(table_json::JSON))
    SELECT ROW_NUMBER() OVER () AS "_RowId",
           elem ->> 'Schema' AS "TableSchema",
           elem ->> 'Name' AS "TableName",
           pelem ->> 'Name' AS "Name",
           UPPER(COALESCE(pelem ->> 'Permissive', 'PERMISSIVE')) AS "Permissive",
           UPPER(COALESCE(pelem ->> 'Command', 'ALL')) AS "Command",
           COALESCE(NULLIF(pelem ->> 'Roles', ''), 'PUBLIC') AS "Roles",
           COALESCE(pelem ->> 'UsingExpression', '') AS "UsingExpression",
           COALESCE(pelem ->> 'WithCheckExpression', '') AS "WithCheckExpression",
           COALESCE(pelem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(pelem ->> 'VariantName', '') AS "VariantName"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Policies')::JSON) AS pelem(value);

    SELECT STRING_AGG('DELETE FROM temp_policies WHERE "_RowId" = ' || "_RowId"::TEXT || ' AND NOT (' || "SchemaSmith"."StripLeadingSelect"("ShouldApplyExpression") || ');', CHR(10))
      INTO sql_script
      FROM temp_policies
      WHERE NULLIF("ShouldApplyExpression", '') IS NOT NULL;
    CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, false);

