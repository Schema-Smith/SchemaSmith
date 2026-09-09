# Course 8 · Module 2 — Structure-change failures

Two induced incidents on one database (`diag_structure`), read end to end. This is the **structure
half** of the mechanical engine: how column *adds* (`MissingTableAndColumnQuench`) and column
*alters* (`ModifiedTableQuench`) fail, and how to read them. Same method as Module 1 — locate the
phase, read the error, recover.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers on your PATH. New to the CLI? [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the sandbox database

**macOS / Linux:** `cd Demos/Learn/course8-module-02 && bash setup-databases.sh`
**Windows:** `cd Demos\Learn\course8-module-02 ; .\setup-databases.ps1`

Prints `PASS` per engine once `diag_structure` exists. Re-running is safe (guarded `CREATE`).

## Step 2 — deploy the baseline (green)

```
cd sqlserver            # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0` and forges the `Shop` schema into `diag_structure`, then a run-once seed populates
`Customer` with four rows — one of them `FullName = 'Ana Fielding-Reyes'` (18 chars). A **populated**
table is the precondition both incidents need.

## Beat 1 — adding a required column over existing data (`4901` / `23502`)

Deploy the change that adds a `NOT NULL` column with no default:

```
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

**Read the trail — and notice the engines disagree.** Same package, three outcomes:

| Engine | Result |
| --- | --- |
| **SQL Server** | **Fails, exit `2`** at `Quenching missing tables and columns`: *"ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT … Column 'LoyaltyTier' cannot be added to non-empty table 'Customer'…"* (error `4901`). |
| **PostgreSQL** | **Fails, exit `2`** at the same phase: `23502: column "loyaltytier" of relation "customer" contains null values`. |
| **MySQL** | **Exits `0`.** No failure — MySQL adds the column and **silently backfills `''`** into every existing row. Even in `STRICT_TRANS_TABLES` mode. |
| **MariaDB** | **Exits `0`.** Identical to MySQL — it adds the column and **silently backfills `''`** into every existing row, even in `STRICT_TRANS_TABLES` mode. |

That MySQL/MariaDB row is the lesson: **the engine that fails loud is protecting you.** SQL Server and
PostgreSQL refuse a required column with no value for the rows already there. MySQL and MariaDB just fill
blanks — you get a `LoyaltyTier` column full of empty strings and no warning. (This is engine
behavior, not SchemaSmith — SchemaSmith issues the same correct `ALTER` everywhere; the two MySQL-family
engines choose to fill rather than refuse.)

**The fix** is what SQL Server / PostgreSQL were asking for: give the new column a `Default`, so the
rows already in the table get a real value. `beat1-fixed/` does exactly that (`Default 'Standard'`):

```
schemaquench --ConfigFile:quench.settings.beat1-fixed.json --LogPath:"$PWD/logs"
```

Green on all four. On SQL Server / PostgreSQL the existing rows now read `Standard`. **On MySQL and
MariaDB they stay `''`** — the column was already added back in `beat1-broken`, and a default only
applies to *new* rows. If MySQL and MariaDB had failed loud like the others, you'd have caught it
before the blanks landed.

## Beat 2 — narrowing a column that still holds long data (`8152` / `22001` / `1406`)

Now a column *alter*. `beat2-broken/` narrows `FullName` from `NVARCHAR(200)` to `NVARCHAR(10)`, but
`'Ana Fielding-Reyes'` is 18 characters:

```
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

This one fails the **same way on all four** — exit `2` at `Quenching modified tables`:

| Engine | Error |
| --- | --- |
| **SQL Server** | `String or binary data would be truncated in table 'diag_structure.dbo.Customer', column 'FullName'.` (error `8152`) |
| **PostgreSQL** | `22001: value too long for type character varying(10)` |
| **MySQL** | `Data too long for column 'FullName' at row 1` (error `1406`) |
| **MariaDB** | `Data too long for column 'FullName' at row 1` (error `1406`) |

**The fix is data, not schema** — the existing values are too long for the shape you asked for.
Shorten them, then redeploy the same change:

```
-- SQL Server (PG / MySQL / MariaDB analogous)
UPDATE dbo.Customer SET FullName = LEFT(FullName, 10);
```
```
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

Green on all four — the narrow now applies. (This redeploy runs on the checkpointed
`ModifiedTables` phase with the failed run's checkpoint still present; PostgreSQL re-converges
cleanly here.)

## Beat 3 — the change an in-place `ALTER` cannot make, and the state that blocks it

Beats 1 and 2 failed loudly. This one is about the phase deciding it needs a bigger hammer — and then
refusing to swing it.

`beat3-reorder/` declares the same three `Customer` columns in a **different order**: `CustomerId`,
`FullName`, `Email`, where the deployed table has `Email` second. Column order is not something an
`ALTER` can change on any of these engines. The only way to get it is to build the table fresh, copy
the rows, and swap — a **rebuild** — and that is what `RebuildPolicy` governs:

```json
{ "RebuildPolicy": { "Mode": "NEVER", "OnOrderMismatch": true } }
```

Read those two fields carefully, because they are not alternatives. `Mode` is `NEVER` (the default —
always alter in place), `ALWAYS`, or `THRESHOLD` (rebuild once the pending-change count reaches
`Threshold`, which is then required). `OnOrderMismatch` is a **separate trigger that composes with
`Mode`** rather than replacing it — deliberately not a fourth `Mode` value, so a table can ask for
"THRESHOLD: 3" *and* order-mismatch at once. Here `Mode` stays `NEVER`, and order drift alone elects
the rebuild.

Deploy the baseline, then the reorder, on any engine:

```bash
cd <engine>
schemaquench --ConfigFile:quench.settings.baseline.json
schemaquench --ConfigFile:quench.settings.beat3-reorder.json
cd ..
../lab-sql.sh sqlserver diag_structure "SELECT c.name, c.column_id FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id WHERE t.name='Customer' ORDER BY c.column_id"
# → CustomerId:1  FullName:2  Email:3       and SELECT COUNT(*) FROM Customer still returns 4
```

The log names the two phases doing it:

```text
      Detect declared-vs-deployed column order drift
      Elect tables for rebuild
```

**Try it without the trigger first.** Deploy `beat3-reorder` with `OnOrderMismatch` removed and the run
is green — exit 0 — and the column order does not move. Nothing failed; the engine simply had no
in-place way to honour the declaration and did not reach for the rebuild you never asked for. That is
the quiet case Course 8 keeps warning you about, and the reason the trigger is opt-in.

### The refusal (SQL Server)

A rebuild replaces the table with a copy, so any state the *database* holds about that table — and
that your package does not describe — would be discarded. SchemaSmith refuses instead. Enable Change
Data Capture and re-run the same reorder:

```bash
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_enable_db"
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_enable_table @source_schema='dbo', @source_name='Customer', @role_name=NULL"
cd sqlserver && schemaquench --ConfigFile:quench.settings.beat3-reorder.json ; cd ..
```

```text
Table rebuild refused for [dbo].[Customer]: Change Data Capture is enabled. A rebuild replaces the
table with a shadow copy, and that state lives outside the schema package -- the copy discards it and
no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the
blocking state first and re-run.
```

Exit **2**, and nothing was attempted. Read the second sentence again — it is the whole design in one
line: *that state lives outside the schema package, and no re-deploy can put it back.* A declarative
tool can always rebuild what it declares. It must never destroy what it does not.

The same guard fires for system versioning, replication, Change Tracking, inheritance and partitioning
— whichever of those the engine in front of it supports. And it fires under `--WhatIf` too (set
`"WhatIfONLY": true` and re-run: still exit 2), because there is no safe preview of a change that
cannot be made.

Clear the blocking state when you are done:

```bash
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_disable_table @source_schema='dbo', @source_name='Customer', @capture_instance='all'; EXEC sys.sp_cdc_disable_db"
```

> **No SQL Agent needed.** CDC's capture *jobs* need SQL Server Agent, and the lab sandbox does not run
> it — you will see "SQLServerAgent is not currently running" when you enable CDC. That is fine here:
> everything this beat teaches is metadata-level, and the guard fires on the CDC configuration, not on
> captured rows.

## What each folder is

| Path | Purpose |
| --- | --- |
| `baseline/` | Healthy `Shop` + the run-once seed that populates `Customer`. |
| `beat1-broken/` | Baseline + a `NOT NULL LoyaltyTier` column, **no default** — the `4901`/`23502` incident. |
| `beat1-fixed/` | `beat1-broken` + `Default 'Standard'` — the recovery. |
| `beat2-broken/` | `beat1-fixed` + `FullName` narrowed to 10 — the `8152`/`22001`/`1406` incident. |
| `beat3-reorder/` | Baseline with `Customer`'s columns declared in a different order + a table-level `RebuildPolicy` with `OnOrderMismatch` — the rebuild, and the CDC refusal. |
| `quench.settings.<state>.json` | One per package, all targeting `diag_structure`, lab-local `artifacts`/`checkpoints`. |

Next: **Module 3 — Index, constraint & FK failures**, where the dup-key incident from Module 1 gets
its full diagnosis. The recovery *toolkit* (`--ResumeQuench`, marking a script done) is **Module 5**.
