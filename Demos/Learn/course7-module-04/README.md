# Course 7, Module 4 — Operating the fleet safely (lab)

Goal: run a schema change across the **whole fleet at once** — and come out the other side with the fleet
whole even when one tenant goes wrong. You'll preview the roster before you touch anything, tune how hard
the run hits your servers, watch one drifted tenant fail while the rest sail through, then **resume** —
redeploying only the tenant you fixed, not the ones already done. All four engines.

This is the capstone. Do Modules 1–3 first — you're now *operating* the fleet those modules built.

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005`).
- The CLI is on your PATH — `schemaquench --version` answers **2.7.0** or later.

Each engine folder ships the same native `Shop` `Package/` as Modules 1–3, plus two settings files and two
drift helpers. The roster is **discovery-driven** — the template's `DatabaseIdentificationScript` finds
every `fleet_tenant_*` database, exactly as in Module 1. No `TemplateTargets` here; this module is about
running the discovered fleet safely.

| File | What it's for |
| --- | --- |
| `quench.settings.json` | The baseline fleet run. |
| `quench.settings.tuned.json` | Same run with `MaxThreads: 3` — the throttle. |
| `drift-tenant-003.sql` | Puts `fleet_tenant_003` into a state that fails to deploy. |
| `reset-tenant-003.sql` | Fixes the drift so the resume can finish it. |

## Step 1: Look before you leap — `--PreviewTargets`

Before a fleet-wide run, confirm the fleet you *think* you're about to hit. `--PreviewTargets` resolves the
whole roster and prints it — and deploys **nothing**:

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.json --PreviewTargets
```

```
Pre-flight diagnostics for Shop (--PreviewTargets)
Template: Main [required]
  db: fleet_tenant_001
  db: fleet_tenant_002
  db: fleet_tenant_003
  db: fleet_tenant_004
  db: fleet_tenant_005
RESULT: PASS
```

Exit code `0`, not a single object touched. This is your dry-run over the *targets* — the roster the run
would fan out across. (If a required template resolved to zero databases, you'd get `RESULT: FAIL` and exit
`2` instead — a fleet run that would have quietly done nothing, caught before it started.)

## Step 2: Tune the throttle — `MaxThreads`

SchemaSmith deploys the fleet through one bounded pool of worker threads — **not** one thread per server.
`MaxThreads` sets the ceiling: default **10**, valid range **1–20**. Lower it to be gentle on a shared
admin box; raise it for headroom on a large fleet. `quench.settings.tuned.json` pins it to `3`:

```json
{
  "MaxThreads": 3,
  ...
}
```

```bash
schemaquench --ConfigFile:quench.settings.tuned.json
```

```
  MaxThreads: 3
...
Completed quench of Shop
```

All five tenants deploy, at most three at a time. One knob, one number — the difference between a polite
rollout and one that saturates your I/O.

## Step 3: The big run — one tenant drifts

Real fleets drift. One tenant's schema gets touched out-of-band and no longer matches the package. Simulate
it — `drift-tenant-003.sql` relaxes `fleet_tenant_003`'s `Product.Sku` to allow NULLs and parks a row with a
NULL `Sku` in it:

```bash
cd ..            # back to the lab folder
../lab-sql.sh sqlserver fleet_tenant_003 --file sqlserver/drift-tenant-003.sql
cd sqlserver     # back into the engine folder
```

Now run the fleet. Wire a checkpoint directory in — you'll need it in Step 4:

```bash
schemaquench --ConfigFile:quench.settings.json --CheckpointDirectory:./checkpoints
```

`fleet_tenant_003` can't reconcile `Sku` back to NOT NULL — it holds a NULL — so that one work unit fails.
Because `ContinueOnDatabaseFailure` defaults to **true**, the rest of the fleet finishes anyway:

```
[localhost,11433].[fleet_tenant_003] FAILED to quench:
Cannot insert the value NULL into column 'Sku', table 'fleet_tenant_003.dbo.Product'; column does not allow nulls. UPDATE fails.
[localhost,11433].[fleet_tenant_001] Successfully Quenched
[localhost,11433].[fleet_tenant_002] Successfully Quenched
[localhost,11433].[fleet_tenant_004] Successfully Quenched
[localhost,11433].[fleet_tenant_005] Successfully Quenched
Template 'Main' had 1 failed work unit(s)
One or more database quenches FAILED
```

Four of five succeeded; one failed; the run exits `2` (partial failure). The exact reconcile error is
engine-specific — PostgreSQL says `column "sku" of relation "product" contains null values`, MySQL (and
MariaDB, which mirrors it) says `Invalid use of NULL value` — but the fleet-level behavior is identical on
all four: one fails, the rest finish, exit `2`, and `Template 'Main' had 1 failed work unit(s)` names the
count.

> **The opposite posture.** Set `"ContinueOnDatabaseFailure": false` on the `Main` template (in
> `Package/Templates/Main/Template.json`) and the run **aborts at the first failure** instead — tenants
> after the failing one are never attempted. With a bounded pool the contrast is stark: where continue mode
> finished four tenants, abort mode finishes only the ones that ran before the failure. Both modes print
> `One or more database quenches FAILED` and exit `2` — that line is the run-level verdict, not the
> difference. The difference is what *didn't* run. Continue is the fleet default because one bad tenant
> shouldn't hold the other 999 hostage.

## Step 4: Resume — redeploy only the tail

You've triaged: the failure was one drifted tenant. Fix it — `reset-tenant-003.sql` clears the offending
row so `Sku` can go back to NOT NULL:

```bash
cd ..            # back to the lab folder
../lab-sql.sh sqlserver fleet_tenant_003 --file sqlserver/reset-tenant-003.sql
cd sqlserver     # back into the engine folder
```

Now **resume** the run. The failed run left its checkpoints behind (a clean run deletes them); `--ResumeQuench`
picks up where it stopped:

```bash
schemaquench --ConfigFile:quench.settings.json --CheckpointDirectory:./checkpoints --ResumeQuench
```

```
[localhost,11433].[fleet_tenant_001]   [fleet_tenant_001] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[localhost,11433].[fleet_tenant_002]   [fleet_tenant_002] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[localhost,11433].[fleet_tenant_003]   [fleet_tenant_003] Resuming from checkpoint (Completed Steps: 1, Completed Scripts: 0)
[localhost,11433].[fleet_tenant_004]   [fleet_tenant_004] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
[localhost,11433].[fleet_tenant_005]   [fleet_tenant_005] Resuming from checkpoint (Completed Steps: 5, Completed Scripts: 0)
...
[localhost,11433].[fleet_tenant_003] Successfully Quenched
```

Read the checkpoint counts: the four that finished are at `Completed Steps: 5` — every step already done, so
they do **no real work**. Only `fleet_tenant_003`, stopped at `Completed Steps: 1`, picks its remaining
steps back up and finishes. The run exits `0`, and on that clean success the checkpoint files are deleted.
On a thousand-tenant fleet where 999 succeeded, resume means you redeploy **one** — not all thousand.

## Step 5: Do it on PostgreSQL, MySQL, and MariaDB

Same five moves in `postgres/`, `mysql/`, and `mariadb/`. `--PreviewTargets`, `MaxThreads`,
`ContinueOnDatabaseFailure`, and `--ResumeQuench` behave identically on all four engines — only the
drifted-tenant reconcile message differs (shown above). Use each engine's `drift-tenant-003.sql` /
`reset-tenant-003.sql` (they run against the `fleet_tenant_003` database directly — see the header
comment in each file).

## Cleanup

Each engine's `reset-tenant-003.sql` already returns `fleet_tenant_003` to a clean, deployable state. If you
ran the abort-mode aside, revert the `Template.json` edit too. Delete any leftover `./checkpoints` directory
(the successful resume removes the checkpoint files; the empty folder is harmless):

```bash
rm -rf sqlserver/checkpoints postgres/checkpoints mysql/checkpoints mariadb/checkpoints
```

## The principle

A fleet-wide deploy shouldn't be a leap of faith. Preview tells you the blast radius before you commit.
`MaxThreads` controls how hard it lands. `ContinueOnDatabaseFailure` keeps one sick tenant from stopping the
rest. And `--ResumeQuench` turns a partial failure into a one-tenant fix-up instead of a full redo. That's
what it takes to run SchemaSmith as fleet infrastructure — not just to deploy the fleet, but to operate it
on the day something goes wrong.

Next up — Module 5, the diagnostic capstone: when a fleet-wide run comes back with more than one tenant
failed, in different phases, you'll locate each tenant's evidence, name the phase, and resume with intent.
