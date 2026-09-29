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
           -- An editor that quotes identifiers stores OldName WITH the quotes -- "users", not users.
           -- PostgreSQL takes an authored name verbatim, so the rename match downstream found nothing,
           -- NO rename happened, the new name was created empty and the old object survived
           -- unreferenced -- and a later deploy with a Drop...RemovedFromProduct flag then dropped it
           -- WITH ITS DATA. Strip one wrapping pair here, once, so the match and the emitted DDL agree.
           -- SQL Server and MySQL already unwrap at their rename sites (fn_StripBracketWrapping /
           -- SchemaSmith_StripBacktickWrapping); PostgreSQL was the engine that did not.
           -- Collapse "" to " ONLY when a wrapper was stripped. Inside a quoted PostgreSQL
           -- identifier "" denotes one ", so the escaped form "a""b" names the column the catalog
           -- stores as a"b -- and stripping the wrapper alone left a""b, which matches nothing and
           -- silently skips the rename exactly as the unwrapped case did. Outside a wrapper the
           -- doubling is literal, so it is deliberately NOT collapsed there.
           CASE WHEN (elem ->> 'OldName') LIKE '"%"' AND LENGTH(elem ->> 'OldName') > 1
                THEN REPLACE(SUBSTRING((elem ->> 'OldName'), 2, LENGTH(elem ->> 'OldName') - 2), '""', '"')
                ELSE COALESCE(elem ->> 'OldName', '') END AS "OldName",
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
           (celem ->> 'Nullable') IS NOT NULL AS "NullableDeclared",
           COALESCE(celem ->> 'Default', '') AS "Default",
           COALESCE(celem ->> 'Collation', '') AS "Collation",
           -- An expression is only ever a generated column on PostgreSQL, so an omitted Generated beside one means
           -- ALWAYS. Reading it as NEVER built a plain column that every later comparison then read as generated.
           CASE WHEN NULLIF(celem ->> 'Generated', '') IS NOT NULL THEN celem ->> 'Generated'
                WHEN NULLIF(celem ->> 'GenerationExpression', '') IS NOT NULL THEN 'ALWAYS'
                ELSE 'NEVER' END AS "Generated",
           COALESCE(celem ->> 'GenerationExpression', '') AS "GenerationExpression",
           COALESCE((celem ->> 'Virtual')::BOOLEAN, false) AS "Virtual",
           COALESCE(celem ->> 'Storage', '') AS "Storage",
           COALESCE(celem ->> 'Compression', '') AS "Compression",
           COALESCE(celem ->> 'ShouldApplyExpression', '') AS "ShouldApplyExpression",
           COALESCE(celem ->> 'VariantName', '') AS "VariantName",
           -- An editor that quotes identifiers stores OldName WITH the quotes -- "users", not users.
           -- PostgreSQL takes an authored name verbatim, so the rename match downstream found nothing,
           -- NO rename happened, the new name was created empty and the old object survived
           -- unreferenced -- and a later deploy with a Drop...RemovedFromProduct flag then dropped it
           -- WITH ITS DATA. Strip one wrapping pair here, once, so the match and the emitted DDL agree.
           -- SQL Server and MySQL already unwrap at their rename sites (fn_StripBracketWrapping /
           -- SchemaSmith_StripBacktickWrapping); PostgreSQL was the engine that did not.
           -- Collapse "" to " ONLY when a wrapper was stripped. Inside a quoted PostgreSQL
           -- identifier "" denotes one ", so the escaped form "a""b" names the column the catalog
           -- stores as a"b -- and stripping the wrapper alone left a""b, which matches nothing and
           -- silently skips the rename exactly as the unwrapped case did. Outside a wrapper the
           -- doubling is literal, so it is deliberately NOT collapsed there.
           CASE WHEN (celem ->> 'OldName') LIKE '"%"' AND LENGTH(celem ->> 'OldName') > 1
                THEN REPLACE(SUBSTRING((celem ->> 'OldName'), 2, LENGTH(celem ->> 'OldName') - 2), '""', '"')
                ELSE COALESCE(celem ->> 'OldName', '') END AS "OldName",
           COALESCE(celem ->> 'CheckExpression', '') AS "CheckExpression"
      FROM my_tables, JSON_ARRAY_ELEMENTS(arr) AS elem
      CROSS JOIN LATERAL JSON_ARRAY_ELEMENTS((elem ->> 'Columns')::JSON) AS celem(value);

    -- A generated column's nullability is the engine's unless the package states one, as on SQL Server. Taking the
    -- live value (nullable for a new column) makes every comparison downstream agree without touching any of them;
    -- reading an omission as NOT NULL narrowed the column a deploy late, and failed outright on rows whose
    -- expression is NULL.
    UPDATE temp_columns tc
       SET "Nullable" = COALESCE((SELECT NOT a.attnotnull
                                    FROM pg_attribute a
                                   WHERE a.attrelid = to_regclass('"' || tc."TableSchema" || '"."' || tc."TableName" || '"')
                                     AND a.attname = tc."Name" AND a.attnum > 0 AND NOT a.attisdropped), TRUE)
     WHERE NOT tc."NullableDeclared"
       AND tc."Generated" = 'ALWAYS' AND COALESCE(tc."GenerationExpression", '') <> '';

    -- A column typed by an enum, domain or composite type is compared against the catalog's own spelling of it --
    -- schema-qualified, identifiers quoted where they need it, and a domain always quoted -- so any other spelling
    -- re-altered the column on every deploy, and a bare name did not resolve at all outside the search path (a
    -- schema-template tenant table typed by its own tenant's enum failed with 42704). Resolve each user-defined type
    -- name once, here, and spell it as the catalog does, so the DDL finds it and every comparison agrees:
    --   * a qualified name as written, quoted or not;
    --   * a bare name first to the type the live column already has, if that is a candidate -- re-resolving an
    --     existing column to a different same-named type would change its type -- then the table's own schema,
    --     then public.
    -- Only real enums, domains and standalone composites qualify: every table and view also has a row type
    -- (typtype 'c'), and the SQL-standard type spellings (serial, character, ...) are not catalog names, so a
    -- table named like one must never capture a column declared with it. Arrays and typmods are left alone.
    UPDATE temp_columns tc
       SET "DataType" = r."Rendered"
      FROM (SELECT c."_RowId",
                   (SELECT CASE WHEN ty.typtype = 'd' THEN '"' || ns.nspname || '"."' || ty.typname || '"'
                                ELSE QUOTE_IDENT(ns.nspname) || '.' || QUOTE_IDENT(ty.typname) END
                      FROM pg_type ty
                      JOIN pg_namespace ns ON ns.oid = ty.typnamespace
                      LEFT JOIN pg_class rc ON rc.oid = ty.typrelid
                     WHERE (ty.typtype IN ('e', 'd') OR (ty.typtype = 'c' AND rc.relkind = 'c'))
                       AND ty.typname = COALESCE(m[3], LOWER(m[4]))
                       AND ns.nspname = ANY (CASE WHEN m[1] IS NOT NULL OR m[2] IS NOT NULL
                                                  THEN ARRAY[COALESCE(m[1], LOWER(m[2]))]
                                                  ELSE ARRAY[live.nspname, c."TableSchema", 'public'] END)
                       AND NOT EXISTS (SELECT 1 FROM pg_type bt JOIN pg_namespace bn ON bn.oid = bt.typnamespace
                                        WHERE bn.nspname = 'pg_catalog' AND bt.typname = ty.typname)
                       AND LOWER(ty.typname) NOT IN ('int', 'integer', 'bigint', 'smallint', 'boolean', 'bool',
                                                     'real', 'float', 'double', 'decimal', 'dec', 'numeric',
                                                     'serial', 'bigserial', 'smallserial', 'serial2', 'serial4',
                                                     'serial8', 'character', 'char', 'varchar', 'nchar',
                                                     'national', 'time', 'timestamp', 'interval')
                     ORDER BY ns.nspname = live.nspname DESC NULLS LAST, ns.nspname = c."TableSchema" DESC
                     LIMIT 1) AS "Rendered"
              FROM temp_columns c
              CROSS JOIN LATERAL REGEXP_MATCH(TRIM(c."DataType"),
                   '^(?:(?:"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))\.)?(?:"([^"]+)"|([A-Za-z_][A-Za-z0-9_]*))$') AS m
              LEFT JOIN LATERAL (SELECT lns.nspname
                                   FROM pg_attribute la
                                   JOIN pg_type lt ON lt.oid = la.atttypid
                                   JOIN pg_namespace lns ON lns.oid = lt.typnamespace
                                  WHERE la.attrelid = TO_REGCLASS(QUOTE_IDENT(c."TableSchema") || '.' || QUOTE_IDENT(c."TableName"))
                                    AND la.attname = c."Name" AND NOT la.attisdropped
                                    AND lt.typname = COALESCE(m[3], LOWER(m[4]))) AS live ON TRUE
             WHERE COALESCE(c."Generated", 'NEVER') NOT LIKE 'GENERATED%IDENTITY%') r
     WHERE tc."_RowId" = r."_RowId"
       AND r."Rendered" IS NOT NULL;

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

