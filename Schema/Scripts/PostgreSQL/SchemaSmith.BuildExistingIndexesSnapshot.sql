-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE PROCEDURE "SchemaSmith"."BuildExistingIndexesSnapshot"()
    LANGUAGE plpgsql
AS $$
DECLARE
  -- Version-adaptive catalog read: pg_index.indnullsnotdistinct is PostgreSQL 15+. Referencing it
  -- on an older server errors at PLAN time (42703) even inside a never-taken branch, so the whole
  -- snapshot SELECT is built dynamically and the column is swapped for a literal FALSE below 15. This
  -- keys on the REAL server version (server_version_num), NOT the override-aware ServerVersionNum():
  -- whether the physical column EXISTS is a property of the actual binary, so a test that forces a
  -- higher version on a genuinely older server must still read the fallback. The declared-side
  -- NullsNotDistinct is neutralised to false below 15 (override-aware) in the emit procs; the emitted
  -- index therefore physically lacks the clause, so FALSE-vs-FALSE produces no phantom churn.
  v_nnd_expr TEXT := CASE WHEN (current_setting('server_version_num')::int / 10000) >= 15 THEN 'idx.indnullsnotdistinct' ELSE 'FALSE' END;
BEGIN
  -- Session-scoped snapshot of existing indexes, consumed by ModifiedTableQuench,
  -- IndexOnlyQuench, and MissingIndexesAndConstraintsQuench. Extracted to one proc so the
  -- three sites can never drift and so a checkpoint-resumed run (which skips the step that
  -- normally builds it) can rebuild it on demand (#332). Depends only on temp_tables.
  DROP TABLE IF EXISTS temp_existing_indexes;
  EXECUTE format($snapshot$
  CREATE TEMPORARY TABLE temp_existing_indexes AS
    SELECT t."Schema" AS "TableSchema",
           t."Name" AS "TableName",
           i.relname AS "IndexName",
           -- indoption is a 0-based int2vector while WITH ORDINALITY counts from 1, so this must be
           -- indoption[idx-1]. Read 1-based it returned the NEXT key's flags (and nothing for the last),
           -- so a DESC key never reported DESC here and the index was re-created on every deploy. Every
           -- other site in this codebase already uses the 0-based form -- this was the only one that did not.
           -- SAME RULE EXTRACTION USES (GenerateTableJson's index read), and it has to be: when the
           -- snapshot and extraction disagree, an index round-trips into a package that the compare
           -- then reports as changed on every deploy.
           --
           -- This joined pg_attribute on attnum = element. An EXPRESSION key has indkey element 0,
           -- which matches no attribute, so the key was dropped from the snapshot entirely while the
           -- authored side carried lower(name) -- never equal, so every expression index churned.
           -- PG_GET_INDEXDEF(indexrelid, n, true) renders both shapes uniformly (lower(name) for an
           -- expression, tag for a column) and needs no join at all. Verified against a real
           -- extraction: SchemaTongs writes "lower(name)" and "tag,lower(name)" -- bare, no wrapping
           -- parens -- so that spelling IS the canonical authored form, not a choice made here.
           --
           -- It also carries extraction's NULLS FIRST/LAST handling, which this side lacked: the
           -- snapshot omitted the modifier while extraction emitted it, so a DESC key with non-default
           -- null ordering was a second way to churn. TRIM(BOTH '"') because PG_GET_INDEXDEF quotes
           -- identifiers it considers to need it and the authored side does not.
           (SELECT STRING_AGG(TRIM(BOTH '"' FROM PG_GET_INDEXDEF(idx.indexrelid, idx::int4, true)) ||
                              CASE WHEN (idx.indoption[idx-1] & 1) = 1 THEN ' DESC' || CASE WHEN (idx.indoption[idx-1] & 2) = 2 THEN '' ELSE ' NULLS LAST' END
                                   ELSE CASE WHEN (idx.indoption[idx-1] & 2) = 2 THEN ' NULLS FIRST' ELSE '' END
                                  END, ',' ORDER BY idx)
              FROM UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
             WHERE idx <= idx.indnkeyatts) AS "IndexColumns",
           (SELECT STRING_AGG(a.attname, ',' ORDER BY idx)
              FROM pg_attribute a
              CROSS JOIN LATERAL UNNEST(idx.indkey) WITH ORDINALITY AS u(element, idx)
              WHERE a.attrelid = idx.indrelid
                AND idx > idx.indnkeyatts
                AND a.attnum = element) AS "IncludeColumns",
           idx.indisunique AS "Unique",
           CASE WHEN con.contype = 'u' THEN TRUE ELSE FALSE END AS "UniqueConstraint",
           idx.indisprimary AS "PrimaryKey",
           idx.indisclustered AS "Clustered",
           COALESCE(PG_GET_EXPR(idx.indpred, idx.indrelid), '') AS "FilterExpression",
           (SELECT am.amname FROM pg_am am WHERE i.relam = am.oid AND i.relkind = 'i') AS "AccessMethod",
           CASE WHEN 'fillfactor=100' = ANY(i.reloptions) THEN 100
                WHEN i.reloptions IS NULL THEN 90 -- Default for B-tree indexes
                ELSE (regexp_match(array_to_string(i.reloptions, ','), 'fillfactor=(\d+)') ) [1] ::int
                END AS "FillFactor",
           %s AS "NullsNotDistinct",
           -- Storage parameters (the WITH clause), canonicalised to match the declared side: reloptions is
           -- already key=value strings, sorted so order does not matter, with fillfactor removed because
           -- FillFactor above owns it. An index with only fillfactor yields '' here and compares equal to a
           -- package that declares no StorageParameters.
           COALESCE((SELECT STRING_AGG(opt, ',' ORDER BY opt)
                       FROM UNNEST(i.reloptions) AS o(opt)
                      WHERE opt NOT LIKE 'fillfactor=%%'), '') AS "StorageParameters",
           COALESCE(con.condeferrable, FALSE) AS "Deferrable",
           COALESCE(con.condeferred, FALSE) AS "InitiallyDeferred"
      FROM temp_tables t
      JOIN pg_index idx ON idx.indrelid = to_regclass('"' || t."Schema" || '"' ||  '.' || '"' ||  t."Name" || '"')
      JOIN pg_class i ON i.oid = idx.indexrelid
      LEFT JOIN pg_catalog.pg_constraint con ON con.contype IN ('p','u') AND con.conrelid = idx.indrelid AND con.conname = i.relname
  $snapshot$, v_nnd_expr);
END $$;
