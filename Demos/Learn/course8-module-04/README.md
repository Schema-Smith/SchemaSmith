# Course 8 · Module 4 — Script-slot & data-delivery failures

Two induced incidents on one database (`diag_scripts`), read end to end. Modules 1–3 diagnosed the
**mechanical** engine — the phases SchemaSmith computes for you (missing tables, modified tables,
indexes, foreign keys). This module diagnoses the other half: the scripts and the data **you** put in
the package. Same method — locate the phase, read the error, recover — but a different evidence shape.

> **Engine floor:** on your own server this lab needs **SQL Server 2016+** or **MySQL 8.0+** — its induced data-delivery failure needs automatic data delivery, which needs `OPENJSON` / `JSON_TABLE`. PostgreSQL and MariaDB run it at any supported version. The Docker sandbox is already above the floor.

## The triage split

When a deploy fails, the first question is *whose problem is it — the engine's computed DDL, or my
input?* One line of `SchemaQuench - Progress.log` tells you:

| The failure is… | It logs… | Its artifact is… |
| --- | --- | --- |
| a **mechanical phase** (M1–M3) | the engine error directly in the `FAILED to quench:` block | a copy-runnable `EXEC`/`CALL <proc>` — **no** batch marker |
| a **user script** (this module) | `Unable to quench '<path>': <error>` | `Failed <script> ….sql` with a `>>> FAILING BATCH (#N)` marker |
| a **data delivery** (this module) | `Error delivering <table>: <error>` | `Failed DataDelivery <table>#0 ….sql` — a copy-runnable MERGE, also marked |

(User-input failures also print a generic `FAILED to quench: / Unable to quench all scripts` at the
end — so `FAILED to quench:` alone doesn't mean "mechanical." The **per-item line** above is the tell.)

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0 or later** on your PATH. New to the CLI? [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the sandbox database

**macOS / Linux:** `cd Demos/Learn/course8-module-04 && bash setup-databases.sh`
**Windows:** `cd Demos\Learn\course8-module-04 ; .\setup-databases.ps1`

Prints `PASS` per engine once `diag_scripts` exists. Re-running is safe (guarded `CREATE`).

## Step 2 — deploy the baseline (green)

```
cd sqlserver            # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0` and forges the `Shop` schema into `diag_scripts`, then a run-once seed adds three
customers (`1,2,3`) with unique emails. `SalesOrder` is deliberately left **empty** — beat 2 delivers
into it. `SalesOrder` already carries `FK_SalesOrder_Customer`, so an orphan will have something to
trip on.

## Beat 1 — a backfill script that forgot a required column (`515` / `23502` / `1048`)

`beat1-broken/` adds a run-once After-script, `02_backfill_customers.sql`, that onboards customers
migrated from a legacy CRM in two batches: an idempotent name-tidy, then one `INSERT` of the migrated
rows. The last row is missing its email — and `Email` is `NOT NULL`:

```
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

Fails at the **After** slot (`Quenching after database scripts`), exit `2`. In `Progress.log` you get
the per-script line — **`Unable to quench '.\beat1-broken\…\02_backfill_customers.sql': <error>`**:

| Engine | Error |
| --- | --- |
| **SQL Server** | `Cannot insert the value NULL into column 'Email', table 'diag_scripts.dbo.Customer'; column does not allow nulls. INSERT fails.` (error `515`) |
| **PostgreSQL** | `23502: null value in column "email" of relation "customer" violates not-null constraint` |
| **MySQL** | `Column 'Email' cannot be null` (error `1048`) |
| **MariaDB** | `Column 'Email' cannot be null` (error `1048`) |

Open the artifact it names — `artifacts/SchemaQuench - Failed 02_backfill_customers ….sql`. Its header
repeats the error, and a **`-- >>> FAILING BATCH (#N) >>>`** marker sits on the exact statement that
blew up:

- On **SQL Server** and **PostgreSQL** the script splits into two batches (SS on `GO`, PG on `;`), so
  the marker reads **`(#2)`** — the `INSERT`, not the tidy-up `UPDATE` before it.
- On **MySQL** and **MariaDB** the whole script runs as **one** batch, so the marker reads **`(#1)`**.
  (Neither engine's batch splitter breaks a plain `;`-separated script apart — a per-engine difference
  worth knowing.)

That is a *user-supplied* failure: the engine ran your script verbatim and your data broke a rule.

**Recover — fix the source, redeploy.** Give row 13 a real email in `02_backfill_customers.sql`:

```
  (13, 'gil.o@shop.test',   'Gil Overton');
```

Redeploy the same config. A failed script is **never** marked complete, so a plain redeploy retries
it — no `--ResumeQuench` needed (that's Module 5). Exits `0`; `Customer` now holds all seven rows.
(The `INSERT` is a single atomic statement, so the first, failed run committed *nothing* — which is
exactly why the retry doesn't collide.)

## Beat 2 — delivering a child row whose parent doesn't exist (`547` / `23503` / `1452`)

`beat2-broken/` adds a `DataDelivery` block to `SalesOrder` — a MERGE-based load from
`data/….SalesOrder.tabledata`. Three orders; one (`OrderId 103`) references `CustomerId 999`, who
was never seeded — an **orphan**:

```
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

Fails at the **TableData** slot, exit `2`. `DataDelivery` orders parents before children and retries
to resolve dependencies — you'll see `Delivering SalesOrder` attempted, then the per-item line
**`Error delivering SalesOrder: <error>`**:

| Engine | Error |
| --- | --- |
| **SQL Server** | `The MERGE statement conflicted with the FOREIGN KEY constraint "FK_SalesOrder_Customer". The conflict occurred in database "diag_scripts", table "dbo.Customer", column 'CustomerId'.` (error `547`) |
| **PostgreSQL** | `23503: insert or update on table "salesorder" violates foreign key constraint "fk_salesorder_customer"` |
| **MySQL** | `Cannot add or update a child row: a foreign key constraint fails (…CONSTRAINT \`FK_SalesOrder_Customer\`…)` (error `1452`) |
| **MariaDB** | `Cannot add or update a child row: a foreign key constraint fails (…CONSTRAINT \`FK_SalesOrder_Customer\`…)` (error `1452`) |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The artifact — `artifacts/SchemaQuench - Failed DataDelivery SalesOrder#0 ….sql` — is the **generated
MERGE**, copy-runnable (on SQL Server, `OPENJSON` shredding your `.tabledata` into a `MERGE`). Paste it
into your client and it fails identically. No ordering or retry can save a row whose parent genuinely
isn't there — the fix is in the data you shipped.

**Recover — fix the content, redeploy.** Reparent the orphan in the `.tabledata` file (`999` → a real
customer, e.g. `1`), then redeploy. `DataDelivery` re-runs every deploy (idempotent MERGE), so the
corrected content lands clean — exit `0`, all three orders delivered.

## Where this leaves you

You can now read all three failure shapes — mechanical phase, user script, data delivery — and name
which one you're looking at from a single log line. Two things this module deliberately left simple:

- **The recovery toolkit** — `--ResumeQuench` and marking a script done in `CompletedMigrationScripts`
  — is **Module 5**. Here a plain redeploy sufficed.
- **How the *same* failure reaches you differently per engine** (the error channels, the batch-splitter
  differences you already glimpsed) is **Module 6**.
