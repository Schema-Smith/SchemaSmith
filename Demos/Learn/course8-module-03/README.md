# Course 8 · Module 3 — Index, constraint & FK failures

Two induced incidents on one database (`diag_keys`), read end to end. This is the two phases *after*
structure: the **index/constraint** half (`MissingIndexesAndConstraintsQuench`) and the
**foreign-key** half (`ForeignKeyQuench`). Same method as Modules 1–2 — locate the phase, read the
error, recover.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0** or later on your PATH. New to the CLI? [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the sandbox database

**macOS / Linux:** `cd Demos/Learn/course8-module-03 && bash setup-databases.sh`
**Windows:** `cd Demos\Learn\course8-module-03 ; .\setup-databases.ps1`

Prints `PASS` per engine once `diag_keys` exists. Re-running is safe (guarded `CREATE`).

## Step 2 — deploy the baseline (green)

```
cd sqlserver            # or postgres, mysql, or mariadb
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0` and forges the `Shop` schema into `diag_keys`, then a run-once seed arms **both**
incidents:

- **Beat 1:** `Customer` gets four rows, two sharing the email `ana.f@shop.test` — harmless while
  `IX_Customer_Email` is non-unique.
- **Beat 2:** `SalesOrder` gets three rows, one (`OrderId 3`) pointing at `CustomerId 999`, which has
  no `Customer` — a **resident orphan**, harmless while `SalesOrder` carries no foreign key.

Both are legal today because the constraints that would catch them aren't declared yet. That is the
whole point: **a constraint you haven't declared can't protect you.** Each beat declares one.

## Beat 1 — flipping an index to unique over duplicate data (`1505` / `23505` / `1062`)

`beat1-broken/` changes `IX_Customer_Email` from non-unique to **unique**. Because a *changed* index
is dropped in the modified-tables phase and rebuilt as "missing," the build lands one phase later —
at the index phase — where it meets the duplicate emails:

```
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

Fails **the same way on all four** — exit `2` at `Quenching indexes and constraints`:

| Engine | Error (in `SchemaQuench - Progress.log`, the `FAILED to quench:` block) |
| --- | --- |
| **SQL Server** | `The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name 'dbo.Customer' and the index name 'IX_Customer_Email'. The duplicate key value is (ana.f@shop.test).` (error `1505`) |
| **PostgreSQL** | `23505: could not create unique index "ix_customer_email"` |
| **MySQL** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (error `1062`) |
| **MariaDB** | `Duplicate entry 'ana.f@shop.test' for key 'Customer.IX_Customer_Email'` (error `1062`) |

The phase left a **copy-runnable** artifact — `artifacts/SchemaQuench - Quench Indexes ….sql` — a one
-line `EXEC`/`CALL …MissingIndexesAndConstraintsQuench`. Paste it into your client and the phase fails
identically: that is your reproduction.

**The fix is data** — the two rows genuinely collide on a column you're about to make unique. Dedupe,
then redeploy the same flip:

```
-- SQL Server (PG / MySQL / MariaDB analogous)
UPDATE dbo.Customer SET Email = 'ana.f7@shop.test' WHERE CustomerId = 7;
```
```
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

Green on all four — the unique index now builds, PostgreSQL included.

## Beat 2 — adding a foreign key over an orphan row (`547` / `23503` / `1452`)

`beat2-broken/` adds `FK_SalesOrder_Customer`. Foreign keys are validated **after** data delivery — by
the time the FK is created, the rows are already resident — so the orphan (`CustomerId 999`) surfaces
here, at the last structural phase, not earlier:

```
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

Fails **the same way on all four** — exit `2` at `Quenching foreign keys`:

| Engine | Error |
| --- | --- |
| **SQL Server** | `The ALTER TABLE statement conflicted with the FOREIGN KEY constraint "FK_SalesOrder_Customer". The conflict occurred in database "diag_keys", table "dbo.Customer", column 'CustomerId'.` (error `547`) |
| **PostgreSQL** | `23503: insert or update on table "salesorder" violates foreign key constraint "fk_salesorder_customer"` |
| **MySQL** | `Cannot add or update a child row: a foreign key constraint fails (… CONSTRAINT \`FK_SalesOrder_Customer\` FOREIGN KEY (\`CustomerId\`) REFERENCES \`Customer\` (\`CustomerId\`))` (error `1452`) |
| **MariaDB** | `Cannot add or update a child row: a foreign key constraint fails (… CONSTRAINT \`FK_SalesOrder_Customer\` FOREIGN KEY (\`CustomerId\`) REFERENCES \`Customer\` (\`CustomerId\`))` (error `1452`) |

Same shape as Beat 1: a copy-runnable `artifacts/SchemaQuench - Quench Foreign Keys ….sql` (an
`EXEC`/`CALL …ForeignKeyQuench`) reproduces it by hand.

**The fix is data** — the FK caught a real gap. Reparent the orphan to a customer that exists (or
delete it), then redeploy the same FK add:

```
-- SQL Server (PG / MySQL / MariaDB analogous)
UPDATE dbo.SalesOrder SET CustomerId = 1 WHERE CustomerId = 999;
```
```
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

Green on all four — the foreign key is created and trusted.

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

## What each folder is

| Path | Purpose |
| --- | --- |
| `baseline/` | Healthy `Shop` (no `SalesOrder` FK) + the run-once seed that arms both beats (duplicate emails; a resident orphan order). |
| `beat1-broken/` | Baseline + `IX_Customer_Email` flipped to **unique** — the `1505`/`23505`/`1062` dup-key incident. Recovery is a data dedupe + redeploy. |
| `beat2-broken/` | `beat1-broken` + `FK_SalesOrder_Customer` added — the `547`/`23503`/`1452` orphan incident. Recovery is a data reparent + redeploy. |
| `quench.settings.<state>.json` | One per package, all targeting `diag_keys`, lab-local `artifacts`/`checkpoints`. |

Each beat recovers by fixing the **data** and redeploying the *same* package — the schema you declared
was right; the data hadn't caught up. The recovery *toolkit* (`--ResumeQuench`, marking a script done)
is **Module 5**; user-script and data-delivery failures are **Module 4**.
