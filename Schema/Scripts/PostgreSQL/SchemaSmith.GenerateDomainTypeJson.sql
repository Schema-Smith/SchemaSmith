-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

CREATE OR REPLACE FUNCTION "SchemaSmith"."GenerateDomainTypeJSON"(p_Schema varchar(200), p_Name varchar(200))
  RETURNS text
  LANGUAGE plpgsql
AS $function$
DECLARE result_string TEXT;
BEGIN
  -- Extracts one domain type as the declarative package form.
  --
  -- format_type() RATHER THAN typname FOR THE BASE TYPE, because it renders the modifier too --
  -- "character varying(20)", "numeric(10,2)" -- and the modifier is part of the type. The quench compares
  -- the declared DataType against this same expression, so a round-tripped package compares equal instead
  -- of looking like a base-type change on every deploy.
  --
  -- contype = 'c' IS LOAD-BEARING, NOT TIDINESS, and it is a genuine version divergence rather than a
  -- theoretical one. PostgreSQL 17 reports a domain's NOT NULL as a pg_constraint row of its own
  -- (contype = 'n', named <domain>_not_null); PostgreSQL 12 -- the supported floor -- does not. Both were
  -- probed. Without the filter, a domain extracted on 17 would carry a phantom CHECK constraint holding
  -- the text "NOT NULL", which is not a valid predicate anywhere and which PostgreSQL 12 would have no
  -- way to produce. NOT NULL is read from pg_type.typnotnull instead, which both versions report the same.
  --
  -- Constraints are ordered BY NAME rather than by OID: OID order is creation order, which differs between
  -- two servers holding the same logical domain and would make an extraction diff noisy for no reason.
  SELECT "SchemaSmith"."FormatJson"(ROW_TO_JSON(t))
    INTO result_string
    FROM (SELECT n.nspname AS "Schema",
                 ty.typname AS "Name",
                 FORMAT_TYPE(ty.typbasetype, ty.typtypmod) AS "DataType",
                 ty.typnotnull AS "NotNull",
                 PG_GET_EXPR(ty.typdefaultbin, 0) AS "Default",
                 -- Only a collation the domain chose: one inherited from its base type is not part of the declaration.
                 CASE WHEN ty.typcollation IN (0, (SELECT bt.typcollation FROM pg_type bt WHERE bt.oid = ty.typbasetype)) THEN NULL
                                 ELSE (SELECT "SchemaSmith"."ColumnCollation"(cn.nspname, co.collname)
                                         FROM pg_collation co JOIN pg_namespace cn ON cn.oid = co.collnamespace
                                        WHERE co.oid = ty.typcollation) END AS "Collation",
                 COALESCE((SELECT JSON_AGG(JSON_BUILD_OBJECT('Name', c.conname,
                                                             -- pg_get_constraintdef renders "CHECK (expr)";
                                                             -- the package carries the predicate alone, so the
                                                             -- wrapper is stripped back off here.
                                                             'Expression', "SchemaSmith"."StripParenWrapping"(
                                                                 REGEXP_REPLACE(PG_GET_CONSTRAINTDEF(c.oid), '^CHECK\s*', '')))
                                           ORDER BY c.conname)
                             FROM pg_constraint c
                            WHERE c.contypid = ty.oid
                              AND c.contype = 'c'), '[]'::JSON) AS "CheckConstraints"
            FROM pg_type ty
            JOIN pg_namespace n ON n.oid = ty.typnamespace
           WHERE ty.typtype = 'd'
             AND n.nspname = p_Schema
             AND ty.typname = p_Name) t;

  RETURN result_string;
END $function$;
