-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- The two-argument form must go explicitly: CREATE OR REPLACE with a new parameter creates an
-- OVERLOAD rather than replacing, and a two-argument call would then be ambiguous between the old
-- function and the new one's default -- an error, on a kindled database that used to work.
DROP FUNCTION IF EXISTS "SchemaSmith"."GenerateTableJSON"(varchar, varchar);

-- p_ObjectOrder: 'Name' (default, alphabetical) or 'Physical' (the table's own column order).
-- COLUMNS ONLY, and that is the whole story rather than a layering detail. Indexes, foreign keys,
-- check constraints and extended statistics are ALWAYS emitted in name order below, whatever this
-- argument says: 'Physical' has no meaning for a set with no physical sequence. This function is the
-- only thing that orders them -- nothing re-sequences the lists after it returns. A comment here used
-- to say the caller did, and it was describing a helper that had no callers.
CREATE OR REPLACE FUNCTION "SchemaSmith"."GenerateTableJSON"(p_Schema varchar(200), p_Table varchar(200), p_ObjectOrder varchar(20) DEFAULT 'Name')
  RETURNS text
  LANGUAGE plpgsql
AS $function$
DECLARE result_string TEXT;
        skipped_fk RECORD;
        invalid_not_null RECORD;
BEGIN
-- PostgreSQL 18 records a NOT NULL constraint in pg_constraint (contype 'n'), and it can be NOT VALID: the rows are not
-- yet checked. The package carries only the column's Nullable, which deploys SET NOT NULL and fails while a row holds
-- NULL -- the engine telling the truth about data the constraint has not vouched for. Said at extraction, where the
-- package is made. On every earlier version no row has that contype and the loop is empty.
FOR invalid_not_null IN SELECT con.conname, a.attname
                          FROM pg_catalog.pg_constraint con
                          JOIN pg_catalog.pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = con.conkey[1]
                         WHERE con.conrelid = ("SchemaSmith"."QuoteName"(p_Schema) || '.' || "SchemaSmith"."QuoteName"(p_Table))::regclass
                           AND con.contype = 'n'
                           AND NOT con.convalidated
                         ORDER BY con.conname LOOP
  RAISE WARNING 'NOT NULL constraint %.%.% on column % is NOT VALID: the package declares the column NOT NULL and the deploy sets it so, which fails while any row holds NULL. Validate the constraint or fix the rows before deploying.',
    p_Schema, p_Table, invalid_not_null.conname, invalid_not_null.attname;
END LOOP;
-- A temporal foreign key (PostgreSQL 18, FOREIGN KEY (..., PERIOD p) REFERENCES ... (..., PERIOD q)) is not modelled:
-- written as a plain key it would deploy as one, which is a different constraint, so it is left out and said so.
-- The warning reaches the SchemaTongs log through the connection's notice channel.
FOR skipped_fk IN SELECT con.conname
                    FROM pg_catalog.pg_constraint con
                   WHERE con.conrelid = ("SchemaSmith"."QuoteName"(p_Schema) || '.' || "SchemaSmith"."QuoteName"(p_Table))::regclass
                     AND con.contype = 'f'
                     AND "SchemaSmith"."ConstraintWithoutOverlaps"(con.oid)
                   ORDER BY con.conname LOOP
  RAISE WARNING 'Foreign key %.%.% uses PERIOD (a temporal foreign key), which is not modelled: left out of the package rather than written as a plain key.',
    p_Schema, p_Table, skipped_fk.conname;
END LOOP;
SELECT "SchemaSmith"."FormatJson"(ROW_TO_JSON(tbl))
  INTO result_string
  FROM (SELECT t.table_schema AS "Schema",
               t.table_name AS "Name",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT c.column_name AS "Name",
                               -- The same function renders the live type for the drift compare, so the two cannot disagree.
                               -- A column whose default is the nextval of a sequence it owns (pg_depend deptype a) is a serial column; it is
                               -- written as the keyword, which recreates and owns the sequence on deploy, and without the default.
                               CASE WHEN ser.objid IS NOT NULL AND a.attidentity = '' AND c.column_default LIKE 'nextval(%'
                                    THEN CASE c.udt_name WHEN 'int2' THEN 'smallserial' WHEN 'int8' THEN 'bigserial' ELSE 'serial' END
                                    ELSE "SchemaSmith"."ColumnDataType"(c.data_type, c.udt_schema, c.udt_name, c.domain_schema, c.domain_name, c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.datetime_precision, a.atttypid, a.atttypmod) END AS "DataType",
                               CASE WHEN c.is_nullable = 'YES' THEN TRUE ELSE FALSE END AS "Nullable",
                               CASE WHEN ser.objid IS NOT NULL AND a.attidentity = '' AND c.column_default LIKE 'nextval(%' THEN NULL
                                    ELSE "SchemaSmith"."StripParenWrapping"(c.column_default) END AS "Default",
                               "SchemaSmith"."ColumnCollation"(c.collation_schema, c.collation_name) AS "Collation",
                               -- ALWAYS has always carried its START WITH and INCREMENT BY; BY DEFAULT writes options only when
                               -- one differs from the default, so a plain one stays as it was. The rest of a sequence's options
                               -- are written when they are not the default, or a re-deploy would build the default sequence.
                               CASE WHEN a.attidentity = 'd'
                                    THEN 'GENERATED BY DEFAULT AS IDENTITY' ||
                                         CASE WHEN s.seqstart IS NOT NULL
                                                   AND (s.seqincrement <> 1 OR s.seqstart <> CASE WHEN s.seqincrement > 0 THEN s.seqmin ELSE s.seqmax END OR io."Extra" <> '')
                                              THEN '(START WITH ' || s.seqstart || ' INCREMENT BY ' || s.seqincrement || CASE WHEN io."Extra" <> '' THEN ' ' || io."Extra" ELSE '' END || ')'
                                              ELSE ''
                                              END
                                    WHEN a.attidentity = 'a'
                                    THEN 'GENERATED ALWAYS AS IDENTITY' ||
                                         CASE WHEN s.seqstart IS NOT NULL
                                              THEN '(START WITH ' || s.seqstart || ' INCREMENT BY ' || s.seqincrement || CASE WHEN io."Extra" <> '' THEN ' ' || io."Extra" ELSE '' END || ')'
                                              ELSE ''
                                              END
                                    ELSE c.is_generated END AS "Generated",
                               c.generation_expression AS "GenerationExpression",
                               CASE WHEN a.attgenerated = '' OR a.attgenerated = 's' THEN FALSE ELSE TRUE END AS "Virtual",
                               CASE a.attstorage
                                   WHEN 'p' THEN 'PLAIN'
                                   WHEN 'm' THEN 'MAIN'
                                   WHEN 'e' THEN 'EXTERNAL'
                                   WHEN 'x' THEN 'EXTENDED'
                                   ELSE 'DEFAULT' END AS "Storage",
                               "SchemaSmith"."ColumnCompression"(a.attrelid, a.attnum) AS "Compression",
                               COL_DESCRIPTION(a.attrelid, a.attnum) AS "Description"
                          FROM information_schema.columns c
                          JOIN pg_attribute a ON a.attrelid = ("SchemaSmith"."QuoteName"(c.table_schema) || '.' || "SchemaSmith"."QuoteName"(c.table_name))::regclass
                                             AND a.attname = c.column_name
                                             AND NOT a.attisdropped
                          LEFT JOIN pg_depend d ON d.refclassid = 'pg_class'::regclass
                                               AND d.refobjid = a.attrelid
                                               AND d.refobjsubid = a.attnum
                                               AND d.classid = 'pg_class'::regclass
                                               AND d.deptype = 'i' -- Internal dependency, specifically for identity columns
                          LEFT JOIN pg_sequence s ON s.seqrelid = d.objid
                          -- The options that are not the default for the sequence's type and direction.
                          LEFT JOIN LATERAL (SELECT CONCAT_WS(' ',
                                                      CASE WHEN s.seqmin <> CASE WHEN s.seqincrement > 0 THEN 1 ELSE bounds.lo END THEN 'MINVALUE ' || s.seqmin END,
                                                      CASE WHEN s.seqmax <> CASE WHEN s.seqincrement > 0 THEN bounds.hi ELSE -1 END THEN 'MAXVALUE ' || s.seqmax END,
                                                      CASE WHEN s.seqcache <> 1 THEN 'CACHE ' || s.seqcache END,
                                                      CASE WHEN s.seqcycle THEN 'CYCLE' END) AS "Extra"
                                               FROM (SELECT CASE s.seqtypid WHEN 'int2'::regtype THEN -32768 WHEN 'int4'::regtype THEN -2147483648 ELSE -9223372036854775808 END AS lo,
                                                            CASE s.seqtypid WHEN 'int2'::regtype THEN 32767 WHEN 'int4'::regtype THEN 2147483647 ELSE 9223372036854775807 END AS hi) bounds) io ON TRUE
                          -- The sequence a serial column owns: an auto dependency from a sequence to the column. An index
                          -- on the column records the same kind of dependency, so the relkind test is what keeps one row
                          -- per column.
                          LEFT JOIN LATERAL (SELECT sd.objid
                                               FROM pg_depend sd
                                               JOIN pg_class sq ON sq.oid = sd.objid AND sq.relkind = 'S'
                                              WHERE sd.refclassid = 'pg_class'::regclass
                                                AND sd.refobjid = a.attrelid
                                                AND sd.refobjsubid = a.attnum
                                                AND sd.classid = 'pg_class'::regclass
                                                AND sd.deptype = 'a'
                                              LIMIT 1) ser ON TRUE
                          WHERE t.table_schema = c.table_schema
                            AND t.table_name = c.table_name
                          -- Column sequence is a caller choice: 'Name' (default, and what the tool
                          -- writes) or 'Physical', the table's own order. Only the column list honours it --
                          -- indexes and constraints have no ordinal anyone authors toward.
                          ORDER BY CASE WHEN LOWER(p_ObjectOrder) = 'physical' THEN c.ordinal_position END,
                                   CASE WHEN LOWER(p_ObjectOrder) = 'physical' THEN NULL ELSE c.column_name END
                         ) sub) AS "Columns",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT i.relname AS "Name",
                               idx.indisprimary AS "PrimaryKey",
                               idx.indisunique AS "Unique",
                               CASE WHEN con.contype = 'u' THEN TRUE ELSE FALSE END AS "UniqueConstraint",
                               idx.indisclustered AS "Clustered",
                               -- The key list as the engine writes it: a non-default operator class, a differing
                               -- COLLATE and the operator-class parameters stay in the key (IndexKeyForms).
                               "SchemaSmith"."IndexKeyList"(idx.indexrelid) AS "IndexColumns",
                               (SELECT STRING_AGG("SchemaSmith"."ListName"(a.attname), ',' ORDER BY idx)
                                  FROM pg_attribute a
                                  CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                  WHERE a.attrelid = idx.indrelid
                                    AND idx > idx.indnkeyatts
                                    AND a.attnum = element) AS "IncludeColumns",
                               CASE WHEN "SchemaSmith"."ConstraintWithoutOverlaps"(con.oid) THEN NULL
                                    ELSE (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid AND i.relkind = 'i') END AS "AccessMethod",
                               "SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)) AS "FilterExpression",
                               -- Only a fill factor someone set is declared; an index without the option is on the
                               -- engine's default for its access method, which is not an authored value and would
                               -- give a hash or SP-GiST index the btree 90 if written.
                               (REGEXP_MATCH(ARRAY_TO_STRING(i.reloptions, ','), 'fillfactor=(\d+)'))[1]::int AS "FillFactor",
                               "SchemaSmith"."IndexNullsNotDistinct"(idx.indexrelid) AS "NullsNotDistinct",
                               "SchemaSmith"."ConstraintWithoutOverlaps"(con.oid) AS "WithoutOverlaps",
                               COALESCE(con.condeferrable, FALSE) AS "Deferrable",
                               COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred",
                               -- reltablespace 0 means "the database default", which is NOT a declared
                               -- placement -- it yields NULL so FormatJson strips the key and an ordinary
                               -- index extracts exactly as it did before this shipped. An index does not
                               -- inherit its table's tablespace, so this is read per index.
                               (SELECT ts.spcname FROM pg_tablespace ts WHERE ts.oid = i.reltablespace) AS "Tablespace",
                               -- Storage parameters (the WITH clause) as an object, minus fillfactor which
                               -- FillFactor above owns. This is what carries a vector index's m /
                               -- ef_construction / lists. JSON_OBJECT_AGG returns NULL for an index with no
                               -- non-fillfactor options, so FormatJson strips the key and an ordinary index
                               -- extracts byte-identically to before this shipped.
                               (SELECT JSON_OBJECT_AGG(SPLIT_PART(opt, '=', 1), SPLIT_PART(opt, '=', 2))
                                  FROM UNNEST(i.reloptions) AS o(opt)
                                 WHERE opt NOT LIKE 'fillfactor=%') AS "StorageParameters",
                               OBJ_DESCRIPTION(i.oid, 'pg_class') AS "Description"
                          FROM pg_index idx
                          JOIN pg_class i ON i.oid = idx.indexrelid
                          LEFT JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype IN ('p', 'u', 'x')
                          WHERE idx.indrelid = ("SchemaSmith"."QuoteName"(t.table_schema) || '.' || "SchemaSmith"."QuoteName"(t.table_name))::regclass
                            AND (con.contype IS NULL OR con.contype IN ('p', 'u'))
                          ORDER BY i.relname) sub) AS "Indexes",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT con.conname AS "Name",
                               (SELECT STRING_AGG("SchemaSmith"."ListName"(a.attname), ',' ORDER BY idx)
                                  FROM pg_attribute a
                                  CROSS JOIN LATERAL UNNEST(con.conkey) WITH ORDINALITY AS u(element, idx)
                                  WHERE a.attrelid = con.conrelid
                                    AND a.attnum = element) AS "Columns",
                               fnsp.nspname AS "RelatedTableSchema",
                               frel.relname AS "RelatedTable",
                               (SELECT STRING_AGG("SchemaSmith"."ListName"(a.attname), ',' ORDER BY idx)
                                  FROM pg_attribute a
                                  CROSS JOIN LATERAL UNNEST(con.confkey) WITH ORDINALITY AS u(element, idx)
                                  WHERE a.attrelid = frel.oid
                                    AND a.attnum = element) AS "RelatedColumns",
                               CASE con.confdeltype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' WHEN 'd' THEN 'SET DEFAULT' END AS "DeleteAction",
                               NULLIF("SchemaSmith"."ForeignKeySetNullColumns"(con.oid), '') AS "SetNullColumns",
                               CASE con.confupdtype WHEN 'a' THEN '' WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'r' THEN 'RESTRICT' WHEN 'd' THEN 'SET DEFAULT' END AS "UpdateAction",
                               COALESCE(con.condeferrable, FALSE) AS "Deferrable",
                               COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred",
                               -- The state attributes only where they are not the default: an omitted one leaves the constraint as it is.
                               CASE WHEN NOT COALESCE(con.convalidated, TRUE) THEN FALSE END AS "Validated",
                               CASE WHEN NOT "SchemaSmith"."ConstraintEnforced"(con.oid) THEN FALSE END AS "Enforced",
                               CASE con.confmatchtype
                                    WHEN 'f' THEN 'FULL'
                                    WHEN 'p' THEN 'PARTIAL'
                                    WHEN 's' THEN 'SIMPLE'
                                    ELSE 'UNKNOWN'
                                    END AS "MatchType"
                          FROM pg_catalog.pg_constraint con
                          JOIN pg_catalog.pg_class frel ON frel.oid = con.confrelid
                          JOIN pg_catalog.pg_namespace fnsp ON fnsp.oid = frel.relnamespace
                          WHERE con.conrelid = ("SchemaSmith"."QuoteName"(t.table_schema) || '.' || "SchemaSmith"."QuoteName"(t.table_name))::regclass
                            AND con.contype = 'f'
                            AND NOT "SchemaSmith"."ConstraintWithoutOverlaps"(con.oid)
                          ORDER BY con.conname) sub) AS "ForeignKeys",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT con.conname AS "Name",
                               -- pg_get_constraintdef renders the state suffixes after the predicate; they are attributes,
                               -- not expression text, so they are stripped here and read from the catalog flags below.
                               "SchemaSmith"."CheckPredicate"(con.oid) AS "Expression",
                               CASE WHEN NOT COALESCE(con.convalidated, TRUE) THEN FALSE END AS "Validated",
                               CASE WHEN con.connoinherit THEN TRUE END AS "NoInherit",
                               CASE WHEN NOT "SchemaSmith"."ConstraintEnforced"(con.oid) THEN FALSE END AS "Enforced"
                        FROM pg_catalog.pg_constraint con
                          WHERE con.conrelid = ("SchemaSmith"."QuoteName"(t.table_schema) || '.' || "SchemaSmith"."QuoteName"(t.table_name))::regclass
                            AND con.contype = 'c'
                          ORDER BY con.conname) sub) AS "CheckConstraints",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT se.stxname AS "Name",
                               -- Written only when the statistics object lives outside its table's schema.
                               NULLIF(sn.nspname, t.table_schema) AS "Schema",
                               -- The target (13+), only when someone set one: -1 (before 17) and NULL (17+) both mean
                               -- the default. Read through to_jsonb so the column's absence below 13 is a NULL, not a
                               -- plan-time error.
                               NULLIF((to_jsonb(se) ->> 'stxstattarget')::int, -1) AS "StatisticsTarget",
                               -- 'e' is left out: EXPRESSIONS is implied by an expression and CREATE STATISTICS rejects it as a
                               -- kind, so extracting it produced a package that could not be deployed.
                               COALESCE((SELECT STRING_AGG(CASE k WHEN 'd' THEN 'NDISTINCT' WHEN 'f' THEN 'DEPENDENCIES' WHEN 'm' THEN 'MCV' ELSE k::text END, ',')
                                           FROM UNNEST(se.stxkind) AS k WHERE k <> 'e'), '') AS "Kind",
                               COALESCE(ARRAY_TO_STRING(ARRAY_CAT(COALESCE((SELECT ARRAY_AGG("SchemaSmith"."ListName"(a.attname::text))
                                                                              FROM UNNEST(se.stxkeys) WITH ORDINALITY AS t(attnum, ord)
                                                                              JOIN pg_attribute a ON a.attrelid = se.stxrelid AND a.attnum = t.attnum
                                                                              WHERE a.attnum > 0),
                                                                           ARRAY[]::text[]),
                                                                  "SchemaSmith"."StatisticsExpressionColumns"(sn.nspname, se.stxname)), ','), '') AS "StatisticsColumns"
                          FROM pg_statistic_ext se
                          JOIN pg_namespace sn ON sn.oid = se.stxnamespace
                          WHERE se.stxrelid = ("SchemaSmith"."QuoteName"(t.table_schema) || '.' || "SchemaSmith"."QuoteName"(t.table_name))::regclass
                          ORDER BY se.stxname) sub) AS "Statistics",
               (SELECT JSON_AGG(ROW_TO_JSON(sub))
                  FROM (SELECT i.relname AS "Name",
                               (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid AND i.relkind = 'i') AS "AccessMethod",
                               -- Each key as the engine writes it (operator class, COLLATE, parameters; IndexKeyForms)
                               -- paired with its exclusion operator by position.
                               (SELECT JSON_AGG(ROW_TO_JSON(cols))
                                  FROM (SELECT k.item AS "Column", COALESCE(nsp.nspname || '.' || o.oprname, o.oprname) AS "Operator"
                                          FROM UNNEST("SchemaSmith"."IndexKeyItems"(idx.indexrelid)) WITH ORDINALITY AS k(item, idx)
                                          LEFT JOIN LATERAL (SELECT (con.conexclop::oid[])[k.idx] AS opr_oid) co ON TRUE
                                          LEFT JOIN pg_operator o ON o.oid = co.opr_oid
                                          LEFT JOIN pg_namespace nsp ON nsp.oid = o.oprnamespace AND nsp.nspname != 'pg_catalog'
                                          ORDER BY k.idx) cols) AS "ExcludeColumns",
                               "SchemaSmith"."StripParenWrapping"(PG_GET_EXPR(idx.indpred, idx.indrelid)) AS "FilterExpression",
                               COALESCE(con.condeferrable, FALSE) AS "Deferrable",
                               COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred",
                               -- The backing index's extras, as an index's: covering columns, every storage parameter
                               -- (fillfactor included, there being no FillFactor property here), and a placement only
                               -- when it is not the database default.
                               (SELECT STRING_AGG("SchemaSmith"."ListName"(a.attname), ',' ORDER BY u.idx)
                                  FROM pg_attribute a
                                  CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
                                  WHERE a.attrelid = idx.indrelid
                                    AND u.idx > idx.indnkeyatts
                                    AND a.attnum = u.element) AS "IncludeColumns",
                               (SELECT JSON_OBJECT_AGG(SPLIT_PART(opt, '=', 1), SPLIT_PART(opt, '=', 2))
                                  FROM UNNEST(i.reloptions) AS o(opt)) AS "StorageParameters",
                               (SELECT its.spcname FROM pg_tablespace its WHERE its.oid = i.reltablespace AND i.reltablespace <> 0) AS "Tablespace"
                          FROM pg_index idx
                          JOIN pg_class i ON i.oid = idx.indexrelid
                          JOIN pg_catalog.pg_constraint con ON con.conrelid = idx.indrelid AND con.conname = i.relname AND con.contype = 'x'
                          WHERE idx.indrelid = ("SchemaSmith"."QuoteName"(t.table_schema) || '.' || "SchemaSmith"."QuoteName"(t.table_name))::regclass
                          ORDER BY i.relname) sub) AS "ExcludeConstraints",
               -- Row-level security policies (#rls). Emitted only when the table has any, so no existing
               -- package gains an empty "Policies": [] on every table. pg_policies exposes the normalised
               -- USING / WITH CHECK text, which is what a re-deploy compares against.
               (SELECT JSON_AGG(sub ORDER BY sub."Name")
                  FROM (SELECT pol.policyname AS "Name",
                               pol.permissive AS "Permissive",
                               pol.cmd AS "Command",
                               ARRAY_TO_STRING(pol.roles, ',') AS "Roles",
                               COALESCE(pol.qual, '') AS "UsingExpression",
                               COALESCE(pol.with_check, '') AS "WithCheckExpression"
                          FROM pg_policies pol
                          WHERE pol.schemaname = t.table_schema
                            AND pol.tablename = t.table_name) sub) AS "Policies",
               rls.relrowsecurity AS "RowLevelSecurity",
               rls.relforcerowsecurity AS "ForceRowLevelSecurity",
               -- Emit the sticky drop-protection marker first-class (only when set true; FormatJson strips the null key
               -- for unprotected tables). Read from the ProductOwnership table-level row by schema+table. #270
               CASE WHEN COALESCE((SELECT po."PreventDrop"
                                     FROM "SchemaSmith"."ProductOwnership" po
                                     WHERE po."Schema" = t.table_schema
                                       AND po."TableName" = t.table_name
                                       AND po."IndexName" IS NULL), FALSE)
                    THEN TRUE END AS "PreventDrop",
               am.amname AS "AccessMethod",
               CASE rls.relpersistence WHEN 'p' THEN 'Logged' WHEN 'u' THEN 'Unlogged' WHEN 't' THEN 'Temporary' END AS "PersistenceType",
               COALESCE((SELECT SPLIT_PART(opt, '=', 2)
                           FROM UNNEST(rls.reloptions) AS opts(opt)
                           WHERE opt LIKE 'fillfactor=%'), '100')::INT2 AS "FillFactor",
               -- 'd' (the default) deliberately yields NULL so FormatJson strips the key: a table that
               -- never touched replica identity extracts exactly as it did before this shipped. #407
               CASE rls.relreplident WHEN 'f' THEN 'FULL' WHEN 'n' THEN 'NOTHING' WHEN 'i' THEN 'INDEX' END AS "ReplicaIdentity",
               CASE WHEN rls.relreplident = 'i'
                    THEN (SELECT ic.relname
                            FROM pg_index ix
                            JOIN pg_class ic ON ic.oid = ix.indexrelid
                           WHERE ix.indrelid = rls.oid
                             AND ix.indisreplident)
                    END AS "ReplicaIdentityIndex",
               -- Same 0-means-default rule as the index block above. Closes the asymmetry with
               -- materialized views, which have carried Tablespace since they shipped.
               (SELECT ts.spcname FROM pg_tablespace ts WHERE ts.oid = rls.reltablespace) AS "Tablespace",
               OBJ_DESCRIPTION(rls.oid, 'pg_class') AS "Description"
          FROM information_schema.tables t
          JOIN pg_class rls ON rls.relname = t.table_name
                           AND rls.relnamespace IN (SELECT oid FROM pg_namespace WHERE nspname = t.table_schema)
                           AND rls.relkind = 'r'
          JOIN pg_am am ON rls.relam = am.oid
          WHERE t.table_type = 'BASE TABLE'
            AND t.table_schema NOT IN ('pg_catalog', 'information_schema', 'pg_toast', 'SchemaSmith')
            AND p_Schema = t.table_schema
            AND p_Table = t.table_name) tbl;

RETURN result_string;
END $function$