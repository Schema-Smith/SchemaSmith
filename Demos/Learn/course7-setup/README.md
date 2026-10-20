# Course 7 — Database Setup

These scripts stand up the **tenant fleet** that the Course 7 labs deploy into: five **empty**
databases per engine — `fleet_tenant_001` through `fleet_tenant_005` — on SQL Server, PostgreSQL,
MySQL, and MariaDB (20 databases in all).

Nothing is seeded into them. The Module 1 deploy is what forges the `Shop` schema (`Customer`,
`Product`, `SalesOrder`, `OrderItem`) into every tenant in a single fan-out run — that is the whole
point of the module.

The `fleet_tenant_` prefix is deliberately distinct from Course 6's `shop_tenant_a/b/c`, which share
the same sandbox. Course 7 discovers its fleet with a catalog query on that prefix, so keeping the
names apart means the fan-out never accidentally reaches into another course's databases.

## Prerequisite

The shared sandbox must be running. See [`Demos/Learn/README.md`](../README.md) for how to start it
and verify it is healthy before continuing.

## Run the setup

**macOS / Linux**

```bash
# from course7-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
# from course7-setup
.\setup-databases.ps1
```

Both scripts print `PASS` or `FAIL` per engine (`PASS` only after all five tenant databases are
confirmed to exist), and finish with a one-line summary. Re-running is safe — every `CREATE` is
guarded, so setup is idempotent.

## What each script does

The setup scripts create each tenant through the shared lab connection helper
([`Demos/Learn/lab-sql.ps1`](../lab-sql.ps1) / [`lab-sql.sh`](../lab-sql.sh)), which issues the same
guarded `CREATE DATABASE` per tenant and also stamps it as lab-provisioned — that stamp is what lets
`-Reset` safely drop and recreate the fleet later without ever touching a same-named database it
didn't create. Each engine's dialect differs (PostgreSQL, for instance, has no `IF NOT EXISTS` for
databases), and the helper handles that for you.

## Verify by hand

Each engine's catalog is the fleet roster — the same query Module 1's `DatabaseIdentificationScript`
runs. To confirm the five tenants are present:

```bash
# from course7-setup
# SQL Server
../lab-sql.sh sqlserver master "SELECT name FROM sys.databases WHERE name LIKE 'fleet[_]tenant[_]%' ORDER BY name"

# PostgreSQL
../lab-sql.sh postgres postgres "SELECT datname FROM pg_database WHERE datname LIKE 'fleet\_tenant\_%' ORDER BY datname"

# MySQL
../lab-sql.sh mysql information_schema "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'fleet\_tenant\_%' ORDER BY schema_name"

# MariaDB
../lab-sql.sh mariadb information_schema "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'fleet\_tenant\_%' ORDER BY schema_name"
```

Onboarding a sixth tenant later (Module 1 demonstrates this) is just another `CREATE DATABASE
fleet_tenant_006` — the next deploy discovers it automatically.

## Starting over: `-Reset`

The Module 1 deploy forges the Shop schema into every tenant, so re-running plain setup afterwards
makes no changes — the guarded `CREATE DATABASE` statements are no-ops on databases that already
exist. To return the fleet to its original empty state — for example, after experimenting with a
fan-out deploy — reset it:

```bash
# from course7-setup
bash setup-databases.sh --reset
```

```powershell
# from course7-setup
.\setup-databases.ps1 -Reset
```

Each engine's five tenant databases are dropped and recreated empty, reported as
`PASS (reset, 5 tenant databases)`. **Only databases these scripts created are ever dropped.** On
your own server, a database carrying one of these names that the labs didn't create is refused and
left untouched — you'll be told to rename or move it. Nothing of yours is at risk.
