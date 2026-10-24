// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

// Two workers: 2h12m at one worker, 1h44m at two, on the demo containers (2026-10-22). SQL Server's tests take about
// 77 of the 127 minutes either way, so they set the floor. The first attempt at two found four product races, all
// fixed: a NOLOCK catalog read that could return a row twice or skip it, a lock timeout that was retried only on SQL
// Server, MySQL 1684 not retried, and two call sites without the deadlock retry the steps around them had.
//
// Three rules keep a fixture from colliding with another one running beside it:
//   - Fixtures run in parallel with each other, and each one's tests run in order ([Parallelizable(ParallelScope.Self)]).
//     Test cases within a fixture share its tables and procedures.
//   - Each table-quench fixture deploys as its own product (FixtureProduct). Removing what a product no longer declares
//     covers everything it owns in the database, so fixtures sharing one product name dropped each other's foreign keys.
//   - Every direct call into a SchemaSmith procedure goes through TransientRetry, which retries what
//     DeadlockClassifier.IsRetryableContention says a deploy retries. A raw ExecuteNonQuery on one is the regression
//     these failures were:
//
//   EventQuench_RemovingOneOfTwoDeclaredEvents_StillDropsOnlyThatOne         MySQL 1467
//   EventQuench_EmptyingTheFolder_WithTheFlagOff_LeavesTheEventDeployed      MySQL 1467
//   EventQuench_EmptyingTheFolder_LeavesAHandMadeEventAlone                  MySQL 1467
//   RemovedFromProduct_DropsByDefault_WithUnknownIndexesOff                  MySQL 1684
//   IndexOnly_CreatingIndex_EchoesVariantNameInOperationMessage              SQL Server 1205
[assembly: LevelOfParallelism(2)]
