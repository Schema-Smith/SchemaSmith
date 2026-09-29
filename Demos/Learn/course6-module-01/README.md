# Course 6, Module 1 — Datafix & limited-privilege deployment (lab)

**Goal:** ship a data-only fix — correct a batch of mispriced order lines across three tenant databases — deploying it through SchemaSmith under the **datafix profile** as a **scoped, least-privilege account**, one tenant at a time (canary). You'll see two independent things hold the structural boundary: the deployment *profile* (SchemaSmith performs no structural DDL) and the deploy *account* (it has no power to drop or alter the product's tables).

## The scenario

A pricing bug double-applied a discount: every `OrderItem` on a **May 2026** `SalesOrder` was written at `Product.UnitPrice × 0.81` (10% off, twice) instead of the intended `× 0.90`. It's live in three tenant databases. You need to correct the prices **and** keep a restorable backup of every row you touch — because it's money, you must be able to prove and reverse exactly what changed.

## Before you start

- The four-engine sandbox is up (`Demos/Learn/docker`) and `schemaquench --version` answers **2.7.0 or later** on your PATH.
- Run **[`course6-setup`](../course6-setup/)** first. It seeds `shop_tenant_a`, `shop_tenant_b`, `shop_tenant_c` on each engine with the price defect, and creates the scoped **`datafix_user`** role (see [`course6-setup/seed/<engine>/datafix_role.sql`](../course6-setup/seed/)).

Confirm the defect is present (SQL Server shown; swap the client per engine):

```sql
-- 10 affected rows per tenant
SELECT COUNT(*) FROM dbo.OrderItem oi
JOIN dbo.SalesOrder so ON so.OrderId = oi.OrderId
WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01';
```

## Step 1 — Look at the deploy account before you trust it

`course6-setup` created `datafix_user` with a deliberately narrow grant set. The important move: the account **owns a dedicated `datafix` schema** where its backup tables live, and has **reader/writer (but no structural rights)** on the product schema. Creating a table in a schema you own needs only `CREATE TABLE` — no `ALTER ON SCHEMA::dbo`, which would have *also* let the account drop the product's tables.

> **Lesson from the field:** ask a DBA for "rights to create a table" and you may be handed `ALTER ON SCHEMA` — a drop capability in disguise. Owning a schema lets you ask for, and *prove*, exactly "create my backup tables, nothing structural." The grant scripts here are codified in the [datafix-role grants reference](../../../docs/end-user/reference/datafix-role-grants.md).

## Step 2 — Read the datafix profile

Open [`sqlserver/quench.settings.json`](sqlserver/quench.settings.json). Two things to notice:

```jsonc
{
  "Target": { "User": "datafix_user", "Password": "DataFix!Demo123", "Databases": ["shop_tenant_a"] },
  "KindleTheForge": false,          // don't (re)create databases
  "UpdateTables": false,            // skip the structural table-quench phase entirely
  "DropTablesRemovedFromProduct": false,  // never drop by absence
  "TrackRunOnceMigrations": false   // every migration script runs every time
}
```

The four flags are the **datafix profile**: SchemaSmith runs your migration scripts and nothing else — no structural reconciliation. `Target.Databases` scopes this run to **one tenant** (`shop_tenant_a`) — your canary. And `TrackRunOnceMigrations: false` means the script runs on *every* deploy, so it **must be idempotent** — which is why the migration backs up and fixes only rows it hasn't already handled.

## Step 3 — Canary: deploy to one tenant, as the scoped account

```bash
cd sqlserver        # or postgres / mysql / mariadb
schemaquench --ConfigFile:quench.settings.json
```

The log shows it connect **as `datafix_user`**, filter **1 of 3** discovered databases, and run only the After-Scripts migration — no table-quench phase:

```
[Target] Resolved 1 work unit(s) after filtering 3 discovered unit(s) for template 'Main'.
[localhost,11433].[shop_tenant_a]   Quenching after database scripts
[localhost,11433].[shop_tenant_a]     Quenching .\Package\...\After Scripts\01_backup_and_fix_prices.sql
[localhost,11433].[shop_tenant_a] Successfully Quenched
```

## Step 4 — Verify the canary

SQL Server shown; adapt the schema/identifier style per the [per-engine notes](#per-engine-notes) below for PostgreSQL (`datafix.orderitem_pricefix_backup`, lowercase) and MySQL/MariaDB (`OrderItem_PriceFix_Backup` in the tenant db).

```sql
-- backup captured the 10 originals (in the datafix schema the account owns)
SELECT COUNT(*) FROM datafix.OrderItem_PriceFix_Backup;          -- 10
-- prices corrected in tenant_a
SELECT COUNT(*) FROM dbo.OrderItem oi
  JOIN dbo.SalesOrder so ON so.OrderId = oi.OrderId
  JOIN dbo.Product p ON p.ProductId = oi.ProductId
 WHERE so.OrderDate >= '2026-05-01' AND so.OrderDate < '2026-06-01'
   AND oi.UnitPrice <> ROUND(p.UnitPrice * 0.90, 2);             -- 0
```

`shop_tenant_b` and `shop_tenant_c` are **untouched** — no `datafix` backup table, defect intact. The canary changed exactly one tenant.

## Step 5 — Idempotency, then widen

Re-run the same command. The backup count stays **10** and 0 rows change — the script is a no-op on already-fixed data (it must be, since run-once tracking is off). Now widen: edit `Target.Databases` to `["shop_tenant_b","shop_tenant_c"]` and deploy again. Both fix; the rollout is complete.

## Step 6 — Prove the boundary

Two layers held the line, and you can demonstrate both:

1. **The profile** — `UpdateTables: false` meant SchemaSmith never entered a table-quench phase. Even pointed at the full package, it would not have reconciled structure.
2. **The account** — try to drop a product table *as* `datafix_user`:

```sql
-- SQL Server
DROP TABLE dbo.OrderItem;     -- Msg 3701: ... you do not have permission
```

Denied. The account can back up and fix data; it cannot alter or drop the product schema. The boundary doesn't depend on remembering to set a flag — it's enforced by what the account *can't do*.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| Product schema | `dbo` (PascalCase) | `public` (lowercase) | the tenant database |
| Backup table | `datafix.OrderItem_PriceFix_Backup` | `datafix.orderitem_pricefix_backup` | `OrderItem_PriceFix_Backup` (in the db) |
| Why the account can't drop | no `ALTER ON SCHEMA::dbo` (owns `datafix` instead) | not the owner of `public` tables; no `CREATE` on `public` | no `DROP` privilege granted |
| Connection | `localhost,11433` | `localhost:15432` | `localhost:13306` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

MySQL and MariaDB have no schema-within-database layer, so their backup table lives in the tenant database directly and their boundary comes from simply never granting `DROP`. The two schema-capable engines (SQL Server, PostgreSQL) put the backup in a dedicated, account-owned `datafix` schema — same principle, native to each engine.

## What you proved

A data fix shipped through the same tool and package you use for everything else, but under a profile that does no structural work and an account that *can't* — fixed across a fleet one tenant at a time, with a restorable backup of every changed row. That's a datafix you can hand to a least-privilege deploy account and a change-review board with equal confidence.
