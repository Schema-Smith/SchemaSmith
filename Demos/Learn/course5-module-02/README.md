# Course 5, Module 2 — Migrating from Liquibase (lab)

Goal: take a database that **Liquibase** built — shop schema plus `DATABASECHANGELOG` and
`DATABASECHANGELOGLOCK` — and move it to SchemaSmith with **extract-and-go**. You'll cast the live
database to declarative files, leave both Liquibase ledgers behind, and quench to a clean no-op that
proves the cast is faithful. All four engines.

You do **not** run Liquibase. The `before/` folder shows a real Liquibase project for reference; the
setup already applied its end state to `shop_from_liquibase` on each engine.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The Course 5 databases exist — run [`../course5-setup`](../course5-setup) once (creates and seeds
  `shop_from_liquibase`, among others).
- The CLI is on your PATH (`schematongs --version` and `schemaquench --version` answer **2.7.0 or later**). New to the
  CLI? Course 1, Module 1 walks the install.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships a `SchemaTongs.settings.json` (the
extract config), a `quench.settings.json` (the deploy config), and the `Package/` this lab produced —
so you can diff your own extract against it.

## Step 1: Look at what Liquibase left behind

```bash
# SQL Server
../lab-sql.sh sqlserver shop_from_liquibase "SELECT name FROM sys.tables ORDER BY name"
# → Customer, DATABASECHANGELOG, DATABASECHANGELOGLOCK, OrderItem, Product, SalesOrder
```

Four shop tables and two bookkeeping tables — one ledger of every changeset, one lock to guard the run.
Both are what you're walking away from.

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

Four tables. No `DATABASECHANGELOG.json`, no `DATABASECHANGELOGLOCK.json` — you named the tables you
wanted, and neither ledger was on the list. One whitelist excludes both. You didn't delete them; you
just didn't invite them.

## Step 3: Quench — adopt, then prove the no-op

```bash
schemaquench --ConfigFile:quench.settings.json
```

The first run adopts your existing tables (stamps them as managed, stands up SchemaSmith's own
bookkeeping in a separate `SchemaSmith` schema). Run it a second time and nothing happens — a clean
no-op. That no-op is the proof: the package is a faithful cast of the live database.

```bash
# confirm both Liquibase tables are still there, untouched — drop them whenever you like
cd ..            # back to the lab folder
../lab-sql.sh sqlserver shop_from_liquibase "SELECT name FROM sys.tables WHERE name LIKE 'DATABASECHANGE%'"
# → DATABASECHANGELOG, DATABASECHANGELOGLOCK  (left exactly where they were)
```

## Step 4: Do it on PostgreSQL, MySQL, and MariaDB

Same three steps in `postgres/`, `mysql/`, and `mariadb/`. The `before/` Liquibase changelog is
engine-agnostic XML — the same changelog drives all four — and the sandbox databases were seeded to
the identical end state, so the extract works the same on each. Only the whitelist's dialect differs:

| | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| `ObjectList` | `dbo.Customer,…` | `public.customer,…` | `Customer,…` | `Customer,…` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

Each one extracts four tables, leaves both ledgers behind, and quenches to a clean no-op.

## The principle

Liquibase's model is two bookkeeping tables — a changelog that remembers every changeset and a lock
that guards the run. SchemaSmith keeps neither, because it doesn't replay your changesets; it reads your
current shape and converges to it. So you don't port a changelog. You extract the state it already
produced, leave both ledgers cold, and manage forward from one declared source.
