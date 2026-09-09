# SchemaQuench Reference

Take your declared schema and harden it onto a live database -- that's what SchemaQuench does. It reads a schema package, connects to the target server, and transforms each database to match the desired state. No hand-written ALTER scripts, no guessing what changed. Run it against dev, staging, and production with the same package, the same confidence, and the same boring, predictable result every time. SchemaQuench compares current state against desired state, makes only the changes necessary, and tracks migration scripts so they execute only once.

One executable, four platforms. The product's `Platform` value (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`) tells SchemaQuench which adapter, which DDL flavor, and which set of helper procedures to use. Everything else looks the same.

---

## Invocation

SchemaQuench ships as part of the SchemaSmith distribution — see the [Installation guide](../guide/installation.md) for how to get the binary on your PATH. Run it from the directory containing `SchemaQuench.settings.json`:

```bash
SchemaQuench
```

Common switches:

```bash
SchemaQuench --ConfigFile:path/to/alternate.settings.json
SchemaQuench --LogPath:path/to/logs

# Pre-flight diagnostics (no deployment)
SchemaQuench --TestConnection     # validate connections + MinimumVersion, then exit
SchemaQuench --PreviewTargets     # validate connections + MinimumVersion + show target report, then exit

# SQL Server
SchemaQuench --ConnectionString:"Data Source=myserver;Initial Catalog=master;User ID=sa;Password=secret;TrustServerCertificate=True;"

# PostgreSQL
SchemaQuench --ConnectionString:"Host=myserver;Port=5432;Database=postgres;Username=deploy;Password=secret;"

# MySQL
SchemaQuench --ConnectionString:"Server=myserver;Port=3306;Database=mysql;User=deploy;Password=secret;"
```

The `--ConnectionString` switch bypasses all `Target` settings and passes the value directly to the platform-appropriate driver.

---

## Configuration Reference

SchemaQuench reads configuration from `SchemaQuench.settings.json` (or the file specified by `--ConfigFile`), environment variables with the `SmithySettings_` prefix, and command-line switches. Later sources override earlier ones. For the shared configuration system see the [Configuration Reference](configuration.md).

### Target Connection Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Target:Server` | string | _(required)_ | Database server hostname or IP. |
| `Target:Port` | string | platform default | TCP port. SQL Server `1433`, PostgreSQL `5432`, MySQL `3306`. |
| `Target:User` | string | _(empty)_ | Login username. SQL Server allows blank for Windows auth. |
| `Target:Password` | string | _(empty)_ | Login password. |
| `Target:SecondaryServers` | string | _(empty)_ | **SQL Server only.** Comma-separated list of additional servers (Availability Group secondaries) to quench in parallel with the primary. See [Secondary Servers](#secondary-servers). |
| `Target:ConnectionProperties` | object | `{}` | Arbitrary key-value pairs appended to the connection string. Platform-specific keys -- see the [Configuration Reference](configuration.md#connection-configuration). |

### Behavior Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `SchemaPackagePath` | string | _(required)_ | Path to the schema package directory or ZIP file. |
| `WhatIfONLY` | bool | `false` | Dry-run mode. Generates SQL without executing. |
| `KindleTheForge` | bool | `true` | Deploy SchemaSmith helper procedures and the migration tracking table to each target database before quenching. |
| `UpdateTables` | bool | `true` | Apply table structure changes (columns, indexes, constraints, foreign keys) from the schema package. |
| `DropTablesRemovedFromProduct` | bool | `true` | Drop tables that exist in the database but aren't defined in the schema package. Also settable as a `Product.json` property — see [DropTablesRemovedFromProduct](#droptablesremovedfromproduct). |
| `PreventDrop` | bool | `false` | Environment-wide no-drop protection: when `true`, this environment never drops an object for being absent from the product (every `Drop…RemovedFromProduct` pass is suppressed) and the run completes normally, itemizing the withheld drops in the deployment summary's `preventDrop` manifest. Transient drop-then-recreate for a declared change is unaffected. See [PreventDrop](#preventdrop). |
| `DropColumnsRemovedFromProduct` | bool | `true` | Drop columns that exist in the database but aren't defined in the schema package. Resolves across a four-tier cascade (env → product → template → table) with explicit-false-sticky semantics. See [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct). |
| `DeliverData` | bool | `true` | Run the per-table `DataDelivery` step and the `TableData`-slot scripts. Set to `false` to ship a structure-only deployment that leaves reference data untouched -- pairs naturally with `UpdateTables: true` for "deploy schema, skip data" pipelines. |
| `RunScriptsTwice` | bool | `false` | Run object scripts twice to verify idempotency. A CI/testing tool. |
| `TrackRunOnceMigrations` | bool | `true` | Track run-once migration scripts. When `false`, all scripts run on every deployment. |
| `PruneObsoleteMigrationTracking` | bool | `true` | Remove tracking entries for scripts no longer in the package. When `Target` filters are active, prune is restricted to the targeted scope. See [PruneObsoleteMigrationTracking](#pruneobsoletemigrationtracking). |
| `CheckpointDirectory` | string | `""` | Directory for checkpoint files used by `--ResumeQuench`. When blank, defaults to a per-platform temp location. See [Checkpoint and Resume](#checkpoint-and-resume). |
| `MaxThreads` | int | `10` | Maximum parallel work units. Covers both database-level and schema-level iterations. Range 1--20. See [MaxThreads](#maxthreads). |
| `VerboseLogging` | bool | `false` | Include `PRINT` / `RAISE NOTICE` / equivalent informational output from user scripts in logs. |
| `ScriptTokens` | object | `{}` | Config-level overrides for product script tokens. |

### Full settings file example

```json
{
  "Target": {
    "Server": "localhost",
    "Port": "",
    "User": "",
    "Password": "",
    "SecondaryServers": "",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    },
    "Templates": [],
    "Databases": [],
    "Schemas": []
  },
  "WhatIfONLY": false,
  "SchemaPackagePath": "./MyProduct",
  "KindleTheForge": true,
  "UpdateTables": true,
  "DropTablesRemovedFromProduct": true,
  "DropColumnsRemovedFromProduct": true,
  "DeliverData": true,
  "RunScriptsTwice": false,
  "TrackRunOnceMigrations": true,
  "PruneObsoleteMigrationTracking": true,
  "CheckpointDirectory": "",
  "MaxThreads": 10,
  "VerboseLogging": false,
  "ScriptTokens": {}
}
```

For environment variable mapping, see [Configuration Reference -- Environment Variables](configuration.md#environment-variables).

---

## Secondary Servers

SQL Server deployments targeting Availability Groups can quench to a primary plus one or more secondary servers in parallel. Configure secondaries on `Target`:

```json
{
  "Target": {
    "Server": "primary-replica",
    "SecondaryServers": "secondary-1,secondary-2"
  }
}
```

When a secondary list is configured, SchemaQuench routes each product-level folder to the right server based on its `ServerToQuench` setting (`Primary`, `Secondary`, or `Both`). Templates target the primary; product-level scripts can target either side. See [Schema Packages -- Secondary Servers](schema-packages.md#secondary-servers) for the package side of the configuration.

> **PostgreSQL, MySQL, and MariaDB** deployments use a single connection. Replication and read-only standbys are typically managed at the database engine level, not by the deployment tool.

---

## Target

Selective execution scope narrows a deployment to a subset of the work the product would otherwise perform. The most common use is deploying to a single newly-onboarded tenant without re-running the full product, canary-deploying a hotfix to one tenant to verify it before rolling out, or re-running a single template after a configuration change. Without `Target`, every template runs against every discovered database and schema.

### Filter dimensions

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Target:Templates` | string array | `[]` | Run only these templates. Empty array means no filter -- all templates run. |
| `Target:Databases` | string array | `[]` | Run only against these databases. Empty array means no filter -- all discovered databases run. |
| `Target:Schemas` | string array | `[]` | Run only against these schema names. Empty array means no filter -- all discovered schemas run. Applies only to schema-template iterations; regular-template work units bypass this filter entirely. |

The three dimensions filter AND together. Setting `Target:Templates: ["TenantWorkspace"]` and `Target:Schemas: ["tenant_newco"]` runs only the TenantWorkspace template, and within that template only the iteration where the schema is `tenant_newco`. Unmatched work units are skipped before any database connections open for them.

SchemaQuench validates filter values against the discovered universe before dispatching any work. A value that doesn't match anything in the discovered set fails immediately with a diagnostic that lists the available options, so a typo surfaces as a clear error rather than a silent empty run.

> **Warning:** When `Target` filters are active, `PruneObsoleteMigrationTracking` is restricted to the targeted scope. This is intentional -- pruning tracking rows outside the targeted scope would delete correct records of migrations applied against databases and schemas that you explicitly excluded from this run. See [PruneObsoleteMigrationTracking](#pruneobsoletemigrationtracking) for the full rule.

### Onboarding example

Deploy TenantWorkspace to a newly-onboarded tenant without touching any existing tenants:

```json
{
  "Target": {
    "Server": "production-db",
    "Templates": ["TenantWorkspace"],
    "Schemas": ["tenant_newco"]
  }
}
```

With this configuration, SchemaQuench runs `TenantWorkspace` and skips every other template in `TemplateOrder`. Within `TenantWorkspace`, it runs only the `tenant_newco` iteration -- `tenant_acme`, `tenant_beta`, and all other tenants are untouched, and their tracking rows in `CompletedMigrationScripts` are preserved exactly.

For a full narrative walkthrough of tenant onboarding, see [Onboarding a new tenant](../guide/10-multi-tenant-deployments.md#onboarding-a-new-tenant).

---

## TemplateTargets

`Target.TemplateTargets` lets the deployment system OWN the universe a schema template fans out across, instead of asking the target server to enumerate it. A template's `DatabaseIdentificationScript` / `SchemaIdentificationScript` still defines the package's contract -- this block replaces the script's result at runtime for one named template, per environment. The pattern unlocks single-canonical-package deployments where each environment's settings file declares which tenants belong on that target, and SchemaQuench reconciles existence (optionally provisioning what's missing) before deploying.

```json
{
  "Target": {
    "TemplateTargets": {
      "TenantBody": {
        "Databases": ["tenant_acme", "tenant_globex"],
        "Schemas":   ["acme", "globex"],
        "CreateIfMissing": true
      },
      "Shared": {
        "Databases": ["tenant_acme"]
      }
    }
  }
}
```

Each key under `TemplateTargets` is a template name as declared in `Product.json.TemplateOrder`. The value is an object with three optional properties.

### Databases

String array. Replaces the result of the named template's `DatabaseIdentificationScript` for this run. When set, the listed databases ARE the universe -- the discovery script does not run. The template must declare a `DatabaseIdentificationScript` in its `Template.json`; if you don't need real discovery, the recommended marker is `"SELECT 'CONFIG-DRIVEN' AS DatabaseName WHERE 1=0"` -- a placeholder that returns no rows and signals "this template is database-fan-out, the universe lives in settings."

### Schemas

String array. Replaces the result of the named template's `SchemaIdentificationScript` for this run. Same shape, same recommended placeholder: `"SELECT 'CONFIG-DRIVEN' AS SchemaName WHERE 1=0"`. When both axes are overridden on a schema template, the cross-product becomes the work-unit set: two databases × two schemas = four iterations.

> **MySQL and MariaDB:** The schema axis does not apply -- MySQL and MariaDB have no schema-inside-database concept. `TemplateTargets.<template>.Schemas` is rejected on MySQL and MariaDB templates by the same validation that rejects `SchemaIdentificationScript`. Use the database axis instead; multi-tenant on MySQL and MariaDB is database-per-tenant.

### CreateIfMissing

Boolean. Default `false`. Controls what happens when an entry in `Databases` or `Schemas` doesn't exist on the target server:

| State | `CreateIfMissing: true` | `CreateIfMissing: false` (default) |
|---|---|---|
| Target exists | Deploy normally | Deploy normally |
| Target missing | Provision (DDL), then deploy | Skip with info log, no error |

When `true`, SchemaQuench issues idempotent per-engine DDL (`CREATE SCHEMA IF NOT EXISTS` on PostgreSQL, the `sys.schemas`-guarded `EXEC('CREATE SCHEMA …')` pattern on SQL Server, `CREATE DATABASE IF NOT EXISTS` on MySQL) before deploying into the new target. Database provisioning runs against the engine's admin database (`master` / `postgres` / `information_schema`) by re-targeting the connection -- the credential the user supplied to SchemaQuench must carry `CREATE DATABASE` privilege there. When `false` and a target is missing, the engine emits an info log naming the skipped target and continues with the rest of the override list; no work units run for the missing target, no error.

> **Warning:** Provisioning requires elevated privileges. `CreateIfMissing: true` on the database axis needs `CREATE DATABASE` on the engine's admin database; on the schema axis it needs `CREATE SCHEMA` on the target database. A permission denial surfaces an actionable diagnostic naming the missing privilege, but the deployment fails fast at that target. If your deployment account is intentionally low-privilege, leave `CreateIfMissing: false` and provision externally; SchemaQuench will pick the targets up as soon as they exist.

### Validation

`TemplateTargets` is validated against the loaded product before any deployment work runs. Six rules fail fast with a precise diagnostic naming the offending entry: unknown template name, template excluded by `Target.Templates`, empty entry (no `Databases` and no `Schemas`), `Schemas` declared without a `SchemaIdentificationScript` on the template, `Databases` declared without a `DatabaseIdentificationScript`, and filter values composing with `Target.Databases` / `Target.Schemas` to produce an empty universe. A misconfiguration cannot reach a deployment connection.

### Filter composition

`TemplateTargets` replaces the SOURCE of a template's fan-out universe; `Target.Templates` / `Target.Databases` / `Target.Schemas` still filter the result. The override produces the universe, then the filters narrow it. See [Target](#target) for the filter semantics -- composition is straightforward: `Target.Databases` keeps only entries that match its allow-list (whether those entries came from discovery or an override), and the same applies for `Target.Schemas`.

> **Tip:** For users who don't need declarative provisioning and are happy letting discovery scripts return the live universe, the existing `DatabaseIdentificationScript` / `SchemaIdentificationScript` (which can interpolate query-tokens, read tenant tables, or query system catalogs) remains the right tool. Reach for `TemplateTargets` when the deployment system needs to OWN the universe declaratively -- typically when one canonical package ships to multiple environments with per-environment tenant rosters.

For a worked end-to-end example -- single canonical package, per-region settings files, first-run provisioning, subsequent-run idempotent refresh, onboarding a new tenant -- see [Region-rotated tenant rosters](../guide/10-multi-tenant-deployments.md#region-rotated-tenant-rosters).

---

## MaxThreads

The `MaxThreads` setting controls how many work units run concurrently across the entire product deployment. A work unit is one database iteration for a regular template, or one `(database, schema)` iteration for a schema template. All work unit types share the same pool -- there is no separate budget per template type.

Default: `10`. Range: `1`--`20`.

With schema templates, a single database can contribute many work units -- one per discovered schema. A product with a single `TenantWorkspace` template applied to one database hosting 100 tenant schemas produces 100 work units. At `MaxThreads: 8`, the dispatcher runs up to 8 schema iterations concurrently regardless of how many templates or databases are in scope. If you also have regular-template work units queued alongside schema-template units, they all draw from the same pool.

> **Note:** Templates with `AllowParallel: false` get their own serial queue. At most one of that template's iterations runs at a time, but other templates' parallel-eligible units continue to run concurrently alongside them. See [AllowParallel](schema-packages.md#allowparallel) for the per-template parallel-disable case.

> **PostgreSQL `max_connections` sizing:** Each active work unit holds roughly four PG connections at peak (one main quench connection plus per-iteration sub-operations). At default `MaxThreads: 10`, plan for around 45 concurrent connections from SchemaQuench. At the cap `MaxThreads: 20`, plan for around 85. Size `max_connections` on the target as `MaxThreads × 4 + headroom for other apps, admin, and monitoring`. PostgreSQL's default `max_connections=100` covers default `MaxThreads` comfortably; tune the database ceiling proportionally if you raise `MaxThreads` or share the server with heavy workloads.

---

## ContinueOnDatabaseFailure

Failure isolation at the database level applies to all templates -- both regular templates and schema templates. When `ContinueOnDatabaseFailure` is `true` (the default), one database's failure does not abort the product run; SchemaQuench logs the failure, continues processing remaining databases, and exits with code 2 after all work units have completed or failed.

When `false`, the first database-level failure aborts subsequent iterations. In-flight work units drain naturally -- SchemaQuench does not cancel active database connections because an incomplete transaction is more hazardous than a completed one. The product run exits with code 2.

`ContinueOnDatabaseFailure` is set in `Template.json`, not in `SchemaQuench.settings.json` -- it is a per-template property, not a global switch. The default is `true`:

```json
{
  "Name": "CustomerDB",
  "DatabaseIdentificationScript": "...",
  "ContinueOnDatabaseFailure": false
}
```

For schema templates, `ContinueOnDatabaseFailure` governs database-level failures during work unit enumeration (a bad `DatabaseIdentificationScript`, an unreachable server). Schema iteration failures inside a schema template are governed separately by `ContinueOnSchemaFailure`. See [ContinueOnSchemaFailure](#continueonschemafailure) for the schema-level analog.

---

## ContinueOnSchemaFailure

Schema templates fan out across multiple schema iterations inside a database. `ContinueOnSchemaFailure` controls what happens when one of those iterations fails. This is a template-level property defined in `Template.json` -- see [Failure isolation](schema-packages.md#failure-isolation) in the Schema Packages reference for the property definition and JSON examples.

When `true` (the default), a single schema iteration's failure does not halt the others. The failed iteration logs an error, the remaining iterations continue, and the product run exits with code 2 after all iterations have completed or failed. This is the appropriate default for production multi-tenant deployments where one tenant's problem should not block every other tenant.

When `false`, the first iteration failure stops the dispatcher: no new iterations start, in-flight iterations drain naturally, and subsequent templates in `TemplateOrder` do not run.

**How failures surface.** Each iteration's log lines carry a `[Schema: <name>]` prefix, so failures are traceable per tenant even in a parallel run. The deployment log will show the per-iteration error line for the failed schema, then continue with remaining iterations (in continue mode) or stop (in abort mode). The exit code is 2 whenever any iteration failed, regardless of mode.

`ContinueOnSchemaFailure` is ignored on regular templates. If set non-default on a regular template, SchemaQuench logs a warning at load time. For database-level failure isolation on any template, use `ContinueOnDatabaseFailure`.

---

## Deployment Execution Flow

When SchemaQuench runs, the product quench executes these steps in order:

1. **Log product info** -- Logs the product name, platform, template order, validation script, and any configured script tokens.
2. **Test server connection** -- Opens a connection to the target server and runs a platform-appropriate liveness check. Aborts if the connection fails.
3. **Validate server** -- If `Product.ValidationScript` is configured, executes it against the platform's administrative database (`master` on SQL Server, `postgres` on PostgreSQL, `information_schema` on MySQL and MariaDB). Aborts if the result is falsy.
4. **Validate baseline** -- If `Product.BaselineValidationScript` is configured, executes it. Aborts if the result is falsy.
5. **Product Before scripts** -- Executes scripts from `Before Product` folder(s). On SQL Server with secondary servers, scripts run in parallel to all eligible servers.
6. **Quench each template** -- For each template name in `Product.TemplateOrder`:
   - Loads `Template.json` and merges template-level `ScriptTokens` over the product token set.
   - Executes `DatabaseIdentificationScript` against the administrative database to discover target databases.
   - For schema templates: executes `SchemaIdentificationScript` against each discovered database to produce one work unit per `(database, schema)` pair. For regular templates: one work unit per discovered database.
   - Dispatches all work units to a pool of up to `MaxThreads` concurrent workers. Each worker runs the full [database quench sequence](#database-quench-sequence) for its assigned iteration.
   - If any iteration fails, logs the failure. Failure routing follows the template's `ContinueOnDatabaseFailure` (regular templates) or `ContinueOnSchemaFailure` (schema templates) settings.
7. **Product After scripts** -- Executes scripts from `After Product` folder(s).
8. **Stamp product version** -- If `Product.VersionStampScript` is configured, executes it.
9. **Log completion** -- Logs "Completed quench of {ProductName}".

After the quench returns, the calling program backs up log files to a numbered directory and exits with code 0 (see [Exit Codes](configuration.md#exit-codes)).

---

## Database Quench Sequence

For each work unit dispatched by a template -- one identified database for regular templates, one `(database, schema)` pair for schema templates -- the database quench runs the following sequence. All steps execute on the identified database. For schema templates, the active schema name is available throughout as `{{SchemaName}}`.

1. **Kindle the Forge** -- Deploys SchemaSmith helper procedures, functions, and the migration tracking table for the active platform. Skipped if `KindleTheForge` is `false`.
2. **Validate baseline** -- Executes `Template.BaselineValidationScript` if configured. Aborts if falsy.
3. **Object scripts (first pass)** -- Executes scripts from all `Objects`-slot folders using the dependency retry loop. If `RunScriptsTwice` is enabled, resets all scripts and runs a complete second pass to verify idempotency.
4. **Parse table JSON** -- Serializes all `Tables/*.json` definitions into temp/staging tables for the modular procedures to consume.
5. **MissingTableAndColumnQuench** -- Creates missing tables and adds missing columns.
6. **Object scripts (second opportunity)** -- Re-attempts any Objects-slot scripts that failed in step 3, now that missing tables exist.
7. **Before scripts** -- Executes migration scripts from any folder in the `Before` slot. Sequential and tracked.
8. **ModifiedTableQuench** -- Alters existing columns (type changes, nullability, defaults, computed/generated columns) and manages indexes and check constraints.
9. **Object scripts (third opportunity)** -- Re-attempts any remaining failed Objects-slot scripts now that table modifications are complete.
10. **BetweenTablesAndKeys scripts** -- Executes migration scripts from any folder in the `BetweenTablesAndKeys` slot. Sequential and tracked.
11. **MissingIndexesAndConstraintsQuench** -- Creates missing indexes, check constraints, default constraints, and (where supported) statistics.
12. **AfterTablesScripts** -- Executes migration scripts from any folder in the `AfterTablesScripts` slot. Sequential and tracked.
13. **AfterTablesObjects scripts** -- Executes scripts from `AfterTablesObjects`-slot folders (triggers, DDL triggers, rules, post-table views) using the dependency retry loop. Also retries any still-unresolved Objects-slot scripts.
14. **Table data delivery** -- Merges table data described by per-table `DataDelivery` blocks, ordered by foreign key dependencies. See [Table Data Delivery](#table-data-delivery). Then executes any hand-written scripts in the `TableData` slot using the dependency retry loop.
15. **ForeignKeyQuench** -- Creates, drops, and modifies foreign keys to match the schema package.
16. **Indexed view / materialized view quench** -- If the template defines indexed views (SQL Server) or materialized views (PostgreSQL), deploys them via the platform's view quench procedure.
17. **After scripts** -- Executes migration scripts from any folder in the `After` slot. Sequential and tracked.
18. **Stamp version** -- Executes `Template.VersionStampScript` if configured.

When `UpdateTables` is `false`, steps 4 through 16 are skipped entirely. When `IndexOnlyTableQuenches` is enabled on a template, steps 4--8 (parse JSON, missing tables, second Objects pass, Before scripts, modified tables) are replaced by a single call to the platform's `IndexOnlyQuench` procedure. Steps 9--16 still execute, with `MissingIndexesAndConstraintsQuench` (step 11) and `ForeignKeyQuench` (step 15) skipped.

---

## Engine Version Compatibility

SchemaSmith deploys the same package to SQL Server, PostgreSQL, MySQL, and MariaDB — and within each platform, it adapts to the target server's version rather than demanding uniformity. You declare one package; SchemaQuench detects the engine version of each target and does the right thing for that target. When you need to enforce a version floor, declare it once in `Product.json`.

### Supported engine floors

These are the minimum versions SchemaSmith supports for deployment:

| Platform | Minimum supported |
|----------|------------------|
| SQL Server | 2008 (major version 10) |
| PostgreSQL | 12 |
| MySQL | 5.7 |
| MariaDB | 10.2 |

**These floors are enforced automatically — you don't declare anything.** Before any deployment (SchemaQuench) or extraction (SchemaTongs) work begins, the target server's version is detected and logged; a below-floor server aborts the run with a clear "unsupported version" message instead of failing later with a raw engine error. For SQL Server, the target database's `compatibility_level` is checked too — it must be `100` or higher (SQL Server 2008); a database below that is reported distinctly from a too-old server. Between compatibility levels 100 and 120 SchemaSmith ingests its schema model as XML rather than JSON (`OPENJSON`'s JSON path is a parse error below 130) — automatically, see [Version-adaptive code generation](#version-adaptive-code-generation) below. `MinimumVersion` (below) is a separate, opt-in gate for raising the floor *further* per product.

**MySQL 5.7 / MariaDB 10.2 — what adapts at the floor.** The schema model parses on both (a version-agnostic `JSON_EXTRACT` shred replaces `JSON_TABLE`, which is MySQL 8.0 / MariaDB 10.6), and column/index renames that need newer DDL fall back to an equivalent path automatically (a below-`RENAME COLUMN`/`RENAME INDEX` target gets `CHANGE COLUMN` / drop-and-recreate — same end state). A few features have no equivalent below their introduction and degrade through the unsupported-feature policy (`Target:UnsupportedFeaturePolicy`, default `warn` → apply without the feature + a downgrade-manifest line; `fail` → abort): **CHECK constraints** require MySQL **8.0.16** (MariaDB enforces them at the 10.2 floor); **descending index key parts** are stored ascending below MySQL 8.0 / MariaDB 10.8; and **automatic table-data delivery** requires MySQL **8.0** (on MariaDB 10.2 it works via a recursive-CTE shred — full support; below the MySQL floor, use manual data scripts). The hard wall is the floor itself — MySQL 5.6 and MariaDB 10.1 have no JSON support at all and are rejected outright.

### MinimumVersion pre-flight gate

You can raise the floor for a specific product by declaring `MinimumVersion` in `Product.json`. Before any deployment work begins, SchemaQuench detects the version of every resolved target. If any target is below the declared floor, the entire run aborts with a manifest naming each below-floor server and its detected version. Nothing is deployed -- no partial work, no side effects on any target.

See [Schema Packages -- Product.json](schema-packages.md#productjson) for the accepted value formats (`16` or `2022` for SQL Server; `15` for PostgreSQL; `8.0` for MySQL; `10.6` for MariaDB) and configuration details. The declared value may be any supported version — for example `5.7` (MySQL) or `10.2` (MariaDB) to pin exactly at the floor.

If a target's version cannot be determined, that is a hard error -- SchemaQuench never deploys blind against an unknown version. An unparseable `MinimumVersion` value fails at startup before any connections open.

#### The detected version is reported in each engine's own form

Pre-flight logs the version it detected for every server, and -- **with one exception** -- that string is whatever the engine itself publishes; SchemaSmith does not reshape the four into a common format. They look quite different, and that is deliberate rather than an inconsistency to work around:

| Platform | Detected version reads like | Why that precision |
|----------|-----------------------------|--------------------|
| SQL Server | `16.0.4260.1` (full build) | Servicing level is load-bearing: `CREATE OR ALTER` arrived in 2016 **SP1**, so `13.0.4001` and `13.0.1601` are the same major and behave differently. |
| PostgreSQL | `16` (major) | PostgreSQL gates features on the major; the minor carries no capability difference to report. The server reports `server_version_num` as a packed integer (`160013`); SchemaSmith normalizes it to the major for display. It is the one engine whose logged version is not the raw string the server published. |
| MySQL | `8.0.45` | Feature boundaries land mid-major -- CHECK constraints at **8.0.16**, so a major alone cannot answer the question. |
| MariaDB | `11.4.12-MariaDB-ubu2404` | Same reason, plus the vendor/build tail the server appends -- `RENAME COLUMN` at **10.5.2**, native `UUID` at **10.7**. |

Normalizing all four to a bare major would read tidier and tell you less: on three of the four engines it would discard the digits SchemaSmith's own version gates turn on, and a degrade you needed to diagnose would become invisible in the log. **The comparison is unaffected either way** -- the floor check and `MinimumVersion` both parse each engine's form correctly, so a passing or failing verdict never depends on how the string is printed.

What you *declare* is a separate, friendlier grammar: `MinimumVersion` takes `16` or `2022` on SQL Server, `15` on PostgreSQL, `8.0` on MySQL, `10.6` on MariaDB. You never have to match the detected string's shape -- declare the floor you need and SchemaSmith compares it correctly against whatever the server reports.

### Version-adaptive code generation

When the supported range across your targets diverges, SchemaSmith adapts the DDL it generates automatically. There is nothing to configure -- you deploy the same package to older and newer engine versions and SchemaSmith picks the right form for each target.

> **PostgreSQL:** The following cases apply only to PostgreSQL, whose supported range (12 through current) spans versions that differ in available DDL.

A feature a target version lacks is either taken by an equivalent longer path (same end state), or -- where there is no equivalent -- degraded through the **unsupported-feature policy** (`Target:UnsupportedFeaturePolicy`, default `warn`): the object is emitted without the unsupported aspect and each affected object is listed under **Unsupported Feature Downgrades** in the deployment summary, so you know exactly what was relaxed. Set `Target:UnsupportedFeaturePolicy=fail` (for example `SmithySettings_Target__UnsupportedFeaturePolicy=fail`) to abort instead with a "requires PostgreSQL N" message rather than deploy a silently-degraded schema.

| Authored feature | Requires | Below that version, SchemaSmith… |
|---|---|---|
| **`NULLS NOT DISTINCT`** (unique index / constraint) | PostgreSQL 15 | emits the object *without* the clause + records a downgrade |
| **`MERGE` data delivery** (`Insert/Update`) | PostgreSQL 15 | uses a manual, NULL-safe INSERT + UPDATE upsert with identical semantics |
| **In-place generated-column expression change** (`SET EXPRESSION`) | PostgreSQL 17 | drops and re-adds the generated column (data type, collation, nullability, storage, compression preserved) |
| **Per-column compression** (`SET COMPRESSION`) | PostgreSQL 14 | omits the compression + records a downgrade |
| **Expression statistics** (`CREATE STATISTICS` on an expression) | PostgreSQL 14 | skips the statistic + records a downgrade |
| **Removing a column's generation** (`DROP EXPRESSION`) | PostgreSQL 13 | drops and re-adds the column as a plain column (the previously-computed values are not preserved, unlike the in-place conversion available on 13+) |

The version-sensitive system-catalog reads SchemaSmith uses to compare and extract state (per-column compression, expression statistics, `NULLS NOT DISTINCT`, INCLUDE columns) are branched automatically so they parse on the older server too — extraction and idempotency work the same on 12 as on current PostgreSQL. Delete-on-absence data delivery uses a single `MERGE … WHEN NOT MATCHED BY SOURCE THEN DELETE` on 17+ and a `MERGE` + follow-on `DELETE … WHERE NOT EXISTS` (keyed identically, same merge filter) on 15/16; below 15 it is the same version-agnostic `DELETE`. In every case the end state is identical — deploy the same package to PostgreSQL 12 through current and you get the same database, minus only the features the target genuinely cannot support (which the deployment summary names).

> **SQL Server:** Two independent adaptations, both automatic. Below **compatibility level** 130 (SQL Server 2016) SchemaSmith switches its entire model-ingest and compare encoding from JSON to XML. Separately, features introduced after the target's **server version** are degraded through the unsupported-feature policy, exactly as on the other engines.

**Adaptation 1 — the encoding switch (compatibility-level gated).** This one is not a degrade; nothing is lost. SchemaSmith hands its parsed schema model to the server as JSON (`OPENJSON` / `FOR JSON`) at compatibility level 130 and above, and as XML (`.nodes()` / `.value()` / `FOR XML PATH`) below 130 — because `OPENJSON`'s JSON path is a parse error under compatibility level 130. The switch is chosen from the detected compatibility level and server version, and applies to deployment (SchemaQuench) and extraction (SchemaTongs) alike, reaching down to compatibility level 100 (SQL Server 2008). Constructs SchemaSmith itself uses — `STRING_AGG … WITHIN GROUP` and `STRING_SPLIT` — fall back to `FOR XML PATH` ordered aggregation and a split function on the XML path, so the end state is identical to a modern deployment. **These two are gated differently, and only one gate is the compatibility level.** `STRING_SPLIT` requires compatibility level 130. `STRING_AGG` requires **SQL Server 2017** (server major 14) and is not compatibility-level gated at all — it parses at every level down to 100 on a server that has it. The distinction matters because a SQL Server 2016 server reports compatibility level 130 while having no `STRING_AGG` whatsoever, so the fallback is chosen from the detected server version, not the compatibility level alone. (`STRING_AGG`'s optional `WITHIN GROUP (ORDER BY …)` clause additionally requires compatibility level 110.)

You normally never touch this, but you can force the encoding with `Target:CompatEncoding` (deployment) or `Source:CompatEncoding` (extraction): `auto` (the default — pick by detected version), `legacy` (XML), or `modern` (JSON) — for example `SmithySettings_Target__CompatEncoding=legacy`.

> **Legacy fallback (SQL Server only):** On the XML (legacy) encoding, the open-ended custom-property `Extensions` bag is dropped when SchemaTongs reverse-engineers a table below the JSON cliff. The typed schema model — columns, indexes, keys, constraints, statistics — round-trips intact; only the free-form `Extensions` metadata is not carried on the legacy encoding.

**Adaptation 2 — feature degrades (server-version gated).** The same **unsupported-feature policy** the other engines use (`Target:UnsupportedFeaturePolicy`, default `warn`): a feature the target's server version predates is applied without the unsupported aspect and each affected object is listed under **Unsupported Feature Downgrades** in the deployment summary. Set `Target:UnsupportedFeaturePolicy=fail` to abort instead, naming the required version. These gate on the **server version**, not the compatibility level — a modern server hosting a database left at an old compatibility level still supports all of them.

| Authored feature | Requires | Below that version, SchemaSmith… |
|---|---|---|
| **Temporal tables** (`IsTemporal`, `SYSTEM_VERSIONING`) | SQL Server 2016 | creates the table *without* system-versioning + records a downgrade |
| **Dynamic data masking** (`MASKED WITH`) | SQL Server 2016 | creates the column *unmasked* + records a downgrade |
| **Always Encrypted** (`ENCRYPTED WITH`) | SQL Server 2016 | creates the column *unencrypted* + records a downgrade |
| **Nonclustered columnstore index** | SQL Server 2012 | skips the index + records a downgrade |
| **Clustered columnstore index** | SQL Server 2014 | skips the index + records a downgrade |

> **Check the manifest before deploying to a pre-2016 target.** Under the default `warn`, a masked column is created unmasked and an Always Encrypted column is created unencrypted — the deploy succeeds and the downgrade is recorded, but the protection is not there. If a silently-unprotected column is worse for you than a failed deployment, set `Target:UnsupportedFeaturePolicy=fail`.

One further case is compatibility-level gated rather than version gated: a `Json`-encoded [data delivery](schema-packages.md#content-encoding) aimed at a below-130 SQL Server target follows the same policy — `warn` skips just that delivery and delivers the rest, `fail` aborts. Re-encode that delivery as `Xml` to deploy it there.

> **MySQL / MariaDB:** The supported range (MySQL 5.7 through current, MariaDB 10.2 through current) spans versions that differ in available DDL and JSON support, so the same package adapts per target.

The schema model itself parses on every supported version — a version-agnostic `JSON_EXTRACT` shred stands in for `JSON_TABLE` (MySQL 8.0 / MariaDB 10.6), so nothing about kindling or ingest depends on the target version. Beyond that, a feature a target lacks is either taken by an equivalent path (same end state) or degraded through the **unsupported-feature policy** (`Target:UnsupportedFeaturePolicy`, default `warn` → emit without the feature + an **Unsupported Feature Downgrades** line; `fail` → abort with a "requires MySQL N" message):

| Authored feature | Requires | Below that version, SchemaSmith… |
|---|---|---|
| **Column rename** (`RENAME COLUMN`) | MySQL 8.0 / MariaDB 10.5.2 | reproduces the rename with `CHANGE COLUMN`, reconstructing the current column definition (same end state) |
| **Index rename** (`RENAME INDEX`) | MariaDB 10.5.2 (MySQL 5.7 has it) | drops and recreates the index under the new name from its live definition (same end state) |
| **CHECK constraints** | MySQL 8.0.16 (MariaDB: at the 10.2 floor) | emits the table *without* the check + records a downgrade (MySQL 5.7 parses-and-ignores CHECK, so it can neither be created nor detected) |
| **Invisible index** (`INVISIBLE`; MariaDB `IGNORED`) | MySQL 8.0 / MariaDB 10.6 | stores the index *visible* — the visibility clause is suppressed — + records a downgrade. The modified-index compare ignores the visibility difference below the floor, so re-deploys stay idempotent instead of churning the index every run |
| **Invisible column** (`Column.Invisible`) | MySQL 8.0.23 / MariaDB 10.3 | stores the column *visible* — the `INVISIBLE` clause is suppressed — + records a downgrade. The modified-column compare ignores the visibility difference below the floor, so re-deploys stay idempotent instead of churning the column every run |
| **Descending index key parts** (`… DESC`) | MySQL 8.0 / MariaDB 10.8 | stores the key part ascending (the engine silently does so anyway) + records a downgrade |
| **Automatic table-data delivery** | MySQL 8.0 | on MariaDB 10.2 uses a recursive-CTE shred (full support); below the MySQL floor, skips delivery with a clear log — use manual data scripts |

The version-sensitive catalog reads (CHECK constraints, index visibility) are branched so they parse on the older server too, and integer display widths / FK default actions are normalized on compare so an unchanged table doesn't phantom-modify across versions. The end state is identical — deploy the same package to MySQL 5.7 through current, or MariaDB 10.2 through current, and you get the same database, minus only the features the target genuinely cannot support (which the deployment summary names).

### MariaDB (MySQL family) — where the native DDL diverges

MariaDB is its own platform (`Platform: MariaDb` in `Product.json`), with its own native DDL generation. It shares dialect kinship and much of the same engine base with MySQL -- close enough that the two are often described together as the "MySQL family" -- but that kinship is dialect similarity, not package portability. You select the platform (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`), and SchemaSmith emits the correct native DDL for that target; a package built for MySQL does not deploy to MariaDB, and vice versa.

Within that shared family, three DDL surfaces diverge between the two engines:

| Feature | MySQL | MariaDB |
|---------|-------|---------|
| **Invisible indexes** | `INVISIBLE` keyword; visibility read back from `INFORMATION_SCHEMA.STATISTICS.IS_VISIBLE` | No `INVISIBLE` keyword -- an index is hidden with `CREATE INDEX … IGNORED`; visibility read back from the inverted `INFORMATION_SCHEMA.STATISTICS.IGNORED` column (`'YES'` = ignored/invisible) |
| **Invisible columns -- NOT NULL default requirement** | Permits `col INT NOT NULL INVISIBLE` with no `DEFAULT` | Rejects a `NOT NULL INVISIBLE` column with no `DEFAULT` (engine error 4108, "must have a default value") -- give it a `Default`, or leave it nullable, to deploy the same package on both engines. SchemaSmith does not pre-validate this; the engine's own error passes through unmodified |
| **Dropping CHECK constraints** | `ALTER TABLE … DROP CHECK name` (MySQL 8.0.16+) | Rejects `DROP CHECK` -- uses the generic `ALTER TABLE … DROP CONSTRAINT name` |
| **Column-default normalization** | Canonical form -- no normalization needed | `INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` reports differently: quotes string literals, emits a literal `NULL` marker for a no-default nullable column, and adds parens to function defaults (`current_timestamp()`); SchemaSmith folds these back to the MySQL canonical form so an unchanged column doesn't phantom-modify on every deploy |

SchemaSmith detects each target's platform and emits the right native form automatically -- the divergence above is handled for you, not something you configure.

> **MariaDB 11.4+ default collation — a note for your own SQL.** This one is not about the DDL SchemaSmith generates; it's about comparison SQL *you* write in migration scripts, After Scripts, or `ValidationScript`. MariaDB 11.4 changed the default collation for `utf8mb4` to `utf8mb4_uca1400_ai_ci`. When you compare a string produced at runtime (for example a value derived from `JSON_TABLE`, which takes that new default) against a table column stored under a different collation, MariaDB raises `Illegal mix of collations` rather than coercing. If you hit this on MariaDB 11.4+, add an explicit `COLLATE` to one side of the comparison (e.g. `WHERE t.name = j.name COLLATE utf8mb4_general_ci`) so both operands share a collation. (Distinct from a `latin1` *target database* charset, which SchemaSmith handles internally.)

---

## Quench Slots

SchemaQuench assigns every script folder to a quench slot that determines when the folder's scripts execute and how they are handled. The slot list is the same on every platform; the **default folders** vary by platform (see [Schema Packages -- Default Folders](schema-packages.md#default-folders)).

### Template quench slots

| Slot | Execution Style |
|------|-----------------|
| `Before` | Sequential, tracked |
| `Objects` | Dependency retry loop |
| `BetweenTablesAndKeys` | Sequential, tracked |
| `AfterTablesScripts` | Sequential, tracked |
| `AfterTablesObjects` | Dependency retry loop |
| `TableData` | Dependency retry loop |
| `After` | Sequential, tracked |

### Product quench slots

| Slot | Execution Style |
|------|-----------------|
| `Before` | Sequential |
| `After` | Sequential |

Product scripts run against the administrative connection, outside the per-database template loop.

**Sequential, tracked** -- scripts run in alphabetical order and are recorded in `CompletedMigrationScripts` so they only run once (unless marked `[ALWAYS]`).

**Dependency retry loop** -- scripts are retried in rounds until all succeed or no progress is made. See [Dependency Retry Loop](#dependency-retry-loop).

---

## WhatIf Mode

See exactly what SchemaQuench would do before it touches a single table. Set `WhatIfONLY` to `true` to perform a dry run. In WhatIf mode:

- **Validation scripts** execute normally (server validation, baseline validation).
- **Table quench procedures** run with `@WhatIf = 1`, generating the SQL that would be executed and logging it without applying changes.
- **Migration scripts** show detailed status for each script:
  - `Would APPLY: {script}` for scripts that haven't yet been tracked.
  - `Would SKIP (previously quenched): {script}` for scripts already recorded in `CompletedMigrationScripts`.
- **Object scripts** (Objects, AfterTablesObjects, Table Data) are logged but not executed.
- **Product Before/After scripts** are logged but not executed.
- **Version stamp scripts** aren't executed; a log message indicates the stamp would occur.

**Console verbosity — `--WhatIfDetail`.** By default WhatIf prints one line per script (`Would APPLY: …` / `Would SKIP …` / `Would DELIVER …`), which is thorough but noisy on a large package. Pass `--WhatIfDetail:concise` to collapse each section into a per-category count (for example `12 would apply, 3 would skip`); `--WhatIfDetail:normal` (the default) keeps the per-script lines; `--WhatIfDetail:verbose` is reserved for future extra detail and currently matches `normal`. This affects only the **console** stream — the `SchemaQuench - Summary.md`/`.json` files always carry the full per-script listing regardless of the switch.

**Important limitation:** WhatIf shows the top level of changes, not the full cascade. Because nothing actually executes, WhatIf can't show ripple effects that depend on earlier changes having been applied. For example, if an object script drops an index, that script doesn't run in WhatIf mode, so the index still exists when WhatIf analyzes table changes -- meaning the table diff won't show the index as needing to be recreated. WhatIf is a confidence check, not a guarantee. It catches the majority of issues but the full deployment may produce additional changes that WhatIf couldn't predict.

### Debug SQL output

During both normal and WhatIf runs, SchemaQuench writes the SQL generated by the table quench process to files in the working directory:

- `SchemaQuench - ParseJson {DatabaseName}.sql`
- `SchemaQuench - MissingTableAndColumnQuench {DatabaseName}.sql`
- `SchemaQuench - ModifiedTableQuench {DatabaseName}.sql`
- `SchemaQuench - MissingIndexesAndConstraintsQuench {DatabaseName}.sql`
- `SchemaQuench - ForeignKeyQuench {DatabaseName}.sql`
- `SchemaQuench - IndexedViewQuench {DatabaseName}.sql` (SQL Server)
- `SchemaQuench - MaterializedViewQuench {DatabaseName}.sql` (PostgreSQL)
- `SchemaQuench - IndexOnlyQuench {DatabaseName}.sql` (when `IndexOnlyTableQuenches` is enabled)

These files can be reviewed to understand exactly what structural changes were (or would be) made.

### When to use WhatIf

Reach for WhatIf while you're debugging a tricky deployment or while you're still building confidence with the tooling. Inspect the generated SQL, confirm the changes match intent, then run for real. Once you trust the package and the pipeline, direct quenches are the normal mode -- WhatIf isn't a required gate on every deployment.

---

## Pre-Flight Diagnostics

You don't have to quench to know whether your configuration is ready. The two CLI switches documented here run targeted diagnostic passes against a live server and exit before touching any schema — so you can validate connectivity, version constraints, and per-environment target rosters as early as your pipeline allows, without deploying a single byte.

They are the connection-based members of a larger pre-flight family. The third read-only switch, [`--Validate`](validate.md), needs no server at all — it lints the package straight from the files on disk, so it runs on every commit rather than only when a database is reachable. See [The pre-flight gate family](validate.md#the-pre-flight-gate-family) for how the four gates layer.

### --TestConnection

```bash
SchemaQuench --TestConnection
```

Opens a connection to every configured server (primary plus any `Target:SecondaryServers`), runs a platform-appropriate liveness query, and validates that each server meets the product's declared `MinimumVersion` floor (if one is set). Nothing is deployed. No schema is read, no helper procedures are installed, no migration scripts are touched.

Use this in your pipeline's readiness check before you commit to a full deployment window — catch a bad connection string, a firewall rule that didn't propagate, or a server below your version floor before the quench itself begins.

**What it validates:**
- Connects to every configured server
- Detects duplicate servers in the configured list
- Enforces the product's `MinimumVersion` floor against every server's detected version

**Exit codes:** `0` on pass, `2` on any connection failure or version violation.

### --PreviewTargets

```bash
SchemaQuench --PreviewTargets
```

Everything `--TestConnection` does, plus a read-only per-template report of the databases and schemas the deployment would target. For each template in scope, the report lists every `(database, schema)` work unit that would run — exactly what the full quench would touch, without touching any of it.

This is the right tool before a large fan-out deployment, before onboarding a new environment, or any time you want human eyes on the scope before committing to the run.

The preview respects the same `Target` filters and `TemplateTargets` overrides a real deployment would use. What it shows is what the quench would actually do.

**What it shows:**

```
Template: TenantWorkspace [required]
  db: acme_prod
    schemas: acme, acme_reporting
  db: globex_prod (would be created)
    schemas: globex
```

**Read-only guarantee:** The preview never provisions databases or schemas and never deploys DDL. A database entry labeled `(would be created)` means `CreateIfMissing: true` is configured for that entry in `TemplateTargets` and the database does not yet exist on the server — the preview reports the intent without acting on it.

**RequireAtLeastOneTarget enforcement:** If a template has `RequireAtLeastOneTarget: true` and the discovery or filter produces zero targets, the preview fails with a `FAIL` result and exit code 2 — the same enforcement that applies at quench time, caught here before any deployment begins.

**Exit codes:** `0` on pass (all required templates have targets), `2` on any connection failure, version violation, or required-template match failure.

> **Tip:** Use `--PreviewTargets` to spot-check `TemplateTargets` configuration for a new environment before its first deployment. When the preview shows the right databases and schemas, the quench will target exactly those — nothing will surprise you at run time.

> **Note:** Neither switch performs WhatIf analysis (no SQL generation, no schema diff), and neither inspects the package's contents — that's [`--Validate`](validate.md)'s job. They validate connectivity and enumerate targets only. For a preview of the structural changes a quench would make, use `WhatIfONLY: true` — see [WhatIf Mode](#whatif-mode).

---

## Partial-Package Deployments (Data Fixes)

A data fix is a deployment of a *deliberately partial* schema package -- usually a handful of migration scripts that correct a specific production issue -- rather than a full release. SchemaQuench supports this as a first-class mode through four configuration flags that flip together, called the **datafix profile**.

The distinction matters because several SchemaQuench behaviors are correct for a full release and wrong for a partial package. A full release treats the package as the complete truth of what the database should look like: tables not in the package get dropped, migration tracking records with no matching script get pruned, helper infrastructure gets redeployed. A data fix does the opposite -- it runs only what's in the partial package and leaves everything else alone.

### When to reach for a data fix

- **Data backfills** -- Populate a new NOT NULL column's values, backfill a computed column, correct values that shipped wrong in a prior release.
- **Compliance scrubs** -- Redact or delete data to meet a GDPR, HIPAA, or PCI deadline without waiting for the next release.
- **Emergency indexes** -- Add an index to stop a query from timing out in production when you can't wait for the next full release cycle.
- **Re-seeding reference tables** -- Restore a reference table that got clobbered by a bad manual change.
- **Targeted data repairs** -- Fix a specific row, reset a stuck workflow state, correct a foreign-key orphan.
- **Permissions-constrained deployments** -- Run data-only changes in an environment where the deployment user lacks DDL permissions.

### How a data fix differs from a regular release

| Dimension | Regular release | Data fix |
|-----------|-----------------|----------|
| Package content | Full product (tables, objects, DataDelivery, all migration scripts) | Partial (usually just migration scripts) |
| Package semantics | Complete truth -- database reconciles to match | Surgical -- only what's in the package executes |
| Table changes | Added, altered, dropped to match the package | No structural changes |
| Migration tracking | Recorded and pruned to match the package | Not recorded; prior tracking preserved |
| Infrastructure | KindleTheForge runs to sync helper procedures | Skipped -- existing infrastructure assumed correct |
| Rerun-ability | Tracked scripts skip; `[ALWAYS]` always runs | Every script runs on every invocation |
| Permissions needed | DDL + data | Data-only in most cases |
| Typical cadence | Scheduled release train | On-demand, between releases |

### The datafix profile

```json
{
  "KindleTheForge": false,
  "UpdateTables": false,
  "DropTablesRemovedFromProduct": false,
  "TrackRunOnceMigrations": false
}
```

Each flag addresses a specific assumption that full-release mode makes and a data fix has to turn off:

- **`KindleTheForge: false`** -- Skip redeployment of SchemaSmith's helper procedures and tracking table. The infrastructure is already in place from the most recent full release; a data fix doesn't need to touch it. Also reduces the DDL permissions the deployment user needs.
- **`UpdateTables: false`** -- Skip the table-quench phase entirely. The partial package doesn't contain table JSON; this flag stops SchemaQuench from interpreting their absence as "drop everything." Combined with `DropTablesRemovedFromProduct: false`, it closes both paths to unintended structural changes.
- **`DropTablesRemovedFromProduct: false`** -- Tables not in the partial package must not be dropped. A datafix package with two migration scripts and no table JSON would otherwise signal "the product has no tables" and trigger a mass drop.
- **`TrackRunOnceMigrations: false`** -- Don't record migration script execution in `CompletedMigrationScripts`, and treat every script as if it carried `[ALWAYS]`. Data fixes often need to run more than once (the first run didn't quite land, the fix needs to be re-applied after data drift), and tracking would prevent that. This also forces `PruneObsoleteMigrationTracking` off regardless of its configured value, which protects the tracking records from prior full releases from being deleted by a partial package's pruning pass.

The four flags are a *profile*, not a menu -- mixing partial-package intent with full-release reconciliation is how tracking records get corrupted or tables get dropped by mistake. Flip all four together.

### Patterns that pair well with data fixes

- **[Checkpoint and resume](#checkpoint-and-resume)** -- When a datafix touches a large dataset and may need to be retried after a connection drop or server restart, enable resume so the fix picks up where it left off instead of re-running completed work.
- **Slot choice** -- Even in a partial package, the migration script's slot still determines *when* in the deployment sequence it runs relative to the (skipped) table-quench phase. The four sequential, tracked migration slots -- `Before`, `BetweenTablesAndKeys`, `AfterTablesScripts`, and `After` -- are the usual homes for data fixes. The slot is a namespacing and timing signal; the fact that a data fix typically has no tables to run *between* doesn't change the ordering contract.

A data fix should not carry `DataDelivery` blocks or table JSON. If your fix is "re-seed this reference table," the right shape is usually a migration script that does the seeding imperatively (or calls a stored procedure that does), not a DataDelivery block in what would then stop being a partial package.

---

## KindleTheForge

Before SchemaQuench can shape your database, it needs its tools in place. KindleTheForge deploys the SchemaSmith infrastructure to each target database. The infrastructure includes a per-platform set of helper functions, modular table-quench procedures, the indexed view or materialized view procedure where applicable, the reverse-engineering procedures used by SchemaTongs, and the `CompletedMigrationScripts` tracking table.

KindleTheForge runs on every quench, but the install itself is **version-stamped and self-skipping**: SchemaSmith records a content-hash stamp of the helper objects in each target database and the call returns immediately when the stamp matches the current tooling — so a normal deployment pays the install cost only when the tooling actually changes. In a normal release pipeline, always leave this `true`. See [`ForceReKindle`](#forcerekindle) for the override that re-installs unconditionally.

**When to set false:** Partial-package datafix deployments flip this off along with the rest of the datafix profile. See [Partial-Package Deployments (Data Fixes)](#partial-package-deployments-data-fixes) for the full profile and the reasoning behind each flag.

---

## ForceReKindle

Default `false`. SchemaSmith records a content-hash stamp of the helper procedures and tables it installs in each target database. On every subsequent run it compares the stamp to the current tooling and **skips the re-install when nothing has changed**, so a normal deployment pays the helper-install cost only when the tooling actually moves. `ForceReKindle` overrides that skip and re-installs the helper objects unconditionally — handy after a manual edit to the helpers, when diagnosing a deploy problem, or any time you want a known-good baseline regardless of stamp state.

Set it in `SchemaQuench.settings.json`, or pass `--ForceReKindle` on the command line (presence enables it, no value needed). When both are present the CLI switch wins.

> **Tip:** Forcing a re-kindle is safe to run concurrently. SchemaSmith serializes the helper re-install per database with a session lock, so parallel deployments don't collide even when every one of them is forcing.

> **Tip:** If you can't change the configuration or CLI invocation but still need a re-kindle, dropping the `SchemaSmith.KindleStamp` marker table (or `SchemaSmith_KindleStamp` on MySQL and MariaDB) has the same effect — the gate sees the missing stamp on the next run and re-installs.

---

## Modular Quench Procedures

The table quench is broken into modular stored procedures, each handling a specific aspect of the table schema. The procedures are deployed during the KindleTheForge step and called in sequence during the database quench. Every platform ships its own implementation, but the responsibilities are the same.

| Procedure | Responsibility |
|---|---|
| **MissingTableAndColumnQuench** | Creates tables that exist in the schema package but not in the database. Adds columns that exist in the table definition but are missing from the existing table. |
| **ModifiedTableQuench** | Alters existing columns to match the schema package definitions. Handles data type, nullability, default constraint, and computed/generated column changes. Drops removed tables when `DropTablesRemovedFromProduct` is enabled. |
| **MissingIndexesAndConstraintsQuench** | Creates indexes, check constraints, default constraints, and (where supported) statistics that exist in the schema package but are missing from the database. |
| **ForeignKeyQuench** | Creates, modifies, and drops foreign keys to match the schema package. Runs late in the sequence so all referenced tables and columns exist. |
| **IndexOnlyQuench** | Alternative to the full sequence. Manages indexes only -- doesn't create tables, add columns, or manage foreign keys. Used when `IndexOnlyTableQuenches` is enabled on a template. |
| **IndexedViewQuench** | Deploys indexed views with diff-based change detection (SQL Server). |
| **MaterializedViewQuench** | Deploys PostgreSQL materialized views, including their indexes. |

The implementation lives in the deployed SQL on the target database -- which means a DBA can read it on the server with `sp_helptext` (SQL Server), `\sf` (PostgreSQL), or `SHOW CREATE PROCEDURE` (MySQL). No black boxes.

### Calling Procedures Directly from Migration Scripts

The quench procedures are deployed to the target database during the KindleTheForge step and remain there afterward. You can call them directly from Before Scripts, After Scripts, or any migration script to bootstrap specific tables or views as part of a data migration.

The typical pattern uses specific-object tokens to quench individual objects that your migration script depends on, rather than passing the entire schema. First, define a token in your `Product.json` or `Template.json`:

```json
{
  "ScriptTokens": {
    "AuditLogTable": "<*SpecificTable*>dbo.AuditLog"
  }
}
```

Then call the procedure from your migration script using the token:

**TableQuench** -- ensures a specific table exists with the right structure before your migration script runs:

> **SQL Server:**
> ```sql
> -- Bootstrap the AuditLog table so we can insert into it during this migration
> EXEC SchemaSmith.TableQuench
>     @ProductName = '{{ProductName}}',
>     @TableDefinitions = '[{{AuditLogTable}}]',
>     @WhatIf = 0,
>     @DropUnknownIndexes = 0,
>     @DropTablesRemovedFromProduct = 0,
>     @UpdateFillFactor = 1;
> ```

> **PostgreSQL:**
> ```sql
> CALL "SchemaSmith"."TableQuench"(
>     '{{ProductName}}',
>     '[{{AuditLogTable}}]',
>     FALSE,  -- p_WhatIf
>     FALSE,  -- p_DropUnknownIndexes
>     FALSE,  -- p_DropTablesRemovedFromProduct
>     TRUE    -- p_UpdateFillFactor
> );
> ```

> **MySQL:**
> ```sql
> CALL SchemaSmith_TableQuench(
>     '{{ProductName}}',
>     '{{MainDB}}',
>     '[{{AuditLogTable}}]',
>     0,  -- p_WhatIf
>     0,  -- p_DropUnknownIndexes
>     0   -- p_DropTablesRemovedFromProduct
> );
> ```

The same pattern works for views. Define the token, then pass it to the procedure:

```json
{
  "ScriptTokens": {
    "OrderSummaryView": "<*SpecificIndexedView*>dbo.vw_OrderSummary",
    "ActiveOrdersView": "<*SpecificMaterializedView*>reporting.active_orders"
  }
}
```

**IndexedViewQuench** (SQL Server):

> ```sql
> EXEC SchemaSmith.IndexedViewQuench
>     @ProductName = '{{ProductName}}',
>     @IndexedViewSchema = '[{{OrderSummaryView}}]',
>     @WhatIf = 0,
>     @UpdateFillFactor = 0;
> ```

**MaterializedViewQuench** (PostgreSQL):

> ```sql
> CALL "SchemaSmith"."MaterializedViewQuench"(
>     '{{ProductName}}',
>     '[{{ActiveOrdersView}}]',
>     FALSE,  -- p_WhatIf
>     TRUE    -- p_UpdateFillFactor
> );
> ```

You can also pass the full schema tokens (`{{TableSchema}}`, `{{IndexedViewSchema}}`, `{{MaterializedViewSchema}}`) to quench all objects of that type, but the specific-object pattern is more common in migration scripts where you need one table or view to exist before proceeding.

> **Legacy SQL Server tier:** these tokens hand your script JSON, which the built-in quench procs shred with `OPENJSON` (compatibility level 130+). If you shred a model-payload token in *your own* SQL on a below-130 database, `OPENJSON` parse-errors — use the [XML twin](script-tokens.md#model-payload-xml-twins) (`{{AuditLogTableXml}}`-style per-object tags, or `{{TableXml}}`) and XQuery `.nodes()`/`.value()` instead, gated behind `{{CompatibilityLevel}}`. The XML shape shreds at every compatibility level.

**Parameter reference:**

| Parameter | TableQuench | IndexedViewQuench | MaterializedViewQuench |
|-----------|:-----------:|:-----------------:|:----------------------:|
| ProductName | Required | Required | Required |
| Definitions (JSON) | Required | Required | Required |
| DatabaseName (MySQL only) | Required | -- | -- |
| WhatIf | Default: off | Default: off | Default: off |
| DropUnknownIndexes | Default: off | -- | -- |
| DropTablesRemovedFromProduct | Default: on | -- | -- |
| DropColumnsRemovedFromProduct | Default: on | -- | -- |
| UpdateFillFactor | SQL Server / PG only -- Default: on | Default: off | Default: on |

> **MySQL note:** `SchemaSmith_TableQuench` on MySQL has no `UpdateFillFactor` parameter -- fill factor is a SQL Server / PostgreSQL concept and the MySQL procedure simply omits it. The MySQL example above (six positional args) reflects the actual signature.

**When to use direct calls:** When a migration script needs a table or view to exist before it can run -- for example, bootstrapping an audit table in a Before Script before inserting migration tracking data, or ensuring a materialized view is deployed before populating dependent tables.

---

## Migration Script Tracking

SchemaQuench remembers what it has already run, so you never have to worry about a migration script executing twice. Migration scripts (scripts in the `Before`, `BetweenTablesAndKeys`, `AfterTablesScripts`, and `After` slots) are tracked in a table SchemaSmith maintains in each target database. On SQL Server and PostgreSQL it is schema-qualified as `SchemaSmith.CompletedMigrationScripts`. MySQL and MariaDB have no schema-inside-database concept, so the same table is named `SchemaSmith_CompletedMigrationScripts` there -- the same flattening `SchemaSmith_KindleStamp` and `SchemaSmith_StatusMessages` use.

| Column | Description |
|--------|-------------|
| `ProductName` | The product name from `Product.json`. |
| `QuenchSlot` | The slot the script belongs to. |
| `ScriptPath` | The relative path of the script within the template. |
| `template_name` | The template the script ran under. |
| `schema_name` | The per-iteration schema for a schema template. Empty for a regular template, and always empty on MySQL and MariaDB, where schema templates do not apply. |
| `QuenchDate` | Timestamp when the script was executed. Named `CompletedAt` on MySQL and MariaDB. |

Those five identity columns -- `ProductName`, `QuenchSlot`, `ScriptPath`, `template_name`, and `schema_name` -- are what make a tracking row unique, so the same script is recorded separately per template and per tenant schema. SQL Server and PostgreSQL enforce them as the primary key. MySQL and MariaDB carry an auto-increment `Id` primary key instead and enforce the same five columns as a unique index, with prefix lengths on the three longest.

### Execution rules

- On each quench run, SchemaQuench checks which scripts in each slot have already been recorded.
- Scripts that appear in the tracking table are skipped.
- Scripts that don't appear are executed, and on success a tracking entry is inserted.

### The [ALWAYS] suffix

Scripts with `[ALWAYS]` in the filename (before the `.sql` extension) run on every quench regardless of tracking:

```
001_SeedReferenceData [ALWAYS].sql
002_RefreshPermissions [ALWAYS].sql
```

`[ALWAYS]` scripts are never recorded in the tracking table.

### Common [ALWAYS] Patterns

**Refreshing PostgreSQL materialized views** -- SchemaQuench deploys materialized view *definitions* via the MaterializedViewQuench procedure, but it does not refresh their data on every deployment. If your materialized views need periodic refreshing, use an `[ALWAYS]` script in the `After Scripts` folder:

```sql
-- After Scripts/001_RefreshMaterializedViews [ALWAYS].sql
REFRESH MATERIALIZED VIEW CONCURRENTLY "reporting"."active_orders";
REFRESH MATERIALIZED VIEW CONCURRENTLY "reporting"."monthly_summary";
```

The `CONCURRENTLY` keyword allows the refresh to happen without locking out concurrent reads -- but it requires a unique index on the materialized view. If your view has no unique index, drop the `CONCURRENTLY` keyword (which will block reads during refresh).

This runs on every deployment, keeping your materialized view data current with the underlying tables. For views that are expensive to refresh, consider gating the refresh with a condition or scheduling it outside of deployment.

### Ordering

Migration scripts within each slot execute in **alphabetical order by filename**. Use numeric prefixes to control execution order:

```
001_CreateStagingTable.sql
002_MigrateData.sql
003_DropStagingTable.sql
```

### Obsolete entry cleanup

When SchemaQuench processes a slot, it compares the tracking table entries against the scripts currently present in the package. Entries for scripts that no longer exist in the package are automatically removed.

### Forcing re-execution

To force a tracked script to run again, either delete the corresponding row from the migration tracking table in the target database (named per engine as above), or rename the script file (tracking is by path, so a renamed script is treated as new).

---

## Dependency Retry Loop

You shouldn't have to name your files in dependency order just so they deploy correctly. Scripts in the `Objects`, `AfterTablesObjects`, and `TableData` slots execute using a dependency retry loop rather than simple sequential execution:

1. Execute all pending (not yet quenched) scripts in the slot.
2. For each script, attempt to execute all its batches. If any batch fails, record the error and move on.
3. If at least one script succeeded in this iteration, loop back to step 1 with only the remaining failed scripts.
4. If zero scripts succeeded in an iteration, the loop terminates.

On the **final attempt** (the last pass when errors are reported), failures are logged as errors and the quench fails.

This mechanism allows scripts with interdependencies to coexist in the same folder without requiring a specific naming order. For example, if View B references View A and is alphabetically first, it will fail on the first pass but succeed on the retry after View A has been created.

The Objects slot gets four opportunities to resolve: (1) before the table quench, (2) after missing tables are created, (3) after table modifications are complete, and (4) during the AfterTablesObjects pass alongside triggers. This handles cases where a view or function references a table column that doesn't yet exist on the first pass.

---

## DropTablesRemovedFromProduct

When `DropTablesRemovedFromProduct` is `true` (the default), `ModifiedTableQuench` drops tables that:

- Exist in the target database.
- Aren't defined in any table JSON file in the schema package.
- Were previously managed by this product.

This keeps the database clean as tables are removed from the schema package over time.

**Three-tier cascade — environment → product → template.** The setting resolves across three tiers, evaluated in order from broadest to narrowest:

- **Environment** — `DropTablesRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropTablesRemovedFromProduct` environment variable). Controls all products deployed in that environment.
- **Product** — `DropTablesRemovedFromProduct` in `Product.json`. Controls a single product regardless of which environment it deploys to.
- **Template** — `DropTablesRemovedFromProduct` in `Template.json`. Controls a single template within a product.

**Explicit-false is sticky — environment guardrail.** A `false` set at any tier locks the effective value to `false` for all lower tiers. A `true` at a lower tier overrides an inherited `true` but can never override an ancestor's `false`. Absent (not set) means inherit from the tier above. This makes a higher-tier `false` a hard guardrail: an environment that sets `false` suppresses the drop pass regardless of what any product or template declares.

A `false` at either the environment or product level suppresses the drop pass for all templates in that product — so a package can declare "never auto-drop my tables" in its own `Product.json` without requiring every operator to configure the environment flag, and an environment can suppress all auto-drops globally without requiring every package to opt out.

**Environment guidance:**

- **CI and local dev** -- `true`. Catch product areas that reference tables you plan to remove.
- **Test/staging** -- `true`. Same rationale, but verify the drop is intentional before promoting to production.
- **Production** -- Often `false`. Dropping a table is a hard drop with no built-in recovery. Teams that need rollback-friendly deployments should leave this off in production. Instead, write specialized migration scripts to rename or archive the table for a retention period before the actual drop.

**The rollback-friendly removal pattern:**
1. Remove the table from your product definition.
2. Keep `DropTablesRemovedFromProduct: false` in the production config.
3. Write a migration script that renames or archives the table (or verifies no dependencies remain).
4. After the retention period, either enable the setting for one deployment or add an explicit DROP in a migration script.

For an alternative that keeps auto-drops on while still protecting data, see [Recyclebin -- Soft-Drop and Restore Hooks](recyclebin.md) (posture 2: drop-but-recoverable via the `SchemaSmith.CustomTableDrop` / `SchemaSmith.CustomTableRestore` hooks).

**Partitioned tables are never dropped by absence -- the run fails instead.** A partitioned table holds data spread across every partition, and no declaration can tell SchemaSmith that data is disposable. So a guard inspects each table selected for drop-by-absence and, on finding it partitioned, refuses to drop it and **fails the run closed** (exit code `2`) rather than proceeding. This applies however the table came to be partitioned -- one a DBA partitioned by hand and one SchemaSmith itself deployed from a declared `Partitioning` block (MySQL/MariaDB) or `PartitionScheme` / `PartitionColumn` (SQL Server) are equally protected. The failure names the table and tells you to drop it manually or mark it `PreventDrop`:

```
Partitioned table(s) skipped by drop-by-absence guard; drop manually or mark PreventDrop.
  Partitioned table removed from product, not dropped (data-loss guard): <table>
```

Partition detection is per engine: `sys.partitions` on SQL Server (heap or clustered index), a partitioned parent or an `ATTACH`ed child partition on PostgreSQL, and `INFORMATION_SCHEMA.PARTITIONS` on MySQL and MariaDB.

This is a safety net for the case where a partitioned table was never marked, not the primary mechanism. `PreventDrop` takes precedence and remains the intended way to protect a table from drop-by-absence: a table marked `PreventDrop` never reaches the guard at all -- the drop is skipped, the run succeeds, and the skip is recorded as `Table <name> removed from product but PreventDrop is set — skipping drop (protected)`.

---

## PreventDrop

Some tables you never want the deployment tool to drop -- not on a rollback, not when someone trims the package, not by accident. The `Drop…RemovedFromProduct` flags help while a table is still in the package, but they share a blind spot: they gate the by-absence drop pass, so they only "see" a table whose definition is still present. The moment you delete a table's `.json`, there's nothing left to carry a `false` -- and the table becomes a drop candidate. `PreventDrop` closes that gap. Set it on a table and SchemaSmith persists the intent *in the database itself*, so the protection outlives the table's own definition.

`PreventDrop` is a per-table boolean set in the table's `.json`, default `false`. When `true`, the table is never dropped by absence -- even after you remove it from the package entirely.

```json
{
  "Name": "[Orders]",
  "PreventDrop": true,
  "Columns": [ /* ... */ ]
}
```

**Sticky by design.** The protection is persisted in SchemaSmith's ownership tracking, so it survives the table leaving the package. On SQL Server it's a `PreventDrop` extended property stamped on the table; on PostgreSQL, MySQL, and MariaDB it's a `PreventDrop` column on the `ProductOwnership` tracking table. Each run, while the table is still in the package, SchemaSmith refreshes the marker to match the package value -- so the stored protection always tracks what your JSON declares.

**Removed, not dropped.** When a protected table is later removed from the package, SchemaSmith reads the persisted marker, logs that it's retaining the table, and skips the drop. Its inbound foreign keys -- constraints on *other* tables that reference the protected table -- are preserved too, so the table stays fully wired into the schema rather than left as an orphan.

**Not a cascade flag.** Unlike `DropTablesRemovedFromProduct` (an environment → product → template cascade that *suppresses* the drop pass), `PreventDrop` is a positive, per-table guard that lives with the table and persists in the database. The cascade flag answers "should this deployment run the drop pass at all?"; `PreventDrop` answers "should this specific table ever be a drop candidate?" -- and keeps answering it after the definition is gone.

### Un-protecting a table

Because the marker is sticky, clearing it is a deliberate, reviewed step -- you can't un-protect a table by simply deleting its JSON, since that's exactly the case the stickiness defends against. Two ways to remove protection:

1. **Refresh, then remove.** Set `PreventDrop: false` and re-deploy while the table is *still* in the package. That run refreshes the sticky marker to `false`. Now remove the table from the package on a later deployment and it drops normally.
2. **Drop via migration script.** Write a migration script that drops the table explicitly. Migration scripts run outside the drop-by-absence pass, so they aren't gated by `PreventDrop` at all.

> **Tip:** Reach for the refresh-then-remove path when you want the removal to flow through the normal declarative pipeline; reach for the migration script when you want the drop recorded as an explicit, reviewable step in the package.

**Cross-engine.** Identical behavior on SQL Server, PostgreSQL, MySQL, and MariaDB -- the persistence mechanism differs per engine, but the contract is the same everywhere.

> **Note:** Ownership is reconciled against the live catalog on every run. If a protected table is dropped out-of-band -- by a migration script, a DBA, or a manual change -- SchemaSmith prunes its ownership record (including the sticky marker) because the table no longer exists in the catalog. No stale protection lingers to confuse a future deployment; the marker only ever protects a table that's actually there.

### Environment-level protection — the `PreventDrop` setting

Per-table `PreventDrop` protects tables you name one at a time. Sometimes you want the opposite default: an entire environment where the deployment tool is simply not allowed to remove anything by omission -- production, a shared staging fleet, any target where an accidental drop is unacceptable. The **environment-level `PreventDrop`** setting is that blanket guardrail.

Set `PreventDrop: true` in `SchemaQuench.settings.json` (or the `SmithySettings_PreventDrop` environment variable) and, for the whole run, SchemaQuench suppresses *every* drop-by-absence pass -- tables, columns, foreign keys, check and exclude constraints, statistics, product-owned indexes, and unknown out-of-band indexes. Nothing is dropped for being absent from the product, regardless of what any package, template, or table declares. It is off by default.

```json
{
  "PreventDrop": true
}
```

**It doesn't drop -- it doesn't explode.** A protected run still completes normally (exit code `0`). SchemaQuench applies every additive and modifying change as usual, skips the drops, logs each one it withheld, and records them in the deployment summary under a `preventDrop` manifest -- so you get a precise list of what was *not* removed (`objectType` + `objectName`) without the run failing. Read the manifest to see whether a package genuinely intends those removals; if it does, deploy that package to an unprotected environment, or clear protection deliberately.

**Transient drops are untouched.** Protection suppresses only removal *by absence*. An object that is still declared but has to be dropped and recreated to apply a change -- dropping an index to alter the column it covers and putting it back, modifying a constraint, recreating a computed column whose expression changed -- reconciles exactly as it always does. Those drops are part of applying your declared schema, not removing something you left out, so protected mode never blocks them.

**How it relates to the other controls.** Three layers, narrowest-winning intent:

- **`Drop…RemovedFromProduct` cascade** — per-object-type, four-tier (environment → product → template → table). Fine-grained: "should *this kind* of by-absence drop run *here*?"
- **Per-table `PreventDrop`** — a sticky, persisted guard on *one named table*, surviving its removal from the package.
- **Environment `PreventDrop`** — a whole-run blanket: "for this deployment, don't remove *anything* by absence." The simplest possible answer when the rule is "this environment never drops."

They compose. The environment switch is the outermost guarantee; the cascade and per-table guards still apply beneath it for environments that aren't fully locked down.

**Cross-engine.** Identical behavior on SQL Server, PostgreSQL, MySQL, and MariaDB.

---

## DropColumnsRemovedFromProduct

When you remove a column from a table JSON file, SchemaQuench needs to know what to do with the column that's already in the database. `DropColumnsRemovedFromProduct` is `true` by default — columns absent from the schema package are dropped, keeping the deployed database in sync with the product definition. Set it to `false` when that drop is unsafe: a production column that other systems still read, a column you want to retire gradually with a migration script rather than a hard drop, or any environment where you want human review before structural column removal happens.

Before this setting existed, the only way to suppress column-drop-by-absence was to disable the entire table-update phase (`UpdateTables: false`), which also prevents column additions, type changes, and everything else the table quench does. `DropColumnsRemovedFromProduct` gives you a narrower knob.

**Four-tier cascade — environment → product → template → table.** The setting resolves across four tiers, evaluated in order from broadest to narrowest:

- **Environment** — `DropColumnsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropColumnsRemovedFromProduct` environment variable). Controls all products deployed in that environment.
- **Product** — `DropColumnsRemovedFromProduct` in `Product.json`. Controls a single product regardless of which environment it deploys to.
- **Template** — `DropColumnsRemovedFromProduct` in `Template.json`. Controls a single template within a product.
- **Table** — `DropColumnsRemovedFromProduct` in a table's `.json` file. Protects the columns of that one table only.

The table tier introduces a per-table tightening option that `DropTablesRemovedFromProduct` (which has no table-level equivalent) does not have. A table can set its own `false` to protect its columns even when higher tiers permit drops. It cannot set `true` to re-enable a drop that a higher tier has suppressed — the table tier can only tighten, never loosen.

**Explicit-false is sticky — hard guardrail in any direction.** A `false` at any tier locks the effective value to `false` for all lower tiers. A `true` at a lower tier overrides an inherited `true` but can never override an ancestor's explicit `false`. Absent (not set) inherits from the tier above. An environment that sets `false` suppresses column drops for the entire deployment; a product that sets `false` protects its own columns regardless of environment; a table that sets `false` protects its own columns regardless of template and product.

**Cross-engine.** Identical behavior on SQL Server, PostgreSQL, MySQL, and MariaDB. The `DropColumnsRemovedFromProduct` column in the parsed-JSON temp tables lets the per-engine `ModifiedTableQuench` procedure apply the table-tier override alongside the resolved env/product/template value — both must permit the drop before SchemaQuench removes a column.

**Environment guidance:**

- **CI and local dev** -- `true`. Detect and exercise column removals alongside the rest of schema reconciliation.
- **Test/staging** -- `true`. Same rationale — staging should mirror production intent, including column drops.
- **Production** -- Consider `false` at the environment level for teams that prefer explicit migration scripts to govern column removal. Dropping a column is a hard, data-losing operation and may break dependent queries or code that the deployment tool can't see.

**Practical example — promote one package across risk tiers.**

A common pattern: a single schema package moves through dev → staging → production. Column drops are welcome in dev and staging (reconciliation, fast feedback), but you want an explicit human step before they hit production:

```json
// SchemaQuench.settings.json (production environment)
{ "DropColumnsRemovedFromProduct": false }
```

Dev and staging settings files omit the key (default `true`). The same package deploys to all three environments; production preserves the columns until a migration script does the removal explicitly and intentionally.

**Protecting a single sensitive table.** When most tables should auto-drop columns but one carries data that must be retired carefully:

```json
// Tables/dbo.AuditLog.json
{
  "Name": "AuditLog",
  "DropColumnsRemovedFromProduct": false,
  ...
}
```

Dev and staging drop removed columns freely on all other tables; `AuditLog` columns are never auto-dropped regardless of environment or template settings.

**The rollback-friendly column removal pattern:**
1. Remove the column from the table JSON.
2. Keep `DropColumnsRemovedFromProduct: false` in the production config (or on the table).
3. Write a migration script that archives or clears the column's data, then issues the `ALTER TABLE … DROP COLUMN` explicitly.
4. Once the script has run in production, remove the table-level or environment-level override.

---

## DropForeignKeysRemovedFromProduct

When you remove a foreign key from a table's JSON, `DropForeignKeysRemovedFromProduct` controls whether SchemaQuench drops the constraint that's still in the database. It's `true` by default — foreign keys absent from the schema package are dropped, keeping the deployed database in sync. Set it `false` when an out-of-band foreign key must be preserved, or where you want human review before a constraint is removed.

It resolves across the same four tiers as [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct) — environment → product → template → table — with the same explicit-false-sticky semantics: a `false` at any tier is a hard guardrail, and a table can tighten to `false` to protect its own foreign keys but can never re-enable a higher-tier suppression.

**Only by-absence removal is gated.** A *modified* foreign key — one whose name still appears in the product but whose definition changed (columns, referenced table/columns, or `ON DELETE` / `ON UPDATE` action) — is always dropped and recreated so the new definition takes effect, regardless of this flag. The flag governs only the case where a foreign key has been removed from the product entirely.

**Cross-engine.** Identical behavior on SQL Server, PostgreSQL, MySQL, and MariaDB. On MySQL and MariaDB this flag also closes a gap: foreign-key cleanup previously required enabling `DropUnknownIndexes`, but is now governed solely by `DropForeignKeysRemovedFromProduct`, matching the other engines.

---

## DropCheckConstraintsRemovedFromProduct

When you remove a table-level CHECK constraint from a table's JSON, `DropCheckConstraintsRemovedFromProduct` controls whether SchemaQuench drops the constraint still in the database. It's `true` by default. Set it `false` to preserve an out-of-band check, or where you want review before a constraint is removed.

It resolves across the same four tiers as [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct) — environment → product → template → table — with the same explicit-false-sticky semantics: a table can tighten to `false` to protect its own check constraints but can never re-enable a higher-tier suppression.

**Table-level only; modified checks always reconcile.** This flag governs *table-level* checks (the `CheckConstraints` array). A column-level check — one driven by a column's `CheckExpression` — is reconciled by the column passes, not this flag. And only by-absence removal is gated: a check whose expression merely changed is always dropped and recreated so the new expression takes effect.

**Cross-engine — closes a normalization gap.** Previously only PostgreSQL dropped an orphaned table-level check by absence; SQL Server, MySQL, and MariaDB dropped a check only as a side effect of dropping its column, leaving a removed check in place. With this flag (default on), all four engines now reconcile orphaned table-level checks identically.

---

## DropExcludeConstraintsRemovedFromProduct

When you remove an EXCLUDE constraint from a table's JSON, `DropExcludeConstraintsRemovedFromProduct` controls whether SchemaQuench drops the constraint still in the database. It's `true` by default.

EXCLUDE constraints are a **PostgreSQL** feature, so this flag applies only to PostgreSQL — it is accepted but has no effect on SQL Server, MySQL, or MariaDB. It resolves across the same four tiers as [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct), with the same explicit-false-sticky semantics, and gates only by-absence removal: an exclude constraint whose definition merely changed is always dropped and recreated.

---

## DropStatisticsRemovedFromProduct

When you remove a statistics definition from a table's JSON, `DropStatisticsRemovedFromProduct` controls whether SchemaQuench drops the user-created statistics object still in the database. It's `true` by default.

It resolves across the same four tiers as [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct), with the same explicit-false-sticky semantics. Only by-absence removal is gated — a statistics object whose definition changed is always dropped and recreated — and **auto-created statistics are never touched**, only the named statistics your product defines.

**Cross-engine — closes a normalization gap.** Previously only PostgreSQL dropped an orphaned statistics object by absence; SQL Server dropped one only as a side effect of changing one of its columns. With this flag (default on), SQL Server and PostgreSQL now reconcile orphaned statistics identically. MySQL and MariaDB have no separate statistics objects, so the flag does not apply there.

---

## DropIndexesRemovedFromProduct

When you remove an index from a table's JSON, `DropIndexesRemovedFromProduct` controls whether SchemaQuench drops the **product-owned** index still in the database — an index SchemaSmith created and tracks. It's `true` by default.

This is distinct from [DropUnknownIndexes](#dropunknownindexes): that flag targets *out-of-band* indexes SchemaSmith never created, while this one targets indexes SchemaSmith owns that have dropped out of the definition. It resolves across the same four tiers as [DropColumnsRemovedFromProduct](#dropcolumnsremovedfromproduct), with the same explicit-false-sticky semantics: a table can tighten to `false` to protect its own indexes but cannot re-enable a higher-tier suppression.

**Index types.** Applies to nonclustered/secondary indexes that SchemaSmith manages; a primary key is never dropped by this path. All four engines gate the removed-from-product drop directly through this flag — MySQL and MariaDB previously coupled it to `DropUnknownIndexes` (so a removed index survived unless that flag was on) and are now brought to parity with SQL Server and PostgreSQL: a product-owned index removed from the definition is dropped by default.

---

## RunScriptsTwice

When `RunScriptsTwice` is `true`, the Objects-slot scripts are executed twice in succession during step 3 of the database quench sequence. On the second pass, all scripts are reset to unquenched and processed through the dependency retry loop again. Both runs must succeed -- if either fails, the deployment fails.

This is an **idempotency testing** tool, not a dependency resolution mechanism. Dependency resolution is already handled by the [retry loop](#dependency-retry-loop), which retries failed scripts as long as progress is being made. RunScriptsTwice answers a different question: "Can my `[ALWAYS]` scripts and object scripts run again safely?"

**When to use:**

- **CI pipelines** -- Verify that `[ALWAYS]` scripts are truly idempotent. If a script fails on the second run, you have caught an idempotency bug before it reaches production.
- **Local development** -- Verify idempotency as you author `[ALWAYS]` scripts.

**When not to use:**

- **Production deployments** -- It doubles the execution time for the object script phase with no production benefit. This is a testing tool.

---

## TrackRunOnceMigrations

When `TrackRunOnceMigrations` is `false`, SchemaQuench treats all migration scripts as if they had the `[ALWAYS]` suffix -- no script is recorded in `CompletedMigrationScripts`, no script is skipped based on prior runs. Every migration script in every slot runs on every deployment.

This flag is the tracking half of the datafix profile -- see [Partial-Package Deployments (Data Fixes)](#partial-package-deployments-data-fixes) for when to flip it and why it pairs with the three other profile flags.

When tracking is off, `PruneObsoleteMigrationTracking` is forced off regardless of its configured value.

---

## PruneObsoleteMigrationTracking

When `PruneObsoleteMigrationTracking` is `true` (the default), SchemaQuench removes entries from `CompletedMigrationScripts` for scripts that no longer exist in the current package. This is correct for full release deployments where the package represents the complete truth.

When `false`, existing tracking entries are left alone regardless of what scripts are in the current package. This setting is relevant for partial-package deployments -- see [Partial-Package Deployments (Data Fixes)](#partial-package-deployments-data-fixes) for the full profile.

This setting is ignored when `TrackRunOnceMigrations` is `false` (no tracking means no pruning).

### With Target scope

When `Target.Templates`, `Target.Databases`, or `Target.Schemas` is set, prune is restricted to the iterations that ran in that deployment. A prune pass only examines tracking rows that match the active `(template, schema_name)` scope for each executed iteration -- it does not touch rows belonging to templates, databases, or schemas that were excluded by the filter.

> **Warning:** This scope restriction is correctness-critical, not a limitation. Without it, a deployment scoped to `tenant_newco` would delete tracking rows for `tenant_acme`, `tenant_beta`, and every other schema that was excluded from the run -- those rows are correct records of migrations already applied against scopes you explicitly chose not to touch. The restriction ensures prune behaves like a scoped operation whose boundary exactly matches the `Target` filter. See [Target](#target) for how the filter is configured.

---

## ShouldApplyExpression and Conditional Deployment

`ShouldApplyExpression` is a SchemaQuench feature that lives on the schema package side. Whenever SchemaQuench evaluates a table component that has a `ShouldApplyExpression` set, it resolves any tokens in the expression, runs the expression against the target database, and skips the component if the result is falsy. This means a single table file can declare components that only apply on certain databases, certain environments, or certain server versions -- no per-environment file copies, no branching logic in your deployment pipeline.

See [Schema Packages -- Conditional Application](schema-packages.md#conditional-application) for the JSON shape and worked examples, and [Custom Properties](custom-properties.md) for how to drive `ShouldApplyExpression` values from team-defined metadata.

The same primitive works one level up: a **script folder** can carry a `ShouldApplyExpression` too. Put it on any product- or template-level folder definition (alongside `ServerToQuench` / `QuenchSlot`). Blank deploys the folder as always; a non-blank expression is evaluated against the target and the folder's scripts deploy only when it returns true -- false skips the entire folder (and its sub-folders), logged so you can see why. A folder expression is run as a scalar query against the target, so write it as a `SELECT` that returns a boolean (or `1`/`0`) -- for example `SELECT CASE WHEN @@version LIKE '%MariaDB%' THEN 1 ELSE 0 END`. It can read `SERVERPROPERTY` / `@@version`, call an environment-type function your team already has, query a control table, or reference resolved tokens -- **template**-folder gates get the full vocabulary, including `{{SchemaName}}` on schema templates; **product**-folder gates get a narrower set, below. Product folders are evaluated per server; template folders are evaluated per database -- and per schema for schema templates -- so the same folder can deploy to one target and be skipped on another in a single run.

> **Note:** A **product**-folder expression runs against the server's admin connection (the platform's init database -- `master` / `postgres` / `information_schema`), because product-level scripts are server-scoped -- no database is selected yet. Use server-scoped predicates there (server properties, version, edition). Of the resolved tokens, only `{{ServerMajorVersion}}` is available at this scope (the server connection already exists); `{{CompatibilityLevel}}` (a property of a database) and `{{SchemaName}}` (a template-iteration concept) are not, and referencing either reaches the server as literal, unresolved text and fails the deployment rather than silently evaluating to the wrong thing. A **template**-folder expression runs against the actual target database (and schema for schema templates), so it can also query target-database state and use the full token set.

This turns "different folders for different flavors of a target" into a declarative property instead of pipeline branching: a `MariaDB/` folder gated on `SELECT CASE WHEN @@version LIKE '%MariaDB%' THEN 1 ELSE 0 END` beside a `MySQL/` folder gated on the negation, a `Jobs/` folder skipped on Azure SQL (`SELECT CASE WHEN SERVERPROPERTY('EngineEdition') <> 5 THEN 1 ELSE 0 END`), or a `TableData/TestData/` folder kept out of production by your environment predicate.

> **Note:** A folder's `ShouldApplyExpression` must return a boolean. If it errors -- a SQL mistake, a missing function -- the deployment fails with a clear message naming the folder, rather than silently skipping it. A gate that quietly dropped schema folders would be the dangerous failure mode, so the engine fails closed.

### Gating on the target version

The most common gate is "apply this only on a new enough target." The `{{ServerMajorVersion}}` and `{{CompatibilityLevel}}` [script tokens](script-tokens.md#servermajorversion-and-compatibilitylevel) expose the version SchemaSmith already detects, so you write one portable integer comparison instead of each engine's native version SQL. The two are not interchangeable — **gate syntax on compatibility level, gate features on server version**:

```jsonc
// A folder whose scripts use OPENJSON / STRING_AGG — compat-130 syntax.
// Gate on the DATABASE compatibility level, because a modern binary can still
// host a compat-100 database where that syntax parse-errors.
"ShouldApplyExpression": "{{CompatibilityLevel}} >= 130"

// A folder that uses a server-version-only capability. Gate on the binary.
"ShouldApplyExpression": "{{ServerMajorVersion}} >= 16"
```

The tokens resolve per target database, so the same folder deploys to a modern database and skips on a legacy one in a single run. The shipped `Demos/Conditional/SqlServer-CompatLevelGate` demonstrates the compatibility-level gate end to end. See [Script Tokens — {{ServerMajorVersion}} and {{CompatibilityLevel}}](script-tokens.md#servermajorversion-and-compatibilitylevel) for the full syntax-vs-feature explanation and the cross-engine `{{CompatibilityLevel}}` fallback.

On a **product** folder, only `{{ServerMajorVersion}}` is available -- there's no database selected yet at that scope, so `{{CompatibilityLevel}}` doesn't resolve. Gate a product-level folder on the server binary, not the database compatibility level.

---

## Script-Level Runtime Skip

`ShouldApplyExpression` covers skip decisions that a SQL expression can make from outside the script. When the decision requires logic that can only run from inside the script -- querying row state, checking role membership, branching on a result from a prior batch -- the script raises the sentinel error instead. SchemaQuench recognizes the sentinel as an intentional skip, logs it, and continues the deployment without an error.

### Sentinel constant

```
SCHEMASMITH: SHOULD NOT APPLY
```

The match is trimmed and case-insensitive. The message must be the entire error message -- an unrelated error that merely contains the phrase does not trigger a skip. Any error with a different message still surfaces as a real failure.

### Per-platform raise

| Platform | Raise form |
|----------|-----------|
| SQL Server | `RAISERROR('SCHEMASMITH: SHOULD NOT APPLY', 16, 1)` |
| PostgreSQL | `RAISE EXCEPTION 'SCHEMASMITH: SHOULD NOT APPLY'` |
| MySQL | `SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SCHEMASMITH: SHOULD NOT APPLY'` |
| MariaDB | `SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SCHEMASMITH: SHOULD NOT APPLY'` |

> **Warning:** SQL Server severity must be ≥ 11. `RAISERROR` at severity ≤ 10 is an informational message -- SchemaQuench does not see it and the script continues executing. Severity 16 is the conventional choice.

### Batch semantics

The sentinel may appear in any batch of a multi-batch script, not only at the top. When the sentinel fires, SchemaQuench stops processing the remaining batches. Earlier batches that already ran are committed -- the engine does not wrap the script in a transaction. The user owns the partial-work semantics.

### Tracking behavior

A migration script (in the `Before`, `BetweenTablesAndKeys`, `AfterTablesScripts`, or `After` slot) that raises the sentinel is recorded in `CompletedMigrationScripts` as completed. It will not be retried on the next deployment. Tracking is per-database and per-schema (for schema templates), so a skip in one database never affects another -- a database with different state re-evaluates the script independently.

### Script surface coverage

| Surface | Sentinel honored |
|---------|-----------------|
| Before / After scripts | Yes |
| Object scripts (procedures, views, functions) | Yes |
| Migration scripts | Yes |
| `[ALWAYS]` scripts | Yes |
| Validation scripts | No -- express N/A through conditional logic inside the validation |
| Tool-generated SQL | No -- use `ShouldApplyExpression` on the component |

For a narrative walkthrough and decision guide (when to use the sentinel vs. `ShouldApplyExpression`), see [Power Workflows -- Runtime sentinel skip](../guide/09-power-workflows.md#runtime-sentinel-skip).

---

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Successful quench (or passing pre-flight). All databases quenched, logs backed up. |
| 2 | Failure. One or more database quenches failed; a `--TestConnection` / `--PreviewTargets` pre-flight found a connection error, version violation, or required-template target miss; or a `--Validate` pass found an Error-severity finding (warnings alone still exit `0`). |
| 3 | Unhandled exception. An unexpected error occurred outside the normal quench flow. |
| 4 | Unable to back up log files. |

---

## Table Data Delivery

Reference data doesn't have to live in a pile of hand-rolled `MERGE` scripts. Declare what each table looks like -- where its rows come from, how the merge should behave, which columns identify a row -- and SchemaQuench delivers the data in foreign-key order, resolving the tricky cases automatically.

Each table that participates in data delivery carries a `DataDelivery` block in its JSON. SchemaQuench walks every table JSON, keeps the ones that declare delivery, orders them by foreign key dependencies, and merges each one using the platform's preferred idiom. Tables without a `DataDelivery` block are left untouched.

See [Schema Packages -- DataDelivery](schema-packages.md#datadelivery) for the full property reference and examples.

### Gated deliveries and variants

A table's `DataDelivery` block can also be an array of independently-gated deliveries, each with its own `ShouldApplyExpression`. SchemaQuench evaluates every delivery's gate against the target once per quench, before any content file is read, and every delivery whose gate passes applies -- not just the first match. A delivery whose gate evaluates false is logged as skipped and never touches the table; a delivery whose gate expression itself errors aborts the deployment (fail-closed), the same way a folder-level gate does.

A delivery's gate resolves the same token vocabulary a folder gate resolves: [`{{ServerMajorVersion}}` and `{{CompatibilityLevel}}`](script-tokens.md#servermajorversion-and-compatibilitylevel) for version gating (see [Gating on the target version](#gating-on-the-target-version) above), plus `{{SchemaName}}` inside a schema-template iteration. A dev/test-only delivery can therefore combine both in one gate -- `"{{CompatibilityLevel}} >= 130 AND DB_NAME() LIKE '%_dev'"` -- without hand-writing the version predicate.

Gate evaluation also runs under `WhatIfONLY`, so a dry run reports the same deliver-vs-skip decisions a real quench would make. The `Insert/Update/Delete` CASCADE-FK check (below) only considers deliveries whose gate is currently passing -- a `Delete` variant that's gated off this run can't abort the deployment over a CASCADE FK it will never execute.

See [Schema Packages -- Multiple Deliveries](schema-packages.md#multiple-deliveries) for the JSON shape, the three common gating patterns, and the delete-overlap warning for multi-delivery tables that mix `Insert/Update/Delete` with overlapping `MergeFilter`s.

### Two-pass FK-aware delivery

Foreign keys turn "load the data" into a graph problem. SchemaQuench solves it automatically:

1. **Pass 1** -- Tables whose required (NOT NULL) foreign keys all point to already-loaded tables are merged first. Nullable FK columns pointing to tables not yet delivered are **deferred** -- the initial merge inserts rows with those columns NULL so the load doesn't block on a constraint that references a row that doesn't exist yet.
2. **Pass 2** -- After every pass-1 table has been delivered, each deferred table's nullable FK columns are back-filled by re-merging the same data with only the deferred columns in play.

Log output tags pass 1 and pass 2 distinctly:

```
  Delivering dbo.Customer (pass 1 - deferred columns as NULL)
  Delivering dbo.Customer (pass 2 - updating deferred FK columns)
```

A circular dependency among NOT NULL foreign keys fails the dependency sort -- SchemaQuench logs the cycle and the quench fails. Make one side of the cycle nullable (or reshape the data model) so delivery can break the loop.

### MergeType options

- `Insert` -- Missing rows inserted. Existing rows and extra rows left alone. The seed-data pattern.
- `Insert/Update` -- Missing rows inserted, changed rows updated. Extra rows left alone. Good for reference tables that environments may append to.
- `Insert/Update/Delete` -- Full sync. Missing rows inserted, changed rows updated, and target rows not present in the source are deleted. Default, and what the demo products use.

The chosen idiom varies per platform -- `MERGE` on SQL Server and PostgreSQL, `INSERT ... ON DUPLICATE KEY UPDATE` plus conditional delete on MySQL -- but the `MergeType` you declare is the same contract everywhere.

### DataDelivery vs hand-written Table Data scripts

You can use both. For each target database, SchemaQuench first delivers every table with a `DataDelivery` block in FK order, then runs any `.sql` files you dropped into the template's `TableData`-slot folders through the dependency retry loop. Use declarative `DataDelivery` for bulk reference data and keep the script slot for special cases -- conditional seeds, one-off rebuilds, procedural loads.

---

## Checkpoint and Resume

Long deployments fail. Network blips, transient lock timeouts, a migration script that tripped on bad data at step 14 of 20. Without checkpointing, a failure in the final stretch means the next run starts from zero -- re-running every step you've already successfully applied.

SchemaQuench writes checkpoints as it goes. Every completed quench step and every completed migration script is recorded to disk. On the next run, already-completed work is skipped and execution resumes at the first incomplete step.

### Enabling resume

```bash
SchemaQuench --ResumeQuench
```

With `--ResumeQuench`, SchemaQuench reads the existing checkpoint files (if any) and skips anything already recorded as complete. Without the switch, the resume logic is off -- every step executes regardless of prior state.

### Where checkpoints live

```bash
SchemaQuench --CheckpointDirectory:/var/schemasmith/checkpoints
```

By default, checkpoints live in `%TEMP%/schemaquench-checkpoints` (or the platform equivalent). Override with `--CheckpointDirectory:<path>` when you need them on a specific volume -- for a CI runner with ephemeral temp storage, a shared build server, or a mounted volume that outlives the container. The directory is created if it doesn't exist.

The same value can be set in `SchemaQuench.settings.json` via the `CheckpointDirectory` key. The CLI switch wins if both are present.

### Checkpoint scopes

SchemaQuench tracks two kinds of progress:

**Product-scoped** -- Cross-database work shared by all templates:
- `Before` and `After` product-level scripts (per server, for SQL Server Availability Group deployments).
- Completed templates (a template with every database finished is itself recorded as complete).

**Database-scoped** -- One checkpoint file per `{product, template, server, database}` combination:

| Step name | What it covers |
|---|---|
| `KindleForge` | Helper procedure deployment for this database. |
| `ValidateBaseline` | Baseline validation script. |
| `MissingTablesAndColumns` | Adding missing tables and missing columns. |
| `ModifiedTables` | Altering existing columns, computed / generated columns, dropping tables. |
| `IndexesAndConstraints` | Creating missing indexes, check constraints, defaults, statistics. |
| `TableDataDelivery` | Both passes of FK-aware data delivery for tables with `DataDelivery` blocks. |
| `ForeignKeys` | Creating, modifying, and dropping foreign keys. |
| `MaterializedViewQuench` | PostgreSQL materialized view deployment. |
| `IndexedViewQuench` | SQL Server indexed view deployment. |
| `VersionStamp` | Version stamp script. |

In addition, each template slot (`Before`, `Objects`, `BetweenTablesAndKeys`, `AfterTablesScripts`, `AfterTablesObjects`, `TableData`, `After`) records the exact scripts that ran, so resumed runs skip each individual script that already succeeded.

### Automatic cleanup after success

Checkpoints exist to protect against failures. When the quench completes without error, SchemaQuench deletes every checkpoint file associated with the product. A clean run leaves no residue to mislead the next deployment. A failed run leaves the checkpoint files in place, ready for the next `--ResumeQuench` invocation.

### Practical resume workflow

A 90-minute deployment to a large production database fails at minute 75 because a migration script hit a transient deadlock. You fix the data, re-run the deployment:

```bash
SchemaQuench --ResumeQuench
```

SchemaQuench reads the checkpoints, sees that KindleTheForge, ValidateBaseline, missing tables, modifications, indexes, constraints, and every `Objects`-slot script already succeeded, logs what it's skipping, and picks up at the first incomplete step. Minutes of work instead of starting from the top.

### When to leave resume off

- **Normal, clean deployments** -- The resume flag is opt-in. Without it, every step executes, and at the end the checkpoint files get cleaned up regardless. There's no cost to leaving it off for fast, green runs.
- **CI pipelines that rebuild databases from scratch** -- Each run is a fresh slate, so resume has nothing to do.

Use `--ResumeQuench` when you specifically expect that a prior run may have left partial state -- typically when re-running after a real failure in a non-trivial deployment.

---

## Related Documentation

- [Configuration Reference](configuration.md) -- Shared configuration system, CLI switches, environment variables
- [Schema Packages Reference](schema-packages.md) -- Package structure, folder layout, execution order
- [Multi-Tenant Deployments](../guide/10-multi-tenant-deployments.md) -- Full walkthrough of schema-per-tenant and database-per-tenant patterns
- [Custom Properties](custom-properties.md) -- The Extensions carrier and how it drives `ShouldApplyExpression`
- [Script Tokens Reference](script-tokens.md) -- Token replacement, advanced tags, automatic tokens
- [SchemaTongs Reference](schematongs.md) -- Extraction tool that creates schema packages
