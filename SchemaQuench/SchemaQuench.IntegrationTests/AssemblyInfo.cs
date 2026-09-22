// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using NUnit.Framework;

// Two workers. This was pinned to 1 because LevelOfParallelism(2) exposed transient deadlock and
// setup races in the MySQL `[Parallelizable(ParallelScope.All)]` TableQuench_* fixtures — one failure
// in two full MariaDB runs, which is intermittent enough that neither result characterised anything.
//
// Those races had a cause, and it has been removed rather than waited out:
//   * the catalog was read WITH (NOLOCK), so a scan concurrent with another worker's DDL could return
//     a row twice or skip one entirely — the duplicate form was observed as a primary key violation on
//     a SchemaSmith temp table, and the skip form would have produced the wrong DDL silently;
//   * a lock timeout (1222) was not classified as retryable contention, so a worker that merely waited
//     too long behind another's DDL failed the run outright rather than re-running;
//   * two call sites executed without the deadlock retry the ten quench steps around them had.
//
// Re-verified after those fixes: five consecutive clean runs at 2 workers — three of the MySQL and
// MariaDB categories (691 passed each) and two of the full assembly (1,666 passed each). The full
// assembly runs 21m45 serial against 17m49–18m32 at two workers, so this is worth about 15%, more than
// the ~8% the lever was originally costed at while it was still flaky.
//
// If this has to go back to 1, record WHICH test and at what rate: reverting on a single red run would
// hide a real race behind serialisation, which is what the pin did for as long as it was in place.
[assembly: LevelOfParallelism(2)]
