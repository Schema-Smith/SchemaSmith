# Course 6, Module 5 — Object-level emergency patching (lab)

Goal: ship a schema change to production **fast**, as a minimal object-level patch — without a full release,
and without collateral damage. You'll see the disaster a naive hand-carved subset causes (it drops the tables
it doesn't mention), then watch **SchemaShears** build a patch that carves the same change safely: it stamps
the emitted package so the omitted objects can't be dropped. All four engines.

This is the Course 6 finale. Module 1 fixed bad *data* as a scoped datafix; here you ship the *schema* side of
an incident response — the object-level patch that a full-product deploy would be overkill (and dangerous) for.

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The CLI is on your PATH — `schemaquench --version` and `schemashears --version` answer **2.4.0** or later.

The lab uses two **dedicated** databases per engine — `shop_patch_canary` (where the safe patch lands) and
`shop_patch_scratch` (the throwaway where we stage the disaster) — so nothing here touches the shared Course 6
fleet. Each engine folder ships:

| Path | What it is |
| --- | --- |
| `baseline/` | The `Shop` product **before** the change (no `PriceReviewBatch` column). Deploy it to establish the owned fleet. |
| `Package/` | The `Shop` product **after** the change — SchemaShears carves the patch from this. |
| `naive-subset/` | A hand-carved subset (just the changed `OrderItem`, **unstamped**) — the disaster input. |
| `patch-manifest.txt` | The list of changed files SchemaShears includes (produced by `git diff`). |
| `quench.settings.{baseline,scratch,canary,allowdrops}.json` | Deploy settings for each step. |

## Setup: stand up an owned fleet

SchemaSmith only drops tables it **owns** (ones it deployed itself) — so first deploy the `baseline/` product to
both tenants. Create + seed them, then deploy the baseline. SQL Server:

```bash
cd sqlserver
# create + seed both tenants (repeat the pattern for postgres/mysql/mariadb with their seed)
cd ..            # back to the lab folder
for db in shop_patch_canary shop_patch_scratch; do
  ../lab-sql.sh sqlserver master "IF DB_ID('$db') IS NULL CREATE DATABASE [$db]"
  ../lab-sql.sh sqlserver "$db" --file ../course6-setup/seed/sqlserver/shop.sql
done
cd sqlserver     # back into the engine folder
schemaquench --ConfigFile:quench.settings.baseline.json      # deploys baseline/ to both tenants (takes ownership)
```

MariaDB is the same pattern with its own client and seed:

```bash
cd mariadb
cd ..            # back to the lab folder
for db in shop_patch_canary shop_patch_scratch; do
  ../lab-sql.sh mariadb information_schema "CREATE DATABASE IF NOT EXISTS \`$db\`"
  ../lab-sql.sh mariadb "$db" --file ../course6-setup/seed/mariadb/shop.sql
done
cd mariadb       # back into the engine folder
schemaquench --ConfigFile:quench.settings.baseline.json
```

Now both tenants carry the owned `Shop` schema. Neither has `PriceReviewBatch` yet — that's the change we ship.

## The change: one column, as an emergency patch

The incident response adds a nullable `PriceReviewBatch` column to `OrderItem` (a tag for which price-review
batch touched each line). It lives in `Package/` (the "after"). `git diff` against the baseline produces the
one-line manifest we ship as `patch-manifest.txt`:

```bash
git diff --name-only <before> <after> -- Package/
# Templates/Main/Tables/dbo.OrderItem.json
```

## Step 1: The naive disaster — carve by hand

Deploy the hand-carved subset (`naive-subset/` — just `OrderItem` + scaffolding, **no drop suppression**) to the
throwaway `shop_patch_scratch`:

```bash
schemaquench --ConfigFile:quench.settings.scratch.json      # SchemaPackagePath: ./naive-subset
```

Because the subset defines only `OrderItem`, SchemaSmith reads the other three tables as *removed from the
product* and drops them:

```
      Drop tables removed from the product
        Dropping table [dbo].Customer
        Dropping table [dbo].Product
        Dropping table [dbo].SalesOrder
FAILED to quench:
Could not create constraint or index. See previous errors.
```

Three tables gone — and the run then fails, because `OrderItem`'s foreign keys now point at tables that no
longer exist. That's the cost of carving a subset by hand. (PostgreSQL, MySQL, and MariaDB drop the same three
tables; the message wording differs by engine.)

## Step 2: Carve the patch with SchemaShears

Same change, built safely. Point SchemaShears at the full `Package/` and the manifest:

```bash
schemashears --Source:Package --Manifest:patch-manifest.txt --Output:patch
```

```
SchemaShears: 3 files into 'patch' (1 from manifest).
```

It emits only the changed object plus scaffolding — `patch/patch-build-report.txt` shows why each file is in:

```
SchemaShears patch build report
================================
Scaffolding   Product.json
Manifest      Templates\Main\Tables\dbo.OrderItem.json
Scaffolding   Templates\Main\Template.json
```

(PostgreSQL, MySQL, and MariaDB reports name their own engine's file — `public.orderitem.json`, `OrderItem.json`, and `OrderItem.json` respectively.)

And it **stamps** the emitted `patch/Product.json` so the omitted objects can't be dropped — every drop
category flips to `false`:

```json
"DropTablesRemovedFromProduct": false,
"DropColumnsRemovedFromProduct": false,
"DropUnknownIndexes": false,
"DropForeignKeysRemovedFromProduct": false,
"DropCheckConstraintsRemovedFromProduct": false,
"DropExcludeConstraintsRemovedFromProduct": false,
"DropStatisticsRemovedFromProduct": false
```

<!-- TRAINING-RELEASE-PIN #fix-shears-stamp -- on 2.7.0, drop DropExcludeConstraintsRemovedFromProduct from this list
and say why: SchemaShears now stamps a flag only on engines that accept it (exclude constraints are PostgreSQL-only,
statistics SQL Server + PostgreSQL). On 2.6.0 the stamp above is what is emitted, and --Validate on the patch fails. -->

## Step 3: Deploy the patch safely

Deploy the SchemaShears patch to the real canary, `shop_patch_canary`:

```bash
schemaquench --ConfigFile:quench.settings.canary.json       # SchemaPackagePath: ./patch
```

The `PriceReviewBatch` column is added; `Customer`, `Product`, and `SalesOrder` are **untouched** — still there,
with all their data. Confirm:

```bash
cd ..            # back to the lab folder
../lab-sql.sh sqlserver shop_patch_canary "SELECT COUNT(*) FROM sys.tables WHERE name IN ('Customer','Product','SalesOrder','OrderItem')"
# -> 4  (all survive; the stamp held the line)
```

The patch is idempotent — re-run it and nothing changes. That's an emergency schema change delivered to one
tenant fast, with no collateral.

> **A note on who deploys this.** The patch is DDL (it alters a table), so it deploys as a schema-capable
> account — *not* the least-privilege `datafix_user` from Module 1. Data hotfix → `datafix_user`; schema hotfix
> → the schema-deploy principal. Two halves of incident response, two different grants.

## Step 4: The deliberate override

Suppression is a **default**, not a cage. When you genuinely intend to drop, `--AllowDrops:<categories>` leaves
those categories enabled. Rebuild the patch allowing table drops, and deploy it to a fresh scratch:

```bash
cd sqlserver     # back into the engine folder
schemaquench --ConfigFile:quench.settings.baseline.json     # restore the owned fleet first
schemashears --Source:Package --Manifest:patch-manifest.txt --Output:patch-allowdrops --AllowDrops:Tables
schemaquench --ConfigFile:quench.settings.allowdrops.json   # SchemaPackagePath: ./patch-allowdrops
```

Now `patch-allowdrops/Product.json` leaves `DropTablesRemovedFromProduct` alone (the other six stay `false`),
and the omitted tables drop — because you said so. The stamp protects you by default and gets out of your way
when you mean it.

## Cleanup

Drop the two dedicated tenants on each engine:

```bash
cd ..            # back to the lab folder
../lab-sql.sh sqlserver master "DROP DATABASE IF EXISTS shop_patch_canary; DROP DATABASE IF EXISTS shop_patch_scratch"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS shop_patch_canary WITH (FORCE)"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS shop_patch_scratch WITH (FORCE)"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS shop_patch_canary; DROP DATABASE IF EXISTS shop_patch_scratch"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS shop_patch_canary; DROP DATABASE IF EXISTS shop_patch_scratch"
```

## The principle

An emergency patch shouldn't force a choice between "deploy the whole product" (slow, risky) and "hand-carve a
subset" (fast, catastrophic). SchemaShears carves the minimal valid patch *and* stamps it so it can only add and
alter — never silently drop the objects it left out. Fast, minimal, and safe by construction. That's how you
patch one object in production without holding your breath.

That's the end of Course 6 — you can now operate SchemaSmith end to end: least privilege, datafix, pre-flight,
CI validation, runtime gates, and object-level emergency patching.
