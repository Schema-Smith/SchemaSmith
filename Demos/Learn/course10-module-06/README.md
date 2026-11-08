# Course 10, Module 6 — The server you upgraded under the package (lab)

Goal: see what the **first deploy after an upgrade** does. Modules 1 to 5 deployed one package to servers on
different versions at the same time. This module is the other way a version changes: the same database, under the
same package, on a newer engine than last week. You'll deploy to the old tier, upgrade, and read what the next deploy
does: which recorded expressions it re-baselines instead of re-applying, what the old tier was skipping that now
gets built, and the one thing a plain dump leaves behind. All four engines.

| Engine | Before | Upgrade | After |
| --- | --- | --- | --- |
| SQL Server | `learn_upgrade` at compatibility level 100 | `ALTER DATABASE … SET COMPATIBILITY_LEVEL = 160`, in place | the same database at 160 |
| PostgreSQL | PostgreSQL 12 (`learn-postgres-12`) | `pg_dump`, then `psql` into PostgreSQL 16 | PostgreSQL 16 |
| MySQL | MySQL 5.7 (`learn-mysql-57`) | `mysqldump`, then `mysql` into MySQL 8.0 | MySQL 8.0 |
| MariaDB | MariaDB 10.2 (`learn-mariadb-102`) | `mysqldump`, then `mariadb` into MariaDB 11.4 | MariaDB 11.4 |

The package is one `Invoice` table carrying every kind of expression SchemaSmith records: check constraints, a
computed (generated) column, a default, and on SQL Server and PostgreSQL a filtered index.

## Before you start

- The mixed fleet is up: run [`../course10-setup`](../course10-setup) (it starts the `mixed-fleet` profile with
  PostgreSQL 12, MySQL 5.7 and MariaDB 10.2 beside the current engines).
- The CLI is on your PATH (`schemaquench --version` answers **2.8.0** or later).

Each engine folder holds the `Package/`, a `quench.settings.before.json` pointing at the old tier and a
`quench.settings.after.json` pointing at the new one. [`upgrade.sh`](upgrade.sh) does the upgrade.

## Step 1: Create the database on the old tier

```bash
# from course10-module-06
../lab-sql.sh sqlserver master "IF DB_ID('learn_upgrade') IS NULL BEGIN CREATE DATABASE learn_upgrade; ALTER DATABASE learn_upgrade SET COMPATIBILITY_LEVEL = 100 END"
LEARN_CONTAINER=learn-postgres-12 ../lab-sql.sh postgres postgres "CREATE DATABASE learn_upgrade"
LEARN_CONTAINER=learn-mysql-57 ../lab-sql.sh mysql information_schema "CREATE DATABASE IF NOT EXISTS learn_upgrade"
LEARN_CONTAINER=learn-mariadb-102 ../lab-sql.sh mariadb information_schema "CREATE DATABASE IF NOT EXISTS learn_upgrade"
```

## Step 2: Deploy to the old tier

```bash
# from course10-module-06/<engine>
schemaquench --ConfigFile:quench.settings.before.json
echo "exit $?"
```

SQL Server, PostgreSQL and MariaDB create the table with every expression and exit `0`. MySQL 5.7 can't hold a check
constraint, so under the default `UnsupportedFeaturePolicy` it builds the rest, skips both checks by name and exits
`1` (Module 1):

```
Skipping check constraint (requires MySQL 8.0.16 - downgraded): Invoice.CK_Invoice_Amount
Skipping check constraint (requires MySQL 8.0.16 - downgraded): Invoice.CK_Invoice_Status
```

Each expression SchemaSmith applies is recorded in its `ExpressionMap` table (`SchemaSmith_ExpressionMap` on MySQL
and MariaDB): the text you declared, the text the engine stored, and the version (and on SQL Server the compatibility
level) it was stored under. That record is what lets the next deploy tell "you changed it" from "the engine renders it
differently".

## Step 3: Upgrade

```bash
# from course10-module-06
bash upgrade.sh <engine>
```

SQL Server raises the compatibility level in place. The other three dump `learn_upgrade` from the old container and
reload it into the current one, with the dump tools' default settings, which is how many teams move a database to a
new major version.

## Step 4: The first deploy after the upgrade

```bash
# from course10-module-06/<engine>
schemaquench --ConfigFile:quench.settings.after.json
echo "exit $?"
```

What happens depends on the engine, and each one teaches something.

**SQL Server: re-baselined, nothing touched.** An expression's stored text is frozen at the compatibility level it
was created under, so every expression on the table now reads as recorded under 100. Re-applying all of them would
be a far bigger event than the upgrade itself. SchemaSmith re-reads them and updates its record instead:

```
Re-baselined 5 recorded expression(s): they were recorded under a different SQL Server version or compatibility level, and their stored canonical text is now refreshed for version 16.0… at compatibility level 160. No object was changed.
```

**PostgreSQL: re-baselined, with one re-applied.** The dump wrote every expression out as text and PostgreSQL 16
parsed it back in, and one of them didn't come back the same. `status IN ('open','paid','void')` on a `varchar`
column is stored as `ANY ((ARRAY[…])::text[])` when you create it, but a reload of that stored form produces
`ANY (ARRAY[(…)::text, …])`. The live object moved, so SchemaSmith re-applies your declaration, then re-baselines the
rest:

```
Check Constraint public.invoice.ck_invoice_status modified or no longer in product
Add missing check constraint public.invoice.ck_invoice_status
Re-baselined 4 recorded expression(s): they were recorded under a different PostgreSQL version, …
```

Re-adding a check validates every row, so on a large table this one-time step takes a while. `pg_upgrade` restores
the schema the same way, so expect it after either.

**MySQL: what 5.7 skipped is built now.** The checks 5.7 couldn't hold get created on the first deploy that reaches
8.0, the generated column is re-baselined, and the run exits `0` instead of `1`:

```
Create check constraint: `Invoice`.`CK_Invoice_Status`
Create check constraint: `Invoice`.`CK_Invoice_Amount`
Re-baselined 1 recorded expression(s): they were recorded under a different server version, …
```

Nothing in the package changed. The skips were never a different package for the old server; they were the same
package, waiting for a server that could hold it.

### MariaDB: the helpers the dump left behind

MariaDB fails, with exit `2`:

```
Kindle stamp current (…) - skipping kindle
Error: PROCEDURE learn_upgrade.SchemaSmith_ParseTableJson does not exist
```

SchemaSmith installs its helper procedures in each database it deploys to and records a stamp so later deploys can
skip reinstalling them. `mysqldump` copies tables, including the stamp, but not stored procedures unless you pass
`--routines`. MariaDB 10.2 and 11.4 compute the same stamp, so the deploy believes the helpers are installed. (MySQL
5.7 and 8.0 compute different stamps, so MySQL reinstalled them and never noticed.) Tell it to reinstall:

<!-- TRAINING-RELEASE-PIN #608: this section teaches the failure a routine-less dump causes on MariaDB. When a release includes the #608 fix: re-run Steps 3 and 4 on MariaDB, and replace the section with what the first deploy now prints. -->
```bash
# from course10-module-06/mariadb
schemaquench --ConfigFile:quench.settings.after.json --ForceReKindle
echo "exit $?"
```

```
Re-baselined 3 recorded expression(s): they were recorded under a different server version, and their stored canonical text is now refreshed for 11.4…. No object was changed.
```

Exit `0`. Dump with `--routines` and this never happens; either way, a deploy that says a helper procedure doesn't
exist right after a restore wants `--ForceReKindle` once.

## Step 5: And the deploy after that

```bash
# from course10-module-06/<engine>
schemaquench --ConfigFile:quench.settings.after.json
echo "exit $?"
```

Exit `0`, nothing re-baselined, nothing applied, on every engine. The record is current, and the upgrade is done as
far as the package is concerned.

## Step 6: Clean up

```bash
# from course10-module-06
../lab-sql.sh sqlserver master "IF DB_ID('learn_upgrade') IS NOT NULL BEGIN ALTER DATABASE learn_upgrade SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE learn_upgrade END"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS learn_upgrade"
LEARN_CONTAINER=learn-postgres-12 ../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS learn_upgrade"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS learn_upgrade"
LEARN_CONTAINER=learn-mysql-57 ../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS learn_upgrade"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS learn_upgrade"
LEARN_CONTAINER=learn-mariadb-102 ../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS learn_upgrade"
```

## The principle

An upgrade changes what the engine says about your objects without you changing anything. SchemaSmith keeps a record
of what it applied and under which version, so the first deploy afterwards can tell the difference: it re-baselines
what only reads differently, re-applies what really moved, and builds what the old server was skipping. Read that
first deploy's log as the upgrade's receipt. And move a MySQL-family database with its routines, or kindle once
after.
