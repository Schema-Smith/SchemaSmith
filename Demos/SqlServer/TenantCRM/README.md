# TenantCRM — Multi-Tenant CRM Demo (SQL Server)

A miniature multi-tenant CRM that showcases the **schema-per-tenant** pattern: one
schema-template definition fanned out across an arbitrary number of tenant
schemas inside a single database. New tenants are onboarded by calling a stored
procedure; new releases re-deploy every tenant's schema from the same source
package; hotfixes can target a single tenant for canary rollout.

## What this demo shows

- A `Shared` template (regular) that owns the tenant directory, the global audit
  log, the per-tenant plan lookup, and a `dbo.OnboardTenant` procedure.
- A `TenantWorkspace` schema template — one `Template.json` deploys the same
  Customers / Contacts / Activities / ActivityTypes shape into every tenant's
  own schema.
- Cross-schema foreign keys — `Customers.CountryCode` → `dbo.Countries.Code` —
  so per-tenant data references shared dimension tables without duplication.
- Per-tenant migration tracking — a Before-slot migration backfills countries,
  tracked once per `(template, tenant)` in `SchemaSmith.CompletedMigrationScripts`
  so a re-quench skips it.
- DataDelivery in a schema template — `ActivityTypes` is seeded via a
  `.tabledata` file with `MergeType: "Insert"`, so each tenant gets the default
  set the first time their schema deploys and tenant-specific additions or
  renames after the seed are left alone.
- The canonical onboarding pattern — `dbo.OnboardTenant` runs `CREATE SCHEMA` +
  `INSERT dbo.Tenants` atomically, pairing with `CreateSchemaIfMissing: false`
  to keep tenant rows and tenant schemas in sync.
- A per-tenant **indexed view** (`vw_ActiveCustomerCount`) — a schema-bound
  aggregate that lives inside each tenant's own schema, exercising
  `SchemaSmith.IndexedViewQuench` under the schema-template fan-out.
- Selective execution scope — deploy a hotfix to a single tenant first with
  `Target.Schemas: ["tenant_acme"]` before rolling it out unrestricted.

## Prerequisites

- Docker (for the local SQL Server container) or any SQL Server 2017+ instance (this package declares that floor because its procedures use `CREATE OR ALTER`, which 2016 only gained in SP1, and automatic data delivery needs `OPENJSON`).
- The SchemaSmith CLI (or use the bundled `docker compose up` flow from the
  parent `Demos/SqlServer/` directory).

A self-contained way to run this demo:

```bash
cd Demos/SqlServer
docker compose pull
docker compose build
docker compose up
```

The compose chain spins up a `demoserver` SQL Server container and quenches the
demo products in sequence. TenantCRM lands after the standard sample databases.

## Deploy it

From a checkout of the repo, with SchemaQuench on `PATH`:

```bash
schemaquench \
  --SchemaPackagePath ./Demos/SqlServer/TenantCRM \
  --Target:Server localhost \
  --Target:User sa \
  --Target:Password <yourpassword>
```

Expected log highlights (truncated):

```
Quenching Template: Initialize
[localhost].[TenantCRM] Successfully Quenched
Quenching Template: Shared
[localhost].[TenantCRM] Successfully Quenched
Quenching Template: TenantWorkspace
[localhost].[TenantCRM] [Schema: tenant_acme] Begin Quench
[localhost].[TenantCRM] [Schema: tenant_beta] Begin Quench
[localhost].[TenantCRM] [Schema: tenant_acme] Successfully Quenched
[localhost].[TenantCRM] [Schema: tenant_beta] Successfully Quenched
Completed quench of TenantCRM
```

The `[Schema: <tenant>]` prefix is the iteration marker — every line that scopes
to a particular tenant carries it. `Shared` runs once; `TenantWorkspace` runs
once per row returned by its `SchemaIdentificationScript`.

Initial deploy creates two empty schemas because `dbo.Tenants` is empty. To see
real per-tenant work, onboard tenants first (next section) and re-quench.

## Inspect the result

After the first quench (with tenants onboarded):

```sql
-- All tenants and their plan
SELECT t.Name, t.DisplayName, p.PlanName, t.Status, t.CreatedAt
  FROM dbo.Tenants t
  JOIN dbo.Plans p ON p.PlanID = t.PlanID
 ORDER BY t.Name;

-- Per-tenant table structure (replace tenant_acme with any tenant)
SELECT s.name AS SchemaName, t.name AS TableName
  FROM sys.tables t
  JOIN sys.schemas s ON s.schema_id = t.schema_id
 WHERE s.name = 'tenant_acme'
 ORDER BY t.name;

-- Migration tracking per tenant
SELECT template_name, schema_name, ScriptPath, QuenchDate
  FROM SchemaSmith.CompletedMigrationScripts
 WHERE ProductName = 'TenantCRM'
 ORDER BY template_name, schema_name, ScriptPath;
```

## Onboard a new tenant

The canonical pattern — call `dbo.OnboardTenant` to create the tenant schema and
the tenant row atomically:

```sql
USE TenantCRM;
EXEC dbo.OnboardTenant
    @Name = N'tenant_acme',
    @DisplayName = N'Acme Corporation',
    @PlanID = 2;  -- Pro

EXEC dbo.OnboardTenant
    @Name = N'tenant_beta',
    @DisplayName = N'Beta Industries',
    @PlanID = 1;  -- Free
```

Then re-quench. `TenantWorkspace` discovers both tenants and runs the full
template iteration for each one — tables, procedures, function, view, trigger,
the `Migration_001_BackfillCustomerCountryCode.sql` Before-slot migration, and the
`ActivityTypes` data delivery.

## Roll out a hotfix to one tenant first

Edit a procedure (say, `RecordActivity.sql`) and quench with `Target.Schemas`
narrowed to one tenant:

```bash
schemaquench \
  --SchemaPackagePath ./Demos/SqlServer/TenantCRM \
  --Target:Server localhost \
  --Target:Schemas:0 tenant_acme
```

Only `tenant_acme` sees the new procedure. The other tenants are skipped — no
log lines, no schema iteration, no migration tracking writes. Once the canary
holds up in production, drop the `Target.Schemas` arg and re-quench unrestricted
to fan out to every tenant.

## Observe per-tenant migration tracking

Run the deploy twice. After the first run, `CompletedMigrationScripts` has one
row per `(TenantWorkspace, tenant)` for `Migration_001_BackfillCustomerCountryCode`. After
the second run, that row still holds and the migration skips silently.
`ActivityTypes` is delivered via DataDelivery (`Insert` merge type), so a
re-quench reconciles missing seed rows but leaves any tenant-added rows alone —
no migration tracking involved. Verify:

```sql
-- After first quench: 1 row per tenant (Migration_001_BackfillCustomerCountryCode)
SELECT schema_name, COUNT(*) AS MigrationsCompleted
  FROM SchemaSmith.CompletedMigrationScripts
 WHERE ProductName = 'TenantCRM' AND template_name = 'TenantWorkspace'
 GROUP BY schema_name
 ORDER BY schema_name;
```

Onboard a third tenant and re-quench. The new tenant's migration runs and its
`ActivityTypes` get seeded; the existing tenants' tracking rows stay put and
the migration skips.

## Caveats

> **Note:** `CreateSchemaIfMissing: false` means the discovery script lists
> tenants whose schemas already exist. Insert a tenant row without first calling
> `dbo.OnboardTenant` and the next quench will abort that iteration with a clear
> error pointing at the three onboarding paths.

> **Warning:** Cross-schema FK refs (`Customers.CountryCode` →
> `dbo.Countries.Code`) require the referenced row to exist before tenant data
> lands. The `Countries.tabledata` seed in `Shared` runs once on every quench
> before `TenantWorkspace`, which guarantees the reference is satisfied.

> **MySQL:** Schema templates are SQL Server and PostgreSQL only — MySQL doesn't
> have a schema-inside-database concept that matches the pattern. For MySQL
> multi-tenancy, use database-per-tenant: one database per tenant, the
> `DatabaseIdentificationScript` returning every tenant database name.

> **Tip:** `Target.Schemas` filters the iteration without affecting tracking
> semantics. A tenant skipped via `Target.Schemas` is genuinely skipped — no
> rows written, no migrations tracked, no log noise — so canary deploys leave
> the rest of your tenants exactly as they were.

> **Note:** The `ContinueOnSchemaFailure: true` setting on `TenantWorkspace`
> means if one tenant's iteration errors (a hand-edited procedure with a syntax
> error, for example), the other tenants still deploy. The failed tenant
> surfaces a per-iteration error; the rest succeed.

> **Note:** Parallel iteration of `IndexedViewQuench` across many tenant
> schemas can momentarily deadlock at the SQL Server catalog level (`sys.indexes`,
> `sys.views`, `sys.schemas` contention under concurrent DDL). SchemaSmith
> detects the deadlock victim (`Transaction was deadlocked on lock resources …`)
> and retries the affected iteration automatically — recovered transparently —
> so the demo ships with `AllowParallel: true` and parallel iteration is the
> production-realistic default. Set `AllowParallel: false` on
> `TenantWorkspace/Template.json` only if you want strictly serial iteration.

## What's not shown

- Failure isolation across templates (`ContinueOnDatabaseFailure`). The demo
  has one database; this setting matters when your product spans multiple.
- DataTongs schema-template data extraction. The `dbo.Plans`, `dbo.Countries`,
  and per-tenant `ActivityTypes` seeds are hand-written `.tabledata` files; in a
  real product, DataTongs would extract per-tenant data with `Source.Schema`
  set to one tenant's schema.
- Tenant offboarding workflow. Setting `dbo.Tenants.Status = 'Inactive'` drops
  the tenant from the discovery script's output; its schema and data are
  preserved on disk but no longer touched by quenches. Cleanup is intentionally
  left to operations tooling.

The full Multi-Tenant Deployments guide chapter walks these scenarios in
context.
