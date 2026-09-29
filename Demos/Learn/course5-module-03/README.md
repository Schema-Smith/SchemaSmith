# Course 5, Module 3 — Migrating from EF Core migrations (lab)

Goal: take a database that **EF Core migrations** built — shop schema plus `__EFMigrationsHistory` —
and move it to SchemaSmith with **extract-and-go**. You'll cast the live database to declarative files,
leave EF's history table behind, and quench to a clean no-op that proves the cast is faithful. All
four engines.

You do **not** run EF. The `before/` folder shows a real EF Core project for reference; the setup
already applied its model's end state to `shop_from_efcore` on each engine.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The Course 5 databases exist — run [`../course5-setup`](../course5-setup) once (creates and seeds
  `shop_from_efcore`, among others).
- The CLI is on your PATH (`schematongs --version` and `schemaquench --version` answer **2.7.0 or later**). New to the
  CLI? Course 1, Module 1 walks the install.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships a `SchemaTongs.settings.json` (the
extract config), a `quench.settings.json` (the deploy config), and the `Package/` this lab produced —
so you can diff your own extract against it.

## Step 1: Look at what EF left behind

```bash
# SQL Server
../lab-sql.sh sqlserver shop_from_efcore "SELECT name FROM sys.tables ORDER BY name"
# → Customer, OrderItem, Product, SalesOrder, __EFMigrationsHistory
```

Four shop tables and EF's migrations-history table. That history table is the thing you're walking
away from. (The migration classes and the `ModelSnapshot` live in the C# project — `before/` — not in
the database.)

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

Four tables. No `__EFMigrationsHistory.json` — you named the tables you wanted, and the history table
wasn't on the list. You didn't delete it; you just didn't invite it.

## Step 3: Quench — adopt, then prove the no-op

```bash
schemaquench --ConfigFile:quench.settings.json
```

The first run adopts your existing tables (stamps them as managed, stands up SchemaSmith's own
bookkeeping in a separate `SchemaSmith` schema). Run it a second time and nothing happens — a clean
no-op. That no-op is the proof: the package is a faithful cast of the live database.

```bash
# confirm __EFMigrationsHistory is still there, untouched — drop it whenever you like
cd ..            # back to the lab folder
../lab-sql.sh sqlserver shop_from_efcore "SELECT name FROM sys.tables WHERE name='__EFMigrationsHistory'"
# → __EFMigrationsHistory  (left exactly where it was)
```

## Step 4: Do it on PostgreSQL, MySQL, and MariaDB

Same three steps in `postgres/`, `mysql/`, and `mariadb/`. The `before/` EF project is shown for the SQL
Server provider; the same `ShopContext` on Npgsql (PostgreSQL) or Pomelo (MySQL/MariaDB) produces the
identical four tables, and the sandbox databases were seeded to that end state. Only the whitelist's
dialect differs:

| | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| `ObjectList` | `dbo.Customer,…` | `public.customer,…` | `Customer,…` | `Customer,…` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

Each one extracts four tables, leaves `__EFMigrationsHistory` behind, and quenches to a clean no-op.

## The principle

EF's model is three parts in lockstep — the migration classes, the snapshot that caches the current
model, and the history table that records what ran. SchemaSmith keeps one: the table files. It doesn't
replay your `Up()` methods; it reads your current shape and converges to it. So you don't port a
migrations folder. You extract the state it already produced, leave the history cold, and manage forward
from one declared source.
