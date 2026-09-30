# Course 7, Module 1 — Database-per-tenant fan-out (lab)

Goal: deploy one product schema — the four-table `Shop` — across a **fleet** of tenant databases in a
single run. You won't name the databases in the package. Instead the template's
`DatabaseIdentificationScript` asks each engine's catalog *"who's in the fleet?"* and SchemaSmith forges
the schema into every tenant it finds. Then you'll onboard a brand-new tenant without touching the
package at all. All four engines.

This is the **database axis** of fan-out — many databases of the same engine. (Course 2, Module 2
covered the *schema* axis — many schemas inside one database. Different axis; don't confuse them.)

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005`,
  **empty**, on each engine).
- The CLI is on your PATH — `schemaquench --version` answers **2.7.0** or later. New to the CLI?
  Course 1, Module 1 walks the install.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships a `quench.settings.json`
(the deploy config) and the `Package/` — the same `Shop` package, four native forms.

## Step 1: See who's in the fleet

The whole module turns on one line in `sqlserver/Package/Templates/Main/Template.json`:

```json
"DatabaseIdentificationScript": "SELECT [Name] FROM sys.databases WHERE [Name] LIKE 'fleet[_]tenant[_]%'"
```

That query — run against the server's catalog — *is* the roster. Preview it without deploying anything:

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.json --PreviewTargets
```

```
Locate Databases To Quench (localhost,11433)
Template: Main [required]
  db: fleet_tenant_001
  db: fleet_tenant_002
  db: fleet_tenant_003
  db: fleet_tenant_004
  db: fleet_tenant_005
```

Five tenants, discovered live. No database name appears anywhere in the package.

## Step 2: Deploy — one run, the whole fleet

```bash
schemaquench --ConfigFile:quench.settings.json
```

One work unit per tenant, dispatched together — and the log says so in a single line:

```
Template 'Main': 5 units - db: DatabaseIdentificationScript; schema: (regular template)
```

Five units, one line. The count is the fleet, and the two `source` facts say where each axis of every
target came from: the databases from your `DatabaseIdentificationScript`, the schema from the template
itself. **The per-target detail is not gone, it moved** — each tenant gets its own entry in the deployment
summary report, carrying `databaseSource` and `schemaSource`, which is the surface that keeps one row per
unit. [Module 6](../course7-module-06) reads that report. At five tenants a line each would have been
fine; at the thousand this feature exists for it was three thousand lines of audit data in a progress
stream, which is why it lives in the report now.

Confirm the `Shop` schema landed in every tenant:

```bash
cd ..            # back to the lab folder
for n in 001 002 003 004 005; do
  ../lab-sql.sh sqlserver fleet_tenant_$n "SELECT COUNT(*) FROM sys.tables WHERE name IN ('Customer','Product','SalesOrder','OrderItem')"
done
# → each tenant reports 4
cd sqlserver     # back into the engine folder
```

## Step 3: Prove idempotence — run it again

```bash
schemaquench --ConfigFile:quench.settings.json
```

Nothing changes. SchemaSmith reconciles each tenant to the declared shape; a fleet already in shape is a
five-way no-op. Re-running the whole fleet is always safe.

## Step 4: Onboard a new tenant — no package change

A new customer signs up. Stand up their database:

```bash
cd ..            # back to the lab folder
../lab-sql.sh sqlserver master "CREATE DATABASE [fleet_tenant_006]"
cd sqlserver     # back into the engine folder
```

Deploy again — you edit nothing:

```bash
schemaquench --ConfigFile:quench.settings.json
```

Discovery now returns **six**. Only `fleet_tenant_006` is forged; `001`–`005` are no-ops. The roster is
the catalog, so onboarding is a `CREATE DATABASE`, not a package edit. (Clean up with
`DROP DATABASE [fleet_tenant_006]` when you're done, to return to the five-tenant baseline.)

## Step 5: Do it on PostgreSQL, MySQL, and MariaDB

Same five steps in `postgres/`, `mysql/`, and `mariadb/`. The package is native per engine, and only the
catalog the `DatabaseIdentificationScript` reads differs:

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Catalog | `sys.databases` | `pg_database` | `information_schema.schemata` |
| Discovery predicate | `[Name] LIKE 'fleet[_]tenant[_]%'` | `datname LIKE 'fleet\_tenant\_%'` | `SCHEMA_NAME LIKE 'fleet\_tenant\_%'` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native
> package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics
> (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for
> you.*

Each one fans the `Shop` schema across all five tenants, re-runs to a clean no-op, and onboards a sixth
the moment its database exists.

## The principle

At scale, you don't deploy *a* database — you deploy *a roster*. The package is canonical and unchanging;
the fleet it reaches is whatever the catalog reports at run time. Adding a tenant is a data change, not a
release. That's the shift from authoring a schema to operating a fleet.

Next: **Module 2** hands the roster to your *deployment system* instead of the catalog — when the list of
tenants to touch should come from config (and a subset), not every database that happens to match a name.
