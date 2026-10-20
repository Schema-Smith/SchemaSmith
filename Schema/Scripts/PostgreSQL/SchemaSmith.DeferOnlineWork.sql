-- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
-- Licensed for use and modification with SchemaSmith products only.
-- Redistribution outside of SchemaSmith product usage is prohibited.

-- OnlineApply: a CONCURRENTLY statement cannot run inside a transaction block, and every quench procedure is one, so
-- the procedure queues it here and SchemaQuench runs the queue on the same session once the procedure returns, each
-- statement on its own, then its FollowUp (the attach, the change row). CleanupIndex names the index a failed build
-- leaves behind, invalid, so the retry can drop it first. A statement already queued is not queued again.
CREATE OR REPLACE PROCEDURE "SchemaSmith"."DeferOnlineWork"(p_Description TEXT, p_Statement TEXT, p_CleanupIndex TEXT, p_FollowUp TEXT)
    LANGUAGE plpgsql
  SET jit = 'off'
AS $$
BEGIN
  IF to_regclass('pg_temp.temp_deferred_online_work') IS NULL THEN
    CREATE TEMPORARY TABLE temp_deferred_online_work ("Seq" INT GENERATED ALWAYS AS IDENTITY, "Description" TEXT, "Statement" TEXT,
                                                      "CleanupIndex" TEXT, "FollowUp" TEXT);
  END IF;
  IF NOT EXISTS (SELECT 1 FROM temp_deferred_online_work WHERE "Statement" = p_Statement) THEN
    INSERT INTO temp_deferred_online_work ("Description", "Statement", "CleanupIndex", "FollowUp")
      VALUES (p_Description, p_Statement, p_CleanupIndex, p_FollowUp);
  END IF;
END $$;
