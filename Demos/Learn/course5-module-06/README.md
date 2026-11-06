# Course 5, Module 6 — Brownfield guardrails, one at a time (lab)

Goal: bring a production database that has years of drift under SchemaSmith **without losing anything on the first
deploy**. You'll extract the package from dev, preview what the defaults would do to prod, deploy with every drop
guardrail on, and then turn the guardrails off one kind of object per release, each time behind a WhatIf preview.
All four engines.

The two databases are the same shop schema, but production has collected four things dev never had:

| In `shop_brownfield_prod` only | What it is | What you'll decide |
| --- | --- | --- |
| `Customer.LoyaltyTier` | A column a hotfix added, holding real customer data | Keep it: put it in the package |
| `CK_Product_LegacySku` | A check left over from an old 12-character SKU rule | Drop it |
| `IX_SalesOrder_Status` | An index a DBA added by hand for a slow report | Keep it: put it in the package |
| `ReportCache` | A table another system writes to | Leave it alone |

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The Course 5 databases exist: run [`../course5-setup`](../course5-setup) once. It creates `shop_brownfield_dev` and
  `shop_brownfield_prod` on every engine. To run this lab again from the start, reset them with
  `bash setup-databases.sh --reset` and put the lab's files back with `git checkout -- .` from this folder.
- The CLI is on your PATH (`schematongs --version` and `schemaquench --version` answer **2.8.0 or later**).

Each engine folder ships a `SchemaTongs.settings.json` that extracts from `shop_brownfield_dev`, and two deploy
settings files that both point the package at `shop_brownfield_prod` through the `MainDb` script token:

- `quench.settings.first-look.json` uses SchemaQuench's defaults, with `"WhatIfONLY": true`, so it can only preview.
- `quench.settings.prod.json` turns every drop guardrail on. This is the file you'll edit, one line per release.

## Step 1: Extract the package from dev

```bash
# from course5-module-06/<engine>
schematongs --ConfigFile:SchemaTongs.settings.json --LogPath:"$PWD/logs"
ls Package/Templates/Main/Tables/
```

Four tables, cast from dev. Dev is where the package is written and reviewed, so this is the package every
environment will get. Production's extras aren't in it, because they were never in dev.

## Step 2: First look — what the defaults would do to production

Preview the deploy to prod with SchemaQuench's defaults:

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.first-look.json --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
grep -E "DROP (COLUMN|CONSTRAINT|CHECK)" "logs/SchemaQuench - Progress.log"
```

The `Drop…RemovedFromProduct` settings all default to `true`, so a first deploy would make prod match the package by
**dropping two things**: the `LoyaltyTier` column, data and all, and the legacy check. The progress log shows both
statements the deploy would run. On SQL Server:

```text
ALTER TABLE [dbo].[Customer] DROP COLUMN [LoyaltyTier];
IF OBJECT_ID('[dbo].[CK_Product_LegacySku]') IS NOT NULL ALTER TABLE [dbo].[Product] DROP CONSTRAINT [CK_Product_LegacySku];
```

<!-- TRAINING-RELEASE-PIN #607: the paragraph below describes a summary that does not count a column dropped by absence, and on SQL Server does not list it. When a release includes the #607 fix: re-run Step 2 on all four engines, quote the summary's column rows, and rewrite the paragraph (and the lesson's matching paragraph and per-engine row). -->
Read the progress log, not just the summary. On PostgreSQL, MySQL and MariaDB the summary's `details` list both drops
as `wouldDrop`; on SQL Server they list only the check. On every engine the `dropped` counts have a bucket for
constraints and none for columns. The preview SQL is the complete list.

Two things the defaults would **not** touch. `IX_SalesOrder_Status` stays, because `DropUnknownIndexes` is off by
default and nothing in the package could put a hand-made index back. `ReportCache` stays, because a table is only
dropped by absence when this product owns it, and nobody does yet.

## Step 3: Release 1 — every guardrail on

Every drop setting in `quench.settings.prod.json` is `false`:

```bash
# from course5-module-06/<engine>
grep '"Drop' quench.settings.prod.json
```

```text
  "DropTablesRemovedFromProduct": false,
  "DropColumnsRemovedFromProduct": false,
  "DropForeignKeysRemovedFromProduct": false,
  "DropCheckConstraintsRemovedFromProduct": false,
  "DropIndexesRemovedFromProduct": false,
  "DropStatisticsRemovedFromProduct": false,
  "DropExcludeConstraintsRemovedFromProduct": false,
  "DropUnknownIndexes": false
```

A `false` in the environment's settings is a hard guardrail: no product, template or table setting below it can turn
the drop back on. `DropStatisticsRemovedFromProduct` applies to SQL Server and PostgreSQL and
`DropExcludeConstraintsRemovedFromProduct` to PostgreSQL; the other engines accept them and ignore them, so one file
shape works everywhere.

Preview, then deploy:

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
schemaquench --ConfigFile:quench.settings.prod.json --LogPath:"$PWD/logs"
```

The preview lists nothing to drop, and the deploy adopts the four declared tables: SchemaQuench marks them as owned
by `Shop` and stands up its bookkeeping. Production is under management and nothing in it changed:

```bash
# from course5-module-06
../lab-sql.sh sqlserver shop_brownfield_prod "SELECT LoyaltyTier FROM dbo.Customer WHERE CustomerId = 1"
../lab-sql.sh postgres shop_brownfield_prod "SELECT loyaltytier FROM customer WHERE customerid = 1"
../lab-sql.sh mysql shop_brownfield_prod "SELECT LoyaltyTier FROM Customer WHERE CustomerId = 1"
../lab-sql.sh mariadb shop_brownfield_prod "SELECT LoyaltyTier FROM Customer WHERE CustomerId = 1"
# → GOLD
```

This is where most adoptions should sit for a while. Every later release turns on one kind of drop, and only after a
preview says exactly what it will take.

## Step 4: Release 2 — check constraints

The legacy check enforced a SKU rule the business dropped years ago. It should go. Set
`DropCheckConstraintsRemovedFromProduct` to `true` in `quench.settings.prod.json`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropCheckConstraintsRemovedFromProduct true -->
```json
"DropCheckConstraintsRemovedFromProduct": true,
```

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
```

One row, the check, `wouldDrop`. That's the whole blast radius of this release, so deploy it:

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
```

The same row, now `dropped`.

## Step 5: Release 3 — columns: capture first, then enforce

`LoyaltyTier` holds data the business uses. The package is what's missing something, not production, so the fix is
to declare the column. Add it to the `Columns` array of your engine's `Customer` table file:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/*Customer.json" append Columns '{"Name": "[LoyaltyTier]", "DataType": "VARCHAR(10)", "Nullable": true}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/*customer.json" append Columns '{"Name": "loyaltytier", "DataType": "varchar(10)", "Nullable": true}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/*Customer.json" append Columns '{"Name": "`LoyaltyTier`", "DataType": "varchar(10)", "Nullable": true}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/*Customer.json" append Columns '{"Name": "`LoyaltyTier`", "DataType": "varchar(10)", "Nullable": true}' -->
```json
{ "Name": "[LoyaltyTier]", "DataType": "VARCHAR(10)", "Nullable": true }
```

That's the SQL Server form. PostgreSQL's is `"Name": "loyaltytier", "DataType": "varchar(10)"`; MySQL and MariaDB's is
``"Name": "`LoyaltyTier`", "DataType": "varchar(10)"``. Then set `DropColumnsRemovedFromProduct` to `true`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropColumnsRemovedFromProduct true -->
```json
"DropColumnsRemovedFromProduct": true,
```

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -E "DROP (COLUMN|CONSTRAINT|CHECK)" "logs/SchemaQuench - Progress.log"
schemaquench --ConfigFile:quench.settings.prod.json --LogPath:"$PWD/logs"
```

The preview finds no column to drop, because every column prod has is now declared. From this release on, a column
someone adds to production by hand shows up in the next preview as a drop, which is exactly the drift alarm you
want once the package is complete. Take the column out of the package and deploy, and it would go.

## Step 6: Release 4 — indexes: the preview says drop, you say keep

Turn on the last guardrail that reaches into hand-made objects, `DropUnknownIndexes`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropUnknownIndexes true -->
```json
"DropUnknownIndexes": true,
```

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
```

The preview says it would drop `IX_SalesOrder_Status`. It's the DBA's index for a slow report, and dropping it would
put the report back on a table scan. This is why each release gets a preview: you find out here, not from the on-call
page. Declare it in your engine's `SalesOrder` table file, in the `Indexes` array:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/*SalesOrder.json" append Indexes '{"Name": "[IX_SalesOrder_Status]", "IndexColumns": "[Status]"}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/*salesorder.json" append Indexes '{"Name": "ix_salesorder_status", "IndexColumns": "status"}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/*SalesOrder.json" append Indexes '{"Name": "IX_SalesOrder_Status", "IndexColumns": "`Status`"}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/*SalesOrder.json" append Indexes '{"Name": "IX_SalesOrder_Status", "IndexColumns": "`Status`"}' -->
```json
{ "Name": "[IX_SalesOrder_Status]", "IndexColumns": "[Status]" }
```

PostgreSQL's form is `"Name": "ix_salesorder_status", "IndexColumns": "status"`; MySQL and MariaDB's is
``"Name": "IX_SalesOrder_Status", "IndexColumns": "`Status`"``. Preview again, then deploy:

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
schemaquench --ConfigFile:quench.settings.prod.json --LogPath:"$PWD/logs"
```

Nothing to drop. The deploy adopts the index as it stands, without rebuilding it, and it's now the product's
like everything else declared.

```bash
# from course5-module-06
../lab-sql.sh sqlserver shop_brownfield_prod "SELECT name FROM sys.indexes WHERE name = 'IX_SalesOrder_Status'"
../lab-sql.sh postgres shop_brownfield_prod "SELECT indexname FROM pg_indexes WHERE indexname = 'ix_salesorder_status'"
../lab-sql.sh mysql shop_brownfield_prod "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'shop_brownfield_prod' AND INDEX_NAME = 'IX_SalesOrder_Status'"
../lab-sql.sh mariadb shop_brownfield_prod "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'shop_brownfield_prod' AND INDEX_NAME = 'IX_SalesOrder_Status'"
# → IX_SalesOrder_Status  (ix_salesorder_status on PostgreSQL)
```

## Step 7: Release 5 — the rest, and the table nobody owns

What's left only ever drops objects this product owns and the package no longer declares: tables, foreign keys,
product-owned indexes, statistics, exclude constraints. Turn them all on:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropTablesRemovedFromProduct true -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropForeignKeysRemovedFromProduct true -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropIndexesRemovedFromProduct true -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropStatisticsRemovedFromProduct true -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.prod.json" set DropExcludeConstraintsRemovedFromProduct true -->
```json
"DropTablesRemovedFromProduct": true,
"DropForeignKeysRemovedFromProduct": true,
"DropIndexesRemovedFromProduct": true,
"DropStatisticsRemovedFromProduct": true,
"DropExcludeConstraintsRemovedFromProduct": true,
```

```bash
# from course5-module-06/<engine>
schemaquench --ConfigFile:quench.settings.prod.json --WhatIf --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
schemaquench --ConfigFile:quench.settings.prod.json --LogPath:"$PWD/logs"
```

Nothing to drop, and `ReportCache` is still there with its row:

```bash
# from course5-module-06
../lab-sql.sh sqlserver shop_brownfield_prod "SELECT ReportName FROM dbo.ReportCache"
../lab-sql.sh postgres shop_brownfield_prod "SELECT reportname FROM reportcache"
../lab-sql.sh mysql shop_brownfield_prod "SELECT ReportName FROM ReportCache"
../lab-sql.sh mariadb shop_brownfield_prod "SELECT ReportName FROM ReportCache"
# → daily-sales
```

`DropTablesRemovedFromProduct` drops a table this product owns once the package stops declaring it. `ReportCache`
was never declared, so `Shop` never adopted it, and no setting here will drop it. Another system's table in the same
database is safe by construction.

Every setting is now back at its default except `DropUnknownIndexes`, which you chose to turn on. The package
describes production completely, and the next preview that lists a drop is telling you about drift.

## The principle

The defaults are right for a database SchemaSmith built. A database it didn't build has a history the package
doesn't know about yet, and the defaults read that history as something to remove. So invert them for the first
deploy, adopt what's there, and hand enforcement back one kind of object at a time. Each release, the preview names
everything that release could drop, and for each one you decide: drop it, or declare it. When the preview stops
naming anything, you're done.
