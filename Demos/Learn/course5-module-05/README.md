# Course 5, Module 5 — Migrating from hand-rolled scripts (lab)

Goal: take a database that a **home-grown script pipeline** built — shop schema plus a hand-maintained
`schema_version` table — and move it to SchemaSmith with **extract-and-go**. You'll cast the live
database to declarative files, leave the numbered-script pile and its tracker behind, and quench to a
clean no-op that proves the cast is faithful. All four engines.

You do **not** run the scripts. The `before/` folder shows a real hand-rolled pipeline for reference —
numbered SQL files with inconsistent idempotency guards and a manual `schema_version` insert in each;
the setup already applied their end state to `shop_from_scripts` on each engine.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The Course 5 databases exist — run [`../course5-setup`](../course5-setup) once (creates and seeds
  `shop_from_scripts`, among others).
- The CLI is on your PATH (`schematongs --version` and `schemaquench --version` answer **2.7.0 or later**). New to the
  CLI? Course 1, Module 1 walks the install.

The `sqlserver/`, `postgres/`, `mysql/`, and `mariadb/` folders each ship a `SchemaTongs.settings.json` (the extract
config), a `quench.settings.json` (the deploy config), and the `Package/` this lab produced — so you can
diff your own extract against it.

## Step 1: Look at the pile

```bash
ls before/
# → 000_init.sql  001_create_customer.sql  002_create_product.sql  003_orders.sql  004_add_status.sql
```

Open a few of them. `000_init.sql` stands up the home-grown `schema_version` tracker. Then notice the
drift: `001_create_customer.sql` has no existence guard, but `002_create_product.sql` wraps its
`CREATE TABLE` in `IF OBJECT_ID(...) IS NULL` — and `003_orders.sql` guards `SalesOrder` but not
`OrderItem` in the *same file*. Each script hand-writes an `INSERT INTO schema_version`, and `004` shows
the classic add-a-column-then-drop-its-default two-step. Guarded here, bare there, tracked when someone
remembered — that inconsistency is exactly what you're leaving behind.

## Step 2: Extract — name the tables you keep

Open `sqlserver/SchemaTongs.settings.json`. The whitelist is the whole trick:

```json
"ShouldCast": { "ObjectList": "dbo.Customer,dbo.Product,dbo.SalesOrder,dbo.OrderItem" }
```

```bash
cd sqlserver
schematongs --ConfigFile:SchemaTongs.settings.json
ls Package/Templates/Main/Tables/
```

```
=== Casting Summary ===
  Tables:     4 extracted, 0 errors

dbo.Customer.json  dbo.OrderItem.json  dbo.Product.json  dbo.SalesOrder.json
```

Four tables. No `schema_version.json` — you named the tables you wanted, and the home-grown tracker
wasn't on the list. The whole pile of numbered scripts is now irrelevant; you captured the result, not
the steps.

## Step 3: Quench — adopt, then prove the no-op

```bash
schemaquench --ConfigFile:quench.settings.json
```

The first run adopts your existing tables (stamps them as managed, stands up SchemaSmith's own
bookkeeping in a separate `SchemaSmith` schema). Run it a second time and nothing happens — a clean
no-op. Idempotency is now the engine's job, not a guard you hand-write into each script.

```bash
# confirm schema_version is still there, untouched — drop it whenever you like
cd ..            # back to the lab folder
../lab-sql.sh sqlserver shop_from_scripts "SELECT name FROM sys.tables WHERE name='schema_version'"
# → schema_version  (left exactly where it was)
```

## Step 4: Do it on PostgreSQL, MySQL, and MariaDB

Same three steps in `postgres/`, `mysql/`, and `mariadb/`. The `before/` scripts are shown in their SQL
Server form; the PostgreSQL, MySQL, and MariaDB sandbox databases were seeded to the identical end
state, so the extract works the same on each. Only the whitelist's dialect differs:

| | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| `ObjectList` | `dbo.Customer,…` | `public.customer,…` | `Customer,…` | `Customer,…` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

Each one extracts four tables, leaves `schema_version` behind, and quenches to a clean no-op.

## The principle

A hand-rolled pipeline makes *you* responsible for everything — the order, the guards, the version
table, remembering to add the row. SchemaSmith makes the engine responsible: it reads your current shape
and converges to it, every run, with no guards to write and no ledger to maintain. You don't port the
pile. You extract the state it produced, leave `schema_version` cold, and manage forward from one
declared source. True data fixes — the scripts that aren't structure — become tracked, run-once
migrations (Course 2), so even those stop being something you hand-guard.
