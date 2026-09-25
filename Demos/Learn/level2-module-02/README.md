# Course 2, Module 2 — Templates: fan-out to find everything (lab)

Goal: deploy one schema package that **discovers its own targets at run time** and updates all of
them in a single run. You'll deploy a multi-tenant CRM whose per-tenant workspace is a *schema
template* — one template definition that fans out into a separate schema for every active tenant,
driven by a query against a registry table. Add a tenant row, re-run once, and its entire workspace
appears — with the existing tenants left untouched.

A **schema template** is a template that carries a `SchemaIdentificationScript`. Where a regular
template runs once against the database its `DatabaseIdentificationScript` names, a schema template
runs that database identification step **and then** runs `SchemaIdentificationScript` to discover a
list of schemas — deploying its full object set into *each* discovered schema in turn. The discovery
query is live SQL against the target, so the set of targets is whatever the database says it is at
deploy time, not a hardcoded list in the package.

This lab ships one product, `TenantCRM`, with two templates deployed in order:

- **`Shared`** — a regular template. Creates the `Tenants` registry table in `dbo` (`public` on
  PostgreSQL) and seeds three active tenants. This is the source of truth the fan-out reads.
- **`TenantWorkspace`** — the schema template. Its `SchemaIdentificationScript` reads the active
  tenants out of the registry; for each one it creates the tenant's schema (because
  `CreateSchemaIfMissing` is `true`) and deploys the per-tenant `Customers` and `Contacts` tables
  into it. The `{{SchemaName}}` token resolves to the current tenant on every iteration.

Each engine folder (`sqlserver/`, `postgres/`) has the full `Package/` and a `deploy.settings.json`.

## Why SQL Server and PostgreSQL only

Schema templates fan out across **schemas within one database** — a tenant gets its own namespace
(`acme.Customers`, `beta.Customers`) inside the shared `learn` database. That model needs an engine
where a schema is a distinct object *inside* a database. SQL Server and PostgreSQL both have that:
`CREATE SCHEMA` makes a namespace within the current database. MySQL does not — in MySQL a "schema"
*is* a database (`CREATE SCHEMA` and `CREATE DATABASE` are synonyms), so there's no in-database schema
axis to fan out across. The equivalent multi-target move on MySQL is fanning out across databases,
which is a different mechanism. This lab is two engines because the feature it teaches is a
two-engine feature, not because a third engine was skipped.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (`./verify-sandbox.sh` /
  `.\verify-sandbox.ps1` — SQL Server and PostgreSQL `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Look at the two templates

Pick an engine and open the package:

```
Package/
  Product.json                                  # TenantCRM; TemplateOrder: [Shared, TenantWorkspace]
  Templates/Shared/Template.json                # regular template — targets the learn database
  Templates/Shared/Tables/...Tenants            # the registry table
  Templates/Shared/Before Scripts/
    Seed Active Tenants [ALWAYS].sql            # idempotently seeds acme, beta, globex
  Templates/TenantWorkspace/Template.json       # the SCHEMA TEMPLATE — SchemaIdentificationScript
  Templates/TenantWorkspace/Tables/...Customers # deployed into EACH tenant schema
  Templates/TenantWorkspace/Tables/...Contacts  # ditto, with an FK to Customers in the same schema
deploy.settings.json                            # SchemaPackagePath: ./Package
```

The two identification scripts are the whole idea. They live on `TenantWorkspace/Template.json`:

| Field | SQL Server | PostgreSQL | What it answers |
| ----- | ---------- | ---------- | --------------- |
| `DatabaseIdentificationScript` | `SELECT [name] FROM master.sys.databases WHERE [name] = '{{TenantCRMDb}}'` | `SELECT datname FROM pg_database WHERE datname = '{{TenantCRMDb}}'` | **Which databases?** Here, the one `learn` database. |
| `SchemaIdentificationScript` | `SELECT [Name] FROM dbo.Tenants WHERE [Status] = N'Active' ORDER BY [Name]` | `SELECT name FROM public.tenants WHERE status = 'Active' ORDER BY name` | **Which schemas — the fan-out?** Every active tenant. |

`DatabaseIdentificationScript` picks the database to connect to; `SchemaIdentificationScript` then runs
*inside* that database and returns the list of schemas to deploy into. Presence of a
`SchemaIdentificationScript` is what makes a template a schema template — that's the switch.

> `{{SchemaName}}` is the per-iteration variable. On each schema the fan-out visits, `{{SchemaName}}`
> resolves to that schema's name, so every script, table, and version stamp is deployed *for that
> tenant*. A table file in a schema template carries **no** `Schema` property — the schema is supplied
> per iteration, not baked into the file.

The lab sets **`CreateSchemaIfMissing: true`** on `TenantWorkspace`, so the fan-out creates each
tenant's schema if it doesn't already exist. The full `Demos/SqlServer/TenantCRM` demo ships this as
`false` — there, schemas are provisioned by a separate onboarding path and the template only deploys
*into* schemas that already exist. For a from-scratch sandbox we want the run to stand up the schemas
itself, so this lab flips it to `true`. That's the only meaningful change from the demo's template.

## Step 2: Deploy — watch one run fan out across every tenant

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

`Shared` runs first and creates the registry, then seeds three tenants:

```
Quenching Template: Shared
Template 'Shared': 1 unit - db: DatabaseIdentificationScript; schema: (regular template)
[localhost,11433].[learn]         Adding new table [dbo].[Tenants]
[localhost,11433].[learn] Successfully Quenched
```

Then `TenantWorkspace` discovers the three active tenants and dispatches **one work unit per tenant**
— a single run, three schemas created, six tables:

```
Quenching Template: TenantWorkspace
Template 'TenantWorkspace': 3 units - db: DatabaseIdentificationScript; schema: SchemaIdentificationScript
[localhost,11433].[learn] [Schema: acme]   Creating schema [acme] (CreateIfMissing: true)
[localhost,11433].[learn] [Schema: beta]   Creating schema [beta] (CreateIfMissing: true)
[localhost,11433].[learn] [Schema: globex]   Creating schema [globex] (CreateIfMissing: true)
[localhost,11433].[learn] [Schema: acme]         Adding new table [acme].[Customers]
[localhost,11433].[learn] [Schema: beta]         Adding new table [beta].[Customers]
[localhost,11433].[learn] [Schema: globex]         Adding new table [globex].[Customers]
[localhost,11433].[learn] [Schema: acme] Successfully Quenched
[localhost,11433].[learn] [Schema: beta] Successfully Quenched
[localhost,11433].[learn] [Schema: globex] Successfully Quenched
Completed quench of TenantCRM
```

Every per-tenant line is tagged `[Schema: <tenant>]` — that prefix is how you read a fan-out in the
log. PostgreSQL is the same shape with PG wording: `Create new table acme.customers` and `Creating
schema "acme" (CreateIfMissing: true)`. Tenants quench in parallel (`AllowParallel: true`), so the
order they interleave in varies run to run.

## Step 3: Verify the fan-out landed

Each tenant now has its own `Customers` and `Contacts`:

```bash
# SQL Server (from a SQL client):
#   SELECT s.name + '.' + t.name FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
#     WHERE s.name IN ('acme','beta','globex') ORDER BY 1;
cd ..                                 # back to the lab folder
../lab-sql.sh postgres learn "SELECT table_schema || '.' || table_name FROM information_schema.tables WHERE table_schema IN ('acme','beta','globex') ORDER BY 1"
```

Six rows: `Customers` and `Contacts` in each of the three tenant schemas — every tenant got the
identical workspace, declared exactly once in `TenantWorkspace`.

## Step 4: The aha — add a tenant, re-run once

This is the heart of the module. Add one tenant row, then run the same command again:

```bash
# SQL Server:  INSERT INTO dbo.Tenants ([Name],[DisplayName]) VALUES (N'initech', N'Initech Inc');
../lab-sql.sh postgres learn "INSERT INTO public.tenants (name, display_name) VALUES ('initech','Initech Inc')"

cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

The discovery query now returns four tenants, so the fan-out dispatches four work units — but only
the **new** one does any work:

```
Template 'TenantWorkspace': 4 units - db: DatabaseIdentificationScript; schema: SchemaIdentificationScript
[localhost,11433].[learn] [Schema: initech]   Creating schema [initech] (CreateIfMissing: true)
[localhost,11433].[learn] [Schema: initech]         Adding new table [initech].[Contacts]
[localhost,11433].[learn] [Schema: initech]         Adding new table [initech].[Customers]
[localhost,11433].[learn] [Schema: acme] Successfully Quenched
[localhost,11433].[learn] [Schema: beta] Successfully Quenched
[localhost,11433].[learn] [Schema: globex] Successfully Quenched
[localhost,11433].[learn] [Schema: initech] Successfully Quenched
Completed quench of TenantCRM
```

Only `initech` shows `Creating schema` and `Adding new table`. The three existing tenants reach
`Successfully Quenched` with **no DDL** between dispatch and completion — their declared state already
matches, so the difference is zero. You onboarded a tenant by adding one row and running the deploy
you already run. No new package, no per-tenant script, no list to maintain. The registry *is* the
list, and the template finds it.

## Per-engine notes

| | SQL Server | PostgreSQL |
| ---------------------------- | --------------------------------------------- | ------------------------------------------------ |
| Database identification | `master.sys.databases` | `pg_database` |
| Schema casing / quoting | `[acme]`, mixed-case object names | `acme`, folded to lowercase, double-quoted |
| Schema-create log line | `Creating schema [acme] (CreateIfMissing: true)` | `Creating schema "acme" (CreateIfMissing: true)` |
| New-table wording | `Adding new table [acme].[Customers]` | `Create new table acme.customers` |
| Identity column | `INT IDENTITY(1,1)` | `INTEGER GENERATED BY DEFAULT AS IDENTITY` |

The fan-out mechanics are identical across both: discover schemas, iterate, substitute `{{SchemaName}}`,
deploy the same object set into each. Only the dialect of the catalog queries and the DDL differs.

## The principle

One template declaration; the targets are discovered, not enumerated. A schema template turns "deploy
this to every tenant" into a single idempotent run whose target list is a live query against your own
data. Adding a target is a data change — one row in a registry table — and the next ordinary deploy
picks it up and stands up its entire workspace. Existing targets are untouched because SchemaSmith
deploys *declared state*, not steps: a tenant whose schema already matches is a no-op, every run.
`DatabaseIdentificationScript` answers "which databases"; `SchemaIdentificationScript` answers "which
schemas" — and that second question is the fan-out.
