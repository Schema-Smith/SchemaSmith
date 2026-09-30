# Course 2, Module 1 — Product boundaries (lab)

Goal: split a deployment into **products** along server-*role* lines, deploy them into the **same**
database, and prove the shared parts are never duplicated. Along the way you'll wire up the real
deployment guardrail — `ValidationScript` — that refuses to run against the wrong target.

A **product** is one `Product.json` package deployed by its own SchemaQuench run. Multiple products
means multiple packages. This module ships two:

- `common/` — Product **`CommonLookups`**: the shared parts every server gets. One template (`Main`),
  one lookup table (`Currency`). In real systems this is your shared reference data, audit
  scaffolding, the things that have to exist everywhere.
- `appserver/` — Product **`OltpApp`**: the role-specific parts only the application servers get. One
  template (`Main`), one role table (`SalesOrder`). A different server role (reporting, a DBA-admin
  product, an ETL box) would be its own product with its own package.

Both deploy into the same `learn` database. The teaching point: decompose by **role**, deploy the
common product everywhere, never copy the shared table into each role's package, and keep
DBA-adjacent concerns in their own product. The three-product shape you're modeling here is
**common + per-server-type + an adjacent DBA product** — this lab builds the first two; the third is
the same move again.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) has both products, each with its own
`Package/` and `deploy.settings.json`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (`./verify-sandbox.sh` /
  `.\verify-sandbox.ps1` — all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Look at the two products

Pick an engine and open both product folders:

```
common/
  Product.json                       # Name: CommonLookups, ValidationScript guardrail
  Templates/Main/Template.json       # targets the `learn` database
  Templates/Main/Tables/...Currency  # the shared lookup table
  deploy.settings.json               # SchemaPackagePath: ./Package
appserver/
  Product.json                       # Name: OltpApp, same guardrail
  Templates/Main/Template.json       # also targets `learn`
  Templates/Main/Tables/...SalesOrder
  deploy.settings.json
```

Each `Product.json` carries a **`ValidationScript`** — the deployment guardrail. It runs once, up
front, against the server you're connecting to and must return a truthy value or the whole quench
aborts before touching anything. Here it confirms the `learn` database exists on the target server:

| Engine     | `ValidationScript` (returns 1 when `learn` exists) |
| ---------- | -------------------------------------------------- |
| SQL Server | `SELECT CASE WHEN DB_ID('learn') IS NOT NULL THEN 1 ELSE 0 END` |
| PostgreSQL | `SELECT CASE WHEN EXISTS (SELECT 1 FROM pg_database WHERE datname = 'learn') THEN 1 ELSE 0 END` |
| MySQL      | `SELECT CASE WHEN EXISTS (SELECT 1 FROM information_schema.schemata WHERE schema_name = 'learn') THEN 1 ELSE 0 END` |

These are server-level catalog checks on purpose: the validation runs against the engine's
admin/init database (`master`, `postgres`, `information_schema`) before the template picks the
target database, so it asks "is the right database *present on this server?*" — not "am I *in* it?".

> `ValidationScript` and `MinimumVersion` are complementary guardrails. `MinimumVersion` in `Product.json`
> is an **enforced pre-flight version floor**: SchemaQuench detects every target's engine version up front
> and aborts the whole run — no partial deploy — if any target is below the declared floor. `ValidationScript`
> is the complementary runtime/state gate for what a version number can't express — right server, right
> environment, right baseline state — alongside its sibling `BaselineValidationScript`.

## Step 2: Deploy the common product

```bash
cd <engine>/common
schemaquench --ConfigFile:deploy.settings.json
```

The guardrail runs (`Validate Server`), then the table is created. SQL Server shown; the table-create
wording varies per engine:

```
Begin Quench of CommonLookups
Validate Server
[localhost,11433].[learn]         Adding new table [dbo].[Currency]
[localhost,11433].[learn]         Creating constraint [dbo].[Currency].[PK_Currency]
[localhost,11433].[learn] Successfully Quenched
Completed quench of CommonLookups
```

PostgreSQL says `Create new table public.Currency`; MySQL says ``Create table `Currency` ``.

## Step 3: Deploy the appserver product into the *same* database

```bash
cd ../appserver
schemaquench --ConfigFile:deploy.settings.json
```

```
Begin Quench of OltpApp
Validate Server
[localhost,11433].[learn]         Adding new table [dbo].[SalesOrder]
[localhost,11433].[learn]         Creating constraint [dbo].[SalesOrder].[PK_SalesOrder]
[localhost,11433].[learn] Successfully Quenched
Completed quench of OltpApp
```

PostgreSQL says `Create new table public.SalesOrder`; MySQL says ``Create table `SalesOrder` ``.

Note what *didn't* happen: deploying `OltpApp` left `Currency` completely alone. SchemaQuench only
manages the objects each product declares. `OltpApp` never mentions `Currency`, so it never touches
it. That's the boundary doing its job.

## Step 4: Prove both products coexist — no duplication

Both tables now live in the one `learn` database, side by side:

```bash
# SQL Server (from a SQL client): SELECT name FROM sys.tables WHERE name IN ('Currency','SalesOrder');
cd ../..                              # back to the lab folder
../lab-sql.sh postgres learn "SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename IN ('Currency','SalesOrder') ORDER BY tablename"
../lab-sql.sh mysql learn "SELECT table_name FROM information_schema.tables WHERE table_schema='learn' AND table_name IN ('Currency','SalesOrder') ORDER BY table_name"
cd <engine>/appserver                 # back to the engine folder
```

Two tables, one shared lookup, declared in exactly one place. If `Currency` had been copied into both
packages, you'd own two definitions that drift apart the first time someone edits one and forgets the
other. Decomposing by role keeps the shared product the single source of truth.

## Step 5: Re-run each — the no-op

```bash
cd ../common && schemaquench --ConfigFile:deploy.settings.json
cd ../appserver && schemaquench --ConfigFile:deploy.settings.json
```

This time there's **no** `Adding new table` / `Create table` line on either — just `Validate Server`,
`Successfully Quenched`, `Completed quench`. Each product's declared state already matches the
database, so the difference is zero. Both products are independently idempotent, even sharing a
database.

## Step 6 (optional): Watch the guardrail refuse the wrong target

The `ValidationScript` only lets a deploy proceed against a server that actually has the `learn`
database. To see it abort, temporarily edit a `Product.json` so the validation looks for a database
that isn't there (e.g. change `'learn'` to `'wrong_target'`) and run the deploy:

```
Begin Quench of CommonLookups
Validate Server
System.Exception: Invalid server for this product
```

The run stops at `Validate Server` and the exit code is non-zero (`3`) — nothing in the database is
touched. Put `'learn'` back when you're done. This is the guardrail you'd point at a production
hostname, a known environment marker, or an expected schema version to keep a package from ever
deploying somewhere it shouldn't.

## Per-engine notes

| Engine     | Schema / casing          | Lookup PK              | Create-table wording                |
| ---------- | ------------------------ | ---------------------- | ----------------------------------- |
| SQL Server | `dbo`, `NVARCHAR`        | `PK_Currency` clustered | `Adding new table [dbo].[Currency]` |
| PostgreSQL | `public`, `VARCHAR`      | `pk_currency`           | `Create new table public.Currency`  |
| MySQL      | no schema, backticks, `VARCHAR` | PK index named `PRIMARY` | ``Create table `Currency` ``  |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

These are the same dialect differences you met in Module 2 — the product-boundary lesson is identical
across all four engines.

## The principle

One product per server *role*. The common product deploys everywhere and owns the shared objects once.
Role-specific products (an OLTP app, a reporting server, an ETL box) each own only their own objects
and compose into whatever database they target. DBA-adjacent concerns — maintenance jobs, monitoring
objects, admin-only tables — belong in their own product too, deployed on its own schedule by whoever
owns that role. Never duplicate a shared object across products; that's the drift you're decomposing
to avoid.
