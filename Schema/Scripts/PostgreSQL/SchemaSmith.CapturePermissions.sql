-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Renders what a DROP, or a rename-and-replace, takes off a relation or a routine that the package does not carry,
-- as the statements that put it back: the ACL on the object and on each column, the owner, and the comments on the
-- object and its columns. The quench calls this before it drops or swaps an object and ReapplyPermissions with the
-- result once the replacement exists. NULL when there is nothing.
--
-- p_Kind is the word the DDL uses: TABLE, VIEW, MATERIALIZED VIEW, FUNCTION or PROCEDURE. A routine is captured for
-- every overload of the name, each by its identity signature. A non-null ACL is explicit, so it replays as a REVOKE
-- from PUBLIC followed by each entry; a null ACL is the engine's default and needs nothing. The owner is replayed
-- only when it is not the role deploying, which is the role the replacement is created as.
CREATE OR REPLACE FUNCTION "SchemaSmith"."CapturePermissions"(p_Schema TEXT, p_Name TEXT, p_Kind TEXT) RETURNS TEXT
    LANGUAGE plpgsql
AS $$
DECLARE
  v_Script TEXT := '';
  v_Qualified TEXT := quote_ident(p_Schema) || '.' || quote_ident(p_Name);
  v_Target TEXT;
  v_Comment TEXT;
  obj RECORD;
  acl RECORD;
BEGIN
  IF p_Kind IN ('TABLE', 'VIEW', 'MATERIALIZED VIEW') THEN
    FOR obj IN
      SELECT c.oid, c.relacl, pg_get_userbyid(c.relowner) AS owner
        FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
       WHERE n.nspname = p_Schema AND c.relname = p_Name
    LOOP
      IF obj.relacl IS NOT NULL THEN
        v_Script := v_Script || 'REVOKE ALL ON TABLE ' || v_Qualified || ' FROM PUBLIC;' || CHR(10);
        FOR acl IN SELECT * FROM aclexplode(obj.relacl) ORDER BY grantee, privilege_type LOOP
          v_Script := v_Script || 'GRANT ' || acl.privilege_type || ' ON TABLE ' || v_Qualified || ' TO '
                   || CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(acl.grantee)) END
                   || CASE WHEN acl.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END || ';' || CHR(10);
        END LOOP;
      END IF;
      FOR acl IN
        SELECT a.attname, (aclexplode(a.attacl)).*
          FROM pg_attribute a
         WHERE a.attrelid = obj.oid AND a.attnum > 0 AND NOT a.attisdropped AND a.attacl IS NOT NULL
         ORDER BY a.attnum, 3, 4
      LOOP
        v_Script := v_Script || 'GRANT ' || acl.privilege_type || ' (' || quote_ident(acl.attname) || ') ON TABLE ' || v_Qualified || ' TO '
                 || CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(acl.grantee)) END
                 || CASE WHEN acl.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END || ';' || CHR(10);
      END LOOP;
      IF obj.owner <> current_user THEN
        v_Script := v_Script || 'ALTER ' || p_Kind || ' ' || v_Qualified || ' OWNER TO ' || quote_ident(obj.owner) || ';' || CHR(10);
      END IF;
      v_Comment := obj_description(obj.oid, 'pg_class');
      IF v_Comment IS NOT NULL THEN
        v_Script := v_Script || 'COMMENT ON ' || p_Kind || ' ' || v_Qualified || ' IS ' || quote_literal(v_Comment) || ';' || CHR(10);
      END IF;
      FOR acl IN
        SELECT a.attname, col_description(obj.oid, a.attnum) AS comment
          FROM pg_attribute a
         WHERE a.attrelid = obj.oid AND a.attnum > 0 AND NOT a.attisdropped AND col_description(obj.oid, a.attnum) IS NOT NULL
         ORDER BY a.attnum
      LOOP
        v_Script := v_Script || 'COMMENT ON COLUMN ' || v_Qualified || '.' || quote_ident(acl.attname) || ' IS ' || quote_literal(acl.comment) || ';' || CHR(10);
      END LOOP;
    END LOOP;
  ELSIF p_Kind IN ('FUNCTION', 'PROCEDURE') THEN
    FOR obj IN
      SELECT p.oid, p.proacl, pg_get_userbyid(p.proowner) AS owner,
             CASE WHEN p.prokind = 'p' THEN 'PROCEDURE' ELSE 'FUNCTION' END AS kind,
             pg_get_function_identity_arguments(p.oid) AS args
        FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
       WHERE n.nspname = p_Schema AND p.proname = p_Name
       ORDER BY p.oid
    LOOP
      v_Target := obj.kind || ' ' || v_Qualified || '(' || obj.args || ')';
      IF obj.proacl IS NOT NULL THEN
        v_Script := v_Script || 'REVOKE ALL ON ' || v_Target || ' FROM PUBLIC;' || CHR(10);
        FOR acl IN SELECT * FROM aclexplode(obj.proacl) ORDER BY grantee, privilege_type LOOP
          v_Script := v_Script || 'GRANT ' || acl.privilege_type || ' ON ' || v_Target || ' TO '
                   || CASE WHEN acl.grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(acl.grantee)) END
                   || CASE WHEN acl.is_grantable THEN ' WITH GRANT OPTION' ELSE '' END || ';' || CHR(10);
        END LOOP;
      END IF;
      IF obj.owner <> current_user THEN
        v_Script := v_Script || 'ALTER ' || v_Target || ' OWNER TO ' || quote_ident(obj.owner) || ';' || CHR(10);
      END IF;
      v_Comment := obj_description(obj.oid, 'pg_proc');
      IF v_Comment IS NOT NULL THEN
        v_Script := v_Script || 'COMMENT ON ' || v_Target || ' IS ' || quote_literal(v_Comment) || ';' || CHR(10);
      END IF;
    END LOOP;
  END IF;
  RETURN NULLIF(v_Script, '');
END $$;
