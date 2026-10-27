# Course 4, Recipe 18 — History the database keeps (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (period columns by role, #523; the precision warning; MariaDB system-time columns, #487/#524): delete the "merged, not yet released" note -->

Goal: "What did this price say last March?" Two engines answer that natively, and in both the answer is
declared state, not a script you run once. **SQL Server** temporal tables keep every old version of a row
in a history table. **MariaDB** system versioning does the same inside the table, and its
**application-time periods** answer a different question — not *when a row was stored*, but *when it was
true*. You declare all of it, deploy it, change it, and extract it back.

Two engines: `sqlserver/` and `mariadb/`, everything against `cookbook_r18`.

> **Merged, not yet released.** This recipe teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r18`).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.
- Ports: SQL Server **11433**, MariaDB **13307**. Each engine folder's `deploy.settings.json` already points at
  the right one.

## Step 1: Declare a temporal table (SQL Server)

[`sqlserver/Package/Templates/Main/Tables/dbo.Price.json`](sqlserver/Package/Templates/Main/Tables/dbo.Price.json)
turns versioning on, names the history table and how long it keeps rows, and declares the two period columns by
their role:

```json
"IsTemporal": true,
"HistoryTableName": "[PriceHistory]",
"HistoryRetentionPeriod": "6 MONTHS",
"Columns": [
  ...
  { "Name": "[SysStart]", "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowStart", "Hidden": true },
  { "Name": "[SysEnd]",   "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowEnd",   "Hidden": true }
],
"Indexes": [
  ...
  { "Name": "[IX_Price_SysStart]", "IndexColumns": "[SysStart]" }
]
```

Leave the period columns out and you get `ValidFrom` / `ValidTo DATETIME2(7)`. Declaring them lets you pick the
names, the precision, and `Hidden` — which keeps them out of `SELECT *` — and lets an index name them. Deploy:

```bash
# from course4-recipe-18/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        Adding new table [dbo].[Price]
        Adding period columns to [dbo].[Price]
        Creating constraint [dbo].[Price].[PK_Price]
        Creating index [dbo].[Price].[IX_Price_SysStart]
        Turn ON Temporal Tracking for [dbo].[Price]
```

The period columns exist before the indexes are built, which is why `IX_Price_SysStart` can name one. You did not
declare `PriceHistory`: SQL Server creates the history table when versioning is turned on with a name that doesn't
exist yet. Check what the catalog holds:

```bash
# from course4-recipe-18
../lab-sql.sh sqlserver cookbook_r18 "SELECT name, generated_always_type_desc, is_hidden FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Price') AND generated_always_type <> 0 ORDER BY column_id"
# → SysStart AS_ROW_START 1
# → SysEnd AS_ROW_END 1
../lab-sql.sh sqlserver cookbook_r18 "SELECT OBJECT_NAME(history_table_id), history_retention_period, history_retention_period_unit_desc FROM sys.tables WHERE name = 'Price'"
# → PriceHistory 6 MONTH
```

Run the deploy again. Exit 0, and nothing under any step: the table is what the package says.

## Step 2: Ask it what the price used to be

```bash
# from course4-recipe-18
../lab-sql.sh sqlserver cookbook_r18 "INSERT dbo.Price (PriceId, Amount) VALUES (1, 10.00); UPDATE dbo.Price SET Amount = 12.00 WHERE PriceId = 1"
../lab-sql.sh sqlserver cookbook_r18 "SELECT * FROM dbo.Price"
# → 1 12.00
../lab-sql.sh sqlserver cookbook_r18 "SELECT PriceId, Amount FROM dbo.Price FOR SYSTEM_TIME ALL ORDER BY SysStart"
# → 1 10.00
# → 1 12.00
```

`SELECT *` shows two columns, because the period columns are hidden. `FOR SYSTEM_TIME ALL` shows both versions: the
old one came from `PriceHistory`. Nobody wrote a trigger.

## Step 3: Change a versioned table — what moves and what doesn't

Rename `SysStart` to `SysBegin` (with `OldName`, so it's a rename and not a drop), make `SysEnd` visible, and point
the index at the new name:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Columns[Name=[SysStart]].OldName" '"[SysStart]"' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Columns[Name=[SysStart]].Name" '"[SysBegin]"' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Columns[Name=[SysEnd]].Hidden" false -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Indexes[Name=[IX_Price_SysStart]].IndexColumns" '"[SysBegin]"' -->
```json
{ "Name": "[SysBegin]", "OldName": "[SysStart]", "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowStart", "Hidden": true },
{ "Name": "[SysEnd]", "DataType": "DATETIME2(2)", "Nullable": false, "GeneratedAlways": "RowEnd", "Hidden": false }
...
{ "Name": "[IX_Price_SysStart]", "IndexColumns": "[SysBegin]" }
```

```bash
# from course4-recipe-18/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        Dropping index [dbo].[Price].[IX_Price_SysStart]
        Rename period column [dbo].[Price].[SysStart] to [SysBegin]
        Creating index [dbo].[Price].[IX_Price_SysStart]
```

Both changes are made in place, on a table that stays versioned the whole time. The rename is `sp_rename`, and the
history table's column follows it. The `Hidden` change is `ALTER COLUMN … DROP HIDDEN`: the log doesn't name it, but
the deployment summary lists `[dbo].[Price].[SysEnd]` under its object changes, and the catalog shows it:

```bash
# from course4-recipe-18
../lab-sql.sh sqlserver cookbook_r18 "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PriceHistory') ORDER BY column_id"
# → PriceId
# → Amount
# → SysBegin
# → SysEnd
../lab-sql.sh sqlserver cookbook_r18 "SELECT PriceId, Amount FROM dbo.Price"
# → 1 12.00
```

Now the change SQL Server won't make. Declare `SysEnd` as `DATETIME2(7)`:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Columns[Name=[SysEnd]].DataType" '"DATETIME2(7)"' -->
```bash
# from course4-recipe-18/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        WARNING: [dbo].[Price]: period column SysEnd is DATETIME2(2), declared DATETIME2(7). SQL Server cannot change the precision of a period column, so the deploy leaves it as it is; re-create the table to change it.
```

SQL Server refuses to alter a period column while the table is versioned. Switching versioning off to make the
change would open a window where updates write no history, so SchemaSmith doesn't do that on your behalf. It
leaves the column alone and tells you, by name, with both types. Put `DATETIME2(2)` back before you go on:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Price.json" set "Columns[Name=[SysEnd]].DataType" '"DATETIME2(2)"' -->

## Step 4: System versioning and application time (MariaDB)

[`mariadb/Package/Templates/Main/Tables/Price.json`](mariadb/Package/Templates/Main/Tables/Price.json) is the
same idea in MariaDB's shape. The history lives inside the table, the row-start and row-end columns are declared by
role and made invisible, and one column opts out of history:

```json
"IsSystemVersioned": true,
"Columns": [
  ...
  { "Name": "`Note`", "DataType": "VARCHAR(200)", "Nullable": true, "WithoutSystemVersioning": true },
  { "Name": "`rs`", "DataType": "TIMESTAMP(6)", "Nullable": false, "GeneratedAlways": "RowStart", "Invisible": true },
  { "Name": "`re`", "DataType": "TIMESTAMP(6)", "Nullable": false, "GeneratedAlways": "RowEnd", "Invisible": true }
]
```

[`Rate.json`](mariadb/Package/Templates/Main/Tables/Rate.json) is the other question. Its `Validity` period is
*application time*: dates your application sets, saying when a rate applies. Its unique key says that two rates for
the same SKU may not overlap:

```json
"Periods": [ { "Name": "Validity", "StartColumn": "ValidFrom", "EndColumn": "ValidTo" } ],
"Indexes": [
  ...
  { "Name": "ux_rate_sku_validity", "Unique": true, "IndexColumns": "`Sku`, Validity", "WithoutOverlaps": true }
]
```

```bash
# from course4-recipe-18/mariadb
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
      Create table `Price`
      Create table `Rate`
      ...
      Create index: Price.ux_price_sku
      Create index: Rate.ux_rate_sku_validity
```

Look at what MariaDB built:

```bash
# from course4-recipe-18
../lab-sql.sh mariadb cookbook_r18 "SHOW CREATE TABLE Price"
```

You'll see `GENERATED ALWAYS AS ROW START INVISIBLE`, `WITHOUT SYSTEM VERSIONING` on `Note`,
`PERIOD FOR SYSTEM_TIME (rs, re)` and `WITH SYSTEM VERSIONING`. You'll also see `PRIMARY KEY (PriceId, re)` and
`UNIQUE KEY ux_price_sku (Sku, re)`. MariaDB adds the row-end column to every key of a versioned table so the old
versions can share a key with the current one. The package declares the keys without `re`, and so does
extraction. Run the deploy again: exit 0, nothing under any step.

## Step 5: Stored vs. true

```bash
# from course4-recipe-18
../lab-sql.sh mariadb cookbook_r18 "INSERT INTO Price (PriceId, Sku, Amount) VALUES (1, 'anvil', 10.00)"
../lab-sql.sh mariadb cookbook_r18 "UPDATE Price SET Amount = 12.00 WHERE PriceId = 1"
../lab-sql.sh mariadb cookbook_r18 "UPDATE Price SET Note = 'checked' WHERE PriceId = 1"
../lab-sql.sh mariadb cookbook_r18 "SELECT PriceId, Amount, Note FROM Price FOR SYSTEM_TIME ALL ORDER BY re"
# → 1	10.00	NULL
# → 1	12.00	checked
```

Three writes, two versions. The `Amount` change wrote history. The `Note` change didn't, because `Note` is
`WithoutSystemVersioning`: use that for a busy column whose history nobody will ask for.

Application time is enforced by the engine. Two rates that meet end to start are fine; one that overlaps them is
refused:

```bash
# from course4-recipe-18
../lab-sql.sh mariadb cookbook_r18 "INSERT INTO Rate VALUES (1, 'anvil', 10.00, '2026-01-01', '2026-07-01')"
../lab-sql.sh mariadb cookbook_r18 "INSERT INTO Rate VALUES (2, 'anvil', 11.00, '2026-07-01', '2027-01-01')"
../lab-sql.sh mariadb cookbook_r18 "INSERT INTO Rate VALUES (3, 'anvil', 12.00, '2026-06-01', '2026-08-01')"     # refused
#   ERROR 1062 (23000) at line 1: Duplicate entry 'anvil-2026-08-01-2026-06-01' for key 'ux_rate_sku_validity'
../lab-sql.sh mariadb cookbook_r18 "SELECT RateId, Amount FROM Rate WHERE Sku = 'anvil' AND '2026-03-15' >= ValidFrom AND '2026-03-15' < ValidTo"
# → 1	10.00
```

The third insert fails with a non-zero exit. That's the lesson, not a broken step: `WITHOUT OVERLAPS` is the rule
your declaration put there.

## Step 6: Extract both and compare

```bash
# from course4-recipe-18/sqlserver
schematongs --ConfigFile:SchemaTongs.settings.json        # exit 0
```

```bash
# from course4-recipe-18/mariadb
schematongs --ConfigFile:SchemaTongs.settings.json        # exit 0
```

Open `sqlserver/extracted/Templates/Main/Tables/dbo.Price.json`. It holds `IsTemporal`, `HistoryTableName`,
`HistoryRetentionPeriod`, and the period pair by role with the names and `Hidden` you deployed: `[SysBegin]` hidden,
`[SysEnd]` visible. Open `mariadb/extracted/Templates/Main/Tables/Price.json` and `Rate.json`. They hold
`IsSystemVersioned`, `rs` and `re` by role and `Invisible`, `WithoutSystemVersioning` on `Note`, the keys without
`re`, the `Validity` period, and `WithoutOverlaps` on its key.

`Rate.json` has no check constraints. MariaDB creates one for every period (`` `ValidFrom` < `ValidTo` ``, named
after the period), and the catalog lists it beside your own checks, but the period owns it. Extraction leaves it out,
so the package doesn't declare a check the period already brings.

One version caveat for your own servers: MariaDB periods work from 10.4.3, but the catalog only *reports* them from
11.4. Extracting from a server in between returns no periods even where the table has them. Deploying works on every
version; only the read is blind.

## What you proved

- Versioning, the history table, retention and the period columns are all declarations, on both engines.
- A rename and a `Hidden` change apply in place on a versioned table. A precision change doesn't, and the deploy says
  so instead of switching versioning off behind your back.
- System time records when a row was **stored**; application time records when it was **true**. MariaDB lets one
  database carry both, and `WITHOUT OVERLAPS` makes the engine enforce the second.

## Clean up

```bash
# from course4-recipe-18
rm -rf sqlserver/extracted mariadb/extracted
../lab-sql.sh sqlserver cookbook_r18 "IF OBJECT_ID('dbo.Price') IS NOT NULL BEGIN ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF); DROP TABLE dbo.Price; DROP TABLE dbo.PriceHistory; END"
../lab-sql.sh mariadb cookbook_r18 "DROP TABLE IF EXISTS Price, Rate"
```

Put the package files back with `git checkout -- sqlserver/Package` if you want to run the recipe again.
