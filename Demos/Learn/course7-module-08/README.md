# Course 7, Module 8 — The servers you don't write to (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (#596 secondaries on host,port; #597 a Secondary folder with no secondaries is skipped; the cold read-only refusal exits 2): delete the "merged, not yet released" note -->

Goal: a fleet has targets you must not write to the way you write to the rest. A read-only copy of a tenant should be
**skipped**, with a line saying so, not counted as a failure. A replica should be **read from** without SchemaSmith trying
to install its helpers on it. And a server that sits beside the primary, an Availability Group secondary, needs some
scripts and not others. This module covers all three.

Say plainly what the lab emulates. A database set `READ_ONLY` stands in for a read-only replica, and a second,
separate SQL Server stands in for a secondary. Neither is an Availability Group: nothing fails over and nothing
replicates. What the lab proves is the part SchemaSmith owns: which targets a run touches, and what it does with each.

`sqlserver/` for all three parts, `postgres/` for the read-only part too.

> **Merged, not yet released.** This module teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up. Part 3 also needs the `edition-tier` profile's second SQL Server (`localhost,11435`):

```bash
# from docker
docker compose --profile edition-tier up -d sqlserver-express
```

- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.

Create two tenants on each engine, one of which will become read-only:

```bash
# from course7-module-08
bash -c '. ../lab-sql.sh && for d in tenant_rw tenant_ro; do lab_confirm_db sqlserver $d; done'
bash -c '. ../lab-sql.sh && for d in tenant_rw tenant_ro; do lab_confirm_db postgres $d; done'
```

## Part 1: A read-only tenant is skipped, not failed

[`readonly/Templates/Main/Template.json`](sqlserver/readonly/Templates/Main/Template.json) finds both tenants and
says what to do with a read-only one:

```json
"DatabaseIdentificationScript": "SELECT [name] FROM sys.databases WHERE [name] IN ('tenant_rw', 'tenant_ro')",
"SkipIfReadOnly": true
```

Deploy while both are writable:

```bash
# from course7-module-08/sqlserver
schemaquench --ConfigFile:quench.settings.readonly.json        # exit 0
```

Now make `tenant_ro` read-only, the way a reporting copy is, and add a column to the package:

```bash
# from course7-module-08
../lab-sql.sh sqlserver master "ALTER DATABASE tenant_ro SET READ_ONLY WITH ROLLBACK IMMEDIATE"
```

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/readonly/Templates/Main/Tables/dbo.Note.json" append Columns '{"Name": "[Author]", "DataType": "NVARCHAR(50)", "Nullable": true}' -->
```json
{ "Name": "[Author]", "DataType": "NVARCHAR(50)", "Nullable": true }
```

```bash
# from course7-module-08/sqlserver
schemaquench --ConfigFile:quench.settings.readonly.json        # exit 0
```

```
[localhost,11433].[tenant_ro] is read-only; skipping template 'Main' (SkipIfReadOnly)
...
[localhost,11433].[tenant_rw]         Adding 1 new columns to [dbo].[Note]
```

`tenant_rw` gets the column. `tenant_ro` is skipped by name, and the summary lists it as `Skipped`, not failed: a
read-only copy was never a target for writes. Turn the flag off and see what it's protecting you from:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/readonly/Templates/Main/Template.json" set SkipIfReadOnly false -->
```bash
# from course7-module-08/sqlserver
schemaquench --ConfigFile:quench.settings.readonly.json        # exit 2
#   Failed to update database "tenant_ro" because the database is read-only.
```

The whole run fails over a database nobody meant to change. Put the flag back:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/readonly/Templates/Main/Template.json" set SkipIfReadOnly true -->

**PostgreSQL** reports a database as read-only when its sessions start read-only (`default_transaction_read_only`), and
the same flag skips it:

```bash
# from course7-module-08/postgres
schemaquench --ConfigFile:quench.settings.readonly.json        # exit 0
```

```bash
# from course7-module-08
../lab-sql.sh postgres learn "ALTER DATABASE tenant_ro SET default_transaction_read_only = on"
```

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/readonly/Templates/Main/Tables/public.note.json" append Columns '{"Name": "author", "DataType": "varchar(50)", "Nullable": true}' -->
```bash
# from course7-module-08/postgres
schemaquench --ConfigFile:quench.settings.readonly.json        # exit 0
#   [localhost].[tenant_ro] is read-only; skipping template 'Main' (SkipIfReadOnly)
```

## Part 2: Read from a replica, never install on it

Extraction kindles the forge too: it needs SchemaSmith's helper objects in the database it reads. A read-only database
can't take them. `tenant_ro` was kindled in Part 1, while it was still writable, so extracting from it works:

```bash
# from course7-module-08/sqlserver
schematongs --ConfigFile:SchemaTongs.readonly.json        # exit 0
#   Kindle stamp current (…) - skipping kindle
```

Now a read-only database nobody ever deployed to:

```bash
# from course7-module-08
bash -c '. ../lab-sql.sh && lab_confirm_db sqlserver tenant_cold'
../lab-sql.sh sqlserver master "ALTER DATABASE tenant_cold SET READ_ONLY WITH ROLLBACK IMMEDIATE"
```

```bash
# from course7-module-08/sqlserver
schematongs --ConfigFile:SchemaTongs.cold.json        # exit 2
```

```
FAILED: The SchemaSmith helper objects are not present on this read-only database, and they cannot be created here. Kindle on the primary (any deploy or extraction against a writable copy does it) and let the change reach this replica, then re-run. SchemaSmith does not attempt the install because a read-only target rejects every write it would need.
```

The tool refuses before it writes anything. On a real replica, the fix is to kindle the primary and let the helpers
replicate. It verifies the helpers on the replica and never installs them there. Exit 2 is the code for a refused
target, the same one a deploy gives.

## Part 3: Scripts for the primary, the secondaries, or both

[`routing/Product.json`](sqlserver/routing/Product.json) has three product-level folders, one per `ServerToQuench`.
Each folder's script writes its own name into `ag_lab.dbo.RunLog` on whichever server runs it:

```json
"ScriptFolders": [
  { "FolderPath": "Both", "QuenchSlot": "After", "ServerToQuench": "Both" },
  { "FolderPath": "Primary", "QuenchSlot": "After", "ServerToQuench": "Primary" },
  { "FolderPath": "Secondary", "QuenchSlot": "After", "ServerToQuench": "Secondary" }
]
```

Create `ag_lab` and its log table on both servers:

```bash
# from course7-module-08
bash -c '. ../lab-sql.sh && lab_confirm_db sqlserver ag_lab'
LEARN_CONTAINER=learn-sqlserver-express bash -c '. ../lab-sql.sh && lab_confirm_db sqlserver ag_lab'
../lab-sql.sh sqlserver ag_lab "IF OBJECT_ID('dbo.RunLog') IS NULL CREATE TABLE dbo.RunLog (Folder sysname NOT NULL, ServerName sysname NOT NULL)"
LEARN_CONTAINER=learn-sqlserver-express ../lab-sql.sh sqlserver ag_lab "IF OBJECT_ID('dbo.RunLog') IS NULL CREATE TABLE dbo.RunLog (Folder sysname NOT NULL, ServerName sysname NOT NULL)"
```

First with no secondaries configured:

```bash
# from course7-module-08/sqlserver
schemaquench --ConfigFile:quench.settings.routing-alone.json        # exit 0
```

```
  Skipping folder 'Secondary' - ServerToQuench is Secondary and no SecondaryServers are configured
```

```bash
# from course7-module-08
../lab-sql.sh sqlserver ag_lab "SELECT Folder FROM dbo.RunLog ORDER BY Folder"
# → Both
# → Primary
```

A `Secondary` folder with nowhere to go runs nowhere, and says why. Now name the second server as a secondary.
[`quench.settings.routing.json`](sqlserver/quench.settings.routing.json) gives it with its port:

```json
"SecondaryServers": ["localhost,11435"]
```

```bash
# from course7-module-08
../lab-sql.sh sqlserver ag_lab "DELETE dbo.RunLog"
```

```bash
# from course7-module-08/sqlserver
schemaquench --ConfigFile:quench.settings.routing.json        # exit 0
```

```
[localhost,11433].[master]    Quenching .\routing\Both\record.sql
[localhost,11435].[master]    Quenching .\routing\Both\record.sql
[localhost,11433].[master]    Quenching .\routing\Primary\record.sql
[localhost,11435].[master]    Quenching .\routing\Secondary\record.sql
```

The log says where each script ran, and each server's table agrees:

```bash
# from course7-module-08
../lab-sql.sh sqlserver ag_lab "SELECT Folder FROM dbo.RunLog ORDER BY Folder"
# → Both
# → Primary
LEARN_CONTAINER=learn-sqlserver-express ../lab-sql.sh sqlserver ag_lab "SELECT Folder FROM dbo.RunLog ORDER BY Folder"
# → Both
# → Secondary
```

On a real Availability Group this is how you build what a readable secondary needs locally, such as a cache table or
a login mapped to its own SID, without touching the primary, and how you keep primary-only work (linked servers,
jobs) off the secondaries. The template, where tables live, ran on the primary only: a secondary's databases get their
schema from the Availability Group, not from the deploy.

## What you proved

- `SkipIfReadOnly` skips a read-only database by name, on SQL Server and PostgreSQL, and the run still succeeds.
- Extraction from a read-only database verifies SchemaSmith's helpers and refuses to install them.
- `ServerToQuench` routes product-level scripts to the primary, the secondaries or both, and with no secondaries a
  `Secondary` folder is skipped and logged.

## Clean up

```bash
# from course7-module-08
rm -rf sqlserver/extracted
bash -c '. ../lab-sql.sh && for d in tenant_rw tenant_ro tenant_cold ag_lab; do lab_remove_db sqlserver $d; done'
LEARN_CONTAINER=learn-sqlserver-express bash -c '. ../lab-sql.sh && lab_remove_db sqlserver ag_lab'
bash -c '. ../lab-sql.sh && for d in tenant_rw tenant_ro; do lab_remove_db postgres $d; done'
```

```bash
# from docker
docker compose rm -sfv sqlserver-express
```

Put the packages back with `git checkout -- .` (from this folder) if you want to run the module again.
