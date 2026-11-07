-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- Declarative partitioning, verified before any DDL (live and WhatIf alike). SchemaSmith creates a declared partition
-- tree and adds a declared partition a table lacks; it never moves rows, so a key, a bound or a leaf's tablespace that
-- differs from the deployed one, a declared partition that exists as something else, and a new partition whose range
-- holds rows of the DEFAULT partition are refused by name. A partition the package does not declare is reported and left.
-- Keys and bounds are compared as the engine renders them: declared text that differs from the live text is rendered by
-- the engine on a temporary copy (always rolled back), under UTC, as the create step and extraction run.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."PartitionVerify"(p_WhatIf BOOLEAN = FALSE)
    LANGUAGE plpgsql
  -- JIT off: see the measurement in SchemaSmith.TableQuench.sql.
  SET jit = 'off'
AS $$
DECLARE
  r RECORD;
  v_saved_tz TEXT := current_setting('TimeZone');
  v_problems TEXT[] := '{}';
  v_live_key TEXT;
  v_rendered TEXT;
  v_live_bound TEXT;
  v_oid OID;
  v_parent_oid OID;
  v_default_oid OID;
  v_condition TEXT;
  v_holds BOOLEAN;
BEGIN
  IF NOT EXISTS (SELECT 1 FROM temp_tables WHERE "PartitionMethod" <> '') THEN RETURN; END IF;
  RAISE NOTICE 'Verify Declared Partitioning';
  PERFORM set_config('TimeZone', 'UTC', true);

  -- The declared key of every managed table (and of every partition that is itself partitioned) against the live one.
  FOR r IN
    SELECT t."Schema" AS sch, t."Name" AS obj, 'Table' AS kind, t."PartitionMethod" AS method, t."PartitionExpression" AS expr
      FROM temp_tables t WHERE t."PartitionMethod" <> ''
    UNION ALL
    SELECT p."TableSchema", p."Name", 'Partition', p."Method", p."Expression" FROM temp_partitions p
  LOOP
    v_oid := to_regclass("SchemaSmith"."QuoteName"(r.sch) || '.' || "SchemaSmith"."QuoteName"(r.obj));
    CONTINUE WHEN v_oid IS NULL;
    IF r.method = '' THEN
      IF (SELECT relkind FROM pg_class WHERE oid = v_oid) = 'p' AND r.kind = 'Partition' THEN
        v_problems := v_problems || format('Partition %s.%s is declared as a leaf, but is deployed as %s. SchemaSmith does not repartition an existing table (that moves every row) -- migrate it manually, or correct the declared partitioning to match.',
                                           r.sch, r.obj, pg_get_partkeydef(v_oid));
      END IF;
      CONTINUE;
    END IF;
    IF (SELECT relkind FROM pg_class WHERE oid = v_oid) <> 'p' THEN
      v_problems := v_problems || format('%s %s.%s declares PARTITION BY %s (%s), but is deployed unpartitioned. PostgreSQL cannot partition an existing table in place -- migrate it manually, or remove Partitioning from the declaration.',
                                         r.kind, r.sch, r.obj, r.method, r.expr);
      CONTINUE;
    END IF;
    v_live_key := pg_get_partkeydef(v_oid);
    CONTINUE WHEN v_live_key = r.method || ' (' || r.expr || ')';
    v_rendered := NULL;
    BEGIN
      EXECUTE format('CREATE TEMPORARY TABLE schemasmith_part_probe (LIKE %I.%I) PARTITION BY %s (%s)', r.sch, r.obj, r.method, r.expr);
      v_rendered := pg_get_partkeydef('pg_temp.schemasmith_part_probe'::regclass);
      RAISE EXCEPTION USING ERRCODE = 'SSPRB';
    EXCEPTION
      WHEN SQLSTATE 'SSPRB' THEN NULL;
      WHEN OTHERS THEN v_rendered := NULL;
    END;
    IF v_rendered IS DISTINCT FROM v_live_key THEN
      v_problems := v_problems || format('%s %s.%s declares PARTITION BY %s (%s), but is deployed as PARTITION BY %s. SchemaSmith does not repartition an existing table (that moves every row) -- migrate it manually, or correct the declared partitioning to match.',
                                         r.kind, r.sch, r.obj, r.method, r.expr, v_live_key);
    END IF;
  END LOOP;

  -- Each declared partition: what it is, where it sits, its bound, its tablespace -- or, when it is new, whether the
  -- DEFAULT partition beside it holds rows its range would take.
  FOR r IN SELECT p.* FROM temp_partitions p ORDER BY p."TableSchema", p."TableName", p."Depth", p."Position" LOOP
    v_parent_oid := to_regclass("SchemaSmith"."QuoteName"(r."TableSchema") || '.' || "SchemaSmith"."QuoteName"(r."ParentName"));
    v_oid := to_regclass("SchemaSmith"."QuoteName"(r."TableSchema") || '.' || "SchemaSmith"."QuoteName"(r."Name"));
    CONTINUE WHEN v_parent_oid IS NULL OR (SELECT relkind FROM pg_class WHERE oid = v_parent_oid) <> 'p';

    IF v_oid IS NOT NULL THEN
      IF NOT EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid = v_oid AND inhparent = v_parent_oid) THEN
        v_problems := v_problems || format('Partition %s.%s of %s.%s is declared, but %s.%s already exists and is not a partition of it (%s). SchemaSmith does not ATTACH an existing table -- attach it manually, or rename the declared partition.',
                                           r."TableSchema", r."Name", r."TableSchema", r."ParentName", r."TableSchema", r."Name",
                                           COALESCE((SELECT 'a partition of ' || ih.inhparent::regclass::text FROM pg_inherits ih JOIN pg_class c ON c.oid = ih.inhrelid
                                                      WHERE ih.inhrelid = v_oid AND c.relispartition), 'a standalone table'));
        CONTINUE;
      END IF;
      v_live_bound := pg_get_expr((SELECT relpartbound FROM pg_class WHERE oid = v_oid), v_oid);
      IF v_live_bound <> r."Bound" THEN
        v_rendered := NULL;
        BEGIN
          EXECUTE format('CREATE TEMPORARY TABLE schemasmith_part_probe (LIKE %I.%I) PARTITION BY %s', r."TableSchema", r."ParentName", pg_get_partkeydef(v_parent_oid));
          EXECUTE format('CREATE TEMPORARY TABLE schemasmith_part_probe_c PARTITION OF pg_temp.schemasmith_part_probe %s', r."Bound");
          v_rendered := pg_get_expr((SELECT relpartbound FROM pg_class WHERE oid = 'pg_temp.schemasmith_part_probe_c'::regclass), 'pg_temp.schemasmith_part_probe_c'::regclass);
          RAISE EXCEPTION USING ERRCODE = 'SSPRB';
        EXCEPTION
          WHEN SQLSTATE 'SSPRB' THEN NULL;
          WHEN OTHERS THEN v_rendered := 'the engine refused it: ' || SQLERRM;
        END;
        IF v_rendered IS DISTINCT FROM v_live_bound THEN
          v_problems := v_problems || format('Partition %s.%s of %s.%s declares %s, but is deployed with %s. SchemaSmith does not change a partition''s bound (that is a DETACH and re-ATTACH, which moves or re-checks rows) -- change it manually, or correct the declared bound to match.',
                                             r."TableSchema", r."Name", r."TableSchema", r."ParentName", r."Bound", v_live_bound);
        END IF;
      END IF;
      IF r."Tablespace" IS NOT NULL AND (SELECT relkind FROM pg_class WHERE oid = v_oid) = 'r'
         AND r."Tablespace" IS DISTINCT FROM COALESCE((SELECT ts.spcname FROM pg_class c JOIN pg_tablespace ts ON ts.oid = c.reltablespace WHERE c.oid = v_oid),
                                                       (SELECT ts.spcname FROM pg_database d JOIN pg_tablespace ts ON ts.oid = d.dattablespace WHERE d.datname = current_database())) THEN
        v_problems := v_problems || format('partition %s.%s declares tablespace %s, but is currently deployed on %s. SchemaSmith does not move an existing object to a different tablespace (that is a rewrite) -- migrate it manually, or correct the declared tablespace to match.',
                                           r."TableSchema", r."Name", r."Tablespace",
                                           COALESCE((SELECT ts.spcname FROM pg_class c JOIN pg_tablespace ts ON ts.oid = c.reltablespace WHERE c.oid = v_oid),
                                                    (SELECT ts.spcname FROM pg_database d JOIN pg_tablespace ts ON ts.oid = d.dattablespace WHERE d.datname = current_database())));
      END IF;
      CONTINUE;
    END IF;

    -- A new partition beside a DEFAULT one that holds rows in its range: adding it would move them.
    CONTINUE WHEN r."IsDefault";
    SELECT ih.inhrelid INTO v_default_oid FROM pg_inherits ih JOIN pg_class c ON c.oid = ih.inhrelid
     WHERE ih.inhparent = v_parent_oid AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';
    CONTINUE WHEN v_default_oid IS NULL;
    v_condition := NULL;
    BEGIN
      EXECUTE format('CREATE TEMPORARY TABLE schemasmith_part_probe (LIKE %I.%I) PARTITION BY %s', r."TableSchema", r."ParentName", pg_get_partkeydef(v_parent_oid));
      EXECUTE format('CREATE TEMPORARY TABLE schemasmith_part_probe_c PARTITION OF pg_temp.schemasmith_part_probe %s', r."Bound");
      v_condition := pg_get_partition_constraintdef('pg_temp.schemasmith_part_probe_c'::regclass);
      RAISE EXCEPTION USING ERRCODE = 'SSPRB';
    EXCEPTION
      WHEN SQLSTATE 'SSPRB' THEN NULL;
      WHEN OTHERS THEN v_condition := NULL;
    END;
    CONTINUE WHEN v_condition IS NULL;
    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s WHERE %s)', v_default_oid::regclass, v_condition) INTO v_holds;
    IF v_holds THEN
      v_problems := v_problems || format('Partition %s.%s cannot be added to %s.%s: its DEFAULT partition %s holds rows inside the bound %s. Adding it would move those rows, which SchemaSmith does not do -- move them manually, then redeploy.',
                                         r."TableSchema", r."Name", r."TableSchema", r."ParentName", v_default_oid::regclass, r."Bound");
    END IF;
  END LOOP;

  -- Live partitions of a managed table the package does not name: reported, never dropped or detached.
  FOR r IN
    WITH RECURSIVE tree AS (
      SELECT t."Schema" AS sch, t."Name" AS parent, to_regclass("SchemaSmith"."QuoteName"(t."Schema") || '.' || "SchemaSmith"."QuoteName"(t."Name")) AS parent_oid
        FROM temp_tables t WHERE t."PartitionMethod" <> ''
      UNION ALL
      SELECT tree.sch, c.relname::TEXT COLLATE "default", c.oid FROM tree JOIN pg_inherits ih ON ih.inhparent = tree.parent_oid JOIN pg_class c ON c.oid = ih.inhrelid)
    SELECT tree.sch, tree.parent, c.relname::TEXT AS child
      FROM tree JOIN pg_inherits ih ON ih.inhparent = tree.parent_oid JOIN pg_class c ON c.oid = ih.inhrelid AND c.relkind IN ('r', 'p')
     WHERE NOT EXISTS (SELECT 1 FROM temp_partitions p WHERE p."TableSchema" = tree.sch AND p."Name" = c.relname)
  LOOP
    RAISE NOTICE '  Partition %.% of %.% is not declared in the package (left as it is; SchemaSmith does not drop or detach partitions)', r.sch, r.child, r.sch, r.parent;
  END LOOP;

  PERFORM set_config('TimeZone', v_saved_tz, true);
  IF array_length(v_problems, 1) > 0 THEN
    FOR i IN 1 .. array_length(v_problems, 1) LOOP
      RAISE NOTICE '  %', v_problems[i];
    END LOOP;
    RAISE EXCEPTION '%', array_to_string(v_problems, E'\n');
  END IF;
END $$;
