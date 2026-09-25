# Course 8 · Module 1 — Reading the black box

One induced failure on one database (`diag_blackbox`), read end to end. Module 0 showed you the trail a *healthy* deploy leaves. This is the first deploy that stops on purpose — a unique index that won't take on dirty data — and the walk from `exit 2` to a named phase to green.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0** or later on your PATH. New to the CLI? [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the sandbox database

**macOS / Linux:** `cd Demos/Learn/course8-module-01 && bash setup-databases.sh`
**Windows:** `cd Demos\Learn\course8-module-01 ; .\setup-databases.ps1`

Prints `PASS` per engine once `diag_blackbox` exists. Re-running is safe (guarded `CREATE`).

## Step 2 — deploy the baseline (green)

```
cd sqlserver            # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0` and forges the `Shop` schema into `diag_blackbox`, then a run-once seed populates `Customer` — including two rows that **share the email** `ana.f@shop.test` (CustomerId 1 and 7). Under the baseline's **non-unique** `IX_Customer_Email` that's perfectly legal. It's also the dirty data the next step trips on.

## Step 3 — deploy the change that fails

`after/` is the baseline with one edit: `IX_Customer_Email` flipped to **unique**. Deploy it over the duplicate emails:

```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD/logs"
```

**Exit `2`.** Now read the black box.

**Fast path.** Open `logs/SchemaQuench - Failures.log` first. One block per failure: the error, a `Debug SQL:` pointer to the artifact, and a `Context (last 25 lines)` phase trail — everything you need without scrolling the full run narrative. The SQL Server block from this lab:

```text
1 failure(s): 1 Template:Main

─── FAILED  [Template:Main]  [localhost,11433].[diag_blackbox] ───
Error: The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Customer' and the index name 'IX_Customer_Email'. The duplicate key value is (ana.f@shop.test).
Debug SQL: ./artifacts\SchemaQuench - Quench Indexes localhost,11433.diag_blackbox.sql
Context (last 25 lines):   [trail abbreviated here — the full 25-line context is shown in the lesson]
    … Quenching indexes and constraints → Add Missing Indexes → Creating index [dbo].[Customer].[IX_Customer_Email] …
```

**Full path.** Open `logs/SchemaQuench - Progress.log` and find the `FAILED to quench:` block — the error is right there, and it names the phase:

| Engine | `FAILED to quench:` block |
| --- | --- |
| **SQL Server** | `The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Customer' and the index name 'IX_Customer_Email'. The duplicate key value is (ana.f@shop.test).` (error `1505`) |
| **PostgreSQL** | `23505: could not create unique index "ix_customer_email"` |
| **MySQL** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (error `1062`) |
| **MariaDB** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (error `1062`) |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The line just above it points at the artifact: `Resolved SQL written to: ./artifacts/SchemaQuench - Quench Indexes ….sql`. That file is **copy-runnable** — a single proc call:

```sql
EXEC [diag_blackbox].SchemaSmith.MissingIndexesAndConstraintsQuench @ProductName = 'Shop', @WhatIf = 0
```

Paste it into your client and the phase fails identically, by hand — that's your reproduction. There's no `>>> FAILING BATCH` marker here; that's a *user-script* thing (Module 4). A mechanical phase like this one puts the error in `Progress.log` and hands you the one-line proc call. (On SQL Server the error also lands in `Errors.log`; on PostgreSQL, MySQL, and MariaDB that file stays empty — `Progress.log` is the cross-engine place to read.)

**Name the phase:** `Quenching indexes and constraints`. That's what turns a wall of SQL into "the unique index couldn't be created because the data has duplicates." And notice `checkpoints/` — the failed run **left a checkpoint** behind (a green run deletes it; a failure keeps it).

## Step 4 — fix the data, redeploy

The index definition is right; the *data* isn't unique yet. Give the duplicate its own address:

```sql
-- SQL Server (PG / MySQL / MariaDB analogous)
UPDATE dbo.Customer SET Email = 'ana.f7@shop.test' WHERE CustomerId = 7;
```

Then redeploy the same change:

```
schemaquench --ConfigFile:quench.settings.after.json --LogPath:"$PWD/logs"
```

Green on all four. The unique index takes, and the leftover checkpoint is **deleted** on the way through — Module 0's punchline, confirmed again: a green run leaves no checkpoint.

## What each folder is

| Path | Purpose |
| --- | --- |
| `baseline/` | Healthy `Shop` + the run-once seed with two duplicate emails, under a non-unique `IX_Customer_Email`. Deploys green. |
| `after/` | Baseline with `IX_Customer_Email` flipped to **unique** — the dup-key incident. Recovery is a data dedupe + redeploy. |
| `quench.settings.<state>.json` | One per package, both targeting `diag_blackbox`, lab-local `artifacts`/`checkpoints`. |

That's the whole method: **locate** the failing phase from the `FAILED to quench:` block, **read** the engine's error, **recover**. Next up — when the fix isn't a one-line `UPDATE`, the recovery *toolkit* (`--ResumeQuench`, marking a script done) is **Module 5**; the full anatomy of index, constraint & FK failures is **Module 3**.
