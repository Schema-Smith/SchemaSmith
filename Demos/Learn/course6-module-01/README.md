# Course 6, Module 1 — Datafix & limited-privilege deployment (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (#547 run-once record): delete the "On 2.7.x" callouts -->

**Goal:** ship a data-only fix — correct a batch of mispriced order lines across three tenant databases — deploying it through SchemaSmith under the **datafix profile** as a **scoped, least-privilege account**, one tenant at a time (canary). You'll see two independent things hold the structural boundary: the deployment *profile* (SchemaSmith performs no structural DDL) and the deploy *account* (it has no power to drop or alter the product's tables).

## The scenario

A pricing bug double-applied a discount: every `OrderItem` on a **May 2026** `SalesOrder` was written at `Product.UnitPrice × 0.81` (10% off, twice) instead of the intended `× 0.90`. It's live in three tenant databases. You need to correct the prices **and** keep a restorable backup of every row you touch — because it's money, you must be able to prove and reverse exactly what changed.

## Before you start

- The four-engine sandbox is up (`Demos/Learn/docker`) and `schemaquench --version` answers **2.7.0 or later** on your PATH.
- Run **[`course6-setup`](../course6-setup/)** first. It seeds `shop_tenant_a`, `shop_tenant_b`, `shop_tenant_c` on each engine with the price defect, and creates the scoped **`datafix_user`** role (see [`course6-setup/seed/<engine>/datafix_role.sql`](../course6-setup/seed/)).

Confirm the defect is present in the canary tenant — each line prints `10` (10 affected rows per tenant):

```bash
# from course6-module-01
../lab-sql.sh sqlserver shop_tenant_a "SELECT COUNT(*) FROM dbo.OrderItem oi JOIN dbo.SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"
../lab-sql.sh postgres shop_tenant_a "SELECT COUNT(*) FROM orderitem oi JOIN salesorder so ON so.orderid = oi.orderid WHERE so.orderdate >= '2026-05-01' AND so.orderdate < '2026-06-01'"
../lab-sql.sh mysql shop_tenant_a "SELECT COUNT(*) FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"
../lab-sql.sh mariadb shop_tenant_a "SELECT COUNT(*) FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'"
```

## Step 1 — Look at the deploy account before you trust it

`course6-setup` created `datafix_user` with a deliberately narrow grant set. The important move: the account **owns a dedicated `datafix` schema** where its backup tables live, and has **reader/writer (but no structural rights)** on the product schema. Creating a table in a schema you own needs only `CREATE TABLE` — no `ALTER ON SCHEMA::dbo`, which would have *also* let the account drop the product's tables.

> **Lesson from the field:** ask a DBA for "rights to create a table" and you may be handed `ALTER ON SCHEMA` — a drop capability in disguise. Owning a schema lets you ask for, and *prove*, exactly "create my backup tables, nothing structural." The grant scripts here are codified in the [datafix-role grants reference](../../../docs/end-user/reference/datafix-role-grants.md).

## Step 2 — An admin kindles the fleet and grants the run-once record

`datafix_user` can't install SchemaSmith's own objects in a tenant, and shouldn't: that is an admin's one-time job. The admin deploys [`<engine>/kindle/`](sqlserver/kindle/) — the same product and template, with no scripts at all — to all three tenants, with `KindleTheForge: true` and the structural phases still off:

```bash
# from course6-module-01/<engine>
schemaquench --ConfigFile:quench.settings.kindle.json
```

Each tenant logs `Kindling the forge` and then `Successfully Quenched`; nothing else runs. Among what the kindle creates is SchemaSmith's **run-once record** — `SchemaSmith.CompletedMigrationScripts` (`SchemaSmith_CompletedMigrationScripts` on MySQL and MariaDB) — where a deploy notes each migration script it has run. The datafix account needs that one table, so the admin grants it in each tenant with [`<engine>/grant_run_once.sql`](sqlserver/grant_run_once.sql), the run-once row of the [datafix-role grants reference](../../../docs/end-user/reference/datafix-role-grants.md):

```bash
# from course6-module-01
../lab-sql.sh sqlserver shop_tenant_a --file sqlserver/grant_run_once.sql
../lab-sql.sh sqlserver shop_tenant_b --file sqlserver/grant_run_once.sql
../lab-sql.sh sqlserver shop_tenant_c --file sqlserver/grant_run_once.sql
../lab-sql.sh postgres shop_tenant_a --file postgres/grant_run_once.sql
../lab-sql.sh postgres shop_tenant_b --file postgres/grant_run_once.sql
../lab-sql.sh postgres shop_tenant_c --file postgres/grant_run_once.sql
../lab-sql.sh mysql shop_tenant_a --file mysql/grant_run_once.sql
../lab-sql.sh mysql shop_tenant_b --file mysql/grant_run_once.sql
../lab-sql.sh mysql shop_tenant_c --file mysql/grant_run_once.sql
../lab-sql.sh mariadb shop_tenant_a --file mariadb/grant_run_once.sql
../lab-sql.sh mariadb shop_tenant_b --file mariadb/grant_run_once.sql
../lab-sql.sh mariadb shop_tenant_c --file mariadb/grant_run_once.sql
```

`SELECT` and `INSERT` let a deploy check for the fix and note it; `DELETE` lets it forget a script you later remove from the package. On MySQL and MariaDB the tenant-wide `SELECT, INSERT` that `course6-setup` granted already cover the record, so only `DELETE` is added.

Skip the grant on SQL Server or PostgreSQL and the datafix deploy stops before the slot runs, exit `2`, naming what is missing (SQL Server's wording; PostgreSQL's names `"SchemaSmith"."CompletedMigrationScripts"` and the schema's `USAGE`):

```text
Cannot read or record which Before scripts have already run: the deploy login has no SELECT and INSERT on SchemaSmith.CompletedMigrationScripts. Nothing in the slot was run, because a run-once script would run again. Grant the deploy login SELECT, INSERT and DELETE on SchemaSmith.CompletedMigrationScripts.
```

> **On 2.7.x** a deploy without the grant does not stop: SQL Server logs the permission errors and exits `0`,
> PostgreSQL logs nothing, and the fix runs again on every deploy (#547).

## Step 3 — Read the datafix profile

Open [`sqlserver/quench.settings.json`](sqlserver/quench.settings.json). Two things to notice:

```jsonc
{
  "Target": { "User": "datafix_user", "Password": "DataFix!Demo123", "Databases": ["shop_tenant_a"] },
  "KindleTheForge": false,          // the admin's kindle (Step 2) already installed SchemaSmith's objects
  "UpdateTables": false,            // skip the structural table-quench phase entirely
  "DropTablesRemovedFromProduct": false,  // never drop by absence
  "TrackRunOnceMigrations": true    // record the fix in each tenant; never run it there twice
}
```

The four flags are the **datafix profile**: SchemaSmith runs your migration scripts and nothing else — no structural reconciliation. `Target.Databases` scopes this run to **one tenant** (`shop_tenant_a`) — your canary. And `TrackRunOnceMigrations: true` means the fix runs **once per tenant**: the deploy notes it in the record Step 2 granted, and every later deploy skips it. Keep the script **idempotent** anyway — a record can be cleared and a fix replayed by hand, and money must not move twice either way — which is why the migration backs up and fixes only rows it hasn't already handled.

## Step 4 — Canary: deploy to one tenant, as the scoped account

```bash
# from course6-module-01/<engine>
schemaquench --ConfigFile:quench.settings.json
```

The log shows it connect **as `datafix_user`**, filter **1 of 3** discovered databases, and run only the After-Scripts migration — no table-quench phase:

```
[Target] Resolved 1 work unit(s) after filtering 3 discovered unit(s) for template 'Main'.
[localhost,11433].[shop_tenant_a]   Quenching after database scripts
[localhost,11433].[shop_tenant_a]     Quenching .\Package\...\After Scripts\01_backup_and_fix_prices.sql
[localhost,11433].[shop_tenant_a] Successfully Quenched
```

## Step 5 — Verify the canary

Two checks per engine, each identifier in its engine's style (see the [per-engine notes](#per-engine-notes) below): PostgreSQL's backup is `datafix.orderitem_pricefix_backup` (lowercase), MySQL/MariaDB's is `OrderItem_PriceFix_Backup` in the tenant db. The first line of each pair counts the backup — the 10 originals, in the backup table the account owns — and prints `10`; the second counts May lines still mispriced in `shop_tenant_a` and prints `0`:

```bash
# from course6-module-01
../lab-sql.sh sqlserver shop_tenant_a "SELECT COUNT(*) FROM datafix.OrderItem_PriceFix_Backup"
../lab-sql.sh sqlserver shop_tenant_a "SELECT COUNT(*) FROM dbo.OrderItem oi JOIN dbo.SalesOrder so ON so.OrderId = oi.OrderId JOIN dbo.Product p ON p.ProductId = oi.ProductId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01' AND oi.UnitPrice <> ROUND(p.UnitPrice * 0.90, 2)"
../lab-sql.sh postgres shop_tenant_a "SELECT COUNT(*) FROM datafix.orderitem_pricefix_backup"
../lab-sql.sh postgres shop_tenant_a "SELECT COUNT(*) FROM orderitem oi JOIN salesorder so ON so.orderid = oi.orderid JOIN product p ON p.productid = oi.productid WHERE so.orderdate >= '2026-05-01' AND so.orderdate < '2026-06-01' AND oi.unitprice <> ROUND(p.unitprice * 0.90, 2)"
../lab-sql.sh mysql shop_tenant_a "SELECT COUNT(*) FROM OrderItem_PriceFix_Backup"
../lab-sql.sh mysql shop_tenant_a "SELECT COUNT(*) FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId JOIN Product p ON p.ProductId = oi.ProductId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01' AND oi.UnitPrice <> ROUND(p.UnitPrice * 0.90, 2)"
../lab-sql.sh mariadb shop_tenant_a "SELECT COUNT(*) FROM OrderItem_PriceFix_Backup"
../lab-sql.sh mariadb shop_tenant_a "SELECT COUNT(*) FROM OrderItem oi JOIN SalesOrder so ON so.OrderId = oi.OrderId JOIN Product p ON p.ProductId = oi.ProductId WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01' AND oi.UnitPrice <> ROUND(p.UnitPrice * 0.90, 2)"
```

`shop_tenant_b` and `shop_tenant_c` are **untouched** — no `datafix` backup table, defect intact. The canary changed exactly one tenant.

## Step 6 — Run once, then widen

Re-run the same command. The fix is not run again — the record says it already ran in `shop_tenant_a` — and the backup count stays **10**:

```bash
# from course6-module-01/<engine>
schemaquench --ConfigFile:quench.settings.json
```

```
[localhost,11433].[shop_tenant_a]     Skipping (previously quenched) .\Package\Templates\Main\After Scripts\01_backup_and_fix_prices.sql
[localhost,11433].[shop_tenant_a] Successfully Quenched
```

Now widen: in `<engine>/quench.settings.json`, edit `Target.Databases` to `["shop_tenant_b","shop_tenant_c"]` and deploy again. Both fix; the rollout is complete.

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.json" set Target.Databases '["shop_tenant_b","shop_tenant_c"]' -->
```bash
# from course6-module-01/<engine>
schemaquench --ConfigFile:quench.settings.json
```

## Step 7 — Prove the boundary

Two layers held the line, and you can demonstrate both:

1. **The profile** — `UpdateTables: false` meant SchemaSmith never entered a table-quench phase. Even pointed at the full package, it would not have reconciled structure.
2. **The account** — try to drop a product table *as* `datafix_user`. On SQL Server and PostgreSQL the admin connection takes on the account's identity first (`EXECUTE AS USER` / `SET ROLE`); the SQL Server line drops only if `USER_NAME()` really is `datafix_user`, and PostgreSQL runs both statements as one transaction, so a failed `SET ROLE` stops the drop. MySQL and MariaDB cannot switch to another account mid-session, so their line lists the account's grants instead — and `DROP` is not among them:

```bash
# from course6-module-01
../lab-sql.sh sqlserver shop_tenant_a "EXECUTE AS USER = 'datafix_user'; IF USER_NAME() = 'datafix_user' DROP TABLE dbo.OrderItem;"
../lab-sql.sh postgres shop_tenant_a "SET ROLE datafix_user; DROP TABLE orderitem"
../lab-sql.sh mysql information_schema "SHOW GRANTS FOR 'datafix_user'@'%'"
../lab-sql.sh mariadb information_schema "SHOW GRANTS FOR 'datafix_user'@'%'"
```

The SQL Server line fails with `Msg 3701: ... you do not have permission` and the PostgreSQL line with `ERROR:  must be owner of table orderitem`; that failure is the proof. (The MySQL and MariaDB lists end with Step 2's `DELETE` on `SchemaSmith_CompletedMigrationScripts`, one line per tenant.) Denied. The account can back up and fix data; it cannot alter or drop the product schema. The boundary doesn't depend on remembering to set a flag — it's enforced by what the account *can't do*.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| Product schema | `dbo` (PascalCase) | `public` (lowercase) | the tenant database |
| Backup table | `datafix.OrderItem_PriceFix_Backup` | `datafix.orderitem_pricefix_backup` | `OrderItem_PriceFix_Backup` (in the db) |
| Why the account can't drop | no `ALTER ON SCHEMA::dbo` (owns `datafix` instead) | not the owner of `public` tables; no `CREATE` on `public` | no `DROP` privilege granted |
| Run-once record grant | `SELECT, INSERT, DELETE` on `SchemaSmith.CompletedMigrationScripts` | `USAGE` on `"SchemaSmith"`, plus `SELECT, INSERT, DELETE` on its `"CompletedMigrationScripts"` | `DELETE` on `SchemaSmith_CompletedMigrationScripts` |
| Connection | `localhost,11433` | `localhost:15432` | `localhost:13306` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

MySQL and MariaDB have no schema-within-database layer, so their backup table lives in the tenant database directly and their boundary comes from simply never granting `DROP`. The two schema-capable engines (SQL Server, PostgreSQL) put the backup in a dedicated, account-owned `datafix` schema — same principle, native to each engine.

## What you proved

A data fix shipped through the same tool and package you use for everything else, but under a profile that does no structural work and an account that *can't* — fixed across a fleet one tenant at a time, with a restorable backup of every changed row. That's a datafix you can hand to a least-privilege deploy account and a change-review board with equal confidence.
