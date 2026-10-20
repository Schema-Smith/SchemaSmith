-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- OnlineApply for one table: the table's own OnlineApply (temp_tables) over the deploy's, and OFF for a table this
-- session created, which is empty, so a plain build costs nothing.
CREATE OR REPLACE FUNCTION "SchemaSmith"."OnlineApplyMode"(p_OnlineApply TEXT, p_Schema TEXT, p_Table TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
  SET jit = 'off'
AS $$
DECLARE
  v_mode TEXT;
BEGIN
  IF EXISTS (SELECT 1 FROM "SchemaSmith"."ChangeAudit" a
              WHERE a."SessionId" = pg_backend_pid() AND a."ObjectType" = 'table' AND a."ActionType" IN ('created', 'wouldCreate')
                AND a."ObjectName" = p_Schema || '.' || p_Table) THEN
    RETURN 'OFF';
  END IF;
  SELECT NULLIF(tt."OnlineApply", '') INTO v_mode FROM temp_tables tt WHERE tt."Schema" = p_Schema AND tt."Name" = p_Table LIMIT 1;
  RETURN COALESCE(v_mode, UPPER(NULLIF(TRIM(p_OnlineApply), '')), 'OFF');
END $$;

-- Why a statement of this kind on this table has no lower-lock form, or NULL when it has one (probed on 12, 17, 18).
-- p_Kind: index, constraint (a primary key or unique constraint, built as an index and attached USING INDEX),
-- withoutOverlaps, exclusion, foreignKey, check.
CREATE OR REPLACE FUNCTION "SchemaSmith"."OnlineUnavailableReason"(p_Schema TEXT, p_Table TEXT, p_Kind TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
  SET jit = 'off'
AS $$
DECLARE
  v_partitioned BOOLEAN := EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                                    WHERE n.nspname = p_Schema AND c.relname = p_Table AND c.relkind = 'p');
BEGIN
  RETURN CASE p_Kind
           WHEN 'check' THEN NULL
           WHEN 'foreignKey' THEN CASE WHEN v_partitioned AND "SchemaSmith"."ServerVersionNum"() < 18
                                       THEN 'a foreign key on a partitioned table cannot be added NOT VALID before PostgreSQL 18' END
           WHEN 'exclusion' THEN 'an exclusion constraint cannot be built concurrently'
           WHEN 'withoutOverlaps' THEN 'a WITHOUT OVERLAPS key cannot be built concurrently'
           ELSE CASE WHEN v_partitioned THEN 'a partitioned table cannot be indexed concurrently' END
         END;
END $$;

-- The form a statement takes: NULL (OnlineApply is OFF for the table), 'online', or 'unavailable'.
CREATE OR REPLACE FUNCTION "SchemaSmith"."OnlineApplyForm"(p_OnlineApply TEXT, p_Schema TEXT, p_Table TEXT, p_Kind TEXT) RETURNS TEXT
    LANGUAGE plpgsql STABLE
  SET jit = 'off'
AS $$
BEGIN
  IF "SchemaSmith"."OnlineApplyMode"(p_OnlineApply, p_Schema, p_Table) = 'OFF' THEN RETURN NULL; END IF;
  RETURN CASE WHEN "SchemaSmith"."OnlineUnavailableReason"(p_Schema, p_Table, p_Kind) IS NULL THEN 'online' ELSE 'unavailable' END;
END $$;
