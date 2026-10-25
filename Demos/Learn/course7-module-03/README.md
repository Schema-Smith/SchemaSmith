# Course 7, Module 3 — Provisioning at onboarding (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (#539 skipped tenant row, #550 WhatIf onboarding): delete the "On 2.7.x" callouts -->

Goal: onboard a brand-new tenant in a **single deploy**. In Module 1 you ran `CREATE DATABASE` by hand
and let discovery find it. Here the config roster names a tenant that *doesn't exist yet* —
`fleet_tenant_006` — and `CreateIfMissing: true` makes SchemaSmith stand the database up itself, on the
admin connection, then forge the `Shop` schema into it. Safe by default, dry-runnable, idempotent, and
permission-gated. All four engines.

This builds on Module 2 — `CreateIfMissing` is a flag on the same `TemplateTargets` roster. Do Modules 1
and 2 first.

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005`).
- `fleet_tenant_006` does **not** exist yet (that's the point). If a Module 1 run left it, drop it first
  (each line is safe when it's already gone):

  ```bash
  # from course7-module-03
  ../lab-sql.sh sqlserver master "IF DB_ID('fleet_tenant_006') IS NOT NULL BEGIN ALTER DATABASE [fleet_tenant_006] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [fleet_tenant_006] END"
  ../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS fleet_tenant_006"
  ../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
  ../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
  ```
- The CLI is on your PATH — `schemaquench --version` answers **2.7.0** or later.

Each engine folder ships the same native `Shop` `Package/` as Modules 1–2, plus three settings files —
all naming a roster of one existing tenant (`001`) and one that doesn't exist yet (`006`):

| Settings file | `CreateIfMissing` | `WhatIfONLY` | What it does |
| --- | --- | --- | --- |
| `quench.settings.skip.json` | *(absent → false)* | false | Deploys `001`; **skips** the missing `006`. |
| `quench.settings.whatif.json` | true | true | Dry-run — shows what it *would* create. |
| `quench.settings.onboard.json` | true | false | Provisions `006` and forges `Shop` in. |

## Step 1: Safe by default — a missing tenant is skipped, not created

Point at the `skip` roster. `006` doesn't exist and `CreateIfMissing` is absent, so SchemaSmith leaves it
alone:

```bash
# from course7-module-03/<engine>
schemaquench --ConfigFile:quench.settings.skip.json
```

```
[localhost,11433] Database 'fleet_tenant_006' does not exist and TemplateTargets CreateIfMissing is false - skipping all iterations for this server-database pair.
```

`001` deploys; `006` is skipped with that line and **not created**. The default never conjures a database
you didn't ask for. (On PostgreSQL, MySQL, and MariaDB the line is identical but for the host prefix —
`[localhost]` without a port.)

The deployment summary keeps the skip on the record, so a report built from it still lists the tenant you
asked for. `SchemaQuench - Summary.md` in the log folder:

```
## Targets (2)
- localhost,11433 / fleet_tenant_001 — Main — Success — …ms
- localhost,11433 / fleet_tenant_006 — Main — Skipped — 0ms
```

`Summary.json` carries the same row in `targets[]`, with `"outcome": "Skipped"`.

> **On 2.7.x** the summary lists only `fleet_tenant_001`: a skipped tenant leaves no row (#539).

## Step 2: Dry-run before you let it create anything

Turn `CreateIfMissing` on, but run under WhatIf first. SchemaSmith tells you exactly what it *would*
provision — and touches nothing:

```bash
# from course7-module-03/<engine>
schemaquench --ConfigFile:quench.settings.whatif.json
```

```
  [WhatIf] Would create database [fleet_tenant_006] (CreateIfMissing: true)
[localhost,11433].[fleet_tenant_006] [WhatIf] Would deploy template 'Main' into the new, empty database 'fleet_tenant_006': every object it declares would be created.
```

Exit `0`. Check the catalog after — `006` still doesn't exist. That's your look-before-you-leap on provisioning.

> **On 2.7.x** the preview reports the creation, then tries to open the database that doesn't exist yet: the
> `fleet_tenant_006` unit fails and the run exits `2` (#550).

## Step 3: Onboard — provision and deploy in one run

Now the real thing. `quench.settings.onboard.json` carries the roster plus `CreateIfMissing: true`:

```json
"TemplateTargets": {
  "Main": { "Databases": [ "fleet_tenant_001", "fleet_tenant_006" ], "CreateIfMissing": true }
}
```

```bash
# from course7-module-03/<engine>
schemaquench --ConfigFile:quench.settings.onboard.json
```

```
  Creating database [fleet_tenant_006] (CreateIfMissing: true)
```

SchemaSmith creates `fleet_tenant_006` on the admin connection, kindles the forge in it, and strikes the
whole `Shop` schema — four tables — into the fresh database. `001` is a no-op. Confirm — the line for your
engine reports `4`:

```bash
# from course7-module-03
../lab-sql.sh sqlserver fleet_tenant_006 "SELECT COUNT(*) FROM sys.tables WHERE name IN ('Customer','Product','SalesOrder','OrderItem')"
../lab-sql.sh postgres fleet_tenant_006 "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('customer','product','salesorder','orderitem')"
../lab-sql.sh mysql fleet_tenant_006 "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('Customer','Product','SalesOrder','OrderItem')"
../lab-sql.sh mariadb fleet_tenant_006 "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('Customer','Product','SalesOrder','OrderItem')"
```

Onboarding a tenant went from "run this DDL, then run the deploy" down to one deploy. Add the tenant to
the roster, quench, done.

## Step 4: Idempotent — re-run changes nothing

```bash
# from course7-module-03/<engine>
schemaquench --ConfigFile:quench.settings.onboard.json
```

No `Creating database` line this time — `006` already exists and is already in shape, so the whole roster
comes back a no-op alongside `001`. SchemaSmith's create is `IF NOT EXISTS` under the hood (a real
existence check on PostgreSQL), so a tenant that already exists is left exactly as-is. `CreateIfMissing`
is safe to leave on permanently — it only ever fills gaps.

## Step 5: Provisioning is a privileged act

Creating a database is a high-privilege operation, and SchemaSmith says so plainly when the deploy account
can't. Run the onboard as a principal that can *see* the fleet but lacks the create right, and the run
fails fast with an actionable message — it does not silently skip.

Set it up. First drop `fleet_tenant_006` again, so there is something to provision, then create a
`fleet_reader` principal that can read the catalog but not create databases (the lines for your engine):

```bash
# from course7-module-03
../lab-sql.sh sqlserver master "IF DB_ID('fleet_tenant_006') IS NOT NULL BEGIN ALTER DATABASE [fleet_tenant_006] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [fleet_tenant_006] END"
../lab-sql.sh sqlserver master "IF SUSER_ID('fleet_reader') IS NULL CREATE LOGIN fleet_reader WITH PASSWORD = 'FleetReader_2026', CHECK_POLICY = OFF"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh postgres postgres "CREATE ROLE fleet_reader LOGIN PASSWORD 'FleetReader_2026' NOCREATEDB"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh mysql information_schema "CREATE USER IF NOT EXISTS 'fleet_reader'@'%' IDENTIFIED BY 'FleetReader_2026'; GRANT SELECT ON *.* TO 'fleet_reader'@'%'"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh mariadb information_schema "CREATE USER IF NOT EXISTS 'fleet_reader'@'%' IDENTIFIED BY 'FleetReader_2026'; GRANT SELECT ON *.* TO 'fleet_reader'@'%'"
```

Then point the onboard at it: in your engine's `quench.settings.onboard.json`, set `Target.User` to
`fleet_reader` and `Target.Password` to `FleetReader_2026`, and run the onboard again:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.User '"fleet_reader"' && python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.Password '"FleetReader_2026"' -->
```json
"User": "fleet_reader",
"Password": "FleetReader_2026",
```

```bash
# from course7-module-03/<engine>
schemaquench --ConfigFile:quench.settings.onboard.json
```

```
[localhost,11433] Database provisioning FAILED for 'fleet_tenant_006' (template 'Main'): Failed to provision database 'fleet_tenant_006'. The connecting account must have CREATE DATABASE permission on the admin database (master). Underlying error: CREATE DATABASE permission denied in database 'master'.
```

The run exits `2`, and `fleet_tenant_001` fails as well: this principal can read the catalog but cannot deploy into any tenant. The provisioning line above is the one this step is about.

Put `Target.User` and `Target.Password` back to the admin account the file shipped with (`sa` /
`postgres` / `root`, password `Learn!Passw0rd`) before you go on.

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.User '"sa"' && python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.Password '"Learn!Passw0rd"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.User '"postgres"' && python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.Password '"Learn!Passw0rd"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.User '"root"' && python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.Password '"Learn!Passw0rd"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.User '"root"' && python -I "$WALK_TOOLS/jedit.py" "<engine>/quench.settings.onboard.json" set Target.Password '"Learn!Passw0rd"' -->

The account that onboards tenants needs the create right on the admin database. What that grant is called
differs by engine:

| Engine | Admin database | Required privilege | Underlying error you'll see |
| --- | --- | --- | --- |
| SQL Server | `master` | `CREATE DATABASE` (or `dbcreator`) | `CREATE DATABASE permission denied in database 'master'` |
| PostgreSQL | `postgres` | `CREATEDB` | `42501: permission denied to create database` |
| MySQL | `information_schema` | `CREATE` | `Access denied … to database 'fleet_tenant_006'` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native
> package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics
> (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for
> you.*

The message shape is identical on all four engines — only the admin-database name and the underlying
error differ (the last two columns above, plus MariaDB which mirrors MySQL's shape). On PostgreSQL
you'll see `… admin database (postgres). Underlying error: 42501: permission denied to create database`;
on MySQL and MariaDB `… admin database (information_schema). Underlying error: Access denied … to
database 'fleet_tenant_006'`.

A reader/writer principal without the create right lands here — with a message that names the database and
the missing privilege, not a mystery failure. The deploy account needs rights for *every* action the tool
takes, reads included: it validates the server by querying the catalog before it provisions anything, so
an account too restricted to even read the fleet fails at that earlier gate. The principal shown here can
read the fleet but can't create — the realistic reader-without-`CREATE` case.

## Step 6: Do it on PostgreSQL, MySQL, and MariaDB

Same five steps from `postgres/`, `mysql/`, and `mariadb/` — every command above carries a line per
engine or runs from your engine's folder. The `CreateIfMissing` config is identical on
all four engines; only the create DDL SchemaSmith emits differs (`[fleet_tenant_006]` on SQL Server,
`"fleet_tenant_006"` on PostgreSQL, `` `fleet_tenant_006` `` on MySQL and MariaDB). Each one skips by
default, dry-runs, provisions + forges in one run, re-runs clean, and fails fast without the create
grant.

## Cleanup

Drop `fleet_tenant_006` (if it exists) and the `fleet_reader` principal on each engine to return to the
five-tenant baseline:

```bash
# from course7-module-03
../lab-sql.sh sqlserver master "IF DB_ID('fleet_tenant_006') IS NOT NULL BEGIN ALTER DATABASE [fleet_tenant_006] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [fleet_tenant_006] END"
../lab-sql.sh sqlserver master "IF SUSER_ID('fleet_reader') IS NOT NULL DROP LOGIN fleet_reader"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh postgres postgres "DROP ROLE IF EXISTS fleet_reader"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh mysql information_schema "DROP USER IF EXISTS 'fleet_reader'@'%'"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS fleet_tenant_006"
../lab-sql.sh mariadb information_schema "DROP USER IF EXISTS 'fleet_reader'@'%'"
```

## The principle

Module 1 discovered the fleet; Module 2 let config steer it; Module 3 lets config *grow* it. Onboarding a
tenant is no longer a database task plus a deploy task — it's one line in a roster and one run, with a dry
run when you want it and a clear stop when the account isn't allowed. That's the fleet operating itself.

Next: **Module 4** — running a thousand-tenant deploy safely: preview the whole roster, tune how hard it
hits, and keep going when a single tenant misbehaves.
