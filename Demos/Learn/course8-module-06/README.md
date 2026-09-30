# Course 8 · Module 6 — Per-engine dialects & your runbook (finale)

The **finale** of Course 8. You've read SQL Server logs all course. Now the same failure lands on
PostgreSQL, MySQL, and MariaDB — and the error isn't where you expected, because **each database hands
it back through a different door**. This module shows all three doors (MariaDB rides MySQL's), then has
you assemble everything into a team diagnostic runbook.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0 or later** on your PATH.

## Step 1 — create the sandbox database

**macOS / Linux:** `cd Demos/Learn/course8-module-06 && bash setup-databases.sh`
**Windows:** `cd Demos\Learn\course8-module-06 ; .\setup-databases.ps1`

Prints `PASS` per engine once `diag_dialects` exists.

## Step 2 — deploy the baseline (green)

```
cd sqlserver            # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0`, forges `Shop`, and seeds customers — two of them (`CustomerId` 1 & 7) share the email
`ana.f@shop.test`. Harmless under the non-unique `IX_Customer_Email`. That's the setup for the one
failure we'll read three ways.

## Demo 1 — the same failure, three doors

`channels-broken/` flips `IX_Customer_Email` to **unique**. Deploying over the duplicates fails at the
index phase — the *same* failure on every engine, surfaced differently:

```
schemaquench --ConfigFile:quench.settings.channels-broken.json --LogPath:"$PWD/logs"
```

Read the `Progress.log` **and** the `Errors.log` on each engine:

| Engine | `Progress.log` | `Errors.log` |
| --- | --- | --- |
| **SQL Server** | `The CREATE UNIQUE INDEX statement terminated… duplicate key… (ana.f@shop.test)` | **populated** — same message + `at Line: 2` |
| **PostgreSQL** | `23505: could not create unique index "ix_customer_email"` | **empty** |
| **MySQL** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (`1062`) | **empty** |
| **MariaDB** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (`1062`) | **empty** |

That's the headline: **only SQL Server populates `Errors.log`** (with the fault + line number). On
PostgreSQL, MySQL, and MariaDB the detail is in the `Progress.log`, and `Errors.log` is empty. *Where
you look for the fault is engine-specific.* Three doors:

- **SQL Server** — an `InfoMessage` event stream. Severity > 10 → both logs (with the line number).
- **PostgreSQL** — a `Notice` stream. The SQLSTATE (`23505`) is printed literally; `Errors.log` stays empty.
- **MySQL and MariaDB** — no async event at all. Progress rides a **`SchemaSmith_StatusMessages`**
  sidecar table that a monitor polls every ~200 ms, because neither engine can push a notice on the busy
  line over a single connection. Confirm it's there: `DESCRIBE diag_dialects.SchemaSmith_StatusMessages`.

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

## Demo 2 — turning up the volume with `VerboseLogging`

SQL Server user scripts love to talk — `PRINT` statements, low-severity warnings. SchemaSmith
**suppresses that noise by default** and gives you one dial to bring it back: `VerboseLogging`
(top-level config, default `false`, **SQL-Server-only**). The `verbose/` package adds a script that
`PRINT`s a line every deploy. Deploy it twice:

```
schemaquench --ConfigFile:quench.settings.verbose.json --LogPath:"$PWD/logs-plain"
schemaquench --ConfigFile:quench.settings.verbose.json --LogPath:"$PWD/logs-verbose" --VerboseLogging=true
```

- **SQL Server** — the `PRINT` line is **absent** from the plain log and **present** in the verbose one.
  That's the dial: your script's output, suppressed by default, surfaced on demand.
- **PostgreSQL** — a `RAISE NOTICE` shows in **both** logs. `VerboseLogging` has no effect here; PostgreSQL surfaces its notices by default.
- **MySQL and MariaDB** — the report line appears in **neither**. User scripts on these engines have no progress channel at all — the `SchemaSmith_StatusMessages` sidecar is the *engine's* channel, not yours.

> **Note:** Use the `=` form — `--VerboseLogging=true` — to set it on the command line, or put `"VerboseLogging": true` in your settings file. The colon form (`--VerboseLogging:true`) is silently ignored for config settings; the `=` form is the config-override syntax.

`VerboseLogging` doesn't touch SchemaSmith's own progress — the tool always shows you its phases — it's
purely a valve for the low-severity chatter your SQL Server scripts produce.

## The capstone — your runbook

Open [`runbook.md`](runbook.md). It's a fill-in team diagnostic runbook that pulls together everything
from Course 8: the phase map, the core loop, the "whose problem is it" per-item lines, the per-engine
evidence table, the per-platform error codes, and the recovery decision tree — plus a blank incident
log to grow over time. Copy it into your own repo. That's the deliverable: a one-page answer to
"the deploy failed — now what?" on any of the four engines.

## Course 8 complete

You can now locate any failure's phase, read its artifact, name whose problem it is, and recover it —
on SQL Server, PostgreSQL, MySQL, or MariaDB. For the lookup tables, see the SchemaSmith end-user
reference (error codes & reporting channels). That's the course.
