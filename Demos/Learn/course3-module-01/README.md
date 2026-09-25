# Course 3, Module 1 — Team workflow & code review (lab)

Goal: see why a **state-based JSON diff** reviews better than a hand-written `ALTER`, and watch a
schema change ride the normal pull-request workflow. You'll diff two copies of a `Customer` table
definition — `starter` (the v1 package) and `solution` (the same package with one column added) — to
see the exact artifact a reviewer reads in a PR, then quench the solution against a live database to
prove the reviewed change deploys cleanly.

The change is a single column: **`LoyaltyTier`** on the `Customer` table — `NVARCHAR(20)` on SQL
Server (`VARCHAR(20)` on PostgreSQL and MySQL), nullable, defaulting to `Standard`.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) appears twice — once under
`starter/` and once under `solution/` — each with its own `Package/` and `dev.settings.json`
targeting `ordersservice_dev`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified — all four engines healthy.
- The target databases exist. Run the Course 3 setup once (idempotent — safe to re-run):

  ```bash
  pwsh ../course3-setup/setup-environments.ps1     # or: ../course3-setup/setup-environments.sh
  ```

  It creates the twelve `ordersservice_{dev,staging,prod}` databases across the four engines and
  reports `PASS` for each.
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Establish the base (the v1 package)

The `starter/` package is the `Customer` table *before* anyone touched it — three columns, a primary
key, no `LoyaltyTier`. Quench it so the database matches the v1 state a teammate would branch from:

```bash
cd starter/<engine>            # sqlserver | postgres | mysql | mariadb
schemaquench --ConfigFile:dev.settings.json
```

You'll see `Customer` and `OrderHeader` created (first run) or a clean no-op (if they're already
there). Either way, `ordersservice_dev` now holds the v1 `Customer` — without `LoyaltyTier`.

## Step 2: See the change the way a reviewer does — the diff

A teammate wants a loyalty tier on every customer. Instead of writing an `ALTER`, they edit the
table definition. The pull request shows it as a plain diff. Run that diff yourself:

```bash
# from this module's root
git diff --no-index \
  starter/sqlserver/Package/Templates/Main/Tables/dbo.Customer.json \
  solution/sqlserver/Package/Templates/Main/Tables/dbo.Customer.json
```

(Use the matching paths for `postgres/.../public.Customer.json`, `mysql/.../Customer.json`, or
`mariadb/.../Customer.json`.) On
SQL Server the reviewable artifact looks like this:

```diff
   "Columns": [
     { "Name": "CustomerId", "DataType": "INT IDENTITY(1,1)" },
     { "Name": "Name", "DataType": "NVARCHAR(100)" },
-    { "Name": "Email", "DataType": "NVARCHAR(200)" }
+    { "Name": "Email", "DataType": "NVARCHAR(200)" },
+    { "Name": "LoyaltyTier", "DataType": "NVARCHAR(20)", "Nullable": true, "Default": "N'Standard'" }
   ],
   "Indexes": [
     { "Name": "PK_Customer", "PrimaryKey": true, "Clustered": true, "IndexColumns": "CustomerId" }
   ]
```

This is the whole teaching point. The new column shows up **in context** — its neighbors above it,
the primary key right below, every index and foreign key in the same file in view. A reviewer
evaluates the *design* (right type? nullable? sensible default? in the right place?), not whether a
hand-written script executes correctly. Compare the equivalent imperative form a reviewer would
otherwise get — `ALTER TABLE Customer ADD LoyaltyTier NVARCHAR(20) NULL DEFAULT 'Standard'` — a verb
with no scene, forcing the reviewer to imagine the table it mutates.

## Step 3: Quench the solution — the reviewed change deploys

Approve the diff, then deploy it. The `solution/` package is the v1 package plus that one column:

```bash
cd solution/<engine>
schemaquench --ConfigFile:dev.settings.json
```

`LoyaltyTier` is added to `Customer`. The per-engine wording differs:

| Engine     | What you'll see |
| ---------- | --------------- |
| SQL Server | `Adding 1 new columns to [dbo].[Customer]` |
| PostgreSQL | `Add new physical columns to public.Customer (LoyaltyTier)` |
| MySQL      | ``Add column: ALTER TABLE `ordersservice_dev`.`Customer` ADD COLUMN `LoyaltyTier` VARCHAR(20) NULL DEFAULT 'Standard'`` |
| MariaDB    | ``Add column: ALTER TABLE `ordersservice_dev`.`Customer` ADD COLUMN `LoyaltyTier` VARCHAR(20) NULL DEFAULT 'Standard'`` |

*MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native
package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics
(invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for
you.*

The column you reviewed is the column that landed — no translation step between the diff and the
deploy.

## Step 4: Confirm the column is there

```bash
cd ../..                              # back to the lab folder
../lab-sql.sh sqlserver ordersservice_dev "SELECT name, TYPE_NAME(system_type_id), max_length FROM sys.columns WHERE object_id=OBJECT_ID('dbo.Customer') AND name='LoyaltyTier'"
../lab-sql.sh postgres ordersservice_dev "SELECT column_name, data_type, character_maximum_length FROM information_schema.columns WHERE table_name='Customer' AND column_name='LoyaltyTier'"
../lab-sql.sh mysql ordersservice_dev "SELECT COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='ordersservice_dev' AND TABLE_NAME='Customer' AND COLUMN_NAME='LoyaltyTier'"
../lab-sql.sh mariadb ordersservice_dev "SELECT COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='ordersservice_dev' AND TABLE_NAME='Customer' AND COLUMN_NAME='LoyaltyTier'"
cd solution/<engine>                  # back to the engine folder
```

You'll see `LoyaltyTier` as a 20-character string column on each engine (`nvarchar` length 40 on SQL Server — `NVARCHAR(20)` is 40 bytes).

## Step 5: Re-run — the no-op

Run the solution quench a second time:

```bash
schemaquench --ConfigFile:dev.settings.json
```

The declared state now matches the database, so SchemaQuench reports **no** `Adding` / `Add column`
line — just the deploy framing and `Successfully Quenched`. The package is idempotent: re-deploying a
change that's already applied does nothing. That's the property that makes promoting the *same*
reviewed package across dev, staging, and prod safe — each environment converges to the declared
state and stops.

## Per-engine notes

| Engine     | `LoyaltyTier` type | Default form in JSON       |
| ---------- | ------------------ | -------------------------- |
| SQL Server | `NVARCHAR(20)`     | `"Default": "N'Standard'"` |
| PostgreSQL | `VARCHAR(20)`      | `"Default": "'Standard'"`  |
| MySQL      | `VARCHAR(20)`      | `"Default": "'Standard'"`  |
| MariaDB    | `VARCHAR(20)`      | `"Default": "'Standard'"`  |

The default's quoting follows each engine's literal syntax — SQL Server's `N'...'` for a Unicode
string literal, plain `'...'` on PostgreSQL, MySQL, and MariaDB. Everything else about the change is
identical across all four.

## The principle

A schema change is a pull request. Because the schema is files, the change is a diff a teammate can
read in context — design over syntax — and the reviewed file is the deployed file. No out-of-band
migration folder, no "who runs the script and when," no drift between what was approved and what
ships. The diff *is* the deploy plan.
