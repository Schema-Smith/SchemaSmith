# Course 8 · Module 5 — The recovery toolkit

Two induced incidents on one database (`diag_recovery`), recovered two different ways. Modules 1–4
were about **diagnosis** — locate the phase, read the artifact, name the failure. Module 4 recovered
everything with a plain redeploy. This module is the **recovery toolkit**: the three tools for the
situations a plain redeploy doesn't cover, and *when to reach for which*.

## Three tools for three situations

1. **Fix the source, re-run plain — the default.** A plain re-run discards any leftover checkpoint and
   starts fresh from the top. That's safe: every mechanical phase is idempotent, and an
   already-succeeded run-once script stays skipped via the `CompletedMigrationScripts` table. Most
   failures need nothing more.
2. **`--ResumeQuench` — opt in to *not* redo completed work.** Keeps the checkpoint, skips the phases
   and scripts that already finished, and resumes at the failure. Reach for it when re-running the
   completed work is expensive.
3. **Mark-done — bypass a script you handled by hand.** Tell SchemaSmith a run-once script is complete
   so it's never re-attempted. Reach for it when you fixed the underlying problem *outside* the package.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0 or later** on your PATH.

## Step 1 — create the sandbox database

**macOS / Linux**

```bash
# from course8-module-05
bash setup-databases.sh --reset
```

**Windows (PowerShell)**

```powershell
# from course8-module-05
.\setup-databases.ps1 -Reset
```

`--reset` (`-Reset`) drops `diag_recovery` and recreates it empty, so every attempt — the first or a retake — starts from the same clean state. Prints `PASS (reset)` per engine. Only a database the labs created is ever dropped.

**Retaking the module?** Run the reset every time. Without it, the customer `01_backfill_ok.sql`
inserted last time (`CustomerId 20`) is still in `diag_recovery`, but the baseline deploy drops that
script's run-once record (the baseline package does not contain it) — so Beat 1 runs `01` again and it
fails on a duplicate key instead of succeeding. Beat 1 also has you edit `02_backfill_broken.sql`; put
that file back first (`git checkout -- course8-module-05`, from `Demos/Learn`), or `02` will not fail on
the retake.

## Step 2 — deploy the baseline (green)

Change into your engine's folder (`sqlserver`, `postgres`, `mysql`, or `mariadb`); every `schemaquench` below runs there:

```bash
# from course8-module-05/<engine>
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0`, forges `Shop` into `diag_recovery`, and seeds three customers. Note the config sets
`CheckpointDirectory` to a lab-local `./checkpoints` — so when a deploy fails, you can open the
checkpoint file right here instead of digging in `%TEMP%`.

## Beat 1 — resume a half-finished deploy (`--ResumeQuench`)

`beat1-broken/` makes a real schema change (adds `IX_Customer_FullName`) and two run-once After-scripts:
`01_backfill_ok.sql` (succeeds) and `02_backfill_broken.sql` (an `INSERT` with a NULL `Email`, which is
NOT NULL). Deploy it:

```bash
# from course8-module-05/<engine>
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

Exits `2` at the After slot: `Unable to quench '…02_backfill_broken.sql': …NULL…Email…` (`515` /
`23502` / `1048`). The index built, `01` ran, `02` failed — a **half-finished deploy**.

**Open the checkpoint** — `checkpoints/Shop.Main.<server>.diag_recovery..checkpoint`:

```ini
[Completed Steps]
ModifiedTables
IndexesAndConstraints
TableDataDelivery
ForeignKeys
...
[After Scripts]
./beat1-broken/Templates/Main/After Scripts/01_backfill_ok.sql
```

That's your map: four phases done (five on SQL Server, which also checkpoints `TableFeatures`, its CDC,
Change Tracking and FILESTREAM pass), `01` done, `02` is where it stopped. Now watch the two re-runs.

**A plain re-run throws the map away.** Re-run *without* the flag (even before fixing `02`) and the log
re-runs every phase — `Quenching modified tables`, `…indexes and constraints`, `…foreign keys`,
`…after database scripts` — because `CleanupCheckpoints()` discarded the checkpoint. (`01` still skips,
via `Skipping (previously quenched)` — that's the `CompletedMigrationScripts` table, not the
checkpoint.) It starts over.

**`--ResumeQuench` picks up where it stopped.** Fix `02` — give it a real email: in
`<engine>/beat1-broken/Templates/Main/After Scripts/02_backfill_broken.sql`, change
`(21, NULL, 'Ivan Bronze')` to `(21, 'ivan.b@shop.test', 'Ivan Bronze')` — then:

<!-- walk: sed -i "s/(21, NULL, 'Ivan Bronze')/(21, 'ivan.b@shop.test', 'Ivan Bronze')/" "<engine>/beat1-broken/Templates/Main/After Scripts/02_backfill_broken.sql" -->
```bash
# from course8-module-05/<engine>
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs" --ResumeQuench
```

The log opens `Resuming from checkpoint (Completed Steps: 4, Completed Scripts: 1)` (`5` steps on SQL Server), then jumps **straight to
`Quenching after database scripts`** — the completed phases are skipped. Exit `0`, checkpoint
deleted. One gotcha you'll see: `Kindling the forge` still runs. Kindling and the missing-tables step
are **never** checkpointed — they rebuild session state the later phases need, so resume always re-runs
them. "Resume" skips the *phases*, not literally everything.

## Beat 2 — bypass a script you fixed by hand (mark-done)

`beat2-broken/` adds `03_seed_catalog_broken.sql` — a `Product` insert with a NULL `Sku`. Deploy it:

```bash
# from course8-module-05/<engine>
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

`01`/`02`/seed all `Skipping (previously quenched)`, and `03` fails (`Sku` NOT NULL: `515` / `23502` /
`1048`). This time you don't want to re-run the script — you'll seed the catalog correctly **by hand**
and tell SchemaSmith the script is done.

**1. Fix it by hand** (the real remediation, outside the package), from the lab folder:

```bash
# from course8-module-05
../lab-sql.sh sqlserver diag_recovery "INSERT INTO Product (ProductId, Name, Sku, UnitPrice) VALUES (1, 'Anvil', 'ANV-001', 199.99);"
../lab-sql.sh postgres diag_recovery "INSERT INTO public.product (productid, name, sku, unitprice) VALUES (1, 'Anvil', 'ANV-001', 199.99);"
../lab-sql.sh mysql diag_recovery "INSERT INTO Product (ProductId, Name, Sku, UnitPrice) VALUES (1, 'Anvil', 'ANV-001', 199.99);"
../lab-sql.sh mariadb diag_recovery "INSERT INTO Product (ProductId, Name, Sku, UnitPrice) VALUES (1, 'Anvil', 'ANV-001', 199.99);"
```

**2. Mark the script done** — insert a row into `CompletedMigrationScripts` so it's never re-attempted.
The five-column key is the script's template-root-relative path, the product, the slot, the template,
and the schema. On PostgreSQL the schema and table **must** be double-quoted — they're case-sensitive
(escaped as `\"` inside the shell string):

```bash
# from course8-module-05
../lab-sql.sh sqlserver diag_recovery "INSERT SchemaSmith.CompletedMigrationScripts ([ScriptPath],[ProductName],[QuenchSlot],[template_name],[schema_name]) VALUES ('After Scripts/03_seed_catalog_broken.sql','Shop','After','Main','');"
../lab-sql.sh postgres diag_recovery "INSERT INTO \"SchemaSmith\".\"CompletedMigrationScripts\" (\"ScriptPath\",\"ProductName\",\"QuenchSlot\",template_name,schema_name) VALUES ('After Scripts/03_seed_catalog_broken.sql','Shop','After','Main','');"
../lab-sql.sh mysql diag_recovery "INSERT INTO SchemaSmith_CompletedMigrationScripts (ScriptPath,ProductName,QuenchSlot,template_name,schema_name) VALUES ('After Scripts/03_seed_catalog_broken.sql','Shop','After','Main','');"
../lab-sql.sh mariadb diag_recovery "INSERT INTO SchemaSmith_CompletedMigrationScripts (ScriptPath,ProductName,QuenchSlot,template_name,schema_name) VALUES ('After Scripts/03_seed_catalog_broken.sql','Shop','After','Main','');"
```

**3. Re-run plain**, back in your engine's folder:

```bash
# from course8-module-05/<engine>
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

`03` now logs `Skipping (previously quenched)` and the deploy exits `0`. You bypassed
the broken script entirely — the catalog is seeded (your hand-fix), and SchemaSmith won't touch `03`
again.

## Which tool, when

- **Plain re-run** — the default. The failure was a script bug; fix it, re-run, done.
- **`--ResumeQuench`** — same fix, but you don't want to redo completed phases (a big deploy).
- **Mark-done** — you handled the problem another way and want the run-once script skipped for good.

One more lever: a script whose filename ends in `[ALWAYS]` runs every deploy and is never tracked (for
truly idempotent maintenance), and `TrackRunOnceMigrations: false` turns run-once tracking off entirely.

## What's next

- **Module 6 · Per-engine dialects & your runbook** — how the *same* failure reaches you differently on
  each engine, and assembling everything from this course into a team diagnostic runbook.
