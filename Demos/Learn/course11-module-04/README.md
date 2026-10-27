# Course 11, Module 4 — What the database owns (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (the Setup slot; #509, #518; the #518 refusal names the longest value): delete the "merged, not yet released" note -->

Goal: a package describes tables, but Change Data Capture and Change Tracking also need a switch that belongs to the
**database**. Turning it on changes retention, cleanup jobs and storage for every table in the database, which is not a
decision one table's JSON gets to make. So SchemaSmith doesn't make it: it records a downgrade and tells you. Then you
give the database switch a home of its own in the package, the `Setup` slot, and both features come on in the same
deploy. The rest of the module is CDC's sharpest operational rule, two capture instances per table, and the refusals
that keep captured history from being thrown away.

SQL Server only: `sqlserver/`, everything against `vault_m5`. The sandbox runs SQL Server Agent, so capture is real: the
rows you see in the change tables were written by the capture job.

> **Merged, not yet released.** This module teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up and [`../course11-setup`](../course11-setup) has been run (it creates `vault_m5`).
- The sandbox's SQL Server runs SQL Server Agent (`MSSQL_AGENT_ENABLED` in `docker-compose.yml`). If you started the
  sandbox before this module existed, run `docker compose up -d` in `../docker` once, then the setup script again.
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.

## Step 1: Ask for it without the database switch

[`dbo.Customer.json`](sqlserver/Package/Templates/Main/Tables/dbo.Customer.json) asks for Change Tracking, columns
included. [`dbo.Payment.json`](sqlserver/Package/Templates/Main/Tables/dbo.Payment.json) asks for CDC:

```json
"EnableChangeTracking": true,
"TrackColumnsUpdated": true
```

```json
"EnableCDC": true
```

```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 1
```

```
        CDC skipped: not enabled on this database (EXEC sys.sp_cdc_enable_db to allow it - downgraded): [dbo].[Payment]
        Change Tracking skipped: not enabled on this database (ALTER DATABASE ... SET CHANGE_TRACKING = ON to allow it - downgraded): [dbo].[Customer]
```

Both tables deploy. Neither feature is on, and the run says so: exit 1, outcome `Incomplete`, and two downgrades in the
summary, each with the statement that would allow it. The deploy could have run those statements itself. It didn't,
because they reach beyond this package.

## Step 2: Give the database switch a home

[`setup-scripts/`](sqlserver/setup-scripts) holds the two statements, each guarded so it only acts when the switch is
off:

```sql
IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID())
  ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
```

```sql
IF NOT EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_cdc_enabled = 1)
  EXEC sys.sp_cdc_enable_db;
```

Put them in a `Setup` folder and tell the template that folder runs in the `Setup` slot, ahead of every object and
table:

```bash
# from course11-module-04/sqlserver
mkdir -p Package/Templates/Main/Setup
cp setup-scripts/*.sql Package/Templates/Main/Setup/
```

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Template.json" set ScriptFolders '[{"FolderPath": "Setup", "QuenchSlot": "Setup"}]' -->
```json
"ScriptFolders": [ { "FolderPath": "Setup", "QuenchSlot": "Setup" } ]
```

```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```
    Quenching .\Package\Templates\Main\Setup\01-change-tracking.sql
    Quenching .\Package\Templates\Main\Setup\02-cdc.sql
        Enable CDC on [dbo].[Payment]
        Enable Change Tracking on [dbo].[Customer]
```

The switches come on first, so the same deploy can turn both features on. CDC brought its two Agent jobs with it:

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "SELECT capture_instance FROM cdc.change_tables"
# → dbo_Payment
../lab-sql.sh sqlserver vault_m5 "SELECT name FROM msdb.dbo.sysjobs WHERE name LIKE 'cdc.vault_m5%' ORDER BY name"
# → cdc.vault_m5_capture
# → cdc.vault_m5_cleanup
```

Deploy again: exit 0, and both Setup scripts run again. The `Setup` slot runs on **every** deploy, unlike a migration
script, which is why each script checks before it changes anything.

## Step 3: Watch it capture

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "INSERT dbo.Payment VALUES (1, 10.00, 'first'), (2, 20.00, 'second')"
../lab-sql.sh sqlserver vault_m5 "UPDATE dbo.Payment SET Amount = 12.00 WHERE PaymentId = 1"
../lab-sql.sh sqlserver vault_m5 "WAITFOR DELAY '00:00:10'; SELECT __\$operation, PaymentId, Amount FROM cdc.dbo_Payment_CT ORDER BY __\$start_lsn, __\$seqval"
# → 2 1 10.00
# → 2 2 20.00
# → 3 1 10.00
# → 4 1 12.00
```

The capture job reads the log every few seconds, hence the wait. Operation 2 is an insert, and 3 and 4 are an update's
before and after images. That history is what the rest of this module protects.

## Step 4: Two capture instances

A capture instance records a fixed set of columns. Widen `Memo` first:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" set "Columns[Name=[Memo]].DataType" '"VARCHAR(40)"' -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Altering Column [dbo].[Payment].[Memo]
```

The column set didn't change, so the capture instance carries on. Now add a column:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" append Columns '{"Name": "[Channel]", "DataType": "VARCHAR(10)", "Nullable": true}' -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```
        CDC ROTATED on [dbo].[Payment]: new capture instance dbo_Payment_2 now captures the new column set. The previous instance dbo_Payment STILL HOLDS ITS HISTORY and was NOT dropped -- drain it, then drop it with EXEC sys.sp_cdc_disable_table @capture_instance = N'dbo_Payment'. Until then the next column change on this table WILL FAIL: SQL Server allows only two capture instances.
```

A new instance captures the new shape, and the old one keeps the history your downstream readers haven't consumed yet.
SQL Server allows two per table, so add another column before draining:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" append Columns '{"Name": "[Region]", "DataType": "VARCHAR(10)", "Nullable": true}' -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 2
```

```
CDC capture-instance limit reached on: [dbo].[Payment]. SQL Server permits two capture instances per table and both are already in use, so this new column, CdcFilegroup, CdcSupportsNetChanges or CdcIndexName change cannot rotate without discarding change history. Nothing has been changed. Drain the older instance on each listed table and drop it (EXEC sys.sp_cdc_disable_table @source_schema = N'<schema>', @source_name = N'<table>', @capture_instance = N'<name>'), then re-run.
```

`Nothing has been changed.` That's the refusal, and it's the one you want: the alternative is dropping an instance and
its history to make room. Take `Region` back out:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" remove-where Columns "Name=[Region]" -->

## Step 5: A change the change table can't hold

Drain the old instance (here, by deciding nobody needs it) and drop it, as the rotation line said:

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'Payment', @capture_instance = N'dbo_Payment'"
```

Now write a long memo, and let the capture job record it:

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "UPDATE dbo.Payment SET Memo = 'a memo of thirty characters!!' WHERE PaymentId = 2"
../lab-sql.sh sqlserver vault_m5 "WAITFOR DELAY '00:00:10'; SELECT COUNT(*) FROM cdc.dbo_Payment_2_CT WHERE LEN(Memo) > 10"
# → 1
```

Narrow `Memo` to `VARCHAR(10)`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" set "Columns[Name=[Memo]].DataType" '"VARCHAR(10)"' -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 2
```

```
CDC capture would stop on [dbo].[Payment]: the change to column [Memo] (VARCHAR(10) COLLATE SQL_Latin1_General_CP1_CI_AS NULL) cannot be applied to change table [cdc].[dbo_Payment_2_CT] of capture instance dbo_Payment_2, which holds values the new type does not take (the longest is 29 characters). SQL Server applies a column type change to every change table that captures the column, and the capture job for the whole database fails until it can. Nothing has been changed. Drain that instance and drop it (EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'Payment', @capture_instance = N'dbo_Payment_2'), then re-run.
```

The table could take the change (you'd lose nothing it holds today), but the change table can't. SQL Server would
apply the narrowing to it too, fail, and stop capture for **every** table in the database. The deploy checks first and
stops by name:

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "SELECT TYPE_NAME(user_type_id), max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Payment') AND name = 'Memo'"
# → varchar 40
```

Put `Memo` back to `VARCHAR(40)`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" set "Columns[Name=[Memo]].DataType" '"VARCHAR(40)"' -->

## Step 6: Leaving it out is not turning it off

Take `EnableCDC` out of `dbo.Payment.json` entirely and deploy:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" del EnableCDC -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 0
```

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "SELECT is_tracked_by_cdc FROM sys.tables WHERE name = 'Payment'"
# → 1
```

Still on. A table that says nothing about CDC leaves it as it is, so capture someone enabled outside the package survives
a deploy. Turning it off drops the capture instances and their history, so it takes an explicit `false`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Payment.json" set EnableCDC false -->
```bash
# from course11-module-04/sqlserver
schemaquench --ConfigFile:quench.settings.json        # exit 0
#         Disable CDC on [dbo].[Payment]
```

```bash
# from course11-module-04
../lab-sql.sh sqlserver vault_m5 "SELECT is_tracked_by_cdc FROM sys.tables WHERE name = 'Payment'"
# → 0
```

## What you proved

- A table can ask for CDC or Change Tracking, but the database switch isn't the table's. Without it, the deploy records a
  downgrade instead of reconfiguring the database.
- The `Setup` slot is where the package says "this database needs these switches", ahead of everything, on every deploy.
- A column change rotates to a new capture instance and keeps the old one's history. A third is refused, and so is a type
  change the change table can't hold. Both say `Nothing has been changed`.
- Leaving `EnableCDC` out is never destructive. Turning capture off takes `false`.

## Clean up

```bash
# from course11-module-04
rm -rf sqlserver/Package/Templates/Main/Setup
bash -c '. ../lab-sql.sh && lab_remove_db sqlserver vault_m5 && lab_confirm_db sqlserver vault_m5'
```

Put the package back with `git checkout -- sqlserver` if you want to run the module again.
