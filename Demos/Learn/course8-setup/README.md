# Course 8 — Setup + the green-run evidence tour

This lab does two things:

1. Stands up the **diagnostics baseline database** — one empty `diag_baseline` per engine
   (SQL Server, PostgreSQL, MySQL, MariaDB) on the shared sandbox.
2. Walks a **successful** deploy of the `Shop` schema into it, so you can open the evidence a
   *healthy* run leaves behind — the logs, the phase-named resolved-SQL artifacts — and see the one
   thing that is **absent** on success: a checkpoint. That contrast is the whole diagnostic method
   Course 8 is built on. Module 1 breaks a deploy on purpose; here you learn what "green" looks like
   first.

The `diag_` prefix keeps this course's databases clear of Course 6's `shop_tenant_*` and Course 7's
`fleet_tenant_*` in the same sandbox.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0** or later on your PATH. New to the CLI? Install it in
  [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the baseline database

**macOS / Linux**

```bash
cd Demos/Learn/course8-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
cd Demos\Learn\course8-setup
.\setup-databases.ps1
```

Prints `PASS` per engine once `diag_baseline` exists on it. Re-running is safe — every `CREATE` is
guarded.

## Step 2 — deploy the Shop baseline (the green run)

Pick an engine, change into its folder, and quench. `--LogPath` is passed as an **absolute** path so
the base logs land next to the package (a relative `--LogPath` leaves them in the tool directory):

**macOS / Linux**

```bash
cd sqlserver          # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

**Windows (PowerShell)**

```powershell
cd sqlserver          # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

It exits `0` and forges the `Shop` schema (`Customer`, `Product`, `SalesOrder`, `OrderItem`) into
`diag_baseline`. `Customer` carries a **non-unique** `IX_Customer_Email` — remember that index; it is
the one a later incident tries to flip to `UNIQUE` on dirty data.

## Step 3 — tour the evidence a green run leaves

Everything the tool leaves is now next to the package. Open each and read it — this is the black box
you will learn to read under failure.

| Where | What it is |
| --- | --- |
| `logs/SchemaQuench - Progress.log` | The run narrative. Read the phase lines in order — `Quenching missing tables and columns` → `modified tables` → `indexes and constraints` → `foreign keys` → `Successfully Quenched`. **This ordered list is the quench phase map**, the spine of every diagnosis in this course. |
| `logs/SchemaQuench - Errors.log` | The error log. On a green run it stays empty — that is the point. |
| `logs/SchemaQuench.0001/` | A numbered backup of the logs. SchemaSmith copies the logs into `.0001`, `.0002`, … on **every** run, so a later run never overwrites the evidence from an earlier one. |
| `artifacts/SchemaQuench - Quench *.sql` | The **resolved-SQL artifacts** — one per mechanical phase, written whether the run succeeds or fails. Open `Quench Missing Tables And Columns …` and note it is **copy-runnable**: it declares the table JSON and executes it, so you can paste it into your client and reproduce exactly what the phase did. Module 1 uses this to reproduce a *failure*. |
| `checkpoints/` | **Empty.** This is the punchline. A checkpoint is written only so an interrupted run can resume — and it is **deleted the moment a run succeeds**. No checkpoint means the last run finished clean. When Module 1's deploy fails, a checkpoint will survive here — its very presence is your first signal that something stopped. |

Re-run the quench and it is a clean no-op: SchemaSmith compares the live database to the declared
model, finds no delta, and generates nothing. That is the convergence engine — it converges to the
declared state and stops.

## Starting over: `--reset`

Want `diag_baseline` back to empty — say, after experimenting with the schema past Step 2, or just
for a clean slate — reset it:

```bash
bash setup-databases.sh --reset
```

```powershell
.\setup-databases.ps1 -Reset
```

The database is dropped and recreated empty, reported as `PASS (reset)`. **Only a database this
script created is ever dropped.** On your own server, a database named `diag_baseline` that the
labs didn't create is refused and left untouched — you'll be told to rename or move it. Nothing of
yours is at risk.

Re-run Step 2 afterwards to re-forge the `Shop` baseline.

## What each engine folder contains

| Path | Purpose |
| --- | --- |
| `<engine>/Package/` | The `Shop` product: `Product.json`, `Templates/Main/` (the four tables), and generated editor `.json-schemas/`. |
| `<engine>/quench.settings.json` | Connection to the sandbox engine, `Target.Databases: ["diag_baseline"]`, and lab-local `ArtifactPath` / `CheckpointDirectory`. |

Next: **Module 1 — Reading the black box**, where a deploy fails on purpose and you follow the trail
from `FAILED to quench` to the exact failing batch, and name the phase.
