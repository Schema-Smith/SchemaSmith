-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- One reading of an exclusion constraint's keys, each "key WITH operator", for the declared side and the live side.
-- ModifiedTableQuench compares the two, and the expression map records and verifies the pair, so all of them read the
-- same text from here. Each key is in IndexKeyForms' canonical form; an operator outside pg_catalog is schema-qualified.

-- The declared keys: the ExcludeColumns JSON of a package's exclusion, in declared order.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ExcludeKeysDeclared"(p_schema TEXT, p_table TEXT, p_access_method TEXT, p_columns JSON) RETURNS TEXT
    LANGUAGE sql STABLE
AS $$
  SELECT STRING_AGG("SchemaSmith"."IndexKeyDeclared"(p_schema, p_table, p_access_method, (celem ->> 'Column')::TEXT) || ' WITH ' || (celem ->> 'Operator')::TEXT,
                    ',' ORDER BY n)
    FROM JSON_ARRAY_ELEMENTS(p_columns) WITH ORDINALITY AS e(celem, n)
$$;

-- The live keys of an exclusion constraint, each paired with its operator by position.
CREATE OR REPLACE FUNCTION "SchemaSmith"."ExcludeKeyList"(p_conoid OID) RETURNS TEXT
    LANGUAGE sql STABLE
AS $$
  SELECT STRING_AGG(k.item || ' WITH ' || COALESCE(nsp.nspname || '.' || o.oprname, o.oprname), ',' ORDER BY k.idx)
    FROM pg_catalog.pg_constraint con
    CROSS JOIN LATERAL UNNEST("SchemaSmith"."IndexKeyItems"(con.conindid)) WITH ORDINALITY AS k(item, idx)
    LEFT JOIN pg_catalog.pg_operator o ON o.oid = (con.conexclop::OID[])[k.idx]
    LEFT JOIN pg_catalog.pg_namespace nsp ON nsp.oid = o.oprnamespace AND nsp.nspname != 'pg_catalog'
   WHERE con.oid = p_conoid
$$;
