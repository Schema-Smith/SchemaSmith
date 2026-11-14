# Multi-Tenant Deployments

Every SaaS team hits the moment when the database model forces a choice: one database per tenant, or one schema per tenant inside a shared database. Each path has real tradeoffs -- license cost, operational isolation, backup granularity, deployment blast radius -- and neither is universally correct.

What they share is the maintenance burden of keeping every tenant's database or schema synchronized as the product evolves. One table gets a new column. Now you replicate that change across dozens or hundreds of separate containers. A stored procedure gets a bug fix. Now you fan it out everywhere. The manual version of this job is exactly the kind of fragile, high-stakes operation SchemaSmith was designed to eliminate. SchemaSmith handles both patterns the same way it handles everything else: you declare the desired state once, and the tooling fans it out -- one quench, every tenant, no manual scripting.

## Database-per-tenant

The database-per-tenant pattern gives each tenant their own isolated database. The schema is identical across tenants, but the blast radius of any individual deployment, backup, or restore operation is bounded to one customer. It's the right shape when your licensing model favors it, when tenants require regulatory isolation, or when per-tenant backup and point-in-time recovery are non-negotiable.

SchemaSmith handles this pattern through the `DatabaseIdentificationScript` returning multiple rows -- one per tenant database. The template deploys to every returned database using the same schema package. No separate product definition per tenant. No manually maintained list of databases.

```json
{
  "Name": "TenantWorkspace",
  "DatabaseIdentificationScript": "SELECT [name] FROM master.sys.databases WHERE [name] LIKE 'tenant_%' AND state_desc = 'ONLINE'"
}
```

Every database that matches gets the full template treatment. Add `MaxThreads` to the SchemaQuench settings file and the deployments run in parallel up to the configured limit. Adding a tenant means adding a database; the next quench automatically picks it up.

This is the pattern [Power Workflows](09-power-workflows.md#multi-database-products) introduced. The rest of this chapter is about the other path.

## Schema-per-tenant

When one database holds all your tenants -- each in their own schema -- you're working with the schema-per-tenant pattern. The advantage over database-per-tenant is operational density: fewer database instances to manage, no per-tenant licensing cost on most engines, simpler backups at the database level. The tradeoff is that tenants share the same database process and can't be independently backed up or restored at the tenant granularity.

What you need to manage this well is a way to declare the per-tenant shape once and deploy it consistently across every schema. That's exactly what a **schema template** does.

A schema template is a regular `Template.json` with one extra field: `SchemaIdentificationScript`. Where `DatabaseIdentificationScript` selects target databases, `SchemaIdentificationScript` selects target schemas inside one database. SchemaQuench runs the full template once per returned row -- tables, procedures, views, migrations, data delivery -- with the active schema name available as `{{SchemaName}}` everywhere it runs. One template. Every active schema. One quench.

The TenantCRM demo is the canonical example. It ships with both SQL Server and PostgreSQL builds. The walkthrough below references the SQL Server version; the PostgreSQL version is structurally identical and both are available in `Demos/SqlServer/TenantCRM/` and `Demos/PostgreSQL/TenantCRM/`.

### Authoring layout

A schema-template product follows the same two-level layout as any multi-database product: `Product.json` at the root, one folder per template under `Templates/`. What changes is the role those templates play.

The TenantCRM layout:

```
TenantCRM/
  Product.json
  Templates/
    Initialize/               ← creates the database on first run
      Before Scripts/
        Create TenantCRM Database [ALWAYS].sql
      Template.json
    Shared/                   ← regular template, runs once per quench
      Tables/
        dbo.Tenants.json
        dbo.Plans.json
        dbo.Countries.json
        dbo.GlobalAuditLog.json
      Procedures/
        dbo.OnboardTenant.sql
      data/
        dbo.Plans.tabledata
        dbo.Countries.tabledata
      Template.json
    TenantWorkspace/          ← schema template, runs once per tenant
      Tables/
        Customers.json        ← no schema prefix in the filename
        Contacts.json
        Activities.json
        ActivityTypes.json
      Procedures/
        AddCustomer.sql
        RecordActivity.sql
      Views/
        ActiveCustomers.sql
      Before Scripts/
        Migration_001_BackfillCustomerCountryCode.sql
      Template.json
```

The `data/` folder under `Shared/` holds `.tabledata` files -- DataDelivery content files (one JSON row per record) that DataTongs produced and SchemaQuench merges in FK order; see [FK-aware data delivery with DataTongs](09-power-workflows.md#fk-aware-data-delivery-with-datatongs) for the full mechanic. The `dbo.Countries` and `dbo.Plans` seed rows live here, referenced from their respective table JSON files via `DataDelivery.ContentFile` paths.

Two things to notice about the file names. In `Shared/Tables/`, the files carry the schema prefix: `dbo.Tenants.json`, `dbo.Countries.json`. That's the standard convention for regular templates -- the filename mirrors the table's own `Schema` property, so a glance at the folder tells you where each table lands. (The prefix is a convention, not an input: SchemaQuench reads the schema from the file's content. See [File naming](../reference/schema-packages.md#file-naming).) In `TenantWorkspace/Tables/`, the files have no schema prefix: `Customers.json`, `Contacts.json`. The schema is the iteration variable. SchemaQuench applies `{{SchemaName}}` automatically when it reads these files, so the same `Customers.json` deploys into `tenant_acme.Customers`, `tenant_beta.Customers`, and every future tenant without any per-tenant copy.

One JSON file. Every tenant schema. Zero copy-paste.

The `TenantWorkspace/Template.json` declares the schema template:

```json
{
  "Name": "TenantWorkspace",
  "DatabaseIdentificationScript": "SELECT [name] FROM master.sys.databases WHERE [name] = '{{TenantCRMDb}}'",
  "SchemaIdentificationScript": "SELECT [Name] FROM dbo.Tenants WHERE [Status] = N'Active' ORDER BY [Name]",
  "RequireAtLeastOneTarget": false,
  "CreateSchemaIfMissing": false,
  "AllowParallel": true,
  "ContinueOnSchemaFailure": true
}
```

`DatabaseIdentificationScript` narrows the template to the `TenantCRM` database. `SchemaIdentificationScript` then selects the active schemas inside that database by querying the tenant directory table. Every `Active` row becomes one iteration. On the PostgreSQL side the same pattern uses `public.tenants` and returns `name` in lowercase.

For every new field here and the full `Template.json` property reference, see [Schema Packages -- Schema Templates](../reference/schema-packages.md#schema-templates).

### A 2-tenant walkthrough

Start with two tenants already onboarded (the onboarding step is covered in the next subsection). With `tenant_acme` and `tenant_beta` in `dbo.Tenants`, deploy TenantCRM:

**SQL Server:**

```bash
schemaquench \
  --SchemaPackagePath=./Demos/SqlServer/TenantCRM \
  --Target__Server=localhost \
  --Target__User=sa \
  --Target__Password=<yourpassword>
```

**PostgreSQL:**

```bash
schemaquench \
  --SchemaPackagePath=./Demos/PostgreSQL/TenantCRM \
  --Target__Server=localhost \
  --Target__User=postgres \
  --Target__Password=<yourpassword>
```

A switch value attaches with `=` (or `:`), never a space -- `--Target:Server localhost` is refused as an unrecognized argument and the run exits `2` before touching anything. `__` stands in for the `:` nesting separator, so `--Target__Server=` and `--Target:Server=` name the same setting. See [CLI Switch Format](../reference/configuration.md#cli-switch-format).

The deployment log shows three templates running in sequence -- the last of them, `TenantWorkspace`, fanning out across both tenant schemas:

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

`Initialize` runs once and self-selects out after the database exists. `Shared` runs once, deploying the tenant directory, plans, countries, audit log, and the onboarding procedure. `TenantWorkspace` runs once per active tenant. With `AllowParallel: true` (the default on every engine), the two iterations overlap -- note `tenant_acme` and `tenant_beta` both begin before either completes.

After the quench, both `tenant_acme` and `tenant_beta` have the full workspace schema: `Customers`, `Contacts`, `Activities`, `ActivityTypes`, `AddCustomer`, `RecordActivity`, `ActiveCustomers`, and the `vw_ActiveCustomerCount` indexed view. The `Migration_001_BackfillCustomerCountryCode` Before-slot script ran once per tenant and was recorded in `SchemaSmith.CompletedMigrationScripts` with the `(TenantWorkspace, tenant_acme)` and `(TenantWorkspace, tenant_beta)` keys. A subsequent quench skips those migrations without any configuration change.

To run the demo hands-on and explore the result -- inspecting the tenant schemas, verifying migration tracking, running the inspect queries -- see the [TenantCRM demo README](../../../Demos/SqlServer/TenantCRM/README.md) (SQL Server) or [TenantCRM demo README](../../../Demos/PostgreSQL/TenantCRM/README.md) (PostgreSQL).

### {{SchemaName}}

Inside a schema template, `{{SchemaName}}` resolves to the active tenant schema for the current iteration -- use it in script bodies to qualify every object reference that belongs to the tenant: table inserts, procedure calls, view definitions, trigger bodies. Unqualified references work on SQL Server within an individual procedure context, but making the schema explicit is better practice and is required on PostgreSQL (see the callout below).

The `AddCustomer` procedure from `TenantWorkspace/Procedures/AddCustomer.sql` shows the pattern:

```sql
CREATE OR ALTER PROCEDURE [{{SchemaName}}].[AddCustomer]
    @CustomerName NVARCHAR(128),
    @Email NVARCHAR(256) = NULL,
    @CountryCode CHAR(2) = NULL,
    @CustomerID INT = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO [{{SchemaName}}].[Customers] ([CustomerName], [Email], [CountryCode])
    VALUES (@CustomerName, @Email, @CountryCode);

    SET @CustomerID = SCOPE_IDENTITY();

    INSERT INTO [dbo].[GlobalAuditLog] ([TenantName], [EventType], [Detail])
    VALUES (N'{{SchemaName}}', N'CustomerAdded',
            N'CustomerID=' + CAST(@CustomerID AS NVARCHAR(16)) +
            N'; Name=' + @CustomerName);
END;
```

`[{{SchemaName}}]` qualifies both the procedure itself and the `Customers` insert. `[dbo].[GlobalAuditLog]` is hard-coded with the `dbo` prefix -- it's a shared table that spans tenants, so the cross-schema reference is explicit and fixed. When SchemaQuench deploys this procedure for `tenant_acme`, every `{{SchemaName}}` resolves to `tenant_acme`. The procedure that lands in the database is a fully-qualified, unambiguous definition scoped to that tenant's schema.

The PostgreSQL equivalent in `add_customer.sql` follows the same pattern with `"{{SchemaName}}"` (double-quoted identifier) and `public.global_audit_log`:

```sql
CREATE OR REPLACE PROCEDURE "{{SchemaName}}".add_customer(
    p_customer_name VARCHAR(128),
    p_email VARCHAR(256) DEFAULT NULL,
    p_country_code CHAR(2) DEFAULT NULL,
    INOUT p_customer_id INTEGER DEFAULT NULL
)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO "{{SchemaName}}".customers (customer_name, email, country_code)
    VALUES (p_customer_name, p_email, p_country_code)
    RETURNING customer_id INTO p_customer_id;

    INSERT INTO public.global_audit_log (tenant_name, event_type, detail)
    VALUES ('{{SchemaName}}', 'CustomerAdded',
            'customer_id=' || p_customer_id::TEXT || '; name=' || p_customer_name);
END;
$$;
```

`{{SchemaName}}` is available in the procedure body as a plain string value -- it's a script token, so it substitutes as text before the script executes. That's what lets you use it in a `VALUES` clause as a tenant identifier string, not just as a schema qualifier.

For the full token reference -- availability scope, resolution timing, how `{{SchemaName}}` interacts with the three-tier resolution order -- see [Script Tokens -- {{SchemaName}}](../reference/script-tokens.md#schemaname).

> **PostgreSQL:** Do not rely on `search_path` for unqualified references inside schema-template procedures. `search_path` is a session-level setting and its interaction with stored procedures on PostgreSQL is subtle -- functions and procedures created without a fixed `SET search_path` can resolve objects against the caller's path at invocation time, not the author's intent. Always qualify tenant-scoped references with `"{{SchemaName}}".` explicitly, as the TenantCRM demo does throughout.

### Cross-schema references

Shared dimension tables -- countries, plans, global audit log -- belong to `dbo` (SQL Server) or `public` (PostgreSQL) and are deployed by the `Shared` template. Per-tenant tables reference them. That's a foreign key crossing the schema boundary.

The `Customers` table in `TenantWorkspace/Tables/Customers.json` demonstrates the pattern:

```json
{
  "Name": "[Customers]",
  "Columns": [ ... ],
  "ForeignKeys": [
    {
      "Name": "[FK_Customers_Countries]",
      "Columns": "[CountryCode]",
      "RelatedTableSchema": "[dbo]",
      "RelatedTable": "[Countries]",
      "RelatedColumns": "[Code]"
    }
  ]
}
```

`RelatedTableSchema: "[dbo]"` is the key. Without it, SchemaQuench assumes the related table is in the same schema as the declaring table -- for a schema template, that would mean `tenant_acme.Countries`, which doesn't exist. The explicit `RelatedTableSchema` overrides that assumption and the FK is created correctly from `tenant_acme.Customers.CountryCode` to `dbo.Countries.Code`.

The same mechanism works for procedures. `RecordActivity.sql` references `[dbo].[GlobalAuditLog]` directly in the procedure body -- a hard-coded cross-schema reference, correct and intentional. When the procedure lands in `tenant_acme`, it writes audit rows to the shared log that spans all tenants.

> **Note:** Cross-schema FKs from a schema template to a Shared template's tables work correctly, but the Shared template must come before the schema template in `TemplateOrder`. Tables in `Shared` must exist before `TenantWorkspace` tries to create FKs that reference them. In `TenantCRM/Product.json`, `TemplateOrder` is `["Initialize", "Shared", "TenantWorkspace"]` -- this ordering is the guarantee. Never deploy the schema template before the template it references.

### Onboarding a new tenant

There are three ways to bring a new tenant into the system. Choose the one that fits your operational style.

**Helper procedure.** Call `dbo.OnboardTenant` (SQL Server) or `public.onboard_tenant` (PostgreSQL) -- the canonical atomic CREATE-SCHEMA-plus-INSERT pattern. The procedure creates the schema and inserts the tenant row in a single transaction -- there's no window where a tenant row exists without a schema or vice versa. This is what the TenantCRM demo ships:

```sql
-- SQL Server
EXEC dbo.OnboardTenant
    @Name = N'tenant_acme',
    @DisplayName = N'Acme Corporation',
    @PlanID = 2;

-- PostgreSQL
CALL public.onboard_tenant('tenant_acme', 'Acme Corporation', 2);
```

After the call, re-quench. `TenantWorkspace` discovers the new tenant via `SchemaIdentificationScript`, finds the schema already exists (because `OnboardTenant` created it), and runs the full iteration: tables, procedures, views, migrations, data delivery. The tenant is fully provisioned in one operation.

**Manual pre-create.** A DBA creates the schema independently -- `CREATE SCHEMA tenant_acme` -- then inserts the tenant row via SQL, perhaps into an orchestration table that feeds your discovery query. The next quench picks up the new tenant. This works well when tenant provisioning goes through a separate workflow and the DBA prefers to own the schema creation step.

**Auto-create.** When `CreateSchemaIfMissing: true`, the iteration pipeline creates any schema returned by `SchemaIdentificationScript` that doesn't yet exist before running the iteration. Useful for CI environments, local development, and any environment where the deployment user has `CREATE SCHEMA` permission and manual pre-create adds friction without benefit.

> **Warning:** `CreateSchemaIfMissing` defaults to `false`. The default is deliberate. A typo in your discovery query -- `WHERE Status = 'Actve'` accidentally becoming `WHERE Status = 'Acte'` or a wrong join returning extra rows -- can silently create schemas you didn't intend. In production, prefer the `OnboardTenant` helper or manual pre-create so that tenant schema creation is always an explicit, audited act. Reserve `CreateSchemaIfMissing: true` for environments where it's safe to auto-provision and you trust the discovery query fully.

> **Note:** `CreateSchemaIfMissing: true` requires the deployment user to have `CREATE SCHEMA` permission on SQL Server, or `CREATE` on the database on PostgreSQL. If the deployment user runs with least-privilege credentials, this setting may require a privilege grant separate from the credentials used for table and procedure deployment.

### Reading the log

When a deployment fails at 2am, the first question is which tenant. The `[Schema: <name>]` prefix in the deployment log is how you find out -- every log line that scopes to a specific tenant carries it: begin, success, individual object actions, migration tracking writes, error messages.

```
[localhost].[TenantCRM] [Schema: tenant_acme] Begin Quench
[localhost].[TenantCRM] [Schema: tenant_beta] Begin Quench
[localhost].[TenantCRM] [Schema: tenant_acme] Successfully Quenched
[localhost].[TenantCRM] [Schema: tenant_beta] Successfully Quenched
```

With `AllowParallel: true` (the default, used by both TenantCRM demos), iterations interleave. The log above shows both tenants beginning before either completes. Timestamps on the log lines resolve ambiguity if you need to trace a specific event.

With `AllowParallel: false`, iterations run serially -- one tenant's lines complete before the next begins. Reach for it when you want to cap concurrent load on a resource-constrained target, or when the template's own migration scripts perform DDL that can't run concurrently. Parallel iteration is otherwise the production-realistic default.

If one tenant's iteration fails and `ContinueOnSchemaFailure: true` is set, the failed tenant surfaces a per-iteration error line and the rest of the tenants continue. The exit code for the quench reflects the failure, but the other tenants are unaffected.

> **WhatIf at scale:** WhatIf output for large-N schema-template deployments is verbose -- one full diff section per tenant, in the tenant's schema. It is most useful here while you develop a new migration, to confirm the generated SQL for one or two tenants before the full fan-out. To ask whether the fleet matches its package, run a [drift check](../reference/schemaquench.md#drift-checks) instead: tenants that need the same change share one line (`Tenant: drift, plan a41c… on 48 of 50 target(s)`), the two that differ are named, and the exit code says whether anything drifted.

Each tenant also answers for itself afterwards. Its [deployment record](../reference/schemaquench.md#deployment-record) holds a row per deployment, with the schema for a schema template, so the question "why does this tenant differ from its neighbours" starts with a query on that tenant: when it was last deployed, from which commit, and how that run ended.

## Picking the pattern

Both patterns deploy from the same schema package format. The choice is a database-design and operational decision, not a tooling one.

| | Database-per-tenant | Schema-per-tenant |
|---|---|---|
| **License cost** | One license per tenant (SQL Server Standard/Enterprise) | One license total |
| **Operational isolation** | Full -- each tenant has their own process space | Shared -- tenants share one database process |
| **Backup granularity** | Per-tenant backup and point-in-time restore | Database-level backup only |
| **Deployment blast radius** | Bounded to one tenant | Full database on failure (mitigated by `ContinueOnSchemaFailure`) |
| **Tenant onboarding cost** | Provision a new database | Create a schema and a row |
| **MySQL / MariaDB support** | Yes | No -- MySQL and MariaDB have no schema-inside-database concept |
| **Best fit when** | Regulatory isolation required; per-tenant SLAs; SQL Server licensing is per-instance | High tenant count; per-tenant license cost is prohibitive; operational simplicity matters |

Neither pattern is better in the abstract. The four tradeoffs -- license cost, operational isolation, backup granularity, blast radius -- usually pick the winner before you consult any other factor.

## Region-rotated tenant rosters

Once the per-tenant shape is governed by a schema template, the next operational question is which tenants live where. A single SaaS product often runs in several regions -- US-East, EU-West, APAC -- with a different tenant list in each. The same canonical package should ship to all of them; what should differ is the per-region tenant roster.

This is what `Target.TemplateTargets` is for. The package stays one source of truth. Each region's SchemaQuench settings file declares which tenants belong on that target, and SchemaQuench reconciles existence -- optionally provisioning what's missing -- before deploying. New tenants onboard by editing the settings file and re-running the same package.

### The package shape

Author the template the way you would for a normal schema-per-tenant fan-out, but use the validator-recommended placeholder script that returns no rows. The placeholder marks the template as schema-fan-out without depending on a per-region tenant table:

```json
{
  "Name": "TenantWorkspace",
  "SchemaIdentificationScript": "SELECT 'CONFIG-DRIVEN' AS SchemaName WHERE 1=0"
}
```

The placeholder is the signal that the universe lives in settings, not in a discovery query. Everything else in the template -- tables, procedures, views, migration scripts -- looks identical to the schema-template authoring shown earlier in this chapter.

### Per-region settings

In Region A's `SchemaQuench.settings.json`, declare the regional tenant roster under `Target.TemplateTargets`:

```json
{
  "Target": {
    "TemplateTargets": {
      "TenantWorkspace": {
        "Schemas": ["tenant_acme", "tenant_globex", "tenant_initech"],
        "CreateIfMissing": true
      }
    }
  }
}
```

`CreateIfMissing: true` says: provision any schema in this list that doesn't yet exist on the target. The package and the settings together declare the desired state; SchemaQuench reconciles it.

### First run

The first SchemaQuench run against a fresh Region A target finds zero of the three schemas. With `CreateIfMissing: true`, SchemaQuench issues idempotent `CREATE SCHEMA` DDL for each missing schema (`CREATE SCHEMA IF NOT EXISTS "tenant_acme"` on PostgreSQL, the `sys.schemas`-guarded `EXEC('CREATE SCHEMA [tenant_acme]')` pattern on SQL Server) and then deploys the full template into each one. A single quench creates the rosters AND populates them.

### Subsequent runs

The next run finds all three schemas already present. SchemaQuench skips the create step (the DDL is idempotent regardless) and deploys the template's current shape into each tenant. The same package that brought a fresh region online now keeps every existing tenant's schema synchronized with the latest model -- no separate "provisioning script" pipeline, no second tool, no manual reconciliation.

### Adding a tenant

A new tenant signs up in Region A. Edit the Region A settings file:

```json
{
  "Target": {
    "TemplateTargets": {
      "TenantWorkspace": {
        "Schemas": ["tenant_acme", "tenant_globex", "tenant_initech", "tenant_newco"],
        "CreateIfMissing": true
      }
    }
  }
}
```

Commit the change to source control. The next CI deployment finds the three existing schemas already populated and one missing -- `tenant_newco` -- provisions it, deploys the full template into it, and continues. Onboarding is a one-line edit and a normal deployment. Nothing about the package itself changed.

Other regions are unaffected. Their settings files declare their own rosters; their own deployments reconcile only against their own targets. The same package, fanned out everywhere, with environment-owned membership.

> **Tip:** The pattern composes with the database axis too. `TemplateTargets.<template>.Databases` declares per-template database fan-out the same way; with `CreateIfMissing: true`, SchemaQuench provisions missing databases (using the engine's admin connection) before deploying. Pure-database-fan-out templates that don't need schema iteration -- such as a per-tenant shared-services bundle -- can use just `Databases`, and database-per-tenant deployments on MySQL and MariaDB use this exclusively.

## Migrating from manual duplication

If you already have hand-replicated schemas -- a `tenant_acme` schema and a `tenant_beta` schema that were originally created by copying SQL scripts -- SchemaTongs can cast the first one into a schema template and rewrite the cross-schema references for you.

Point SchemaTongs at one of your existing tenant schemas using `Source.Schema`:

```json
{
  "Source": {
    "Schema": "tenant_acme"
  },
  "Template": {
    "SchemaIdentificationScript": "SELECT [Name] FROM dbo.Tenants WHERE [Status] = N'Active'"
  }
}
```

SchemaTongs extracts the schema's tables and procedures, then replaces qualified references to `tenant_acme.` in SQL bodies with `{{SchemaName}}.`, and sets `RelatedTableSchema` on any FK that points to a shared schema like `dbo`. The resulting package is a schema template -- ready to deploy and govern every tenant's schema from one source.

The full mechanics live in the SchemaTongs reference's new extraction section: [SchemaTongs -- Schema-Template Extraction](../reference/schematongs.md#schema-template-extraction).

## MySQL and MariaDB -- database-per-tenant only

MySQL and MariaDB do not have a schema-inside-database concept that maps to the pattern described in this chapter. A MySQL or MariaDB "database" and "schema" are the same thing -- `CREATE DATABASE` and `CREATE SCHEMA` are synonyms. There is no way to create isolated object namespaces inside a single MySQL or MariaDB database.

Multi-tenant deployments on MySQL and MariaDB use database-per-tenant: one database per tenant, `DatabaseIdentificationScript` returning every tenant database name. Everything described in the [Database-per-tenant](#database-per-tenant) section applies. The schema-template fields (`SchemaIdentificationScript`, `CreateSchemaIfMissing`, `AllowParallel`, `ContinueOnSchemaFailure`) are supported on SQL Server and PostgreSQL only.

> **MySQL and MariaDB:** If you're deploying TenantCRM or a similar multi-tenant product to MySQL or MariaDB, model each tenant as their own database. The `DatabaseIdentificationScript` returning a list of tenant databases is the direct equivalent -- one iteration per tenant, the same schema package fanned out across all of them.

---

Database-per-tenant or schema-per-tenant, the core workflow is the same: declare the shape once, quench. Adding a tenant, rolling out a migration, deploying a hotfix to one tenant first for canary testing -- all of it composes naturally from the same tooling. When the patterns in this chapter aren't enough -- renames, manual migration scripts, objects that need custom handling -- the next chapter covers the escape hatches SchemaSmith provides for exactly those situations. [Edge Cases & Escape Hatches](11-edge-cases.md)
