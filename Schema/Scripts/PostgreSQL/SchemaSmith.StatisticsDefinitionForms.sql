-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Extended statistics are compared in ONE form, built the same way from the declaration and from the catalog.
-- The catalog does not keep what was authored: stxkeys is the column SET in attnum order with expressions held
-- apart, and stxkind carries an 'e' for any object that has an expression. Compared raw, an expression statistic
-- never matched its declaration (its kind always read EXPRESSIONS on top of the declared kinds) and neither did
-- a column list written in any order other than attnum, so each was dropped and re-created on every deploy.
--
-- Columns: the plain columns sorted (a statistic's columns are a set -- order means nothing to it), then the
-- expressions in declared order with any outer parentheses removed. Expression TEXT still differs where
-- PostgreSQL rewrites it; the expression map (#242, slot STATISTIC/columns) vouches for that.
-- Kind: the declarable kinds sorted, EXPRESSIONS removed (it is implied by an expression and is not accepted by
-- CREATE STATISTICS), with no kinds meaning all of them.

CREATE OR REPLACE FUNCTION "SchemaSmith"."NormalizeStatisticsColumnList"(p_List TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
AS $$
  SELECT COALESCE(ARRAY_TO_STRING(
           ARRAY(SELECT TRIM(BOTH '"' FROM TRIM(x))
                   FROM UNNEST("SchemaSmith"."SplitTopLevelList"(p_List)) AS x
                  WHERE TRIM(x) <> '' AND TRIM(x) NOT LIKE '%(%'
                  ORDER BY TRIM(BOTH '"' FROM TRIM(x)) COLLATE "C")
           || ARRAY(SELECT "SchemaSmith"."StripParenWrapping"(TRIM(x))
                      FROM UNNEST("SchemaSmith"."SplitTopLevelList"(p_List)) WITH ORDINALITY AS u(x, ord)
                     WHERE TRIM(x) LIKE '%(%'
                     ORDER BY ord), ','), '')
$$;

CREATE OR REPLACE FUNCTION "SchemaSmith"."NormalizeStatisticsKind"(p_Kind TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
AS $$
  SELECT COALESCE(NULLIF(ARRAY_TO_STRING(
           ARRAY(SELECT DISTINCT UPPER(TRIM(k))
                   FROM UNNEST(STRING_TO_ARRAY(COALESCE(p_Kind, ''), ',')) AS k
                  WHERE UPPER(TRIM(k)) NOT IN ('', 'EXPRESSIONS')
                  ORDER BY 1), ','), ''), 'DEPENDENCIES,MCV,NDISTINCT')
$$;

-- The kind clause for CREATE STATISTICS: the declared kinds without EXPRESSIONS, or nothing at all when none
-- remain -- which a single-expression statistic requires, and which is how an extracted one used to come back.
CREATE OR REPLACE FUNCTION "SchemaSmith"."StatisticsKindClause"(p_Kind TEXT)
    RETURNS TEXT
    LANGUAGE sql
    IMMUTABLE
AS $$
  SELECT COALESCE(' (' || NULLIF(ARRAY_TO_STRING(
           ARRAY(SELECT UPPER(TRIM(k))
                   FROM UNNEST(STRING_TO_ARRAY(COALESCE(p_Kind, ''), ',')) AS k
                  WHERE UPPER(TRIM(k)) NOT IN ('', 'EXPRESSIONS')), ','), '') || ')', '')
$$;

-- The live statistics object in the normalised column form above.
CREATE OR REPLACE FUNCTION "SchemaSmith"."StatisticsLiveColumns"(p_StatOid OID)
    RETURNS TEXT
    LANGUAGE plpgsql
    STABLE
AS $$
DECLARE
  v_exprs TEXT[] := ARRAY[]::TEXT[];
BEGIN
  -- pg_get_statisticsobjdef_expressions is PostgreSQL 14+, so it is referenced only through EXECUTE; below 14
  -- no statistic can have an expression.
  IF (current_setting('server_version_num')::int / 10000) >= 14 THEN
    EXECUTE 'SELECT COALESCE(pg_get_statisticsobjdef_expressions($1), ARRAY[]::text[])' INTO v_exprs USING p_StatOid;
  END IF;
  RETURN COALESCE(ARRAY_TO_STRING(
           ARRAY(SELECT a.attname::text
                   FROM pg_statistic_ext se
                   CROSS JOIN LATERAL UNNEST(se.stxkeys) AS k(attnum)
                   JOIN pg_attribute a ON a.attrelid = se.stxrelid AND a.attnum = k.attnum
                  WHERE se.oid = p_StatOid
                  ORDER BY a.attname::text COLLATE "C")
           || ARRAY(SELECT "SchemaSmith"."StripParenWrapping"(e) FROM UNNEST(v_exprs) WITH ORDINALITY AS u(e, ord) ORDER BY ord),
           ','), '');
END $$;
