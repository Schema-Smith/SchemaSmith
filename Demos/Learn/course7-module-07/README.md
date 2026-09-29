# Course 7, Module 7 — Registry-driven fleet roster (lab)

Goal: source the fleet roster from a **control-plane registry table** — a database you own, not a naming
convention (Module 1) and not a config file (Module 2). The template's `DatabaseIdentificationScript`
queries a `Tenants` table; `IdentificationDatabase: "{{ControlDb}}"` points that query at whichever
registry database the environment's `ControlDb` token resolves to. Deactivate a tenant with an `UPDATE`
against the registry — no redeploy, no config change — and the very next run drops it from the roster.
One package, three registries (`Dev`, `Prod`, `Empty`), one token deciding which one a run reads. All four
engines.

This builds on Module 2's `{{...}}` token substitution and Module 1's fan-out — here the thing the token
points at is itself a table you query, not a literal value.

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005` on
  each engine).
- The registries exist — run [`./setup-registry.sh`](./setup-registry.sh) once. It creates
  `FleetRegistry_Dev`, `FleetRegistry_Prod`, and `FleetRegistry_Empty` (lowercase `fleetregistry_*` on
  PostgreSQL, MySQL, and MariaDB) on each engine and seeds each one's
  `Tenants` table (Dev and Prod list all five tenants — Dev has `005` inactive, Prod has it active; Empty
  gets the table with zero rows).
- The CLI is on your PATH — `schemaquench --version` answers **2.7.0** or later.

Each engine folder ships the same native `Shop` `Package/` — its `Template.json` now carries
`IdentificationDatabase: "{{ControlDb}}"` alongside the `DatabaseIdentificationScript` — plus three
settings files:

| Settings file | `ControlDb` token | Registry it reads |
| --- | --- | --- |
| `quench.settings.json` | *(package default)* `FleetRegistry_Dev` | Dev roster — four tenants (`005` inactive). |
| `quench.settings.prod.json` | `FleetRegistry_Prod` | Prod roster — all five tenants. |
| `quench.settings.empty.json` | `FleetRegistry_Empty` | Empty registry — zero rows. |

## Step 1: Deploy against the dev registry

Nothing in `quench.settings.json` overrides `ControlDb`, so the token resolves from the package's own
default (`Product.json`) — the roster still comes from the registry table, not a literal list:

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.json
```

```
Version: 2.7.0.0
  Product Script Tokens:
    ControlDb: FleetRegistry_Dev
```

Four tenants dispatched — `fleet_tenant_005` is absent, because its `FleetRegistry_Dev` row is
`Active = 0`:

```
Template 'Main': 4 units - db: DatabaseIdentificationScript; schema: (regular template)
```

Each tenant then: `Kindling the forge` → tables quenched → `Successfully Quenched`; the run ends
`Completed quench of Shop`. Re-run the same command and it comes back a clean no-op — same four tenants
dispatched, no `Adding`/`Creating` lines, all four `Successfully Quenched` again. The registry read is
idempotent exactly like discovery and config-driven rosters are.

## Step 2: One package, many environments — the prod roster

The package never changes. Point `--ConfigFile` at the prod settings instead, and the `{{ControlDb}}`
token re-targets the very same `IdentificationDatabase` query at `FleetRegistry_Prod` — where `005` is
active:

```bash
schemaquench --ConfigFile:quench.settings.prod.json --PreviewTargets
```

```
Template: Main [required]
  db: fleet_tenant_001
  db: fleet_tenant_002
  db: fleet_tenant_003
  db: fleet_tenant_004
  db: fleet_tenant_005
```

Five tenants this time — nothing in the package moved. Only the token's resolved value changed, and that
changed which registry database the identification query ran against.

## Step 3: Deactivate a tenant — a registry row, not a redeploy

Flip one row in the registry, no config or package change at all:

```sql
UPDATE dbo.Tenants SET Active = 0 WHERE DbName = 'fleet_tenant_004'
```

Re-run the dev deploy and the roster shrinks on its own:

```bash
schemaquench --ConfigFile:quench.settings.json
```

```
Template 'Main': 3 units - db: DatabaseIdentificationScript; schema: (regular template)
```

**Four units became three** — `fleet_tenant_004` dropped off with no `TemplateTargets` edit and no
settings-file edit, just an `UPDATE` against the control plane. The count in the rollup is the roster;
which tenants it resolved to is one entry per target in the deployment summary report. (Restore it to `Active = 1` afterward to get back to the four-tenant baseline.)
The deactivation statement's shape differs slightly by engine:

| Engine | Statement |
| --- | --- |
| SQL Server / MySQL / MariaDB | `UPDATE Tenants SET Active = 0 WHERE DbName = 'fleet_tenant_004'` (SQL Server: `dbo.Tenants`) |
| PostgreSQL | `UPDATE public.tenants SET active = false WHERE dbname = 'fleet_tenant_004'` |

## Step 4: Empty registry — the loud refusal

The `Empty` registry has the `Tenants` table but zero rows. Point at it and the run refuses before
touching anything, rather than silently deploying to nothing:

```bash
schemaquench --ConfigFile:quench.settings.empty.json
```

```
No database targets discovered for template 'Main' (RequireAtLeastOneTarget: true)
```

Exit code `2`. `RequireAtLeastOneTarget` is what turns "the registry happens to be empty" into a build-stopping
error instead of a quiet no-op — the same protection Module 1's catalog discovery gives you, now guarding
a registry query too.

## Step 5: The PostgreSQL "only way"

`IdentificationDatabase` isn't just a convenience on PostgreSQL — it's the *only* way to read a registry
table at enumeration time, because a PostgreSQL connection is bound to a single database and can't
cross-database-query. Blank it out in `postgres/Package/Templates/Main/Template.json` (`""` instead of
`"{{ControlDb}}"`) and the enumeration query runs against whatever database the connection is already on
— the sandbox's init database, `postgres` — where the registry table doesn't exist:

```bash
schemaquench --ConfigFile:quench.settings.json --PreviewTargets
```

```
Pre-flight diagnostics for Shop (--PreviewTargets)
Locate Databases To Quench (localhost)
[localhost] Database enumeration FAILED for template 'Main': 42P01: relation "public.tenants" does not exist
  ERROR: matched 0 targets for required template 'Main' - no databases or schemas were discovered
```

Restore `IdentificationDatabase: "{{ControlDb}}"` and the same preview resolves the roster again from
`fleetregistry_dev`. On SQL Server, MySQL, and MariaDB you *could* in principle write a
`DatabaseIdentificationScript` that cross-database-queries the registry directly (three-part naming) and
skip `IdentificationDatabase` — but on PostgreSQL, `IdentificationDatabase` is the only door in.

## Step 6: Do it on PostgreSQL, MySQL, and MariaDB

Same six steps in `postgres/`, `mysql/`, and `mariadb/` — host prefix `[localhost]` with no port (SQL
Server is the only engine that logs a port, `[localhost,11433]`). MySQL runs on `13306`, MariaDB on
`13307` (see each folder's `quench.settings.json`); on both, schema and database are the same thing, so
`Tenants` lives directly in the registry database, same as SQL Server's `dbo.Tenants`. PostgreSQL's query
is lowercase and boolean (`SELECT dbname FROM public.tenants WHERE active = true`); MySQL/MariaDB use
`SELECT DbName FROM Tenants WHERE Active = 1`.

The sandbox's MySQL still has a leftover `fleet_tenant_006` database from an earlier module — but it's not
a row in `FleetRegistry_Dev`, so it never appears in the roster. That's the registry-driven proof in one
sentence: the registry decides, not the catalog.

## Cleanup

Reset the three registries back to their seeded baseline:

```bash
./setup-registry.sh --reset
```

Or drop them by hand on any one engine:

```bash
../lab-sql.sh sqlserver master "DROP DATABASE FleetRegistry_Dev"
../lab-sql.sh sqlserver master "DROP DATABASE FleetRegistry_Prod"
../lab-sql.sh sqlserver master "DROP DATABASE FleetRegistry_Empty"
```

## The principle

Module 1 let the catalog name the fleet; Module 2 let config name it. Module 7 puts the roster in a table
you own and query — the control plane. Deactivating a tenant is now an `UPDATE`, not a deploy: the next
run just reads the registry and reacts. And on PostgreSQL specifically, `IdentificationDatabase` isn't
optional polish — it's the only mechanism that lets a single-database connection read a roster that lives
somewhere else.
