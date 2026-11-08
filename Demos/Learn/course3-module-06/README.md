# Course 3, Module 6 — Drift: what Monday's deploy does with Friday's hotfix (lab)

Goal: see exactly what SchemaQuench does to a production database someone changed by hand, before the next deploy
does it for you. Friday night a DBA fixes an incident directly in prod. Monday the next release ships. In between,
the database and the package disagree, and the deploy is going to settle the argument. This lab has you read that
argument in a **WhatIf** first, sort the hotfix into what to keep and what to let go, bring the keepers into the
package, and finish with a scheduled WhatIf that tells you when it happens again.

The release is the continuous `OrdersService` spine, the same `v1` package Module 3 rolled back to: `Customer` with
`LoyaltyTier` and the `IX_Customer_Email` index, `OrderHeader`, the `FK_OrderHeader_Customer` foreign key, and the
recyclebin. This time it targets **`ordersservice_prod`**, because drift is a production story.

## Layout

```
course3-module-06/
  <engine>/Package/            the OrdersService release (= Module 3's v1)
  <engine>/base.settings.json  connection details for the sandbox engine
```

`<engine>` is `sqlserver`, `postgres`, `mysql`, or `mariadb`. Every `schemaquench` command runs from your engine's
folder; every SQL command runs from the lab folder through `../lab-sql.sh`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and all four engines are healthy.
- Start from an empty prod. Earlier modules deploy into these same databases, so reset them (this drops and recreates
  the twelve Course 3 databases, empty):

  ```bash
  # from course3-module-06
  bash ../course3-setup/setup-environments.sh --reset
  ```

- The CLI is reachable. The commands below assume `schemaquench` is on your PATH (`schemaquench --version` reports
  `SchemaQuench - Version: 2.8.0.0` or later). If it isn't, set `SCHEMAQUENCH` to the full path to the executable and
  substitute `$SCHEMAQUENCH` for `schemaquench`.

## Step 1 — Ship the release, and give prod some data

Deploy the release to prod:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json
```

Then seed two customers and an order each, the way real traffic would:

```bash
# from course3-module-06
../lab-sql.sh sqlserver ordersservice_prod "INSERT INTO dbo.Customer(Name, Email) VALUES(N'Ada Lovelace', N'ada@example.com'), (N'Grace Hopper', N'grace@example.com'); INSERT INTO dbo.OrderHeader(CustomerId, OrderDate, TotalAmount) VALUES(1, '2026-11-02', 120.00), (2, '2026-11-03', 75.50)"
../lab-sql.sh postgres ordersservice_prod "INSERT INTO public.\"Customer\"(\"Name\", \"Email\") VALUES('Ada Lovelace', 'ada@example.com'), ('Grace Hopper', 'grace@example.com'); INSERT INTO public.\"OrderHeader\"(\"CustomerId\", \"OrderDate\", \"TotalAmount\") VALUES(1, '2026-11-02', 120.00), (2, '2026-11-03', 75.50)"
../lab-sql.sh mysql ordersservice_prod "INSERT INTO Customer(Name, Email) VALUES('Ada Lovelace', 'ada@example.com'), ('Grace Hopper', 'grace@example.com'); INSERT INTO OrderHeader(CustomerId, OrderDate, TotalAmount) VALUES(1, '2026-11-02', 120.00), (2, '2026-11-03', 75.50)"
../lab-sql.sh mariadb ordersservice_prod "INSERT INTO Customer(Name, Email) VALUES('Ada Lovelace', 'ada@example.com'), ('Grace Hopper', 'grace@example.com'); INSERT INTO OrderHeader(CustomerId, OrderDate, TotalAmount) VALUES(1, '2026-11-02', 120.00), (2, '2026-11-03', 75.50)"
```

## Step 2 — Friday: the hotfix nobody put in the package

An incident, a DBA, a direct connection to prod. Three changes go in by hand:

1. **An index the package never declared.** The order-history screen is slow, so `OrderHeader` gets an index on
   `OrderDate`.
2. **A column the package never declared.** Support needs a callback number, so `Customer` gets a `Phone` column, and
   one customer's number goes into it straight away.
3. **A declared index, removed.** A bulk load was crawling, so the DBA drops `IX_Customer_Email` to speed it up and
   means to put it back later.

Make all three, as the DBA did:

```bash
# from course3-module-06
../lab-sql.sh sqlserver ordersservice_prod "CREATE INDEX IX_OrderHeader_OrderDate ON dbo.OrderHeader(OrderDate); ALTER TABLE dbo.Customer ADD Phone NVARCHAR(30) NULL; DROP INDEX IX_Customer_Email ON dbo.Customer"
../lab-sql.sh sqlserver ordersservice_prod "UPDATE dbo.Customer SET Phone = N'+1 555 0100' WHERE CustomerId = 1"
../lab-sql.sh postgres ordersservice_prod "CREATE INDEX ix_orderheader_orderdate ON public.\"OrderHeader\"(\"OrderDate\"); ALTER TABLE public.\"Customer\" ADD COLUMN \"Phone\" VARCHAR(30) NULL; DROP INDEX public.ix_customer_email"
../lab-sql.sh postgres ordersservice_prod "UPDATE public.\"Customer\" SET \"Phone\" = '+1 555 0100' WHERE \"CustomerId\" = 1"
../lab-sql.sh mysql ordersservice_prod "CREATE INDEX IX_OrderHeader_OrderDate ON OrderHeader(OrderDate); ALTER TABLE Customer ADD COLUMN Phone VARCHAR(30) NULL; DROP INDEX IX_Customer_Email ON Customer"
../lab-sql.sh mysql ordersservice_prod "UPDATE Customer SET Phone = '+1 555 0100' WHERE CustomerId = 1"
../lab-sql.sh mariadb ordersservice_prod "CREATE INDEX IX_OrderHeader_OrderDate ON OrderHeader(OrderDate); ALTER TABLE Customer ADD COLUMN Phone VARCHAR(30) NULL; DROP INDEX IX_Customer_Email ON Customer"
../lab-sql.sh mariadb ordersservice_prod "UPDATE Customer SET Phone = '+1 555 0100' WHERE CustomerId = 1"
```

Nothing in the package changed. The database has **drifted**: it no longer matches what the package says it is.

## Step 3 — Monday: read the deploy before it runs

The next release goes out Monday. Before it does, ask SchemaQuench what it would do to prod. WhatIf runs the full
deploy logic, prints every statement it would run, and changes nothing:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
  schemaquench --ConfigFile:./base.settings.json
```

Two statements stand out. **SQL Server:**

```
RAISERROR('  Dropping columns from [dbo].[Customer] ([Phone])', 10, 100) WITH NOWAIT;
ALTER TABLE [dbo].[Customer] DROP COLUMN [Phone];
RAISERROR('  Creating index [dbo].[Customer].[IX_Customer_Email]', 10, 100) WITH NOWAIT;
CREATE NONCLUSTERED INDEX [IX_Customer_Email] ON [dbo].[Customer] ([Email]) WITH (DATA_COMPRESSION=NONE);
```

**PostgreSQL:**

```
RAISE NOTICE '  Column public.Customer.Phone no longer in product';
ALTER TABLE "public"."Customer" DROP COLUMN IF EXISTS "Phone" CASCADE;
RAISE NOTICE '  Add missing Index public.Customer.ix_customer_email';
CREATE INDEX "ix_customer_email" ON "public"."Customer" USING btree ("Email") ;
```

**MySQL and MariaDB:**

```
Drop column: ALTER TABLE `ordersservice_prod`.`Customer` DROP COLUMN `Phone`
CREATE INDEX `IX_Customer_Email` ON `ordersservice_prod`.`Customer` (Email)
```

Three hotfixes, three different fates:

| Friday's change | What Monday's deploy does | Why |
| --- | --- | --- |
| `IX_Customer_Email` dropped | **Puts it back.** | The package declares it. A declared object is converged to its declaration, whoever changed it. |
| `Phone` column added | **Drops it, and the number in it.** | `DropColumnsRemovedFromProduct` defaults to `true`: a column on a managed table that the package does not declare is treated as removed. |
| `OrderDate` index added | **Leaves it alone.** It isn't in the output at all. | `DropUnknownIndexes` defaults to `false`: an index SchemaSmith never created is kept until you say otherwise. |

The middle row is the one to read twice. The recyclebin you installed in Module 3 catches dropped **tables**, never
columns, so a deploy that ran without this preview would have taken the callback number with it. The two defaults
differ on purpose. An index nobody declared is cheap to keep and can be expensive to lose, because a query somebody
tuned may be leaning on it. A column the package does not describe is either something you forgot to declare or
something you removed, and SchemaSmith takes the package at its word.

### Turn index enforcement on, in preview

The `OrderDate` index survives only because enforcement is off. See what Monday would do with it on:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
SmithySettings_DropUnknownIndexes=true \
  schemaquench --ConfigFile:./base.settings.json
```

The same two statements, plus a third that drops the hotfix index. SQL Server prints
`RAISERROR('  Dropping index [dbo].[OrderHeader].[IX_OrderHeader_OrderDate]', 10, 100) WITH NOWAIT;`, PostgreSQL
`DROP INDEX IF EXISTS "public"."ix_orderheader_orderdate";`, and MySQL and MariaDB
``DROP INDEX `IX_OrderHeader_OrderDate` ON `ordersservice_prod`.`OrderHeader` ``.

## Step 4 — Decide, and put the keepers in the package

A hotfix is a change somebody needed. The question is never "how do I stop the deploy undoing it", it is "does it
belong in the product". Here the answer splits:

- **The `OrderDate` index stays.** It fixed a real problem. Declare it.
- **The `Phone` column stays.** Support is using it. Declare it, and the next deploy keeps the data.
- **`IX_Customer_Email` goes back.** The bulk load is over. Let the deploy converge it; that is the package doing its
  job.

Add the column to the `Customer` table file and the index to the `OrderHeader` table file. On SQL Server the column is
`NVARCHAR(30)`; on the other three engines it is `VARCHAR(30)`, and on PostgreSQL the index is named
`ix_orderheader_orderdate`, the name the hotfix gave it.

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Customer.json" append Columns '{"Name": "Phone", "DataType": "NVARCHAR(30)", "Nullable": true}' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.OrderHeader.json" append Indexes '{"Name": "IX_OrderHeader_OrderDate", "IndexColumns": "OrderDate"}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.Customer.json" append Columns '{"Name": "Phone", "DataType": "VARCHAR(30)", "Nullable": true}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Tables/public.OrderHeader.json" append Indexes '{"Name": "ix_orderheader_orderdate", "IndexColumns": "OrderDate"}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/Customer.json" append Columns '{"Name": "Phone", "DataType": "VARCHAR(30)", "Nullable": true}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Main/Tables/OrderHeader.json" append Indexes '{"Name": "IX_OrderHeader_OrderDate", "IndexColumns": "OrderDate"}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/Customer.json" append Columns '{"Name": "Phone", "DataType": "VARCHAR(30)", "Nullable": true}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Tables/OrderHeader.json" append Indexes '{"Name": "IX_OrderHeader_OrderDate", "IndexColumns": "OrderDate"}' -->
```json
// Customer: append to "Columns"
{ "Name": "Phone", "DataType": "NVARCHAR(30)", "Nullable": true }

// OrderHeader: append to "Indexes"
{ "Name": "IX_OrderHeader_OrderDate", "IndexColumns": "OrderDate" }
```

With a whole schema's worth of drift, you would not type this by hand. SchemaTongs, from Course 1 Module 4, casts the
live database back into the package files, and `git diff` reads the drift back to you as one change per line. For two
objects, editing the files is quicker and teaches you what SchemaTongs would have written.

## Step 5 — Preview again, then ship

Run Monday's WhatIf again:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
  schemaquench --ConfigFile:./base.settings.json
```

The column drop is gone, and the only schema change left is the one you chose: `IX_Customer_Email` going back. SQL
Server also shows SchemaSmith taking ownership of the index you adopted:

```
EXEC sp_addextendedproperty @name = N'ProductName', @value = 'OrdersService', @level0type = N'Schema', @level0name = 'dbo', @level1type = N'Table', @level1name = 'OrderHeader', @level2type = N'Index', @level2name = 'IX_OrderHeader_OrderDate';
```

That is SQL Server's ownership marker; PostgreSQL, MySQL and MariaDB track ownership without printing a statement. On
all four engines a declared index now belongs to the product, so if the `OrderDate` index ever leaves the package, the
next deploy drops it as removed, not kept as unknown. Now deploy for real:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json
```

The log names the one change: `Creating index [dbo].[Customer].[IX_Customer_Email]` on SQL Server,
`Add missing Index public.Customer.ix_customer_email` on PostgreSQL, `Create index: Customer.IX_Customer_Email` on MySQL
and MariaDB. The callback number survived:

```bash
# from course3-module-06
../lab-sql.sh sqlserver ordersservice_prod "SELECT CustomerId, Phone FROM dbo.Customer ORDER BY CustomerId"
../lab-sql.sh postgres ordersservice_prod "SELECT \"CustomerId\", \"Phone\" FROM public.\"Customer\" ORDER BY 1"
../lab-sql.sh mysql ordersservice_prod "SELECT CustomerId, Phone FROM Customer ORDER BY CustomerId"
../lab-sql.sh mariadb ordersservice_prod "SELECT CustomerId, Phone FROM Customer ORDER BY CustomerId"
```

Customer 1 still has `+1 555 0100`; customer 2 has none.

## Step 6 — The drift report: a WhatIf on a schedule

Prod and the package agree again. The way to keep it that way is to ask the Step 3 question on a schedule, not only on
deploy day. A WhatIf with `--report:` writes the deployment summary where you can read it. Run it with index
enforcement on, so a stray index counts as drift too:

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
SmithySettings_DropUnknownIndexes=true \
  schemaquench --ConfigFile:./base.settings.json --report:./drift-report
cat drift-report.md
```

Read **Object Changes**. A clean database reads all zeroes:

```
## Object Changes
- Created: tables=0, columns=0, indexes=0, constraints=0, foreignKeys=0, procedures=0, views=0, functions=0
- Modified: tables=0, columns=0
- Dropped: tables=0, columns=0, indexes=0, constraints=0, foreignKeys=0
```

Now let it drift again. Somebody tunes another query by hand:

```bash
# from course3-module-06
../lab-sql.sh sqlserver ordersservice_prod "CREATE INDEX IX_OrderHeader_TotalAmount ON dbo.OrderHeader(TotalAmount)"
../lab-sql.sh postgres ordersservice_prod "CREATE INDEX ix_orderheader_totalamount ON public.\"OrderHeader\"(\"TotalAmount\")"
../lab-sql.sh mysql ordersservice_prod "CREATE INDEX IX_OrderHeader_TotalAmount ON OrderHeader(TotalAmount)"
../lab-sql.sh mariadb ordersservice_prod "CREATE INDEX IX_OrderHeader_TotalAmount ON OrderHeader(TotalAmount)"
```

Run the drift report again (the same command, from your engine folder):

```bash
# from course3-module-06/<engine>
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
SmithySettings_DropUnknownIndexes=true \
  schemaquench --ConfigFile:./base.settings.json --report:./drift-report
cat drift-report.md
```

`Dropped` now reads `indexes=1`, and `drift-report.json` names it in `objectChanges.details` with the action
`wouldDrop`. That non-zero count is your alert: a job that runs this nightly and fails on any non-zero count catches the
next Friday before the next Monday.

<!-- TRAINING-RELEASE-PIN #607: the note below describes a summary that does not count a column dropped by absence. When a release includes the #607 fix: re-run Step 3 with --report, quote the column count it shows, and replace the note with the summary reading for the column drop. -->

> **One gap to know about.** The summary does not yet count a **column** a deploy would drop because the package does
> not declare it ([#607](https://github.com/Schema-Smith/SchemaSmith/issues/607)). PostgreSQL, MySQL and MariaDB list it
> in `drift-report.json` under `objectChanges.details` as `wouldDrop`; SQL Server does not list it there yet. Until that
> ships, have the job also search the run's log for the drop line (`Dropping columns from` on SQL Server, `no longer in
> product` on PostgreSQL, `Drop column:` on MySQL and MariaDB), because that is the drift that loses data.

## Start over

Step 4 edited the package files, so restore them before running the lab again from the top: from the repository
root, `git checkout -- Demos/Learn/course3-module-06`. Then reset the databases as in "Before you start". Without the
restore, Step 1 deploys `Phone` and the `OrderDate` index, and Step 2's hotfix fails because both already exist.

## The principle

A declarative deploy does not detect drift and then ask you what to do. It **converges**, every time, to what the
package says. That is what makes drift survivable: whatever happened on Friday, Monday's deploy puts prod back on the
record. It is also why you read the preview first. Drift you want to keep has to be written into the package, or the
deploy will faithfully undo it, and for a column that means its data. Preview, decide, declare the keepers, ship, and
let a scheduled WhatIf tell you the next time somebody fixes prod by hand.
