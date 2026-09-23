// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

// Serial execution. Two workers were tried on this branch and reverted, and the reason is worth keeping
// because the attempt was NOT wasted -- it found a real product bug, and that bug is fixed and shipped
// independently of this setting.
//
// WHAT TWO WORKERS FOUND, and what was fixed because of it:
//   * the catalog was read WITH (NOLOCK), so a scan concurrent with another worker's DDL could return a
//     row twice or skip one entirely -- the skip form would have emitted the wrong DDL silently;
//   * a lock timeout was not classified as retryable contention on any engine but SQL Server, where it
//     is least likely to fire (InnoDB's innodb_lock_wait_timeout defaults to 50 seconds);
//   * MySQL error 1684 -- "table was skipped since its definition is being modified by concurrent DDL
//     statement" -- was not retried, though it is the same event PostgreSQL reports as "could not open
//     relation with OID", which HAS been retried for releases. An accidental single-engine hole;
//   * two call sites executed without the deadlock retry the ten quench steps around them had.
// All four are product fixes that a parallel DEPLOY needed regardless of how this suite runs.
//
// WHY IT WENT BACK TO 1 ANYWAY. The residual failures are not product races -- every one was an
// exception, never a wrong answer -- they are test drivers that bypass the retry the product performs.
// DatabaseQuench wraps every convergence call in ExecuteNonQueryHandlingMessages(retryOnDeadlock: true);
// roughly 38 fixtures instead drive `EXEC SchemaSmith.*` / `CALL SchemaSmith_*` straight through
// ExecuteNonQuery with no retry at all, so at two workers they fail on contention that a real deploy
// simply re-runs. Measured across eight full-assembly runs at two workers, about one in four went red:
//
//   EventQuench_RemovingOneOfTwoDeclaredEvents_StillDropsOnlyThatOne         MySQL 1467
//   EventQuench_EmptyingTheFolder_WithTheFlagOff_LeavesTheEventDeployed      MySQL 1467
//   EventQuench_EmptyingTheFolder_LeavesAHandMadeEventAlone                  MySQL 1467
//   RemovedFromProduct_DropsByDefault_WithUnknownIndexesOff                  MySQL 1684
//   IndexOnly_CreatingIndex_EchoesVariantNameInOperationMessage              SQL Server 1205
//
// Auditing and retry-wrapping ~38 fixtures across four engines is real work and real risk, and it buys a
// suite that runs about 15% faster. An intermittently red gate costs a full CI matrix re-run every time
// it trips, which is worth more than the 15%. So this stays at 1 until those drivers are made uniformly
// retry-safe -- a test-infrastructure task, not a release one.
//
// The three drivers that were already shared DO retry now (BaseTableQuenchTests.RetryingTransientConcurrency,
// which RunTableQuenchProc, RunTableQuenchSteps and RunEventQuench all use), so that much is done.
//
// If this is raised again, raise it with the remaining drivers fixed first, and re-measure: the failures
// above are the regression list, and a single clean run proves nothing against a one-in-four rate.
[assembly: LevelOfParallelism(1)]
