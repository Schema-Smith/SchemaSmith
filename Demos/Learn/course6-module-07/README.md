# Course 6, Module 7 — Change a busy table without blocking it (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (OnlineApply, #564; the not-covered line): delete the "merged, not yet released" note -->

Goal: a deploy that adds an index or widens a column takes locks while the change runs, and on a big table the
application waits as long as the change does. `OnlineApply` asks each engine for its lower-lock form. This lab shows
the setting, what each engine actually does under `Prefer` and records in the summary, what `Require` refuses and why,
the per-table override, and SQL Server's edition limit.

A real stall needs millions of rows and a second session, so the walked steps prove the **form** each change took, not
a timed lock. There's an optional two-terminal aside at the end if you want to watch one.

All four engines, each in its own folder, everything against `online_m7`.

> **Merged, not yet released.** This module teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up. Step 6 also needs the `edition-tier` profile's SQL Server Express (`localhost,11435`).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.

The packages: `baseline/` is an `Orders` table and an `OrderStatus` lookup. `change/` adds a `Channel` column declared
between `CustomerId` and `Status`, widens `Status`, widens `OrderStatus.Label`, adds `IX_Orders_Customer`, and on SQL
Server and PostgreSQL a check constraint. The settings files differ only in `OnlineApply`:

```json
"OnlineApply": "Prefer"
```

## Step 1: Baseline, with rows

```bash
# from course6-module-07
bash -c '. ../lab-sql.sh && for e in sqlserver postgres mysql mariadb; do lab_remove_db $e online_m7; lab_confirm_db $e online_m7; done'
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:quench.settings.baseline.json); done
# exit 0, each
../lab-sql.sh sqlserver online_m7 "INSERT dbo.OrderStatus VALUES ('new','New'),('paid','Paid'); WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b) INSERT dbo.Orders (OrderId, CustomerId, Status, Amount) SELECT i, i % 50, 'new', i FROM n"
../lab-sql.sh postgres online_m7 "INSERT INTO public.order_status VALUES ('new','New'),('paid','Paid'); INSERT INTO public.orders (order_id, customer_id, status, amount) SELECT i, i % 50, 'new', i FROM generate_series(1,2000) i"
../lab-sql.sh mysql online_m7 "INSERT INTO OrderStatus VALUES ('new','New'),('paid','Paid'); INSERT INTO Orders (OrderId, CustomerId, Status, Amount) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 900) SELECT i, i % 50, 'new', i FROM n"
../lab-sql.sh mariadb online_m7 "INSERT INTO OrderStatus VALUES ('new','New'),('paid','Paid'); INSERT INTO Orders (OrderId, CustomerId, Status, Amount) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 900) SELECT i, i % 50, 'new', i FROM n"
```

The MySQL-family seed stays under 1,000 rows because recursive CTEs stop there by default.

## Step 2: Off — the plain statements

```bash
# from course6-module-07
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:quench.settings.off.json); done
# exit 0, each
```

SQL Server logs the changes with no suffix:

```
        Altering Column [dbo].[Orders].[Status]
        Creating index [dbo].[Orders].[IX_Orders_Customer]
```

The summary's change rows carry no `mode`: nothing chose a form. On MySQL and MariaDB, look where `Channel` went:

```bash
# from course6-module-07
../lab-sql.sh mysql online_m7 "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'online_m7' AND TABLE_NAME = 'Orders'"
# → OrderId,CustomerId,Status,Amount,Channel
```

Last. Under `Off` a new column goes at the end, as it always has.

## Step 3: Prefer — the online forms

Start again from the baseline, then deploy the same change with `Prefer`:

```bash
# from course6-module-07
bash -c '. ../lab-sql.sh && for e in sqlserver postgres mysql mariadb; do lab_remove_db $e online_m7; lab_confirm_db $e online_m7; done'
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:quench.settings.baseline.json); done
# exit 0, each
../lab-sql.sh sqlserver online_m7 "INSERT dbo.OrderStatus VALUES ('new','New'),('paid','Paid'); WITH n AS (SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b) INSERT dbo.Orders (OrderId, CustomerId, Status, Amount) SELECT i, i % 50, 'new', i FROM n"
../lab-sql.sh postgres online_m7 "INSERT INTO public.order_status VALUES ('new','New'),('paid','Paid'); INSERT INTO public.orders (order_id, customer_id, status, amount) SELECT i, i % 50, 'new', i FROM generate_series(1,2000) i"
../lab-sql.sh mysql online_m7 "INSERT INTO OrderStatus VALUES ('new','New'),('paid','Paid'); INSERT INTO Orders (OrderId, CustomerId, Status, Amount) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 900) SELECT i, i % 50, 'new', i FROM n"
../lab-sql.sh mariadb online_m7 "INSERT INTO OrderStatus VALUES ('new','New'),('paid','Paid'); INSERT INTO Orders (OrderId, CustomerId, Status, Amount) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 900) SELECT i, i % 50, 'new', i FROM n"
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:quench.settings.prefer.json); done
# exit 0, each
```

**SQL Server** — `ONLINE = ON` for the column and the index. The check constraint has no online form, and says so:

```
        Altering Column [dbo].[Orders].[Status] (online)
        Creating index [dbo].[Orders].[IX_Orders_Customer] (online)
        Adding check constraint [dbo].[Orders].[CK_Orders_Amount] (not covered by OnlineApply)
```

**PostgreSQL** — the index is built `CONCURRENTLY` and the check added `NOT VALID` and validated, each after the step
that needs it, because neither can run inside the step's transaction. A column type change has no lower-lock form:

```
        Add missing Index public.orders.ix_orders_customer (online, after this pass)
        Add missing check constraint public.orders.ck_orders_amount (online, validated after this pass)
      Modified columns found in public.orders(status) (not covered by OnlineApply)
```

**MySQL** — SchemaSmith asks for the lightest form and the engine decides. The column is added instantly, and in its
declared place:

```
      Add column: ALTER TABLE `online_m7`.`Orders` ADD COLUMN `Channel` VARCHAR(20) NULL AFTER `CustomerId`
        (instant: ALGORITHM=INSTANT)
      Create index: Orders.IX_Orders_Customer
        (online: ALGORITHM=INPLACE, LOCK=NONE)
```

```bash
# from course6-module-07
../lab-sql.sh mysql online_m7 "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'online_m7' AND TABLE_NAME = 'Orders'"
# → OrderId,CustomerId,Channel,Status,Amount
```

**MariaDB** — the same, and the `VARCHAR` widenings are instant too.

Every form is in the summary: each change row's `mode` is `online`, `instant`, `resumable`, `unavailable` (the plain
form, where the online one couldn't be used) or `notCovered`. Deploy again with `Prefer`: exit 0, no changes.

## Step 4: Require — a promise or a refusal

`Require` asks for the online form of every change the engine's table lists, and refuses the deploy rather than fall
back. Each engine has a change it can't make online.

### SQL Server: a columnstore index

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.Orders.json" append Indexes '{"Name": "[NCCI_Orders]", "ColumnStore": true, "IndexColumns": "[CustomerId], [Amount]"}' -->
```json
{ "Name": "[NCCI_Orders]", "ColumnStore": true, "IndexColumns": "[CustomerId], [Amount]" }
```

```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.require.json        # exit 2
```

```
OnlineApply is Require, and these indexes cannot be built online: [dbo].[Orders].[NCCI_Orders] (a columnstore index is built offline). Set the table's OnlineApply to Prefer to build them with the plain statement.
```

Under `Prefer` it's built the plain way, and the log and summary say so:

```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.prefer.json        # exit 0
#         Creating index [dbo].[Orders].[NCCI_Orders] (offline: a columnstore index is built offline)
```

A columnstore index also takes online column changes away from its table. Widen `Status` again under `Require`:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.Orders.json" set "Columns[Name=[Status]].DataType" '"VARCHAR(30)"' -->
```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.require.json        # exit 2
```

```
OnlineApply is Require, and these column changes cannot run online: [dbo].[Orders].[Status] (the table has a columnstore index). Set the table's OnlineApply to Prefer to make them with the plain statement.
```

On SQL Server and PostgreSQL the refusal comes before any existing index, key or column changes. Take both back out:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.Orders.json" set "Columns[Name=[Status]].DataType" '"VARCHAR(20)"' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.Orders.json" remove-where Indexes "Name=[NCCI_Orders]" -->

### PostgreSQL: an exclusion constraint, and a build that fails

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.orders.json" set ExcludeConstraints '[{"Name": "ex_orders_order_id", "ExcludeColumns": [{"Column": "order_id", "Operator": "="}]}]' -->
```json
"ExcludeConstraints": [ { "Name": "ex_orders_order_id", "ExcludeColumns": [ { "Column": "order_id", "Operator": "=" } ] } ]
```

```bash
# from course6-module-07/postgres
schemaquench --ConfigFile:quench.settings.require.json        # exit 2
```

```
P0001: OnlineApply is Require, and these cannot be built online: public.orders.ex_orders_order_id (an exclusion constraint cannot be built concurrently). Set the table's OnlineApply to Prefer to build them with the plain statement.
```

```bash
# from course6-module-07/postgres
schemaquench --ConfigFile:quench.settings.prefer.json        # exit 0
#         Add missing exclude constraint public.orders.ex_orders_order_id (online unavailable: an exclusion constraint cannot be built concurrently)
```

Take it back out. Now the cost of `CONCURRENTLY`: a build that fails leaves an invalid index behind. Declare a unique
index over `customer_id`, which repeats:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.orders.json" set ExcludeConstraints '[]' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.orders.json" append Indexes '{"Name": "ux_orders_customer", "Unique": true, "IndexColumns": "customer_id"}' -->
```bash
# from course6-module-07/postgres
schemaquench --ConfigFile:quench.settings.prefer.json        # exit 2
```

```
    Building Index public.orders.ux_orders_customer concurrently
    The build stopped and left the index invalid; dropping it and building once more
23505: could not create unique index "ux_orders_customer"
```

```bash
# from course6-module-07
../lab-sql.sh postgres online_m7 "SELECT COUNT(*) FROM pg_class WHERE relname = 'ux_orders_customer'"
# → 0
```

It drops the invalid index, tries once more, and fails the deploy with nothing left behind. Take the index back out:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.orders.json" remove-where Indexes "Name=ux_orders_customer" -->

### MySQL: a type change

Change `CustomerId` to `BIGINT`, and add a `Note` column in the same deploy:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/change/Templates/Main/Tables/Orders.json" set 'Columns[Name=`CustomerId`].DataType' '"BIGINT"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/change/Templates/Main/Tables/Orders.json" append Columns '{"Name": "`Note`", "DataType": "VARCHAR(40)", "Nullable": true}' -->
```bash
# from course6-module-07/mysql
schemaquench --ConfigFile:quench.settings.require.json        # exit 2
```

```
      Add column: ALTER TABLE `online_m7`.`Orders` ADD COLUMN `Note` VARCHAR(40) NULL AFTER `Amount`
        (instant: ALGORITHM=INSTANT)
      Modify column: ALTER TABLE `online_m7`.`Orders` MODIFY COLUMN `CustomerId` BIGINT NOT NULL
      OnlineApply is Require, and this cannot run online: ALTER TABLE `online_m7`.`Orders` MODIFY COLUMN `CustomerId` BIGINT NOT NULL (ALGORITHM=INPLACE is not supported. Reason: Cannot change column type INPLACE. Try ALGORITHM=COPY.)
```

```bash
# from course6-module-07
../lab-sql.sh mysql online_m7 "SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'online_m7' AND TABLE_NAME = 'Orders' AND COLUMN_NAME IN ('CustomerId', 'Note') ORDER BY COLUMN_NAME"
# → CustomerId	int
# → Note	varchar
```

MySQL can't take DDL back, so `Require` stops at the statement: `Note`, added before it, stays. Under `Prefer` the
type change runs the plain way and is recorded as `unavailable`:

```bash
# from course6-module-07/mysql
schemaquench --ConfigFile:quench.settings.prefer.json        # exit 0
#         (online unavailable: ALGORITHM=INPLACE is not supported. Reason: Cannot change column type INPLACE. Try ALGORITHM=COPY.)
```

### MariaDB: the same change, online

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/change/Templates/Main/Tables/Orders.json" set 'Columns[Name=`CustomerId`].DataType' '"BIGINT"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/change/Templates/Main/Tables/Orders.json" append Columns '{"Name": "`Note`", "DataType": "VARCHAR(40)", "Nullable": true}' -->
```bash
# from course6-module-07/mariadb
schemaquench --ConfigFile:quench.settings.require.json        # exit 0
```

```
      Modify column: ALTER TABLE `online_m7`.`Orders` MODIFY COLUMN `CustomerId` BIGINT NOT NULL
        (online: ALGORITHM=COPY, LOCK=NONE)
```

MariaDB 11.2 and later can copy a table without blocking writes, so the change MySQL refused is online here.

## Step 5: Per-table override

`OnlineApply` in a table's JSON overrides the environment. A small lookup table rewrites in milliseconds, so turn it
off there and widen `Label` again under environment-wide `Prefer`:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.OrderStatus.json" set OnlineApply '"Off"' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.OrderStatus.json" set "Columns[Name=[Label]].DataType" '"VARCHAR(80)"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.order_status.json" set OnlineApply '"Off"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/change/Templates/Main/Tables/public.order_status.json" set "Columns[Name=label].DataType" '"varchar(80)"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/change/Templates/Main/Tables/OrderStatus.json" set OnlineApply '"Off"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/change/Templates/Main/Tables/OrderStatus.json" set 'Columns[Name=`Label`].DataType' '"VARCHAR(80)"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/change/Templates/Main/Tables/OrderStatus.json" set OnlineApply '"Off"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/change/Templates/Main/Tables/OrderStatus.json" set 'Columns[Name=`Label`].DataType' '"VARCHAR(80)"' -->
```json
"OnlineApply": "Off"
```

```bash
# from course6-module-07
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:quench.settings.prefer.json); done
# exit 0, each
```

On SQL Server the line is `Altering Column [dbo].[OrderStatus].[Label]`, with no suffix, and its summary row has no
`mode`. The rest of the package is still under `Prefer`.

## Step 6: SQL Server's two extras

### A resumable build

From SQL Server 2019, [`quench.settings.resumable.json`](sqlserver/quench.settings.resumable.json) adds
`"OnlineApplyMaxDuration": 60`, and each new index becomes a resumable build:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/change/Templates/Main/Tables/dbo.Orders.json" append Indexes '{"Name": "[IX_Orders_Amount]", "IndexColumns": "[Amount]"}' -->
```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.resumable.json        # exit 0
#         Creating index [dbo].[Orders].[IX_Orders_Amount] (online, resumable)
```

If the build runs past its time, SQL Server pauses it and ends the deploy's session, so the deploy fails at that
statement. The next deploy deals with the paused build first: it resumes it when the package still declares the index,
and abandons it when it doesn't.

### The edition

Online operations need Enterprise or Developer edition (or Azure SQL). Start the Express server and deploy there:

```bash
# from docker
docker compose --profile edition-tier up -d sqlserver-express
```

```bash
# from course6-module-07
LEARN_CONTAINER=learn-sqlserver-express bash -c '. ../lab-sql.sh && lab_remove_db sqlserver online_m7; lab_confirm_db sqlserver online_m7'
```

```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.express-baseline.json        # exit 0
schemaquench --ConfigFile:quench.settings.express-require.json        # exit 2
```

```
OnlineApply is Require for [dbo].[Orders], but this server cannot change an index or a column online: that needs Enterprise or Developer edition, or Azure SQL, on SQL Server 2012 or later. Set OnlineApply to Prefer to deploy with the plain statements.
```

```bash
# from course6-module-07/sqlserver
schemaquench --ConfigFile:quench.settings.express-prefer.json        # exit 0
```

```
        Altering Column [dbo].[Orders].[Status] (offline: online column changes need Enterprise or Developer edition, or Azure SQL, on SQL Server 2016 or later)
        Creating index [dbo].[Orders].[IX_Orders_Customer] (offline: online index operations need Enterprise or Developer edition, or Azure SQL, on SQL Server 2012 or later)
```

Only `[dbo].[Orders]` is named: `OrderStatus` opted out in Step 5. Under `Prefer` the same package deploys, and every
change on `Orders` is recorded `unavailable`, with the reason.

## Optional: watch a lock (not walked)

Two terminals, SQL Server. In the first, hold a lock on `Orders`:

```sql
BEGIN TRAN; UPDATE dbo.Orders SET Amount = Amount WHERE OrderId = 1;   -- leave it open
```

In the second, deploy a new index under `Off` and watch it wait until you `COMMIT` in the first. Then repeat under
`Prefer`: the online build runs alongside the open transaction and only needs a brief lock at the very end.

## What you proved

- `Off` is the plain statements, no `mode`. `Prefer` uses each engine's online form where it can, and records which.
- `Require` refuses by name. SQL Server and PostgreSQL refuse before touching existing objects; MySQL stops at the
  statement, with earlier changes kept. MariaDB can do online what MySQL can't.
- A change no engine can do online runs as written and says `not covered by OnlineApply`.
- A table can opt out, and SQL Server's edition decides what `Prefer` can do.

## Clean up

```bash
# from course6-module-07
bash -c '. ../lab-sql.sh && for e in sqlserver postgres mysql mariadb; do lab_remove_db $e online_m7; done'
LEARN_CONTAINER=learn-sqlserver-express bash -c '. ../lab-sql.sh && lab_remove_db sqlserver online_m7'
```

```bash
# from docker
docker compose rm -sfv sqlserver-express
```

Put the packages back with `git checkout -- .` (from this folder) if you want to run the module again.
