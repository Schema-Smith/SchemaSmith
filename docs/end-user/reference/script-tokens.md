# Script Tokens Reference

Applies to: SchemaQuench, SchemaTongs, DataTongs — SQL Server, PostgreSQL, MySQL, and MariaDB.

---

Script tokens are how one schema package becomes ten environments. Define a token once, reference it everywhere, override it per environment without touching a script file. The simple form is just two curly braces and a name -- but tokens go far beyond that. They can pull in file contents, execute server-side queries to generate values at deployment time, embed entire table schemas as JSON, and surface custom metadata you've attached through `Extensions`. One feature, many superpowers, all in the free toolset.

## Token Syntax

The basic form:

```
{{TokenName}}
```

Token replacement is **case-insensitive**. These all resolve to the same value:

```sql
SELECT * FROM [{{MainDB}}].dbo.Customers
SELECT * FROM [{{maindb}}].dbo.Customers
SELECT * FROM [{{MAINDB}}].dbo.Customers
```

Tokens that appear in scripts but have no matching definition are left in place unchanged -- there's no silent corruption from an unresolved token.

---

## Where Tokens Are Resolved

Tokens are replaced in every place SchemaSmith processes script content -- both in JSON expression fields and in `.sql` files.

> **"Every place" includes `--` comments.** SchemaSmith does a plain text substitution across the whole script; it does not skip comments. A single-line token in a `--` comment is harmless, but a **multi-line** token value -- the pretty-printed JSON of `{{TableSchema}}`, for example -- expands past the end of the first line, and everything after that first line is no longer commented: it becomes live SQL and usually fails to parse. So don't reference a multi-line token inside a `--` line comment unless you mean to run its value. Use a `/* ... */` block comment, or describe the token in words instead of writing it out.

**Product-level JSON properties:**

- `BaselineValidationScript`
- `ValidationScript`
- `VersionStampScript`

**Template-level JSON properties:**

- `BaselineValidationScript`
- `DatabaseIdentificationScript`
- `VersionStampScript`

**Table-component expression fields** (table JSON files):

- `CheckExpression` on columns
- `Default` on columns
- `Expression` on table-level check constraints
- `FilterExpression` on indexes (where supported)
- `ShouldApplyExpression` on tables, columns, indexes, foreign keys, check constraints, indexed views, materialized views, data deliveries, and other supported components

**SQL script files** in every product and template script folder:

| Folder | Scope |
|---|---|
| `Before Product/`, `After Product/` | Product |
| `Before Scripts/`, `After Scripts/` | Template |
| `Schemas/` | Template |
| `DataTypes/` (SQL Server) / `Domain Types/`, `Enum Types/`, `Composite Types/` (PostgreSQL) / equivalents | Template |
| `Functions/`, `Views/`, `Procedures/` | Template |
| `Triggers/`, `DDLTriggers/` | Template |
| `Table Data/` | Template |
| Any custom script folders defined in `Template.json` `ScriptFolders` | Template |

The exact set of default folders depends on the product's `Platform`. Anything you add via custom `ScriptFolders` participates in token resolution exactly the same way as the defaults.

---

## Product Tokens

Define tokens in `Product.json` under `ScriptTokens`. These are available across every template and product-level script.

```json
{
  "Name": "SaasProduct",
  "TemplateOrder": ["Registry", "Client"],
  "ScriptTokens": {
    "RegistryDb": "Registry",
    "MigrationVersion": "1.0.1"
  }
}
```

In this example, `{{RegistryDb}}` resolves to `Registry` everywhere -- in every template's scripts and in every SQL file across the product.

---

## Template Tokens

Define tokens in `Template.json` under `ScriptTokens`. Template tokens override product tokens with the same key for the duration of that template's execution, and they can also introduce new tokens that only exist inside the template.

Say your `Product.json` defines a product-wide default for `MainDB`:

```json
{
  "Name": "SaasProduct",
  "TemplateOrder": ["Registry", "Reporting"],
  "ScriptTokens": {
    "MainDB": "ProductionMain"
  }
}
```

Then one of its templates overrides `MainDB` and adds a new `SchemaOwner` token that the product never declared:

```json
{
  "Name": "Reporting",
  "ScriptTokens": {
    "MainDB": "ReportingAlias",
    "SchemaOwner": "rpt"
  }
}
```

Inside the Reporting template, `{{MainDB}}` resolves to `ReportingAlias` instead of the product-scope `ProductionMain`. The new `{{SchemaOwner}}` token is available only inside this template; other templates in the same product don't see it.

---

## Automatic Tokens

SchemaSmith adds these tokens automatically. You don't define them -- they appear when relevant.

| Token | Value | Available in |
|---|---|---|
| `{{ProductName}}` | The `Name` from `Product.json` | All product and template scripts |
| `{{TemplateName}}` | The `Name` from the current `Template.json` | Template scripts only |
| `{{TableSchema}}` | Full serialized JSON of every table in the current template, with single quotes escaped (`''`) for safe embedding in SQL string literals | Template scripts |
| `{{IndexedViewSchema}}` | Full serialized JSON of every indexed view in the current template (SQL Server) | Template scripts |
| `{{MaterializedViewSchema}}` | Full serialized JSON of every materialized view in the current template (PostgreSQL) | Template scripts |
| `{{TableXml}}` | XML twin of `{{TableSchema}}` — the same table model as ingest XML, for shredding with `.nodes()`/`.value()` on a below-compat-130 SQL Server where `OPENJSON` parse-errors. See [Model-payload XML twins](#model-payload-xml-twins) | Template scripts |
| `{{IndexedViewXml}}` | XML twin of `{{IndexedViewSchema}}` | Template scripts |
| `{{MaterializedViewXml}}` | XML twin of `{{MaterializedViewSchema}}` | Template scripts |
| `{{TableSchema_<TemplateName>}}` | Same as `{{TableSchema}}` but reaches across templates -- name another template explicitly to read its table set | Any template |
| `{{IndexedViewSchema_<TemplateName>}}` | Cross-template indexed view JSON | Any template |
| `{{MaterializedViewSchema_<TemplateName>}}` | Cross-template materialized view JSON | Any template |
| `{{TableXml_<TemplateName>}}` | Cross-template XML twin of `{{TableSchema_<TemplateName>}}` | Any template |
| `{{IndexedViewXml_<TemplateName>}}` | Cross-template XML twin of `{{IndexedViewSchema_<TemplateName>}}` | Any template |
| `{{MaterializedViewXml_<TemplateName>}}` | Cross-template XML twin of `{{MaterializedViewSchema_<TemplateName>}}` | Any template |
| `{{ObjectScripts_<TemplateName>}}` | Cross-template inventory of programmable object scripts (functions, views, procedures, triggers) | Any template |
| `{{QueryTokens_<TemplateName>}}` | Cross-template inventory of query-style tokens for sharing | Any template |
| `{{ServerMajorVersion}}` | The detected target server major version — SQL Server `16` (2022) / `13` (2016) / `10` (2008); PostgreSQL `16`; MySQL `800` (8.0); MariaDB `1006` (10.6) | Template scripts + expression fields |
| `{{CompatibilityLevel}}` | The SQL Server database compatibility level (e.g. `160`, `130`); on PostgreSQL / MySQL / MariaDB it resolves to the same value as `{{ServerMajorVersion}}` | Template scripts + expression fields |

**Why this matters.** `{{TableSchema}}` is the entire current template's table model serialized as JSON, ready to drop into a stored procedure parameter, a `JSON_VALUE`/`json_each`/`JSON_TABLE` query, or an audit row. Combined with the [Custom Properties](custom-properties.md) feature, you can write a single migration script that introspects your table definitions and reads your team's custom metadata at deployment time -- without ever leaving SchemaSmith.

The cross-template variants (`{{TableSchema_OtherTemplate}}`) are how a deployment script in one template can read the schema of another. This unlocks coordination between linked templates without copy-pasting JSON.

> **`Nullable` is written only when it carries information.** In the table payload (`{{TableSchema}}`, `{{TableXml}}`, their cross-template forms and `<*SpecificTable*>`), an ordinary column carries `Nullable` only when it is `true`; a `NOT NULL` column has no `Nullable` key at all, even when its package file spells out `"Nullable": false`. A computed or generated column carries `Nullable` whenever its package declares it, `true` or `false`; an undeclared one has no key, which means the engine decides its nullability from the expression. So a script must read a missing `Nullable` as `false` on an ordinary column -- `ISNULL(Nullable, 0)` with `OPENJSON` or `.value()`, `COALESCE` on PostgreSQL, MySQL and MariaDB. Every other boolean property is written whether it is `true` or `false`.

> **Note:** When the referenced template is a schema template, each table / view's `Schema` field would normally hold the per-iteration `{{SchemaName}}` token. The cross-template snapshot replaces that token with the literal placeholder `<per-iteration>` so the consuming template sees clearly that schemas in the referenced set are iteration-dependent. Use the cross-template variants for structural introspection (column counts, custom properties, table names) — schema-qualified DDL still needs to live inside the schema template itself, where `{{SchemaName}}` resolves per iteration.

### Custom property tokens

Anything you put in an `Extensions` object on a table component is also available as a token in that component's expression fields. Bare names from the component's own Extensions, `Table.`-prefixed names from the parent table. See [Custom Properties](custom-properties.md) for the full mechanism.

```json
{
  "Name": "[Orders]",
  "Extensions": { "Environment": "Production" },
  "Indexes": [
    {
      "Name": "[IX_Orders_CreatedDate]",
      "IndexColumns": "[CreatedDate]",
      "ShouldApplyExpression": "'{{Table.Environment}}' = 'Production'"
    }
  ]
}
```

That single expression lets the index decide whether to apply itself based on metadata that lives on the parent table, without leaving the schema package or splitting the definition across environment-specific files.

---

## {{SchemaName}}

Schema templates make the active iteration's schema name available everywhere tokens resolve — script bodies, JSON field values, table data references, and dependent token values. Without `{{SchemaName}}`, deploying a product to dozens or hundreds of tenant schemas would require either per-tenant script files or a complex external preprocessing step. With it, a single set of scripts fans out to every schema in one quench, and each iteration sees a fully-qualified, unambiguous version of every object reference it touches.

`{{SchemaName}}` is a built-in token set by the engine at the start of each schema-template iteration. You don't define it in `ScriptTokens` — it appears automatically when the engine is running inside a schema template's iteration context.

### Availability

`{{SchemaName}}` is available inside any iteration of a schema template: a `Template.json` whose `SchemaIdentificationScript` field is set. It resolves in every place tokens normally resolve:

- SQL script files in every slot (Before, Objects / Procedures / Views / Functions, Tables (via engine-generated DDL), BetweenTablesAndKeys, AfterTablesObjects, TableData, After)
- JSON expression fields: `Default`, `CheckExpression`, `Expression`, `FilterExpression`, `ShouldApplyExpression`
- `BaselineValidationScript` and `VersionStampScript` on the template
- User-defined `ScriptTokens` values (see [In token values](#in-token-values) below)

`{{SchemaName}}` is **not available** outside a schema-template iteration:

- In a regular template (`Template.json` without `SchemaIdentificationScript`), `{{SchemaName}}` is unresolved. The engine produces a clear error if it appears in a context where it would need to substitute.
- In product-level scripts (`Product.json` `BaselineValidationScript`, `VersionStampScript`) — those run at product scope, before and after all template iterations, with no iteration in scope.
- On MySQL and MariaDB — MySQL and MariaDB have no schema-inside-database concept and schema templates are not supported on those platforms. See [Multi-Tenant Deployments](../guide/10-multi-tenant-deployments.md#mysql-and-mariadb----database-per-tenant-only) for the alternative.

### In script files

The most common use is qualifying every object reference that belongs to the current tenant's schema. From the TenantCRM demo's `AddCustomer.sql`:

```sql
-- SQL Server
CREATE OR ALTER PROCEDURE [{{SchemaName}}].[AddCustomer]
    @CustomerName NVARCHAR(128),
    @Email NVARCHAR(256) = NULL,
    @CustomerID INT = NULL OUTPUT
AS
BEGIN
    INSERT INTO [{{SchemaName}}].[Customers] ([CustomerName], [Email])
    VALUES (@CustomerName, @Email);

    INSERT INTO [dbo].[GlobalAuditLog] ([TenantName], [EventType], [Detail])
    VALUES (N'{{SchemaName}}', N'CustomerAdded', N'Name=' + @CustomerName);
END;
```

```sql
-- PostgreSQL
CREATE OR REPLACE PROCEDURE "{{SchemaName}}".add_customer(
    p_customer_name VARCHAR(128),
    p_email VARCHAR(256) DEFAULT NULL
)
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO "{{SchemaName}}".customers (customer_name, email)
    VALUES (p_customer_name, p_email);

    INSERT INTO public.global_audit_log (tenant_name, event_type, detail)
    VALUES ('{{SchemaName}}', 'CustomerAdded', 'name=' || p_customer_name);
END;
$$;
```

Notice that `{{SchemaName}}` qualifies the procedure itself, the insert target, and also appears as a plain string value in the `GlobalAuditLog` insert — recording which tenant's schema this event came from. `{{SchemaName}}` substitutes as text before the script executes, so it can play both roles: identifier qualifier and string literal.

Cross-schema references like `[dbo].[GlobalAuditLog]` and `public.global_audit_log` are hard-coded with their literal schema prefix. Shared tables that span tenants are not in the iteration schema, so they stay fully qualified with their actual schema name.

### In table JSON

Tables in a schema template have their `Schema` field defaulted to `{{SchemaName}}` automatically — you don't need to set it. When you omit the schema prefix from the filename (`Customers.json` instead of `dbo.Customers.json`), the engine fills it in for each iteration.

For foreign keys pointing to shared tables in another schema, set `RelatedTableSchema` explicitly:

```json
{
  "Name": "[Customers]",
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

Without an explicit `RelatedTableSchema`, the engine defaults it to `{{SchemaName}}` — which would incorrectly point the FK at `tenant_acme.Countries` instead of `dbo.Countries`. Explicit cross-schema references are preserved as-is.

For the full `Template.json` field reference and all schema-template fields, see [Schema Templates](schema-packages.md#schema-templates).

### In token values

A user-defined `ScriptTokens` value can itself contain `{{SchemaName}}`. The engine detects this at template load and escalates that token to per-iteration resolution — it cannot be cached at load time or across database deployments because the value differs by schema. See [Token Resolution Order](#token-resolution-order) for how this fits into the three-tier resolution model.

```json
{
  "ScriptTokens": {
    "TenantAuditTable": "{{SchemaName}}.AuditLog"
  }
}
```

Any script that references `{{TenantAuditTable}}` will get the iteration-qualified value for each tenant without needing to write `{{SchemaName}}` in every script.

> **Note:** Using an iteration-scoped token in a product-level script (`Product.json` `VersionStampScript`, `BaselineValidationScript`) is a validation error — those scripts run outside any iteration context and the engine has no schema name to substitute.

For a narrative walkthrough of schema templates end to end — authoring layout, deployment log, tenant onboarding, and cross-schema FK patterns — see [Multi-Tenant Deployments](../guide/10-multi-tenant-deployments.md).

---

## {{ServerMajorVersion}} and {{CompatibilityLevel}}

Version-adaptive packages need to gate on what the target can actually do — apply a temporal-table migration only on SQL Server 2016+, keep a `STRING_AGG` rewrite off a legacy database, branch a `Before` script by engine version. SchemaSmith already detects the target version when it connects; these two tokens expose it so a `ShouldApplyExpression` (folder, component, or the [per-script sentinel](schemaquench.md#script-level-runtime-skip)) or a script body can gate on version without you hand-writing each engine's native version predicate.

`{{ServerMajorVersion}}` is the detected server major version; `{{CompatibilityLevel}}` is the SQL Server database compatibility level. Both are set by the engine per target database — you don't define them in `ScriptTokens`, and they substitute as plain integer literals, so they drop straight into a comparison.

### Gate syntax on compatibility level, gate features on server version

These are two different questions, and confusing them is a real footgun. A modern binary can host a database left at an old compatibility level — a SQL Server 2022 server (`{{ServerMajorVersion}}` = `16`) with a database at compatibility level 100 (`{{CompatibilityLevel}}` = `100`). Compatibility-level-gated **syntax** — `STRING_SPLIT` and `OPENJSON` (compat 130), `TRY_CONVERT` and `STRING_AGG`'s optional `WITHIN GROUP (ORDER BY …)` clause (compat 110) — parse-errors on that database even though the binary is brand new. So:

> ⚠ **`STRING_AGG` itself is *not* compatibility-level gated**, and this list used to imply it was. The function requires **SQL Server 2017** (server major 14) and parses at every compatibility level down to 100 on a server that has it — so it is a `{{ServerMajorVersion}}` gate, not a `{{CompatibilityLevel}}` one. Only its optional `WITHIN GROUP (ORDER BY …)` clause carries a compatibility-level requirement (110). Conflating the two is what allowed a compat-only probe to emit `STRING_AGG` onto a SQL Server 2016 server, which reports compatibility level 130 while having no `STRING_AGG` at all — see [SchemaQuench — the encoding switch](schemaquench.md#version-adaptive-code-generation), corrected for the same reason.

- **Syntax availability** follows the database's compatibility level → gate on `{{CompatibilityLevel}}`.
- **Server features** (a new engine capability, a version-only DDL form) follow the binary → gate on `{{ServerMajorVersion}}`.

`SERVERPROPERTY('ProductMajorVersion') >= 16` is the gate authors reach for first, and it is the *wrong* gate for syntax: it passes on that compat-100 database and the SQL still fails.

### Availability

Both tokens resolve everywhere template-scoped tokens resolve — script bodies in every slot, and the `Default` / `CheckExpression` / `Expression` / `FilterExpression` / `ShouldApplyExpression` JSON fields — and they are resolved **per target database**, after SchemaSmith connects and detects the version. They are not available in product-level scripts (`Product.json` `BaselineValidationScript` / `VersionStampScript`), which run at server scope before any database is selected.

A product folder's own `ShouldApplyExpression` (the `Before`/`After` entries in `Product.json`'s top-level `ScriptFolders`) is a narrower case: it runs at **product scope**, on the same open server connection but before any database is selected. `{{ServerMajorVersion}}` resolves there — the server connection already exists — but `{{CompatibilityLevel}}` (a property of a database) and `{{SchemaName}}` (a template-iteration concept) do not exist yet at that scope, so they are left unresolved. Referencing either in a product-folder gate reaches the target as literal, unresolved text and fails there, the same way any genuinely unresolvable token does — it does not silently evaluate to a wrong-but-plausible comparison.

> **SQL Server:** `CompatibilityLevel` is a SQL Server concept. On PostgreSQL, MySQL, and MariaDB there is no separate compatibility level, so `{{CompatibilityLevel}}` resolves to the same value as `{{ServerMajorVersion}}` — one portable expression shape works across the per-platform packages, and the syntax-vs-feature distinction above only bites on SQL Server.

### Example — a version-gated folder

The shipped `Demos/Conditional/SqlServer-CompatLevelGate` gates a `Programmability/Modern/` folder on the database compatibility level with a raw scalar query. The token form says the same thing more directly:

```jsonc
// Template.json — folder gate, raw SQL vs. the token form
"ShouldApplyExpression": "(SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) >= 160"
"ShouldApplyExpression": "{{CompatibilityLevel}} >= 160"
```

Pair each token with the matching version-gated script variant and you get one declarative, engine-detected gate instead of a wall of `SERVERPROPERTY` / `current_setting` SQL. For the folder-gate mechanics and the syntax-vs-feature rule in a deployment context, see [SchemaQuench — ShouldApplyExpression and Conditional Deployment](schemaquench.md#shouldapplyexpression-and-conditional-deployment).

---

## Model-payload XML twins

The model-payload tokens (`{{TableSchema}}` and friends) hand your script the current model as JSON, ready for `OPENJSON` / `json_each` / `JSON_TABLE`. But `OPENJSON` requires SQL Server database compatibility level 130 — on a database left at an older level it parse-errors, so a self-service script that shreds `{{TableSchema}}` is unavailable on the legacy tier. Every model-payload token therefore has an **XML twin** that carries the *same* model as ingest XML, shreddable with XQuery (`.nodes()` / `.value()`) at every compatibility level. The author picks which to shred; both are always present.

Each JSON token maps to a twin by replacing `Schema` with `Xml` (aggregate + cross-template forms), and each `<*Specific…*>` tag has a `<*Specific…Xml*>` counterpart:

| JSON (shred with `OPENJSON`) | XML twin (shred with `.nodes()`/`.value()`) |
|---|---|
| `{{TableSchema}}` / `{{IndexedViewSchema}}` / `{{MaterializedViewSchema}}` | `{{TableXml}}` / `{{IndexedViewXml}}` / `{{MaterializedViewXml}}` |
| `{{TableSchema_<TemplateName>}}` (+ IV/MV) | `{{TableXml_<TemplateName>}}` (+ IV/MV) |
| `<*SpecificTable*>`, `<*SpecificIndexedView*>`, `<*SpecificMaterializedView*>` | `<*SpecificTableXml*>`, `<*SpecificIndexedViewXml*>`, `<*SpecificMaterializedViewXml*>` |

The twin is the same model — a `<Tables><Table>…</Table></Tables>` document (or `<Table>…</Table>` for a single-object tag) — so an author pairs it with the JSON form behind version-gated script variants (using [`{{CompatibilityLevel}}`](#servermajorversion-and-compatibilitylevel)): the JSON script on the modern tier, the XML script on the legacy tier.

```sql
-- Legacy-tier variant (ShouldApplyExpression: {{CompatibilityLevel}} < 130) — shred the XML twin
DECLARE @model xml = '{{TableXml}}';
SELECT t.n.value('(Name)[1]', 'nvarchar(256)') AS TableName
FROM @model.nodes('/Tables/Table') AS t(n);
```

> **SQL Server:** the encoding cliff is SQL-Server-only — PostgreSQL has JSON functions since 9.2, MySQL since 5.7, and MariaDB since 10.2, so their `{{TableSchema}}` shreds at every supported version. The XML twins are produced on every engine for one portable authoring surface, but only SQL Server *needs* them.

---

## Advanced Token Tags

Here's where tokens go from "find and replace" to "deployment-time content engine." A token's *value* in `ScriptTokens` can start with a special tag that tells SchemaSmith to resolve it at deployment time -- by reading a file, by querying the live database, or by embedding a serialized object's JSON. The token name in your scripts stays a plain `{{TokenName}}`; the magic happens in how the value is computed before substitution.

The advanced tags:

| Tag | Purpose |
|---|---|
| `<*File*>relative\path\file.sql` | Replace the token value with the contents of a text file, resolved relative to the product directory |
| `<*BinaryFile*>relative\path\image.png` | Replace the token value with the file contents as a platform-appropriate binary literal -- `0x<hex>` for SQL Server, MySQL, and MariaDB, `E'\\x<hex>'::bytea` for PostgreSQL |
| `<*Query*>SELECT ... FROM ...` | Execute a SQL query against the deployment target before substitution and replace the token value with the first column's rows joined by newlines |
| `<*QueryFile*>relative\path\query.sql` | Same as `<*Query*>` but the query body is loaded from a file first |
| `<*SpecificTable*>schema.tablename` | Replace the token value with the full serialized JSON of one specific table in the current template |
| `<*SpecificIndexedView*>schema.viewname` | Same as `<*SpecificTable*>` but for SQL Server indexed views |
| `<*SpecificMaterializedView*>schema.viewname` | Same as `<*SpecificTable*>` but for PostgreSQL materialized views |
| `<*SpecificTableXml*>schema.tablename` | XML twin of `<*SpecificTable*>` — resolves to one table's ingest XML instead of its JSON (see [Model-payload XML twins](#model-payload-xml-twins)) |
| `<*SpecificIndexedViewXml*>schema.viewname` | XML twin of `<*SpecificIndexedView*>` |
| `<*SpecificMaterializedViewXml*>schema.viewname` | XML twin of `<*SpecificMaterializedView*>` |

All resolution happens automatically. Your SQL scripts just see `{{TokenName}}` and the right value lands there at deployment time.

### Example — embed a file's contents

You have a long stored-procedure body or a static reference dataset stored in a `.sql` file outside your script folders, and you want to drop its contents into a migration script verbatim:

```json
{
  "Name": "DataMigration",
  "ScriptTokens": {
    "ReferenceData": "<*File*>resources/reference-data.sql"
  }
}
```

In any script in this template:

```sql
-- The next line will be replaced with the
-- entire contents of resources/reference-data.sql
{{ReferenceData}}
```

### Example — embed binary content as hex

For test images, certificate blobs, signing keys -- anything you need to inject as binary into a column at deployment time:

```json
{
  "ScriptTokens": {
    "DefaultLogo": "<*BinaryFile*>resources/logo.png"
  }
}
```

```sql
INSERT dbo.BrandAssets(Name, Image) VALUES('Default', {{DefaultLogo}});
```

The token resolves to the platform-appropriate binary literal form automatically, chosen from the product's `Platform`:

- **SQL Server** — `0x89504E47...` (`VARBINARY` literal)
- **MySQL** — `0x89504E47...` (`BLOB` literal)
- **MariaDB** — `0x89504E47...` (`BLOB` literal, same hex form as MySQL)
- **PostgreSQL** — `E'\\x89504E47...'::bytea` (`BYTEA` literal with escape-string + explicit cast)

The same `<*BinaryFile*>` token works across all four engines with no per-environment editing. The resolver reads the file once and emits the correct literal form for the target; your SQL script just sees `{{DefaultLogo}}` land as whatever that engine will accept.

### Example — query the target server for a value

Your deployment script needs a value from the target database itself -- a tenant ID, a feature flag, the next batch number, the result of a row count check. Resolve it at deployment time, before any of your scripts run, against the actual server you're deploying to.

**SQL Server:**

```json
{
  "ScriptTokens": {
    "ActiveTenants": "<*Query*>SELECT TenantId FROM dbo.Tenants WHERE Active = 1"
  }
}
```

```sql
-- After resolution, {{ActiveTenants}} contains one tenant ID per line
EXEC dbo.ProvisionAuditTables @TenantIdList = '{{ActiveTenants}}';
```

**PostgreSQL:**

```json
{
  "ScriptTokens": {
    "ActiveTenants": "<*Query*>SELECT tenant_id FROM public.tenants WHERE active = true"
  }
}
```

```sql
-- After resolution, {{ActiveTenants}} contains one tenant ID per line
CALL public.provision_audit_tables('{{ActiveTenants}}');
```

**MySQL:**

```json
{
  "ScriptTokens": {
    "ActiveTenants": "<*Query*>SELECT `TenantId` FROM `Tenants` WHERE `Active` = 1"
  }
}
```

```sql
-- After resolution, {{ActiveTenants}} contains one tenant ID per line
SET @TenantIdList = '{{ActiveTenants}}';
CALL ProvisionAuditTables(@TenantIdList);
```

### Example — query body in a file

Long queries stay readable when they live in their own files. The token value points at the file; the file contents become the query.

```json
{
  "ScriptTokens": {
    "DriftReport": "<*QueryFile*>queries/drift-report.sql"
  }
}
```

### Example — embed one table's JSON

When you only need *one* table's metadata in a script -- not the whole template -- the specific-table tag is the surgical option. The token value names one table; the resolved content is that table's full JSON, ready to hand to a stored procedure that introspects columns, indexes, or custom metadata.

**SQL Server:**

```json
{
  "ScriptTokens": {
    "OrdersTable": "<*SpecificTable*>dbo.Orders"
  }
}
```

```sql
DECLARE @TableJson NVARCHAR(MAX) = '{{OrdersTable}}';
EXEC dbo.GenerateAuditTriggerForTable @TableJson;
```

**PostgreSQL:**

```json
{
  "ScriptTokens": {
    "OrdersTable": "<*SpecificTable*>public.orders"
  }
}
```

```sql
DO $$
DECLARE
  v_table_json TEXT := '{{OrdersTable}}';
BEGIN
  CALL public.generate_audit_trigger_for_table(v_table_json);
END $$;
```

**MySQL:**

```json
{
  "ScriptTokens": {
    "OrdersTable": "<*SpecificTable*>Orders"
  }
}
```

```sql
SET @TableJson = '{{OrdersTable}}';
CALL GenerateAuditTriggerForTable(@TableJson);
```

The same pattern works for indexed views (`<*SpecificIndexedView*>`, SQL Server) and materialized views (`<*SpecificMaterializedView*>`, PostgreSQL).

### Resolution order for advanced tags

1. File tags (`<*File*>`, `<*BinaryFile*>`, `<*QueryFile*>`) are resolved first, against the product directory.
2. Specific-object tags (`<*SpecificTable*>`, `<*SpecificIndexedView*>`, `<*SpecificMaterializedView*>`) are resolved against the loaded template.
3. Query tags (`<*Query*>`, `<*QueryFile*>` after its file is loaded) are deferred until just before each script runs and execute against the open deployment connection.
4. After all resolution, the remaining `{{TokenName}}` substitutions are performed in your script content.

If a file is missing, a query fails, or a specific table can't be found, the deployment **stops with a clear error** -- you find out at the start, not after a half-applied database. Safe by default.

---

## Config-Level Overrides

This is where tokens earn their keep. Override product token values in a settings file without modifying the schema package. Same package, different environments, different values.

Add a `ScriptTokens` section to your tool's settings file (e.g., `SchemaQuench.settings.json`):

```json
{
  "Target": {
    "Server": "staging-server"
  },
  "ScriptTokens": {
    "RegistryDb": "Registry_Staging",
    "MigrationVersion": "1.0.1-rc1"
  }
}
```

Config overrides only apply to tokens that **already exist** in `Product.json`. You can't introduce new tokens via configuration alone -- the package declares the contract, the environment fills in the values.

---

## Environment Variable Tokens

Override product token values using environment variables. The naming pattern is:

```
SmithySettings_ScriptTokens__TokenName=Value
```

Note the prefix `SmithySettings_` (single underscore) and the double underscore `__` before the token name. This follows the .NET configuration environment variable convention.

```bash
# Linux/macOS
export SmithySettings_ScriptTokens__RegistryDb="Registry_CI"
export SmithySettings_ScriptTokens__MigrationVersion="1.0.1-ci.42"

# Windows (cmd)
set SmithySettings_ScriptTokens__RegistryDb=Registry_CI
set SmithySettings_ScriptTokens__MigrationVersion=1.0.1-ci.42

# Windows (PowerShell)
$env:SmithySettings_ScriptTokens__RegistryDb = "Registry_CI"
$env:SmithySettings_ScriptTokens__MigrationVersion = "1.0.1-ci.42"
```

Like config-level overrides, environment variable overrides only apply to tokens that already exist in `Product.json`.

---

## Token Resolution Order

When the same token name appears in multiple places, the most specific definition wins. Tokens resolve in layers, from lowest to highest priority:

| Priority | Source | Scope |
|---|---|---|
| 1 (lowest) | `Product.json` `ScriptTokens` | All scripts |
| 2 | Settings file `ScriptTokens` section | Overrides matching product keys |
| 3 | Environment variables (`SmithySettings_ScriptTokens__*`) | Overrides matching product keys |
| 4 (highest) | `Template.json` `ScriptTokens` | Template scripts only |

**How it works step by step:**

1. Product tokens are loaded from `Product.json`.
2. Config file and environment variable overrides replace matching product token values. (Steps 2 and 3 are handled together by the .NET configuration layering.)
3. The automatic `ProductName` token is added.
4. When each template loads, its `ScriptTokens` are merged on top of the resolved product tokens. Template tokens with matching keys win.
5. The automatic `TemplateName`, `TableSchema`, `IndexedViewSchema`, and `MaterializedViewSchema` tokens are added.
6. Cross-template `*_<TemplateName>` tokens are added once all templates have loaded.
7. Custom property tokens from `Extensions` are merged in per component when the table model is processed.
8. Advanced tag values (`<*File*>`, `<*Query*>`, etc.) resolve as described above, then the simple `{{TokenName}}` substitution runs against the final token map.

### Resolution timing

Independent of the priority order above, each token also has a *resolution frequency* — how many times the engine computes its value during a deployment run. There are three tiers:

| Tier | When the value is computed | Which tokens |
|---|---|---|
| **Per product** | Once, at template load — before any database connection is opened | Fully static tokens: plain string values with no `<*Query*>` tag and no `{{SchemaName}}` reference |
| **Per database** | Once per target database, just before that database's scripts run | `<*Query*>` tokens whose body does not reference `{{SchemaName}}` directly or transitively |
| **Per iteration** | Once per schema-template iteration, with the iteration's `{{SchemaName}}` substituted in first | Any token whose body references `{{SchemaName}}` directly or transitively |

The tier matters because a per-product value is computed once and reused everywhere, while a per-iteration value is freshly computed for `tenant_acme`, then again for `tenant_beta`, and so on. Per-database query tokens run against the live server but are cached across the iterations of a single target database — so the connection round-trip happens once per database, not once per tenant.

**The escalation rule:** the engine can only promote a token's resolution tier, never demote it. A `<*Query*>` token without any `{{SchemaName}}` reference could legitimately depend on database-specific state — the absence of `{{SchemaName}}` is not proof that the result is the same for every tenant. Only the *presence* of `{{SchemaName}}` (directly in the token's body, or transitively through a token it references) lets the engine conclude the value must be recomputed per iteration. Any other token keeps its natural tier.

**Transitive escalation:** if token `A`'s body references `{{SchemaName}}` and token `B`'s body references `{{A}}`, then `B` is also per-iteration — even if `B`'s body contains no `{{SchemaName}}` text. The engine walks the token dependency graph at template load and marks every transitively reachable token.

Worked example: a template defines `ScriptTokens.TenantTable: "{{SchemaName}}.Orders"`. At template load, the engine sees `{{SchemaName}}` in the value and marks `TenantTable` as per-iteration. At each schema-template iteration, `{{TenantTable}}` resolves to `tenant_acme.Orders` (or `tenant_beta.Orders`, etc.) before the script runs. The per-product and per-database computation passes never see this token — it sits out until its iteration is active.

> **Note:** An iteration-scoped token used in a product-level script (`Product.json` `BaselineValidationScript` or `VersionStampScript`) is a validation error at template load. Those scripts run outside any iteration, and the engine has no schema name to substitute into the token's value.

---

## Practical Examples

### Multi-environment deployment

A SaaS product uses tokens to manage database names and version stamps across development, staging, and production.

**Product.json** defines the baseline:

```json
{
  "Name": "SaasProduct",
  "TemplateOrder": ["Registry", "Client"],
  "ScriptTokens": {
    "RegistryDb": "Registry",
    "MigrationVersion": "3.2.0"
  }
}
```

**A migration script** announces the deployment target using the resolved tokens. The token shape is identical across platforms; only the logging statement differs.

**SQL Server:**

```sql
PRINT 'Deploying {{ProductName}} '
    + '{{MigrationVersion}} against '
    + '{{RegistryDb}}';
```

**PostgreSQL:**

```sql
DO $$ BEGIN
  RAISE NOTICE 'Deploying % % against %',
    '{{ProductName}}',
    '{{MigrationVersion}}',
    '{{RegistryDb}}';
END $$;
```

**MySQL:**

```sql
SELECT CONCAT(
  'Deploying {{ProductName}} ',
  '{{MigrationVersion}} ',
  'against {{RegistryDb}}'
) AS deployment_banner;
```

For staging, drop a `ScriptTokens.RegistryDb` override into `SchemaQuench.settings.json` and ship the same package. For CI, do the same via `SmithySettings_ScriptTokens__RegistryDb`. The schema package is unchanged across all three environments; only the resolved token values differ.

### Cross-database references with multiple tokens

When templates need to reference sibling schema managed by other templates, product-level tokens keep the references consistent. The token pattern is the same across platforms; the naming the tokens encode differs because each engine isolates differently -- SQL Server, MySQL, and MariaDB use separate databases, PostgreSQL uses schemas within one database.

**SQL Server** -- separate databases, three-part names:

```json
{
  "Name": "ECommerce",
  "TemplateOrder": ["Catalog", "Orders", "Reporting"],
  "ScriptTokens": {
    "CatalogDb": "ProductCatalog",
    "OrdersDb": "OrderProcessing"
  }
}
```

```sql
CREATE OR ALTER VIEW dbo.SalesSummary AS
SELECT o.OrderDate, p.ProductName, o.Quantity, o.Total
FROM [{{OrdersDb}}].dbo.Orders o
JOIN [{{CatalogDb}}].dbo.Products p ON o.ProductId = p.Id;
```

**PostgreSQL** -- one database, separate schemas:

```json
{
  "Name": "ECommerce",
  "TemplateOrder": ["Catalog", "Orders", "Reporting"],
  "ScriptTokens": {
    "CatalogSchema": "product_catalog",
    "OrdersSchema": "order_processing"
  }
}
```

```sql
CREATE OR REPLACE VIEW analytics.sales_summary AS
SELECT o.order_date, p.product_name, o.quantity, o.total
FROM {{OrdersSchema}}.orders o
JOIN {{CatalogSchema}}.products p ON o.product_id = p.id;
```

**MySQL** -- separate databases, db-qualified names:

```json
{
  "Name": "ECommerce",
  "TemplateOrder": ["Catalog", "Orders", "Reporting"],
  "ScriptTokens": {
    "CatalogDb": "product_catalog",
    "OrdersDb": "order_processing"
  }
}
```

```sql
CREATE OR REPLACE VIEW `analytics`.`SalesSummary` AS
SELECT o.`OrderDate`, p.`ProductName`, o.`Quantity`, o.`Total`
FROM `{{OrdersDb}}`.`Orders` o
JOIN `{{CatalogDb}}`.`Products` p ON o.`ProductId` = p.`Id`;
```

> **PostgreSQL note:** PG can't join across separate databases natively -- the idiomatic pattern is one database with multiple schemas, and SchemaSmith deploys each template against its own schema within that database. Cross-database queries via `postgres_fdw` or `dblink` are possible but add setup that's out of scope for most deployments; if you need them, the token approach still works -- define foreign-server tokens and reference them the same way.

### Generating audit DDL from `{{TableSchema}}`

A migration script that consumes the live table model to drive its own logic. The stored procedure walks the JSON, reads each table's columns and any `Extensions.AuditScope` you've attached, and emits the right `CREATE TRIGGER` statements -- one declarative source of truth, one runtime that adapts to it.

**SQL Server:**

```sql
DECLARE @TableSchema NVARCHAR(MAX) = '{{TableSchema}}';
EXEC dbo.GenerateAuditTriggers @TableSchema;
```

**PostgreSQL:**

```sql
DO $$
DECLARE
  v_table_schema TEXT := '{{TableSchema}}';
BEGIN
  CALL public.generate_audit_triggers(v_table_schema);
END $$;
```

**MySQL:**

```sql
SET @TableSchema = '{{TableSchema}}';
CALL GenerateAuditTriggers(@TableSchema);
```

### Pulling deployment-time data from the target

A `Before` migration script needs to know which rows on the server are flagged for the new feature. Point the token at a query file using `<*QueryFile*>` so the SQL stays readable, and SchemaSmith runs that query once against the actual server you're deploying to before substituting the result into every script that references the token. The token definition is the same shape across platforms; the query body differs because each engine has its own catalog and quoting style.

**SQL Server:**

```json
{
  "ScriptTokens": {
    "EnabledTargets": "<*QueryFile*>queries/enabled-targets.sql"
  }
}
```

`queries/enabled-targets.sql`:

```sql
SELECT TargetName
FROM dbo.FeatureFlags
WHERE FlagName = 'NewBilling'
  AND Enabled = 1;
```

Any script in the template:

```sql
-- {{EnabledTargets}} resolves to one target per line
PRINT 'Applying billing schema to: {{EnabledTargets}}';
```

**PostgreSQL:**

```json
{
  "ScriptTokens": {
    "EnabledTargets": "<*QueryFile*>queries/enabled-targets.sql"
  }
}
```

`queries/enabled-targets.sql`:

```sql
SELECT target_name
FROM public.feature_flags
WHERE flag_name = 'NewBilling'
  AND enabled = true;
```

Any script in the template:

```sql
-- {{EnabledTargets}} resolves to one target per line
DO $$ BEGIN
  RAISE NOTICE 'Applying billing schema to: %', '{{EnabledTargets}}';
END $$;
```

**MySQL:**

```json
{
  "ScriptTokens": {
    "EnabledTargets": "<*QueryFile*>queries/enabled-targets.sql"
  }
}
```

`queries/enabled-targets.sql`:

```sql
SELECT `TargetName`
FROM `FeatureFlags`
WHERE `FlagName` = 'NewBilling'
  AND `Enabled` = 1;
```

Any script in the template:

```sql
-- {{EnabledTargets}} resolves to one target per line
SELECT CONCAT('Applying billing schema to: ',
              '{{EnabledTargets}}') AS status;
```

Across these four scenarios, the token shape stays constant -- `{{TokenName}}` in scripts, `ScriptTokens` in JSON -- while the SQL dialect and the override surface flex per environment. The same schema package ships unchanged to development, staging, and production; the token layer is where each environment's shape gets expressed.

---

## Related Documentation

- [Schema Packages Reference](schema-packages.md) -- Product and template structure
- [Custom Properties](custom-properties.md) -- Attach metadata via `Extensions` and consume it through tokens
- [Configuration Reference](configuration.md) -- Settings files and environment variables
- [SchemaQuench Reference](schemaquench.md) -- Where token resolution fits into deployment
