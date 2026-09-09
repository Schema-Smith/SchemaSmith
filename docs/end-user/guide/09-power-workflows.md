# Power Workflows

You've built your schema, validated it, and set up your deployment pipeline. Now it's time to handle the real-world complexity that every production system eventually demands. Script tokens let a single codebase span dev, staging, and production without manual edits. Multi-database products keep related databases in lockstep with a single deployment. DataTongs turns reference data into version-controlled, repeatable scripts. Custom metadata via `Extensions` drives deployment-time decisions. Secondary servers keep Availability Groups in sync. And execution slots give you precise control over exactly when each script runs. These are the features that let you deploy with confidence at scale -- solving problems that would otherwise require manual intervention or fragile scripting. And every one of them is in the Community offering, for every supported platform, free.

## Script tokens

Your product deploys to dev, staging, and production. One of your stored procedures needs to query a linked reporting database -- but the database has a different name in each environment. Hardcoding the name means maintaining three copies of the script. Script tokens solve this cleanly.

**Step 1: Define the token in Product.json.**

```json
{
  "Name": "MyProduct",
  "Platform": "SqlServer",
  "ScriptTokens": {
    "ReportingDB": "ReportingDev",
    "ReleaseVersion": "2.1.0"
  }
}
```

**Step 2: Use the token in your SQL scripts.**

```sql
CREATE OR ALTER PROCEDURE [dbo].[GetSalesReport]
AS
BEGIN
  SELECT s.OrderDate, s.Total, r.RegionName
    FROM [dbo].[Sales] s
    JOIN [{{ReportingDB}}].[dbo].[Regions] r ON s.RegionID = r.RegionID
END
```

**Step 3: Override per environment via environment variables.**

For deployment to staging or production, override any token without touching the config files:

```
SmithySettings_ScriptTokens__ReportingDB=ProdReporting
```

One script. Three environments. Zero manual edits between them.

Two tokens are always available automatically: `{{ProductName}}` (from `Product.json` Name) and `{{TemplateName}}` (from `Template.json` Name). Token replacement is case-insensitive.

### Advanced token tags -- the quiet superpower

This is where tokens go from "find and replace" to "deployment-time content engine." The *value* you give a token can start with a special tag that tells SchemaSmith to resolve it dynamically: read a file from disk, query the live server, or embed a specific table's JSON. These aren't teaser features -- they're in Community today, free, for every supported platform.

**Embed a file's contents:**

```json
"ScriptTokens": {
  "ReferenceData": "<*File*>resources/reference-data.sql"
}
```

The `{{ReferenceData}}` token in any script resolves to the entire contents of `resources/reference-data.sql`. Long migration bodies can live in their own files and be included where needed.

**Embed a binary file as hex:**

```json
"ScriptTokens": {
  "DefaultLogo": "<*BinaryFile*>resources/logo.png"
}
```

`{{DefaultLogo}}` becomes a `0x...` hex literal, ready to drop into a `VARBINARY` insert on SQL Server or a `BYTEA` literal on PostgreSQL.

**Query the target server at deployment time:**

```json
"ScriptTokens": {
  "ActiveTenants": "<*Query*>SELECT TenantId FROM dbo.Tenants WHERE Active = 1"
}
```

The query runs once against the actual server you're deploying to, and `{{ActiveTenants}}` resolves to a newline-separated list of tenant IDs. Your migration script can consume that list without any hardcoded environment knowledge.

**Embed one specific table's JSON:**

```json
"ScriptTokens": {
  "OrdersTable": "<*SpecificTable*>dbo.Orders"
}
```

Useful when a script needs to introspect exactly one table's shape -- a schema-aware stored procedure that builds audit triggers, for example, or a diagnostic query that reads column metadata.

And at the template level, the automatic tokens `{{TableSchema}}`, `{{IndexedViewSchema}}` (SQL Server), and `{{MaterializedViewSchema}}` (PostgreSQL) give you the entire current template's table or view model serialized as JSON -- ready to drop into a stored procedure call or a dynamic SQL generator.

For the full token reference -- scopes, custom-property tokens from `Extensions`, resolution order, every advanced tag -- see [Script Tokens Reference](../reference/script-tokens.md).

## Custom properties that drive behavior

Tokens aren't just for deployment parameters. Custom metadata you attach to your tables via the `Extensions` carrier becomes available as tokens in every expression field on that table and its components. This is where SchemaSmith starts to feel like a governance platform instead of just a deployment tool.

The `Extensions` property is an open JSON bag that lives on every entity in the schema package -- tables, columns, indexes, foreign keys, check constraints, all of them. Attach whatever your team cares about: environment markers, data classification tags, team ownership, replication configuration, sensitivity flags. One declaration rides through source control, survives SchemaTongs re-extraction, and flows into every `ShouldApplyExpression` on that component.

**Declare once:**

```json
{
  "Name": "[Orders]",
  "Extensions": {
    "Environment": "Production",
    "DataClassification": "PII",
    "OwningTeam": "Identity"
  },
  "Indexes": [
    {
      "Name": "[IX_Orders_AuditCreatedAt]",
      "IndexColumns": "[CreatedAt]",
      "ShouldApplyExpression": "'{{Table.Environment}}' = 'Production'"
    }
  ]
}
```

**Consume everywhere:** `{{Table.Environment}}` drives whether the audit index is created. `{{Table.DataClassification}}` could drive a script token that generates masking policies. `{{Table.OwningTeam}}` could drive ownership tags. A component's own `Extensions` keys flatten to `{{KeyName}}`; the parent table's `Extensions` keys are available as `{{Table.KeyName}}` anywhere inside that table.

**Full table graph as JSON.** At the template level, the automatic `{{TableSchema}}` token carries the entire current template's table graph serialized as JSON -- every table, every column, every index, all your `Extensions` metadata included. A migration script can consume this token, parse it in-engine (via `JSON_VALUE`, `json_each`, or `JSON_TABLE` depending on platform), and drive behavior entirely from your declared metadata. Write a single script that reads column-level sensitivity flags and generates dynamic masking policies. Write a procedure that reads team-ownership tags and builds an audit trail. The metadata lives in your schema package, travels through source control, and gets acted on at deployment time -- without any external tooling.

**Extensions round-trip.** When SchemaTongs re-extracts a table from the live database, it preserves the `Extensions` bag from your original table JSON. Your custom properties aren't lost to the extraction cycle -- they round-trip intact.

See [Custom Properties](../reference/custom-properties.md) for the full mechanism -- scoping, nested objects, array flattening, preservation through SchemaTongs re-extraction, and the JSON Schema validation pattern for team governance.

## Extensions as a source of truth

The section above put custom metadata to work *gating* deployments -- a flag on a table decides whether an index gets built. But the same metadata is readable as a whole. A deploy-time script can read your *entire* model -- every table, every column, and every `Extensions` bag you hung on them -- and derive something new from it on each quench. This is where a schema package stops merely describing structure and becomes the single source of truth your other tooling reads from.

The `{{TableSchema}}` token from [Script tokens](#script-tokens) above is the key: it hands a script the whole template's table model as JSON, every `Extensions` bag intact. Shred it in a deploy-time script -- `OPENJSON` on SQL Server, `json_each` / `JSON_TABLE` on PostgreSQL and MySQL -- and walk the tables, reading their metadata:

```sql
-- Read the whole model; keep only the tables your metadata flagged:
SELECT t.[value]
FROM OPENJSON(N'{{TableSchema}}') AS t
WHERE JSON_VALUE(t.[value], '$.Extensions.ReplicationEnabled') = 'true';
```

Because the script runs on every deploy and reads the live model, whatever it builds can never drift from the schema it is derived from. Two patterns show the range:

**A data dictionary.** Tag your tables and columns with the facts a governance team cares about -- a business domain, a data owner, a sensitivity level -- and a deploy-time script shreds the `Extensions` and `MERGE`s a queryable `DataDictionary` table on every quench. New column, new row; renamed column, updated row; dropped column, gone. It is *computed* from the schema, so it stays in step -- unlike the spreadsheet that was right the day it was made and wrong ever since. Works the same on every engine.

**A provisioned replica.** Mark which tables replicate -- `"ReplicationEnabled": true` on the ones that belong on a reporting subscriber -- and a deploy-time script filters the model to the marked set and provisions the subscriber's schema to match. Changing what replicates is a one-flag edit on the model, not a hand-cut DDL script that falls behind. (SQL Server, where one connection can reach a second database.)

The move is the same in both: declare the fact once, on the object it is true of, and let a script derive the rest on every deploy. Structure, governance metadata, replication intent -- all in the same file, under the same review, in the same source control.

For the token that makes it work, see [Script Tokens](../reference/script-tokens.md); for the metadata carrier, see [Custom Properties](../reference/custom-properties.md).

## Conditional deployment with ShouldApplyExpression

`ShouldApplyExpression` is how you declare "this thing only exists in certain places." It lives on tables, columns, indexes, foreign keys, check constraints, indexed views, materialized views, full-text indexes -- every structural component. Before deploying any component with a `ShouldApplyExpression`, SchemaQuench resolves the tokens, runs the expression against the target database, and skips the component if the result is falsy.

```json
{
  "Name": "[LargeTextColumn]",
  "DataType": "NVARCHAR(MAX)",
  "Nullable": true,
  "ShouldApplyExpression": "SERVERPROPERTY('EngineEdition') NOT IN (5, 8)"
}
```

This column exists on on-prem SQL Server but not Azure SQL Database. One table definition, two deployments, no per-environment file copies.

```json
{
  "Name": "[IX_Orders_ReplicaOptimized]",
  "IndexColumns": "[OrderDate], [CustomerId]",
  "ShouldApplyExpression": "DB_NAME() LIKE '%_replica'"
}
```

An index that only exists on tables whose database name ends with `_replica`. Trivial to declare, impossible to get wrong, reviewable in the PR diff.

Full-text indexes (SQL Server) take this one step further: declare an **array** of full-text variants on a table -- each with its own catalog and a `ShouldApplyExpression` -- and each target deploys only the variant that matches it. One schema package, region-specific full-text catalogs, no per-environment package forks.

### Naming your variants

When a component carries several same-named variants gated by mutually exclusive expressions, give each one a `VariantName`. The expression says *when* a variant applies; the name says *why* it exists -- and that name is how a human (or a downstream tool) tells two complex, SQL-gated variants apart at a glance.

```json
"Indexes": [
  {
    "Name": "[IX_Orders_Region]",
    "VariantName": "Modern engines",
    "IndexColumns": "[Region]",
    "FilterExpression": "[Region] IS NOT NULL",
    "ShouldApplyExpression": "SERVERPROPERTY('ProductMajorVersion') >= 16"
  },
  {
    "Name": "[IX_Orders_Region]",
    "VariantName": "Legacy engines",
    "IndexColumns": "[Region]",
    "ShouldApplyExpression": "SERVERPROPERTY('ProductMajorVersion') < 16"
  }
]
```

Both variants share a name and target the same column, but only one matches any given server. When the matching variant deploys, its name rides along in the log:

```
  Creating index dbo.Orders.IX_Orders_Region (variant: Modern engines)
```

The same `(variant: ...)` suffix appears in WhatIf output, so a dry run tells you exactly which variant *would* be applied to each target -- before you commit to it. `VariantName` is metadata only: it has no effect on what gets deployed, it just makes the deployment legible. It's an optional label, up to 128 characters, and it round-trips through SchemaTongs re-extraction alongside the rest of the variant set.

For deploying a partial package of migration scripts to fix a specific production issue, see [Operational Profiles](07-cicd-integration.md#operational-profiles) in the CI/CD chapter and [Partial-Package Deployments (Data Fixes)](../reference/schemaquench.md#partial-package-deployments-data-fixes) in the SchemaQuench reference.

### Conditional deployment: whole table vs. within a table

`ShouldApplyExpression` works at more than one level, and it helps to keep them straight:

- **Within a table.** Columns, indexes, foreign keys, check constraints, statistics, and the other components inside a table file can each declare conditional variants -- same name, mutually exclusive expressions -- and the matching one is chosen per target. These variant sets are fully preserved when SchemaTongs re-extracts the table.
- **A table's data.** A table's `DataDelivery` block works differently from the components above: it can be an array of gated deliveries, but *every* delivery whose gate passes applies -- not just one match. That's what makes additive patch slices possible alongside mutually-exclusive environment variants. See [Gating deliveries by environment](#gating-deliveries-by-environment) below.
- **The whole table.** A table-level `ShouldApplyExpression` gates the entire table present-or-absent on a given target, and that gating round-trips through extraction too.
- **A whole folder.** A product- or template-level script folder can carry a `ShouldApplyExpression`, so an entire folder of scripts deploys or is skipped per target -- a `MariaDB/` variant gated on `@@version`, a `Jobs/` folder skipped on Azure SQL, or `TableData/TestData/` kept out of production. Its tokens are resolved before evaluation, like every other gate. See [Conditional Deployment](../reference/schemaquench.md#shouldapplyexpression-and-conditional-deployment).

**Recommendation:** to vary a table's *structure* by target, prefer component-level variants inside a single table file (or give the structurally different tables distinct names). Two separate same-named whole-table variant files will deploy correctly, but SchemaTongs normalizes a table to one file per name on extraction, so a multi-file same-named-table layout isn't reproduced when you re-extract.

### Runtime sentinel skip

Sometimes the skip decision can't be expressed as a static expression in the package at all -- it depends on something only the target server can answer at the moment the script runs. Is this database on a replica? Did a prior script's data migration land correctly? Is this a tenant that hasn't opted into a feature yet? `ShouldApplyExpression` covers those cases when the answer is a SQL query. But if the logic is inside the script itself -- reading row counts, calling a stored procedure, checking a role membership, branching on a version+edition combination -- you need the script to decide at runtime.

Raise the sentinel error and SchemaQuench treats the script as an intentional skip, not a failure:

```sql
-- SQL Server
IF SERVERPROPERTY('EngineEdition') NOT IN (5, 8)
    RAISERROR('SCHEMASMITH: SHOULD NOT APPLY', 16, 1);
```

```sql
-- PostgreSQL
DO $$
BEGIN
    IF current_setting('server_version_num')::int < 150000 THEN
        RAISE EXCEPTION 'SCHEMASMITH: SHOULD NOT APPLY';
    END IF;
END;
$$;
```

```sql
-- MySQL
SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SCHEMASMITH: SHOULD NOT APPLY';
```

SchemaQuench recognizes the exact message `SCHEMASMITH: SHOULD NOT APPLY` (trimmed, case-insensitive, matched as the entire message). It logs the skip and moves on -- the deployment succeeds. Any other error still surfaces as a real failure.

> **Warning:** SQL Server severity matters. `RAISERROR` at severity ≤ 10 is an informational message, not an error -- SchemaQuench never sees it and the script continues executing. **Use severity ≥ 11** (16 is the conventional choice) so the raise is an abort-level error that SchemaQuench can catch.

**Any batch may carry the sentinel** -- not just the top of the script. Earlier batches that already ran are committed (the engine does not wrap the script in a transaction). The sentinel stops the rest of the script; the work those earlier batches did is preserved. "Do real setup in early batches, then decide later batches shouldn't apply" is fully supported -- you own the partial-work semantics.

**Run-once scripts record a sentinel skip as completed.** A migration script that raises the sentinel is recorded in `CompletedMigrationScripts` just like a script that ran normally -- it will not be retried on the next deployment. The skip decision is per-database, so a database with different state re-evaluates independently.

**Which script surfaces honor the sentinel:** Before/After scripts, object scripts (procedures/views/functions), migration scripts, and `[ALWAYS]` scripts all respect the sentinel. Validation scripts do not -- a validation should express "N/A here" through its own conditional logic, and it must still fail on a real error. Tool-generated SQL does not raise the sentinel; use `ShouldApplyExpression` on the component instead.

Think of the sentinel as the `ShouldApplyExpression` for logic that can only run inside the script. Use `ShouldApplyExpression` when a single SQL expression makes the call; use the sentinel when the script needs to inspect, branch, or call procedures before it can decide.

For the full mechanics -- match rules, per-platform raise forms, slot coverage table -- see [SchemaQuench -- Script-Level Runtime Skip](../reference/schemaquench.md#script-level-runtime-skip).

## Multi-database products

Some applications span more than one database -- a main transactional database plus a reporting database, or a primary database plus an audit log. SchemaSmith handles this as a single product with multiple templates. One quench, all databases updated.

**Product.json defines the deployment order:**

```json
{
  "Name": "MyProduct",
  "Platform": "SqlServer",
  "TemplateOrder": ["Main", "Reporting"],
  "ScriptTokens": {
    "MainDB": "AppMain",
    "ReportingDB": "AppReporting"
  },
  "ValidationScript": "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM master.sys.databases WHERE [name] = '{{MainDB}}') THEN 1 ELSE 0 END AS BIT)"
}
```

**Each template identifies its own target database:**

```json
{
  "Name": "Main",
  "DatabaseIdentificationScript": "SELECT [name] FROM master.sys.databases WHERE [name] = '{{MainDB}}'"
}
```

Each template has its own complete folder structure -- `Tables/`, `Procedures/`, `Before Scripts/`, and so on. SchemaQuench quenches them in the order specified by `TemplateOrder`. One run, both databases updated, all in a single atomic operation from the pipeline's perspective.

**Multi-tenant in one template.** The `DatabaseIdentificationScript` can return multiple rows. One template, one declaration, every tenant database gets the same schema. Combine with `MaxThreads` in the SchemaQuench settings file and the tenants deploy in parallel up to the configured limit.

**Schema-per-tenant.** If your tenants share one database -- each tenant owning their own schema -- a schema template fans out the same way. Add `SchemaIdentificationScript` to `Template.json` and SchemaQuench runs the full template once per returned schema, with `{{SchemaName}}` available everywhere. The pattern, the field reference, and a full walkthrough of the TenantCRM demo are in [Multi-Tenant Deployments](10-multi-tenant-deployments.md).

## Validation gates

Every SchemaQuench run has a pre-flight window before a single table is touched. That window exists to answer one question: is this the right server, in the right state, for this package? Two properties -- `ValidationScript` and `BaselineValidationScript` -- give you precise control over those gates, and they work together as a versioned handshake between your package and its deployment targets.

**Right server.** `ValidationScript` is a required Product-level property. It runs first, against the server's admin connection (the platform's init database: `master`, `postgres`, or `information_schema`). If it returns a falsy value, the deployment aborts before any quench begins. This is your identity check: am I on the database server I think I am? Common patterns:

- Verify an expected database exists on the server
- Confirm a linked server or infrastructure dependency is in place
- Gate on server version or edition to prevent running an incompatible package

```sql
-- SQL Server: confirm the target database exists before touching anything
SELECT CAST(CASE WHEN EXISTS(
    SELECT 1 FROM master.sys.databases WHERE [name] = '{{MainDB}}'
) THEN 1 ELSE 0 END AS BIT)
```

```sql
-- Version gate: abort if the server is below SQL Server 2019
SELECT CAST(CASE WHEN CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 15
    THEN 1 ELSE 0 END AS BIT)
```

Token replacement applies -- `{{MainDB}}`, `{{ReleaseVersion}}`, any token you define in `ScriptTokens` -- so the validation script isn't hardcoded to one environment.

**Right state.** `BaselineValidationScript` is optional and runs at two levels. At the Product level, it runs after server validation but before template processing. At the Template level, it runs per database, before that database's quench. Both answer the same question: is this environment at the state this package expects?

The canonical use is anti-rollback protection. Pair `BaselineValidationScript` with `VersionStampScript`: the stamp records a version identifier after a successful deployment; the baseline checks for that identifier before the next deployment. If someone accidentally runs an older package against an already-upgraded environment, the baseline check aborts before any harm is done.

```json
{
  "VersionStampScript": "UPDATE dbo.DeploymentInfo SET Version = '{{ReleaseVersion}}'",
  "BaselineValidationScript": "SELECT CAST(CASE WHEN EXISTS(SELECT 1 FROM dbo.DeploymentInfo WHERE Version = '{{PreviousVersion}}') THEN 1 ELSE 0 END AS BIT)"
}
```

**Version floor.** `MinimumVersion` is the declarative companion -- set it in `Product.json` and SchemaQuench enforces a hard engine version floor before running anything. Use it for the unconditional floor ("this package requires PostgreSQL 15 or later"); write `ValidationScript` logic for conditional gates or checks that need to read server state.

For the full property reference -- Product vs. Template scope, return type requirements, token availability, and worked examples across all four platforms -- see [Schema Packages](../reference/schema-packages.md) and [SchemaQuench](../reference/schemaquench.md).

## Secondary server support (SQL Server)

For SQL Server deployments targeting Availability Groups, SchemaQuench can quench to a primary replica plus one or more secondaries in parallel. Configure the secondary list on the target connection:

```json
{
  "Target": {
    "Server": "primary-replica",
    "SecondaryServers": "secondary-1,secondary-2"
  }
}
```

Then declare per-folder routing on your product-level folders:

```json
{
  "ScriptFolders": [
    { "FolderPath": "Before Product",       "QuenchSlot": "Before", "ServerToQuench": "Both" },
    { "FolderPath": "Linked Server Setup",  "QuenchSlot": "Before", "ServerToQuench": "Primary" },
    { "FolderPath": "Local Cache Build",    "QuenchSlot": "After",  "ServerToQuench": "Secondary" }
  ]
}
```

`ServerToQuench` accepts `Primary`, `Secondary`, or `Both`. SchemaQuench runs each folder's scripts against exactly the servers you specified, in parallel across the replica set. See [Schema Packages -- Secondary Servers](../reference/schema-packages.md#secondary-servers) for the full mechanics.

## FK-aware data delivery with DataTongs

Lookup tables, seed data, the whole library of reference rows every environment needs -- they're a schema-management problem as much as the tables themselves. SchemaSmith treats reference data the same way it treats structure: declare the state, let the tool do the rest. DataTongs extracts your data, writes it alongside the table JSON, and SchemaQuench delivers it in foreign-key order. You don't write merge scripts. You don't sort the load order. You declare.

**The declarative loop:**

1. Run **DataTongs** with `--ConfigureDataDelivery` against your source database. For every table listed, DataTongs writes a `.tabledata` file (raw JSON rows) and updates the table's JSON with a `DataDelivery` block -- `ContentFile`, `MergeType`, `MatchColumns`.
2. Commit the `.tabledata` files and the updated table JSONs.
3. Run **SchemaQuench**. It discovers every `DataDelivery` block, orders the tables by foreign-key dependencies, and merges the data using the platform's preferred idiom.

**Example DataTongs configuration:**

```json
{
  "Source": {
    "Platform": "SqlServer",
    "Server": "production-server",
    "Database": "AppMain"
  },
  "ContentPath": "./Templates/Main/data",
  "ScriptPath":  "./Templates/Main/Table Data",
  "ShouldCast": {
    "ConfigureDataDelivery": true,
    "MergeUpdate": true,
    "MergeDelete": true,
    "DisableTriggers": false
  },
  "Tables": [
    { "Name": "dbo.Countries" },
    { "Name": "dbo.StatusCodes" },
    { "Name": "dbo.PermissionTypes", "KeyColumns": "PermissionTypeID", "Filter": "IsActive = 1" }
  ]
}
```

After this runs, each configured table's JSON gains a block like:

```json
"DataDelivery": {
  "ContentFile": "data/dbo.PermissionTypes.tabledata",
  "MergeType": "Insert/Update/Delete",
  "MatchColumns": "[PermissionTypeID]",
  "MergeFilter": "IsActive = 1"
}
```

No hand-written merge script. Reference data is part of the table definition now.

### Two-pass FK-aware delivery

Foreign keys turn data loading into a graph problem. SchemaQuench solves it automatically. The pass sequence:

- **Pass 1** -- Tables whose required (NOT NULL) foreign keys point to already-loaded tables are merged first. Rows with nullable FK columns pointing to tables still waiting their turn get inserted with those columns NULL. The merge doesn't block on a constraint that references a row that doesn't exist yet.
- **Pass 2** -- Once every pass-1 table has delivered, each deferred table gets merged again with only the deferred FK columns in play, back-filling the real values.

```
  Delivering dbo.Employee (pass 1 - deferred columns as NULL)
  Delivering dbo.Department (pass 1)
  Delivering dbo.Employee (pass 2 - updating deferred FK columns)
```

Self-referential tables, cross-table cycles broken by nullable columns, complex relational graphs -- all handled declaratively.

### Gating deliveries by environment

The `DataDelivery` block on a table doesn't have to be a single object. Make it an array and each entry becomes an independently-gated delivery, evaluated with the same `ShouldApplyExpression` you already know from components -- with one twist: **every** delivery whose gate passes applies, not just the first match. That covers three shapes teams reach for:

- Seed or test rows that only merge into dev/test databases.
- A rich dataset in dev and a lean reference set in production, one table, two mutually-exclusive gates.
- Several deliveries with disjoint `MergeFilter`s that each own a slice of the table, all applying together as additive patch slices.

```json
"DataDelivery": [
  {
    "ContentFile": "data/dbo.PermissionTypes.core.tabledata",
    "MergeType": "Insert/Update/Delete",
    "MatchColumns": "[PermissionTypeID]",
    "MergeFilter": "Category = 'Core'",
    "ShouldApplyExpression": "DB_NAME() = 'AppMain'",
    "VariantName": "Core permission types"
  },
  {
    "ContentFile": "data/dbo.PermissionTypes.regional.tabledata",
    "MergeType": "Insert/Update/Delete",
    "MatchColumns": "[PermissionTypeID]",
    "MergeFilter": "Category = 'Regional'",
    "ShouldApplyExpression": "DB_NAME() = 'AppMain'",
    "VariantName": "Regional permission types"
  }
]
```

Both deliveries share a gate, so on `AppMain` both apply -- but each is scoped to its own `Category` slice by `MergeFilter`, so neither delivery's delete pass touches the other's rows. Skip that discipline and you've got a foot-gun: two `Insert/Update/Delete` deliveries without disjoint filters will delete each other's data on every quench. See [Schema Packages -- Multiple Deliveries](../reference/schema-packages.md#multiple-deliveries) for the full pattern, the delete-overlap warning, and gate examples across all four engines.

### Hand-written scripts still welcome

Sometimes you need procedural control: conditional seeds, one-off data rebuilds, multi-step migrations. Those still work -- drop SQL files into a `TableData`-slot folder (the default `Table Data/` folder works, or declare your own) and they run right after the declarative delivery step. Use whichever style fits the problem.

**The golden source pattern:** Extract from production (the source of truth), commit the generated artifacts to your repository, and quench to every other environment via SchemaQuench. Reference data changes travel through the same review flow as structural changes. No more "staging has stale lookup data" surprises.

For the full DataTongs configuration reference -- key column detection, filter semantics, per-platform type handling, `--ConfigureDataDelivery` mechanics -- see [DataTongs Reference](../reference/datatongs.md). For the `DataDelivery` JSON schema, see [Schema Packages -- DataDelivery](../reference/schema-packages.md#datadelivery).

## Emergency patch with SchemaShears

You have a production incident. Two stored procedures need to go out now -- not the full release, not the whole product, just those two procedures. SchemaShears builds exactly that patch.

### The one-liner manifest

Generate your manifest from the commit range that holds the fix:

```bash
git diff --name-only <before-sha> <after-sha> -- MyProduct/ > patch.manifest
```

This produces a list of every file changed between those two commits inside the product folder -- exactly what belongs in the patch. No hand-editing a file list; the VCS tells you what changed.

### Build the patch

```bash
SchemaShears --Source:./MyProduct --Manifest:./patch.manifest --Output:./MyProduct-patch
```

SchemaShears reads the manifest, pulls in the scaffolding (`Product.json` plus the relevant `Template.json` files), and writes the patch package to `./MyProduct-patch`. A `patch-build-report.txt` in the output root lists every included file and why it was included.

### Verify before deploying

```bash
SchemaQuench --SchemaPackagePath=./MyProduct-patch --WhatIfONLY=true
```

The WhatIf output shows exactly which objects the patch will touch. Review it against your intent: if the manifest was complete, only the objects you chose will appear. If something unexpected shows up -- or something expected is missing -- adjust the manifest and rebuild.

### Deploy

Once verified against a non-production target, deploy to production:

```bash
SchemaQuench --SchemaPackagePath=./MyProduct-patch
```

The patch updates only the objects it includes. Every other object in the target database -- tables, procedures, indexes, anything not in the patch -- is left untouched. Drop suppression is baked into the emitted `Product.json` by SchemaShears: absent objects are preserved, not reconciled away.

For the full SchemaShears reference -- manifest format, always-include file, `--AllowDrops`, drop suppression details -- see [SchemaShears Reference](../reference/schemashears.md).

## Resuming a failed deployment

Long-running deployments fail. A 90-minute production quench hits a transient deadlock at minute 75, or the migration script at stage 12 of 18 trips on unexpected data. Without checkpointing, the recovery is brutal: start over from zero, re-run every step you already applied, hope the interrupted state is self-consistent.

SchemaQuench writes checkpoints as it goes. Every completed step (helper procedure deployment, missing tables, modified tables, indexes, FK-aware data delivery, foreign keys, materialized or indexed view rebuilds) and every completed migration script gets recorded to disk. Re-run with `--ResumeQuench` and everything already recorded is skipped:

```bash
SchemaQuench --ResumeQuench
```

The next run picks up where the last one stopped -- minutes of work instead of hours. On a clean success the checkpoint files are deleted automatically; on a failure they're preserved for the next resume. Point the checkpoint directory somewhere durable if you need them to survive a container restart or agent rotation:

```bash
SchemaQuench --ResumeQuench --CheckpointDirectory:/var/schemasmith/checkpoints
```

For the checkpoint scopes, the full step list, and the practical guidance on when to opt in, see [SchemaQuench -- Checkpoint and Resume](../reference/schemaquench.md#checkpoint-and-resume).

## Custom script folders

The default folder set is a strong starting point, but your team's deployment lifecycle might not match it exactly. Maybe you want a folder called `Permissions` that runs in the `After` slot. Maybe you want to split `Procedures` into `Procedures/Public` and `Procedures/Internal` for code review purposes. Maybe you need a brand-new custom slot for your datafix pipeline.

Declare your own folder layout via `ScriptFolders` in `Template.json`:

```json
{
  "Name": "MyTemplate",
  "DatabaseIdentificationScript": "...",
  "ScriptFolders": [
    { "FolderPath": "Before Scripts",    "QuenchSlot": "Before" },
    { "FolderPath": "Schemas",           "QuenchSlot": "Objects", "ObjectType": "Schemas" },
    { "FolderPath": "Functions",         "QuenchSlot": "Objects", "ObjectType": "Functions" },
    { "FolderPath": "Views",             "QuenchSlot": "Objects", "ObjectType": "Views" },
    { "FolderPath": "Procedures/Public", "QuenchSlot": "Objects", "ObjectType": "Procedures" },
    { "FolderPath": "Procedures/Internal", "QuenchSlot": "Objects", "ObjectType": "Procedures" },
    { "FolderPath": "Triggers",          "QuenchSlot": "AfterTablesObjects", "ObjectType": "Triggers" },
    { "FolderPath": "Table Data",        "QuenchSlot": "TableData" },
    { "FolderPath": "Permissions",       "QuenchSlot": "After" },
    { "FolderPath": "After Scripts",     "QuenchSlot": "After" }
  ]
}
```

When you declare a `ScriptFolders` array, it fully replaces the platform default set -- so include every folder you want active. This is a full replacement, not a merge, which means you're always in control of exactly which folders run.

### Organizing subfolders

When a package grows beyond a handful of objects, flat script folders become hard to navigate. SchemaQuench discovers scripts recursively -- all `.sql` files in a folder and every subfolder underneath it, sorted alphabetically by full path -- so you can group related objects without changing their deployment behavior.

```
Procedures/
  Reporting/
    dbo.GetMonthlyRevenue.sql
    dbo.GetQuarterlyReport.sql
  Core/
    dbo.ProcessOrder.sql
    dbo.ValidateCustomer.sql
```

The alphabetical-by-full-path sort determines execution order, so `Core/` scripts run before `Reporting/` scripts. Name your folders and files to make the order legible -- a prefix convention (`01-Core/`, `02-Reporting/`) makes intent obvious at a glance.

Table and view JSON follows the same rule. Files in `Tables/Analytics/Orders.json` and `Tables/Transactional/Customers.json` are both discovered and loaded; the subfolder is purely organizational.

**Layout round-trips.** When you re-extract a table, SchemaTongs looks up the object's existing file path in its index and writes back to that same location. Your subfolder organization survives the extraction cycle intact.

> **Note:** If the same filename appears in more than one subfolder -- for example, `Reporting/dbo.GetReport.sql` and `Archive/dbo.GetReport.sql` -- SchemaTongs logs a warning and writes to the base folder instead of an ambiguous location. Keep object filenames unique across subfolders to avoid this.

## Script folder execution order

SchemaQuench quenches scripts in a precise order. Understanding the execution slots lets you place scripts exactly where they need to run in the deployment lifecycle. The full sequence, from first to last:

| Order | Slot | What runs |
|---|---|---|
| 1 | Product `Before` | Cross-database setup, linked servers, server-level migrations |
| 2 | Template `Objects` | Programmable objects with dependency retry (schemas, types, functions, views, procedures) |
| 3 | _(new tables / columns)_ | New tables and columns created from table JSON |
| 4 | Template `Before` | Pre-modification migration scripts |
| 5 | _(table modification)_ | Existing tables altered to match JSON |
| 6 | Template `BetweenTablesAndKeys` | Runs after table structure is final but before FK constraints |
| 7 | _(indexes and constraints)_ | Missing indexes, checks, defaults, statistics |
| 8 | Template `AfterTablesScripts` | Runs after structure and indexes, before triggers |
| 9 | Template `AfterTablesObjects` | Triggers, DDL triggers, rules -- dependency retry |
| 10 | Template `TableData` | Reference data sync scripts |
| 11 | _(foreign keys)_ | Foreign key constraints applied after all data is in place |
| 12 | _(indexed / materialized views)_ | SQL Server indexed views / PostgreSQL materialized views |
| 13 | Template `After` | Post-migration final scripts |
| 14 | Product `After` | Cross-database post-migration |

**Why BetweenTablesAndKeys exists:** When you split a table or restructure foreign keys, you need a window after the new tables are created but before SchemaSmith tries to create the foreign keys. This slot gives you that window to move data into the new structure so the FK constraints succeed.

**Why AfterTablesScripts exists:** Sometimes you need to run scripts after indexes and constraints are in place but before triggers are enabled. Data backfills that must satisfy constraints but shouldn't fire triggers go here.

For the complete deployment flow including per-platform modular procedures, see [SchemaQuench Reference](../reference/schemaquench.md#database-quench-sequence).

---

Tokens, custom properties, `ShouldApplyExpression`, multi-database products, secondary servers, DataTongs, custom script folders, execution slots. That's the toolkit for 95% of what you'll encounter -- and all of it is in Community. When one tenant per database isn't the right shape, the next chapter shows how a single template can fan out across schemas inside one database. [Multi-Tenant Deployments](10-multi-tenant-deployments.md)
