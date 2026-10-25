# Course 7, Module 5 — Diagnosing a fleet failure (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (#576 debug SQL): delete the "On 2.7.x" callouts -->

Goal: roll out a new unique index across the whole fleet, watch **two** tenants fail in **two different
phases**, and work through the exact sequence a real operator uses to locate each tenant's evidence, name its
phase, remediate it, and resume the run. You'll do all of this on SQL Server, then repeat the same moves on
PostgreSQL, MySQL, and MariaDB.

This is the diagnostic capstone of Course 7. Do Modules 1–4 first — you already know how to build the
fleet, tune it, handle drift, and resume a partial failure. M5 is the middle step those modules skipped:
*what do you actually look at when more than one tenant fails in different ways?*

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once if you haven't already.
- The CLI is on your PATH — `schemaquench --version` answers **2.7.0** or later.

Each engine folder ships a `baseline/` package (the established fleet state), an `after/` package (adds the
`UQ_Customer_Email` unique index to `Customer`), two settings files, and drift + reset helpers for the two
failing tenants.

| File | What it's for |
| --- | --- |
| `baseline/` | The established Shop package — no `UQ_Customer_Email` yet. |
| `after/` | The rollout package — adds `UQ_Customer_Email` to `Customer.Email`. |
| `quench.settings.baseline.json` | Fleet run against `baseline/`. |
| `quench.settings.after.json` | Fleet run against `after/` (wires `ArtifactPath` and `CheckpointDirectory`). |
| `drift-tenant-002.sql` | Plants duplicate emails in `fleet_tenant_002` so the unique index fails to build. |
| `reset-tenant-002.sql` | Fixes the duplicate so the index can succeed on resume. |
| `drift-tenant-004.sql` | Drops `FK_OrderItem_Product` and inserts an orphan row in `fleet_tenant_004`. |
| `reset-tenant-004.sql` | Removes the orphan so the FK re-add succeeds on resume. |

## Step 1: Deploy the baseline

Land the `baseline/` package fleet-wide so every tenant starts at the same known state before the rollout:

```bash
# from course7-module-05/<engine>
schemaquench --ConfigFile:quench.settings.baseline.json
```

All five tenants deploy cleanly. Exit code `0`. Keep this as your clean starting point — you'll return to it
via the reset scripts in Step 5.

## Step 2: Stage the drift

Real fleet failures come from tenants that changed between your last deploy and this one. Simulate both
failure modes before the rollout. Each engine has its own drift files; run the lines for your engine.

**Stage the index failure on tenant 002** (duplicate emails):

```bash
# from course7-module-05
../lab-sql.sh sqlserver fleet_tenant_002 --file sqlserver/drift-tenant-002.sql
../lab-sql.sh postgres fleet_tenant_002 --file postgres/drift-tenant-002.sql
../lab-sql.sh mysql fleet_tenant_002 --file mysql/drift-tenant-002.sql
../lab-sql.sh mariadb fleet_tenant_002 --file mariadb/drift-tenant-002.sql
```

**Stage the FK failure on tenant 004** (dropped FK + orphan row):

```bash
# from course7-module-05
../lab-sql.sh sqlserver fleet_tenant_004 --file sqlserver/drift-tenant-004.sql
../lab-sql.sh postgres fleet_tenant_004 --file postgres/drift-tenant-004.sql
../lab-sql.sh mysql fleet_tenant_004 --file mysql/drift-tenant-004.sql
../lab-sql.sh mariadb fleet_tenant_004 --file mariadb/drift-tenant-004.sql
```

Look at the comments at the top of each drift file — they describe exactly what broke and why the rollout
will trip over it. Reading the drift script is the first diagnostic move: it shows you the *intent* of the
damage, which the engine's error will confirm.

## Step 3: Run the rollout — watch it fail

Deploy the `after/` package fleet-wide. The settings file already wires `ArtifactPath` and
`CheckpointDirectory`. Add `--WriteDebugSql`, because Step 4 reads the SQL each failed phase ran — debug SQL is
written only when you ask for it, since it holds every token's real value:

```bash
# from course7-module-05/<engine>
schemaquench --ConfigFile:quench.settings.after.json --WriteDebugSql
```

(On a real incident you usually find out you want it after the fact: without the switch, the line under each
error reads `Rerun with --WriteDebugSql to write the resolved SQL of this step to ./artifacts.`)

> **On 2.7.x** the switch is logged as `Ignored - not recognized` and the artifacts are written anyway.

The run finishes with exit code `2`. Tenants 001, 003, and 005 succeed. Two fail:

```
[localhost,11433].[fleet_tenant_002] FAILED to quench:
The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Customer' and the index name 'UQ_Customer_Email'. The duplicate key value is (dupe@shop.example).
[localhost,11433].[fleet_tenant_004] FAILED to quench:
The ALTER TABLE statement conflicted with the FOREIGN KEY constraint "FK_OrderItem_Product". The conflict occurred in database "fleet_tenant_004", table "dbo.Product", column 'ProductId'.
Template 'Main' had 2 failed work unit(s)
One or more database quenches FAILED
```

Two failures, two different engine errors — and they happened in two different phases of the convergence
pipeline. The failure count tells you how many; the error text names the phase.

## Step 4: Locate what failed and where

This is the diagnostic work. Start with the fast fleet-triage read, then go deeper per-tenant.

### 4a) Read Failures.log first

Open `logs/SchemaQuench - Failures.log`. It opens with the count, then one
block per failed tenant — error, phase trail, and artifact pointer all in one place:

```
2 failure(s): 2 Template:Main

─── FAILED  [Template:Main]  [localhost,11433].[fleet_tenant_002] ───
Error: The CREATE UNIQUE INDEX statement terminated because a duplicate key was found...
Debug SQL: ./artifacts\SchemaQuench - Quench Indexes localhost,11433.fleet_tenant_002.sql
Context (last 25 lines):
    ...
        Add Missing Indexes
          Creating index [dbo].[Customer].[UQ_Customer_Email]
  The CREATE UNIQUE INDEX statement terminated...

─── FAILED  [Template:Main]  [localhost,11433].[fleet_tenant_004] ───
Error: The ALTER TABLE statement conflicted with the FOREIGN KEY constraint "FK_OrderItem_Product"...
Debug SQL: ./artifacts\SchemaQuench - Quench Foreign Keys localhost,11433.fleet_tenant_004.sql
Context (last 25 lines):
    ...
    Quenching foreign keys
        Add Missing Foreign Keys
          Adding foreign key [dbo].[OrderItem].[FK_OrderItem_Product]
  The ALTER TABLE statement conflicted...
```

One read names both failed tenants, both errors, both phases (index phase for 002, FK phase for 004), and
both artifact paths. That's everything you need before opening anything else.

### 4b) The interleaved Progress.log (deeper drill-down)

**The `FAILED to quench:` blocks**

The log lines from Step 3 are all you need to name the phase:

- `002`: `CREATE UNIQUE INDEX` terminated on a duplicate key — the index build failed. This is the
  **index phase** (`Quench Indexes`). Error 1505 (SQL Server dup-key on index create).
- `004`: `ALTER TABLE` conflicted with a `FOREIGN KEY` constraint — a foreign key re-add tripped on an
  orphan row. This is the **FK phase** (`Quench Foreign Keys`). Error 547 (SQL Server FK conflict).

Notice: 004's FK was not in the `after/` change. The convergence engine re-checks the whole model on every
run; it found `FK_OrderItem_Product` missing, recreated it WITH CHECK, and the orphan failed it. The rollout
didn't cause the drift — it exposed it.

**The checkpoint files**

After a partial-failure run, **all five tenants** keep a checkpoint file — the checkpoint directory is not a
"failed tenants" manifest. The diagnostic signal is `[Completed Steps]`. On SQL Server:

- `001`, `003`, `005` (succeeded): five steps recorded — `ModifiedTables`, `IndexesAndConstraints`,
  `TableFeatures` (SQL Server's FILESTREAM / CDC / Change Tracking step), `TableDataDelivery`, `ForeignKeys`
- `002` (stopped at indexes): `[Completed Steps]` lists only `ModifiedTables`
- `004` (stopped at FK): four steps — everything except `ForeignKeys`

Read the checkpoint alongside the error: 002's one completed step tells you exactly what the engine
*finished* before the failure. The unfinished phase is the next one — indexes. 004's four steps confirm it
got all the way through index creation and the table-features step before the FK phase terminated it.
PostgreSQL, MySQL, and MariaDB have no `TableFeatures` step, so their succeeded tenants record four steps and `004` three.

**The resolved-SQL artifacts**

The `./artifacts` folder (there because of `--WriteDebugSql`) holds thin, copy-runnable `EXEC` wrappers — one per phase per tenant — named
`<server>.<database>.sql`. For example:

```
SchemaQuench - Quench Indexes localhost,11433.fleet_tenant_002.sql
SchemaQuench - Quench Foreign Keys localhost,11433.fleet_tenant_004.sql
```

These let you run the exact SQL the engine would execute — useful when you want to test the fix before
resuming. The engine error is in the **log**, not in the artifact; the artifact is the runnable command.
Notice that `002` has no `Quench Foreign Keys` artifact — the run stopped before it reached that phase.

> **Cross-reference Course 8:** *Reading the black box* (Index constraints, FK failures) covers the
> per-phase diagnostic method in depth — how to read the artifact, map the log error to the right table, and
> trace an orphan. M5 teaches the fleet overlay: locating which tenants failed and at which phase. Use both
> together on a real incident.

## Step 5: Remediate and resume

Fix each drifted tenant, then resume the run. Resume replays only the tenants that didn't finish — the ones
that succeeded skip past all their already-completed steps instantly.

**Fix tenant 002** (deduplicate the emails):

```bash
# from course7-module-05
../lab-sql.sh sqlserver fleet_tenant_002 --file sqlserver/reset-tenant-002.sql
../lab-sql.sh postgres fleet_tenant_002 --file postgres/reset-tenant-002.sql
../lab-sql.sh mysql fleet_tenant_002 --file mysql/reset-tenant-002.sql
../lab-sql.sh mariadb fleet_tenant_002 --file mariadb/reset-tenant-002.sql
```

**Fix tenant 004** (remove the orphan):

```bash
# from course7-module-05
../lab-sql.sh sqlserver fleet_tenant_004 --file sqlserver/reset-tenant-004.sql
../lab-sql.sh postgres fleet_tenant_004 --file postgres/reset-tenant-004.sql
../lab-sql.sh mysql fleet_tenant_004 --file mysql/reset-tenant-004.sql
../lab-sql.sh mariadb fleet_tenant_004 --file mariadb/reset-tenant-004.sql
```

**Resume the run:**

```bash
# from course7-module-05/<engine>
schemaquench --ConfigFile:quench.settings.after.json --ResumeQuench
```

On SQL Server:

```
[fleet_tenant_003] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[fleet_tenant_004] Resuming from checkpoint (Completed Steps: 4, Completed Scripts: 0)
[fleet_tenant_001] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[fleet_tenant_005] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[fleet_tenant_002] Resuming from checkpoint (Completed Steps: 1, Completed Scripts: 0)
```

All five report `Successfully Quenched`. Exit code `0`. The checkpoint files are cleared — the run finished
clean.

Read the step counts: 001/003/005 are at 5 — every checkpointed step already done, so they skip past all
of them. 004 restarts from step 5 (FK phase only). 002 restarts from step 2 (indexes onward). On the
other engines the counts are one lower where `TableFeatures` would be: 001/003/005 at 4, 004 at 3, 002 at 1. The checkpoint is the engine's
memory of exactly how far it got, so resume is surgical: you redeploy *only what didn't finish*.

## Step 6: Do it on PostgreSQL, MySQL, and MariaDB

Same six moves in `postgres/`, `mysql/`, and `mariadb/`. The fleet behavior is identical — two tenants fail,
exit `2`, resume exits `0`. Only the error text changes:

| Failure | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Index phase (002) | `The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Customer' and the index name 'UQ_Customer_Email'. The duplicate key value is (dupe@shop.example).` | `23505: could not create unique index "uq_customer_email"` | `Duplicate entry 'dupe@shop.example' for key 'Customer.UQ_Customer_Email'` |
| FK phase (004) | `The ALTER TABLE statement conflicted with the FOREIGN KEY constraint "FK_OrderItem_Product". The conflict occurred in database "fleet_tenant_004", table "dbo.Product", column 'ProductId'.` | `23503: insert or update on table "orderitem" violates foreign key constraint "fk_orderitem_product"` | `Cannot add or update a child row: a foreign key constraint fails` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native
> package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics
> (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for
> you.* Its error text for both failures here matches the MySQL column above.

The drift and reset lines in Steps 2 and 5 already carry each engine's own files. Run, diagnose, reset, and
resume from each engine's folder. The artifact naming pattern is the same across all four engines, and
the checkpoint counts differ only by SQL Server's extra `TableFeatures` step — beyond that, only the error
surface differs (on
PostgreSQL, detail lands in `ProgressLog`; on MySQL and MariaDB, it surfaces via the
`SchemaSmith_StatusMessages` sidecar and `ProgressLog` FAILED block — `Errors.log` is empty on all
non-SS engines).

> **Note on Failures.log across engines:** `SchemaQuench - Failures.log` is produced on all four engines
> (SQL Server, PostgreSQL, MySQL, MariaDB). The block format and phase context trail are identical; only
> the error text inside each block changes per engine.

## Cleanup

The reset scripts return both tenants to a clean, deployable state. A successful resume deletes the
checkpoint files automatically; if you bailed out early, remove leftover directories:

```bash
# from course7-module-05
rm -rf sqlserver/checkpoints sqlserver/artifacts
rm -rf postgres/checkpoints postgres/artifacts
rm -rf mysql/checkpoints mysql/artifacts
rm -rf mariadb/checkpoints mariadb/artifacts
```

## The principle

One failure count can hide unrelated causes. `Template 'Main' had 2 failed work unit(s)` tells you *how
many* — not *why each one*, not *where each one stopped*. The diagnostic sequence is always the same:
open `Failures.log` first — it names every failed tenant, its error, the phase context, and
the artifact pointer in one read. Then drill into the per-tenant checkpoint to confirm depth, and the
artifact to see the exact SQL the engine tried to run. Fix with intent — not just "it worked" but "I know
what broke, I removed the specific condition that caused it, and I confirmed the fix before resuming." Then
resume, and the engine handles the rest.

That's Course 7 complete. You can stand up a fleet, steer it, grow it, operate it safely when a tenant
drifts, and now diagnose it precisely when more than one tenant fails in different ways.
