# Course 7, Module 9 — One product, two templates: the registry and the fleet it lists (lab)

Goal: ship the control plane **with** the fleet. In Module 7 the registry was a database someone else built and
filled. Here one product carries both: a `Registry` template that creates the registry database, its table and its
rows, and a `Tenant` template that reads that table to find the fleet. `TemplateOrder` runs them in that order, so
a tenant you add to the registry's content file is deployed by the **same** run that adds it. Each tenant is
stamped with the release it received. All four engines.

This builds on Module 3 (`CreateIfMissing`), Module 7 (`IdentificationDatabase` and a registry roster), and
Course 2 Module 5 (data delivery).

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists: run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005` on each engine).
  To start this lab from a clean fleet, run it with `--reset`.
- The CLI is on your PATH (`schemaquench --version` answers **2.8.0** or later).

## Step 1: Look at the product

```bash
# from course7-module-09/<engine>
cat Package/Product.json
```

```text
"TemplateOrder": [ "Registry", "Tenant" ],
"ScriptTokens": { "ControlDb": "fleet_control", "ReleaseVersion": "1.0.0" },
```

Two templates, and the order they run in:

| Template | Deploys to | What it declares |
| --- | --- | --- |
| `Registry` | `fleet_control`, named in `quench.settings.json`'s `TemplateTargets` with `CreateIfMissing: true` | A `Tenants` table, and a `DataDelivery` block that fills it from `data/…Tenants.tabledata` (`Insert/Update/Delete`, so the file is the whole roster) |
| `Tenant` | Every database the `Tenants` table lists: `IdentificationDatabase: "{{ControlDb}}"` runs the `DatabaseIdentificationScript` there | The `Shop` tables, a `ProductVersion` table, and a `VersionStampScript` that records `{{ReleaseVersion}}` in each tenant |

The roster starts with three tenants:

```bash
# from course7-module-09/<engine>
cat Package/Templates/Registry/data/*.tabledata
```

## Step 2: One deploy builds the registry, then the fleet it lists

```bash
# from course7-module-09/<engine>
schemaquench --ConfigFile:quench.settings.json
```

The log runs the templates in `TemplateOrder`. On SQL Server:

```
Quenching Template: Registry
  Creating database [fleet_control] (CreateIfMissing: true)
...
[localhost,11433].[fleet_control]     Delivering dbo.Tenants
[localhost,11433].[fleet_control] Successfully Quenched
Quenching Template: Tenant
Template 'Tenant': 3 units - db: DatabaseIdentificationScript; schema: (regular template)
```

`fleet_control` didn't exist when the run started. By the time the `Tenant` template asked who's in the fleet, the
registry had been created and filled, so the answer was the three tenants in the content file. Each of them now
carries its release:

```bash
# from course7-module-09
for n in 001 002 003; do ../lab-sql.sh sqlserver fleet_tenant_$n "SELECT Version FROM dbo.ProductVersion"; done
for n in 001 002 003; do ../lab-sql.sh postgres fleet_tenant_$n "SELECT version FROM productversion"; done
for n in 001 002 003; do ../lab-sql.sh mysql fleet_tenant_$n "SELECT Version FROM ProductVersion"; done
for n in 001 002 003; do ../lab-sql.sh mariadb fleet_tenant_$n "SELECT Version FROM ProductVersion"; done
# → 1.0.0
```

## Step 3: Onboard a tenant with a line in the package

Add `fleet_tenant_004` to the registry's content file, as a fourth row:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_004"}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Registry/data/*.tabledata" append . '{"databasename": "fleet_tenant_004"}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_004"}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_004"}' -->
```json
{"DatabaseName":"fleet_tenant_004"}
```

(`databasename` on PostgreSQL.) Deploy:

```bash
# from course7-module-09/<engine>
schemaquench --ConfigFile:quench.settings.json
```

```
Template 'Tenant': 4 units - db: DatabaseIdentificationScript; schema: (regular template)
```

Four units, and only `fleet_tenant_004` gets new tables; the other three are no-ops. The tenant joined the fleet
through a reviewed change to the package, and the same run delivered the row and deployed the tenant.

## Step 4: Ship a release, and every tenant says which one it has

Bump `ReleaseVersion` in `Package/Product.json` to `1.1.0`:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set ScriptTokens.ReleaseVersion '"1.1.0"' -->
```json
"ScriptTokens": { "ControlDb": "fleet_control", "ReleaseVersion": "1.1.0" },
```

```bash
# from course7-module-09/<engine>
schemaquench --ConfigFile:quench.settings.json
```

```bash
# from course7-module-09
for n in 001 002 003 004; do ../lab-sql.sh sqlserver fleet_tenant_$n "SELECT Version FROM dbo.ProductVersion"; done
for n in 001 002 003 004; do ../lab-sql.sh postgres fleet_tenant_$n "SELECT version FROM productversion"; done
for n in 001 002 003 004; do ../lab-sql.sh mysql fleet_tenant_$n "SELECT Version FROM ProductVersion"; done
for n in 001 002 003 004; do ../lab-sql.sh mariadb fleet_tenant_$n "SELECT Version FROM ProductVersion"; done
# → 1.1.0
```

The `VersionStampScript` runs at the end of each tenant's deploy, so a tenant that failed partway would still say
`1.0.0`. Reading the stamp across the fleet is how you find who didn't get the release.

## Step 5: The order is the dependency

Reverse the order, so `Tenant` runs first, and onboard a fifth tenant in the same change:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set TemplateOrder '["Tenant", "Registry"]' -->
<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_005"}' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Registry/data/*.tabledata" append . '{"databasename": "fleet_tenant_005"}' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_005"}' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Registry/data/*.tabledata" append . '{"DatabaseName": "fleet_tenant_005"}' -->
```json
"TemplateOrder": [ "Tenant", "Registry" ],
```

```bash
# from course7-module-09/<engine>
schemaquench --ConfigFile:quench.settings.json
```

```
Quenching Template: Tenant
Template 'Tenant': 4 units - db: DatabaseIdentificationScript; schema: (regular template)
...
Quenching Template: Registry
```

Four units, not five. `Tenant` read the registry **before** `Registry` delivered the new row, so `fleet_tenant_005`
wasn't in it yet. The row is there now, so run it again:

```bash
# from course7-module-09/<engine>
schemaquench --ConfigFile:quench.settings.json
```

```
Template 'Tenant': 5 units - db: DatabaseIdentificationScript; schema: (regular template)
```

Five, one deploy late. Nothing failed and nothing warned: the order was simply wrong for a template that reads what
another template writes. Put it back:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set TemplateOrder '["Registry", "Tenant"]' -->
```json
"TemplateOrder": [ "Registry", "Tenant" ],
```

## Step 6: Clean up

Drop `fleet_control` (the line for your engine), and reset the fleet with `../course7-setup/setup-databases.sh --reset`
before you run another Course 7 lab:

```bash
# from course7-module-09
../lab-sql.sh sqlserver master "IF DB_ID('fleet_control') IS NOT NULL BEGIN ALTER DATABASE [fleet_control] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [fleet_control] END"
../lab-sql.sh postgres postgres "DROP DATABASE IF EXISTS fleet_control"
../lab-sql.sh mysql information_schema "DROP DATABASE IF EXISTS fleet_control"
../lab-sql.sh mariadb information_schema "DROP DATABASE IF EXISTS fleet_control"
```

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL / MariaDB |
| --- | --- | --- | --- |
| Registry table | `dbo.Tenants (DatabaseName)` | `public.tenants (databasename)` | `Tenants (DatabaseName)` |
| Where the roster query runs | `IdentificationDatabase` | `IdentificationDatabase` (the only way: PostgreSQL can't query another database) | `IdentificationDatabase` |
| Version table | `dbo.ProductVersion` | `public.productversion` | `ProductVersion` |

## The principle

A product can carry its own control plane. Put the template that writes the roster before the template that reads
it, and onboarding a tenant is one reviewed line in the package, deployed by one run. `TemplateOrder` is a
dependency order, not a display order: a template that reads what another one writes must come after it, and when
it doesn't, nothing breaks loudly. The fleet just arrives one deploy late.
