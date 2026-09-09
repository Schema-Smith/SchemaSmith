# SchemaTongs Reference

Cast your live database into version-controlled code with a single command. SchemaTongs grips every object in your database -- on **SQL Server**, **PostgreSQL**, **MySQL**, or **MariaDB** -- and extracts them into a clean schema package: tables as JSON, programmable objects as SQL scripts, everything organized and ready to commit to source control. Point it at a database, run it, and you have a deployable package that SchemaQuench can quench onto any compatible target.

The same tool, the same package format, four engines. Whatever your team runs, SchemaTongs casts it.

---

## Installation

See the [Installation guide](../guide/installation.md) for all install channels — Chocolatey, `.deb`, `.rpm`, `install.sh`, and manual download.

---

## Running SchemaTongs

From the directory containing your `SchemaTongs.settings.json`:

```bash
SchemaTongs
```

With a custom config file:

```bash
SchemaTongs --ConfigFile:C:\configs\extract-staging.json
```

With a full connection string override (platform-appropriate):

```bash
# SQL Server
SchemaTongs --ConnectionString:"Data Source=myserver;Initial Catalog=NorthwindDev;User ID=sa;Password=secret;TrustServerCertificate=True;"

# PostgreSQL
SchemaTongs --ConnectionString:"Host=myserver;Port=5432;Database=northwind;Username=tongs;Password=secret;"

# MySQL
SchemaTongs --ConnectionString:"Server=myserver;Port=3306;Database=northwind;User=tongs;Password=secret;"
```

SchemaTongs connects to the source database, reads every enabled object type, and writes the results to the configured product path. On first run it creates the full package structure; on subsequent runs it updates existing files in place and **preserves any custom metadata you have attached** under `Extensions`.

### Schema-only mode

If you just need to regenerate the `.json-schemas/*.schema` validation files for an existing product -- without connecting to a database -- use the `--WriteSchemasOnly` switch. The product path comes from your `SchemaTongs.settings.json` (or an environment variable override), not from the command line:

```bash
# Run from the directory that contains SchemaTongs.settings.json with Product:Path already set
SchemaTongs --WriteSchemasOnly

# Or set the path inline via environment variable (note the double underscore for the nested key)
SmithySettings_Product__Path=./my-product SchemaTongs --WriteSchemasOnly
```

This reads the `Platform` from `Product.json`, regenerates the schema files on the fly from the current engine's C# domain types, and exits. No database connection, no extraction. Useful when you've updated SchemaSmith and want CI validation to match the new engine without re-extracting.

---

## Version Pre-Flight

Before any extraction work begins, SchemaTongs detects the **source** server's version, logs it, and enforces SchemaSmith's intrinsic per-engine floor. The log line is written for every run:

```
Source SqlServer version 16.0.4260.1 (compatibility level 160)
```

The compatibility level is appended on SQL Server only; the other engines have no such concept. The version string is whatever the engine itself publishes — see [SchemaQuench — The detected version is reported in each engine's own form](schemaquench.md#the-detected-version-is-reported-in-each-engines-own-form) for why the four differ.

**The floor is structural, not opt-in.** You declare nothing. It is the version below which the engine scripts do not run at all — SQL Server 2008, PostgreSQL 12, MySQL 5.7, MariaDB 10.2. A below-floor source aborts before extraction starts:

```
srv01: detected SqlServer version 9.00.5000.00 is below the minimum supported version
2008 (major 10). SchemaSmith cannot run against it.
```

On SQL Server the **source database's** compatibility level is checked as well and must be `100` or higher. That is reported as a distinct case, because it is a different problem with a different fix:

```
srv01: database Northwind is at compatibility level 90; SchemaSmith requires 100
(SQL Server 2008) or higher. Raise it with ALTER DATABASE ... SET COMPATIBILITY_LEVEL = 100.
```

Failing here is deliberate. Without the pre-flight, a below-floor source dies deep inside extraction with a raw engine error — on SQL Server, typically `'STRING_AGG' is not a recognized built-in function name`, which reads like a SchemaSmith bug rather than an unsupported server.

> **This is not `Product.MinimumVersion`.** That is a separate, opt-in gate you declare per product to raise the floor *further*, and it is a deployment-time concern — see [SchemaQuench — MinimumVersion pre-flight gate](schemaquench.md#minimumversion-pre-flight-gate). The floor described here is always enforced and cannot be lowered or disabled.

---


## Extracting from a Read-Only Replica

SchemaTongs installs a small set of helper procedures into the database it reads from, and refreshes them whenever they are out of date. That needs write access — which the copy you most want to extract from often will not give you. A SQL Server Availability Group readable secondary, a PostgreSQL hot standby, and a MySQL/MariaDB replica are all read-only by design, and they are precisely the copies you are allowed to query hard without upsetting anyone.

Against a read-only target SchemaTongs **verifies the helpers instead of installing them**, then extracts as normal. Four outcomes:

| What it finds | What happens |
|---|---|
| Helpers present and matching this build | Extraction proceeds; nothing to report. |
| Helpers present but **older** than this build | **Warning**, then extraction proceeds. |
| Helpers present, version **not determinable** | **Warning** saying the install was skipped and they might be out of date, then extraction proceeds. |
| Helpers **absent** | **Error.** Nothing is extracted. |

> **Action required:** if extraction stops because the helpers are absent, run SchemaSmith once against the **primary** — any deploy or any extraction against a writable copy installs them — and let the change reach the replica. SchemaSmith does not attempt the install itself, because a read-only target rejects every write it needs.

A stale-helpers warning is not fatal on purpose. Refusing would make a replica useless the moment the primary is one version ahead of your CLI, which is most of the time. The warning exists because an extraction taken with older helpers can be quietly incomplete — it may not know about properties this build understands — so treat it as a reason to check the result against the primary if anything looks wrong, not as a reason to stop.

Deployment is unaffected: SchemaQuench still requires a writable target, because applying a schema genuinely cannot work without one. Use [`SkipIfReadOnly`](schema-packages.md) on a template if you want a deploy to pass over a read-only target rather than fail against it.

## Configuration Reference

SchemaTongs reads configuration from multiple sources, merged in precedence order (highest priority last):

1. **Settings file** -- `SchemaTongs.settings.json` in the working directory (or the file specified by `--ConfigFile`)
2. **User secrets** (debug builds only)
3. **Environment variables** with the `SmithySettings_` prefix
4. **Command-line switches** (highest precedence)

For the full explanation of configuration loading, environment variable mapping, and CLI switches, see the [Configuration Reference](configuration.md).

### Complete SchemaTongs.settings.json

```json
{
  "Source": {
    "Server": "",
    "Port": "",
    "User": "",
    "Password": "",
    "Database": "",
    "Platform": "",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    }
  },
  "Product": {
    "Path": "",
    "Name": "",
    "CheckConstraintStyle": "ColumnLevel"
  },
  "Template": {
    "Name": ""
  },
  "ShouldCast": {
    "Tables": true,
    "Views": true,
    "Functions": true,
    "Procedures": true,
    "TableTriggers": true,
    "ObjectList": "",
    "ValidateScripts": false,
    "SaveInvalidScripts": true,
    "ScriptDynamicDependencyRemovalForFunctions": false,

    "Schemas": true,

    "UserDefinedTypes": true,
    "Catalogs": true,
    "StopLists": true,
    "DDLTriggers": true,
    "XMLSchemaCollections": true,
    "IndexedViews": true,

    "DomainTypes": true,
    "EnumTypes": true,
    "CompositeTypes": true,
    "Aggregates": true,
    "Sequences": true,
    "Rules": true,
    "MaterializedViews": true,

    "Events": true
  },
  "OrphanHandling": {
    "Mode": "Detect"
  },
  "FolderMapping": {
    "Schemas": "Schemas",
    "Functions": "Functions",
    "Views": "Views",
    "Procedures": "Procedures",
    "Triggers": "Triggers"
  }
}
```

The platform-specific `ShouldCast` flags are simply ignored when they don't apply to the configured `Source:Platform`. You can leave the full set in place across all your environments.

### Source connection

| Key | Type | Default | Description |
|---|---|---|---|
| `Source:Platform` | string | _(required)_ | One of `"SqlServer"`, `"PostgreSQL"`, `"MySQL"`, or `"MariaDb"`. Determines which extraction adapter runs. |
| `Source:Server` | string | _(required)_ | Database server hostname or IP. |
| `Source:Port` | string | platform default | TCP port. SQL Server `1433`, PostgreSQL `5432`, MySQL `3306`. |
| `Source:User` | string | _(empty)_ | Login username. SQL Server allows blank for Windows authentication. |
| `Source:Password` | string | _(empty)_ | Login password. |
| `Source:Database` | string | _(required)_ | Name of the database to extract from. |
| `Source:ConnectionProperties` | object | `{}` | Arbitrary key-value pairs appended to the connection string. Platform-specific keys -- see the [Configuration Reference](configuration.md#connection-configuration). |

The `--ConnectionString` switch bypasses all `Source` settings entirely. When provided, `Server`, `Port`, `User`, `Password`, `Database`, and `ConnectionProperties` are all ignored. `Source:Platform` is still required so the right adapter can claim the connection.

### Product output

| Key | Type | Default | Description |
|---|---|---|---|
| `Product:Path` | string | _(required)_ | Directory where the schema package is created or updated. |
| `Product:Name` | string | _(directory name)_ | Product name written to `Product.json`. If blank, defaults to the last segment of `Product:Path`. |
| `Product:CheckConstraintStyle` | string | `ColumnLevel` | Controls how check constraints are written when creating a new `Product.json`. See [CheckConstraintStyle](#checkconstraintstyle). |
| `Product:ObjectOrder` | string | `Name` | Sequence used for a table's object lists when there is nothing to preserve. `Name` sorts alphabetically; `Physical` uses the table's own column order. See [Column and object order](#column-and-object-order). |
| `Product:PreserveExistingOrder` | bool | `true` | When a table file already exists, keep the order it already had. See [Column and object order](#column-and-object-order). |
| `Template:Name` | string | Source database name | Template name. Creates the template directory under `Templates/<Name>/`. Defaults to the `Source:Database` value when not specified. |

---

## ShouldCast Flags

Control exactly what gets cast from the database. Each object type can be individually enabled or disabled. Flags that don't apply to the active platform are simply ignored.

### All platforms

| Flag | Default | What It Extracts |
|---|---|---|
| `Tables` | `true` | Table definitions as JSON files |
| `Views` | `true` | View definitions as SQL scripts |
| `Functions` | `true` | User-defined functions |
| `Procedures` | `true` | Stored procedures |
| `TableTriggers` | `true` | Table-level triggers |
| `ObjectList` | _(empty)_ | Comma- or semicolon-separated list of specific objects to extract. See [ObjectList Filtering](#objectlist-filtering). |
| `ValidateScripts` | `false` | Parses each extracted SQL script for validity. See [Script Validation](#script-validation). |
| `SaveInvalidScripts` | `true` | When `ValidateScripts` is enabled, saves failing scripts as `.sqlerror` files. |
| `ScriptDynamicDependencyRemovalForFunctions` | `false` | Generates a dynamic dependency-removal preamble for function scripts. See [ScriptDynamicDependencyRemovalForFunctions](#scriptdynamicdependencyremovalforfunctions). |

### SQL Server and PostgreSQL

| Flag | Default | What It Extracts |
|---|---|---|
| `Schemas` | `true` | Schema creation scripts (MySQL and MariaDB don't have schemas distinct from databases) |

### SQL Server only

| Flag | Default | What It Extracts |
|---|---|---|
| `UserDefinedTypes` | `true` | User-defined data types and table types |
| `Catalogs` | `true` | Full-text catalogs |
| `StopLists` | `true` | Full-text stop lists with their stop words |
| `DDLTriggers` | `true` | Database-level DDL triggers |
| `XMLSchemaCollections` | `true` | XML schema collections |
| `IndexedViews` | `true` | Indexed (materialized) view definitions as JSON files |

### PostgreSQL only

| Flag | Default | What It Extracts |
|---|---|---|
| `DomainTypes` | `true` | `CREATE DOMAIN` user-defined types |
| `EnumTypes` | `true` | `CREATE TYPE ... AS ENUM` types |
| `CompositeTypes` | `true` | `CREATE TYPE ... AS (...)` composite types |
| `Aggregates` | `true` | User-defined aggregate functions |
| `Sequences` | `true` | Sequence objects |
| `Rules` | `true` | Rules attached to tables and views |
| `MaterializedViews` | `true` | Materialized view definitions as JSON files |

### MySQL and MariaDB

| Flag | Default | What It Extracts |
|---|---|---|
| `Events` | `true` | Scheduled events |

---

## Folder Mapping

By default, each extracted object type lands in the standard folder for the active platform (see [Schema Packages -- Default Folders](schema-packages.md#default-folders)). The optional `FolderMapping` section lets you rename those default folders without breaking SchemaQuench's recognition of them.

```json
{
  "FolderMapping": {
    "Schemas": "01_Schemas",
    "Functions": "02_Functions",
    "Views": "03_Views",
    "Procedures": "04_Procedures",
    "Triggers": "05_Triggers"
  }
}
```

The keys are `ScriptObjectType` values (e.g., `Schemas`, `Functions`, `Views`, `Procedures`, `Triggers`, `DomainTypes`, `EnumTypes`, `CompositeTypes`, `Sequences`, `Rules`, `Aggregates`, `Events`, etc.). The values are the folder names you want to use under each template directory.

A few rules:

- **Fixed folders cannot be remapped.** `IndexedViews` (SQL Server) and `MaterializedViews` (PostgreSQL) always live in `Indexed Views/` and `Materialized Views/`. Tables always live in `Tables/`.
- **Duplicate folder names are rejected.** SchemaTongs validates the mapping at startup; if two object types map to the same folder, you get an error before extraction begins.
- **Unmapped types use the platform default.** Anything you leave out of `FolderMapping` keeps its standard folder name.

This pairs naturally with the `ScriptFolders` array on `Template.json`. You can extract using your renamed folder layout and SchemaQuench will still pick up the right slot for each folder.

---

## ObjectList Filtering

The `ObjectList` setting restricts extraction to a specific set of objects. Provide a comma- or semicolon-separated list of object names:

```json
"ShouldCast": {
  "ObjectList": "dbo.Customer,dbo.vw_ActiveCustomers,usp_GetOrders"
}
```

Names can be specified with or without a schema prefix. Matching is case-insensitive. When `ObjectList` is empty (the default), all objects matching the enabled `ShouldCast` flags are extracted.

When `ObjectList` is active, **orphan detection is automatically disabled** -- SchemaTongs can't tell which files are genuinely orphaned when only a subset of objects is being extracted.

---

## Orphan Detection

Databases change. Objects get dropped or renamed. The old script files linger in your package -- unless you tell SchemaTongs what to do about them. Configure orphan behavior with `OrphanHandling:Mode`:

| Mode | Behavior |
|---|---|
| `Detect` | Orphaned files are logged as warnings. No files are modified or deleted. **Default.** |
| `DetectWithCleanupScripts` | Orphaned files are logged. For each orphan in a script folder, a cleanup script containing a `DROP` statement is generated as `_OrphanCleanup_<FolderName>.sql` in the log directory. The orphaned files themselves are left in place. |
| `DetectDeleteAndCleanup` | Orphaned files are deleted from the package. Cleanup `DROP` scripts are generated in the log directory. |

### When to use each mode

- **`Detect`** -- Safe default. Use during routine re-extraction when you want to review orphans manually before taking action.
- **`DetectWithCleanupScripts`** -- Use when you want SchemaTongs to prepare the cleanup work but you want to review the generated scripts and delete orphaned files yourself.
- **`DetectDeleteAndCleanup`** -- Use when you trust the extraction to be authoritative and want a fully clean package after each run. Particularly useful in automated pipelines.

### How it works

SchemaTongs builds a file index for each extraction folder before extraction begins. As objects are extracted and written to disk, each file is marked as "written." After extraction completes, any indexed file that wasn't written is an orphan. Cleanup scripts from previous runs are archived into numbered `SchemaTongs.NNNN` backup directories before new ones are generated.

Orphan detection only runs for object types that were fully extracted (`ShouldCast` flag enabled and no `ObjectList` filter active).

### The core tension

An "orphan" might be a script for an object that was genuinely removed from the database, OR it might be a new object you added to the package that has not been deployed yet. Deleting it automatically would destroy your pending work. The default (`Detect`) is conservative because it never destroys anything -- it just tells you.

---

## Custom Property Preservation

Custom metadata you've attached to your tables under [`Extensions`](custom-properties.md) is preserved across re-extractions. When SchemaTongs writes a table file, it checks whether a previous file existed for the same table and copies the previous `Extensions` content forward onto the freshly extracted table.

The preservation pass uses the component's `Name` (with quote/bracket characters stripped, case-insensitive). For columns and the table itself, matching also falls back to `OldName`, so renamed components keep their custom metadata as long as you set `OldName` correctly before the rename is deployed.

| Component | Matched By | All Platforms |
|---|---|---|
| Table | root object | Yes |
| Column | `Name`, then `OldName` | Yes |
| Index | `Name` | Yes |
| ForeignKey | `Name` | Yes |
| CheckConstraint | `Name` | Yes |
| ExcludeConstraint | `Name` | PostgreSQL only |
| Statistic | `Name` | PostgreSQL only |
| FullTextIndex | `Name` | SQL Server, MySQL |
| MaterializedView | root object | PostgreSQL only |
| IndexedView | root object | SQL Server only |
| XmlIndex | `Name` | SQL Server only |

This is what makes [Custom Properties](custom-properties.md) trustworthy across the SchemaTongs / SchemaQuench round-trip: you attach metadata once, and it survives every re-extraction as long as the underlying object stays.

---

## Table File Naming

SchemaTongs writes each table file under a canonical name derived from the table's content:

```
<schema>.<table>[.<VariantName>].json
```

The optional `VariantName` segment comes after the schema and table, so a table's [conditional variants](schema-packages.md#conditional-application) sort together in source control and in a file listing. The schema segment is omitted whenever the table's content carries no `Schema` (`<table>[.<VariantName>].json`): MySQL and MariaDB, which have no per-table schema; schema-template packages, where the schema is the iteration variable; and PostgreSQL tables in the default `public` schema, which SchemaTongs omits from the written content so the deploy re-resolves it. A PostgreSQL table in a *named* schema keeps both the content `Schema` and the filename prefix (`sales.order_lines.json`).

A table's identity lives in its file *content* -- `Schema`, `Name`, and `VariantName` -- not in its filename. SchemaTongs matches an existing file to an extracted table by that content identity, so a file you renamed by hand is still found and refreshed in place rather than duplicated. The canonical name is a convention, not a contract: if a file's name drifts from canonical, the deploy still works and [`--Validate`](validate.md#file-naming) emits an `SS-FILE-NAME-003` warning pointing at the canonical name.

## Variant Reconciliation on Re-Extraction

When a table -- or one of its components (column, index, foreign key, constraint, statistic, full-text index) -- carries an authored [variant set](schema-packages.md#conditional-application) (two or more same-named entries each gated by `ShouldApplyExpression`), SchemaTongs can only ever see one physical shape on the source database. Rather than discard what it extracted, it **evaluates each variant's gate against the source and folds the extracted shape into the variant that is active there**, keeping that variant's gate and `VariantName` and leaving the inactive variants untouched. Real drift on the deployed variant is captured, not lost.

If no single variant's gate is active on the source, the extracted shape is written as an **ungated** entry alongside the authored variants; `--Validate` then flags the ungated-plus-gated mix (`SS-DUP-001`) so you can fold it into the right variant or gate it. A malformed or erroring gate fails the extraction rather than guessing (fail-closed).

---

## Script Validation

Catch problems at extraction time instead of discovering them during deployment. When `ShouldCast:ValidateScripts` is `true`, SchemaTongs tests each extracted SQL script for validity immediately after writing it. The validation strategy depends on the platform and the object type, but the principle is the same: try to compile the object server-side, roll back, and flag failures.

### Invalid script handling

Scripts that fail validation are handled according to `ShouldCast:SaveInvalidScripts`:

| SaveInvalidScripts | Behavior |
|---|---|
| `true` (default) | The script is saved with a `.sqlerror` extension instead of `.sql`. The original `.sql` file (if any) is removed. |
| `false` | The script isn't written to disk at all. |

### .sqlerror files

`.sqlerror` files are SQL scripts that failed extraction validation. They serve as a record of potential problems without blocking the rest of the package.

| Tool | Behavior |
|---|---|
| **SchemaQuench** | Skips `.sqlerror` files -- only `.sql` files are loaded and executed. |
| **SchemaTongs** | On re-extraction, overwrites `.sqlerror` files with the latest content. If the script still fails validation, it stays as `.sqlerror`. If it now passes, it's written as `.sql` and the `.sqlerror` is removed. |

### False positives

Validation failures aren't always genuine errors. Common false positives include:

- **Cross-database references** -- Scripts referencing objects in another database may fail when the parser can't resolve the cross-database context.
- **Temporary objects** -- References to temp tables or variables created elsewhere in the same batch.
- **Forward references inside the same batch** -- A function that calls another function created later in the same script.

To override a false positive, rename the file from `.sqlerror` to `.sql`. SchemaQuench will then include it in the next deployment.

When any scripts fail validation, SchemaTongs generates an `_InvalidObjectCleanup.sql` file in the log directory containing diagnostic information for all invalid objects detected during the extraction run.

---

## Subfolder Preservation

Organize your scripts however makes sense for your team -- SchemaTongs respects that structure across re-extractions. Any extracted script folder supports user-created subfolders for organizing scripts. For example, you might organize stored procedures by domain:

```
Procedures/
  Orders/
    public.usp_get_order.sql
    public.usp_create_order.sql
  Customers/
    public.usp_get_customer.sql
```

SchemaTongs preserves these subfolders across re-extraction. Before extraction begins, it builds a file index that maps every `.sql`, `.sqlerror`, and `.json` file to its full path, including any subfolder. When writing an extracted object, SchemaTongs checks the index:

- If the file already exists in a subfolder, it's written back to that same subfolder.
- If the file exists in multiple subfolders, a warning is logged and the file is written to the folder root.
- If the file is new (not in the index), it's written to the folder root.

New objects always appear in the root of their folder. Move them into subfolders as desired -- SchemaTongs will remember the location on the next run. This means you can shape your extracted package to fit *your* code review and team conventions, not the other way around.

---

## Package Initialization

The first extraction is where your schema package is born. When SchemaTongs runs against a path that doesn't yet contain a schema package, it creates the full structure:

1. Creates the product directory at `Product:Path`.
2. Generates `Product.json` with the configured product name and the `Platform` from `Source:Platform`.
3. Creates the template directory under `Templates/<TemplateName>/`.
4. Generates `Template.json` with a `DatabaseIdentificationScript` targeting the source database.
5. Creates all standard script folders for the active platform (see [Default Folders](schema-packages.md#default-folders)).
6. Creates a `.json-schemas/` directory with JSON Schema validation files generated **on the fly** from the live engine types.

On subsequent runs against an existing package, SchemaTongs overwrites object scripts and table definitions with the current database state. It does not modify `Product.json` or `Template.json` -- if you change `Platform`, `Name`, or `TemplateOrder` after the first extraction, those edits stick.

### Helper procedures

SchemaTongs maintains lightweight helper procedures in the source database under the `SchemaSmith` schema -- installing or refreshing them on every run where writes are allowed, and, against a read-only target, verifying them instead (see [Extracting from a Read-Only Replica](#extracting-from-a-read-only-replica)). These procedures generate the JSON representation of tables and views and are used internally by the extraction adapter. They're read-only, schema-prefixed, and excluded from extraction output.

---

## Column and object order

Two settings decide the sequence of the lists inside an extracted table file — `Columns`, `Indexes`,
`ForeignKeys`, `CheckConstraints` and, on SQL Server, `Statistics` and `XmlIndexes`. They answer different
questions and compose without conflict.

### PreserveExistingOrder (default `true`)

When SchemaTongs overwrites a table file that already exists, every entry still present keeps the position
that file gave it. Entries the database no longer has drop out. Entries that are genuinely new are appended
using `ObjectOrder`.

The point is that re-extracting should produce a diff of what actually changed. Without this, a one-column
change arrives buried in a whole-file reshuffle — and a package whose ordering was arranged by hand loses
that arrangement every time it is refreshed.

> **This does not change your database.** It preserves the order *in the file*. It does **not** make a
> deployed table's physical column order match the package — reordering the columns of an existing table
> is a table rebuild, not a formatting preference, and is a separate opt-in. If you have never re-extracted
> over an existing file, this setting has no effect on anything.

Set it to `false` to have every extraction re-sequence the whole file from scratch using `ObjectOrder`.

### ObjectOrder (default `Name`)

`Name` sorts alphabetically. It is the default because it is stable: the same table extracts identically
whichever engine it came from, and it does not shift when a source table's internal column order changes.

`Physical` uses the table's own column order instead, which is useful when you want the package to read
the way the table does. Two points worth knowing:

- It applies to the **column list only**. Indexes, foreign keys, check constraints and statistics have no
  ordinal that anyone authors toward, so those stay alphabetical either way.
- The physical order is a property of *that* database. The same logical table on another server can order
  its columns differently, so a package extracted with `Physical` is not guaranteed to match elsewhere.

Because `PreserveExistingOrder` defaults to `true`, `ObjectOrder` decides a **first** extraction and any
newly-added entries after that. Once a file exists, its order is what survives.

### Calling the extraction procedures directly

The procedures SchemaTongs uses are usable by hand, and they take the same choice. The mechanism differs by
engine because of what each engine's stored routines support:

```sql
-- SQL Server
EXEC SchemaSmith.GenerateTableJSON @p_Schema = 'dbo', @p_Table = 'Customer', @p_ObjectOrder = 'Physical';

-- PostgreSQL
SELECT "SchemaSmith"."GenerateTableJSON"('public', 'customer', 'Physical');

-- MySQL and MariaDB: a session variable, because their stored procedures cannot
-- declare default parameter values -- adding a parameter would break every existing CALL.
SET @SchemaSmith_ObjectOrder = 'Physical';
CALL SchemaSmith_GenerateTableJSON('mydb', 'customer');
```

Omit the argument (or leave the session variable unset) and you get `Name`, which is what the tool writes
by default.
> **Called by hand, the argument reorders columns only.** The setting orders a table's other object
> lists too — indexes, foreign keys, check constraints, and on SQL Server statistics and XML indexes
> — but SchemaTongs sequences those itself after the procedure returns. So a direct `CALL` with
> `Physical` gives you physical *column* order and leaves the rest as the procedure emitted them.


---

## CheckConstraintStyle

The `Product:CheckConstraintStyle` setting controls how check constraints are represented in extracted table JSON. It only takes effect when creating a new `Product.json` -- on existing products, the style is read from the product file itself.

### ColumnLevel (default)

Each check constraint is stored as a `CheckExpression` property on the column it applies to:

```json
{
  "Name": "[Status]",
  "DataType": "varchar(20)",
  "CheckExpression": "[Status] IN ('Active','Inactive','Pending')"
}
```

### TableLevel

All check constraints are promoted to named entries in the table's `CheckConstraints` array:

```json
{
  "CheckConstraints": [
    {
      "Name": "[CK_Orders_Status]",
      "Expression": "[Status] IN ('Active','Inactive','Pending')"
    }
  ]
}
```

`TableLevel` style preserves the original constraint names from the database. Use it when constraint naming matters for your deployment process or when you have multi-column check constraints.

If the config specifies a different style than what is already in `Product.json`, SchemaTongs logs a warning and extracts using the style from the existing product file. To change styles on an existing product, update `Product.json` directly.

---

## ScriptDynamicDependencyRemovalForFunctions

When `ShouldCast:ScriptDynamicDependencyRemovalForFunctions` is `true`, SchemaTongs generates a dynamic SQL preamble before each function script. This preamble detects and temporarily removes database objects that depend on the function (computed columns, check constraints, foreign keys involving computed columns, indexes on computed columns or filter expressions referencing the function). At deployment time, SchemaQuench executes the preamble first, dropping all dependents, then creates or alters the function. The dependents are recreated by subsequent steps in the quench (table definitions restore computed columns, constraints, and indexes).

### Surgical use only

This is a surgical fix, not a default. Enable it on functions where:

1. The function is referenced by computed columns or filtered indexes, AND
2. Changes regularly enough that manual dependency management is impractical, AND
3. The dependency drop-and-rebuild is acceptably fast.

**The risk that makes this opt-in:** if a computed column is persisted or indexed on a 500-million-row table, dropping that dependency means rebuilding the persisted column or index after the function is updated -- which could take a very long time and impact production availability. For functions that rarely change, the right approach is to leave this flag off and write a migration script that handles the dependencies carefully when the function truly needs to change.

---

## Encrypted Object Handling (SQL Server)

When SchemaTongs encounters an encrypted object on SQL Server (a function, view, stored procedure, or trigger whose definition in `sys.sql_modules` is `NULL`), it logs a warning, skips the object, and excludes it from orphan detection. Encrypted objects can't be extracted because the engine doesn't expose their source text. If you need these objects in your schema package, script them manually and add the files to the appropriate folder.

---

## Excluded Objects

SchemaTongs automatically excludes the platform's system schemas and internal infrastructure:

- **System objects** -- Anything flagged as system-shipped by the source engine.
- **System schemas** -- `sys` and `INFORMATION_SCHEMA` (SQL Server); `pg_catalog`, `information_schema`, `pg_toast`, and the per-session `pg_temp_*` / `pg_toast_temp_*` schemas (PostgreSQL). On MySQL, SchemaTongs is single-schema-scoped: it extracts only the schema named in `Source:Database`, so the system schemas (`mysql`, `information_schema`, `performance_schema`, `sys`) are simply outside scope unless you point at one explicitly.
- **User schemas are NOT excluded.** SQL Server's `dbo` and `guest`, and PostgreSQL's `public`, are user schemas. Their tables, views, procedures, functions, triggers, and types are all extracted normally. The shipped Northwind demo lives under `dbo` and round-trips end to end.
- **SchemaSmith infrastructure** -- All objects in the `SchemaSmith` schema (the helper procedures SchemaTongs and SchemaQuench deploy).
- **Schema-creation script gaps (SQL Server)** -- The pass that emits `Schemas/*.sql` scripts additionally skips system-shipped schemas (`schema_id <= 4`, which covers `dbo`, `guest`, `INFORMATION_SCHEMA`, `sys`) and database-role schemas (names matching `db[_]%`). Object extraction under those schemas is not affected -- the gap is only in the standalone `CREATE SCHEMA` scripts.
- **Replication artifacts** (SQL Server) -- Tables prefixed with `MSPeer_` or `MSPub_`.
- **Legacy system tables** (SQL Server) -- `dtproperties` and `sysdiagrams`.

---

## Schema-Template Extraction

You have N tenant schemas hand-replicated inside one database. One is the canonical copy. Instead of maintaining those copies by hand, point SchemaTongs at the canonical schema, give it the name, and it extracts a schema template: unqualified filenames, source-schema references rewritten to `{{SchemaName}}`, and a `Template.json` stub wired for schema-level fan-out. SchemaQuench can then govern every tenant schema from that single template.

Schema-template extraction is supported on **SQL Server and PostgreSQL only**. MySQL and MariaDB have no schema-inside-database concept -- use `DatabaseIdentificationScript` with a database-per-tenant template instead.

### Activation

Two settings activate schema-template extraction mode — `Source.Schema` in the `Source` section and `SchemaIdentificationScript` in the `Template` section of `SchemaTongs.settings.json`:

```json
"Source": {
  "Schema": "tenant_acme"
},
"Template": {
  "Name": "TenantBody",
  "SchemaIdentificationScript": ""
}
```

| Field | Type | When set |
|---|---|---|
| `Source:Schema` | string | Required. Non-empty value activates schema-template extraction mode. The value is the schema to extract from the source database (e.g. `"tenant_acme"`). |
| `Template:SchemaIdentificationScript` | string | Optional. Written verbatim into the generated `Template.json`. When blank, SchemaTongs generates a stub that returns the source schema as a single row so you can quench-test the package immediately. |

`Source:Schema` is the switch. When it is empty (the default), SchemaTongs behaves exactly as it always has. When it is non-empty, schema-template mode activates and all transformations in this section apply.

See [Schema Templates](schema-packages.md#schema-templates) for the full `Template.json` property reference.

### Transformations

In schema-template mode, SchemaTongs rewrites extracted content so it is schema-agnostic before it lands on disk.

| Output element | Regular mode | Schema-template mode |
|---|---|---|
| **Filename** | `tenant_acme.Customers.json` | `Customers.json` (schema prefix dropped) |
| **JSON `Schema` field** | Set to source schema | Omitted -- engine fills `{{SchemaName}}` at template load |
| **FK `RelatedTableSchema`** | Set to actual reference schema | Omitted when it matches the source schema; preserved as-is for cross-schema FKs |
| **SQL bodies** | Object names emitted as `tenant_acme.Obj` | Source-schema-qualified refs rewritten to `{{SchemaName}}.Obj`; cross-schema refs (`dbo.Countries`, `public.LookupTable`) preserved literally |
| **Expression fields in JSON** | Emitted as-is | `Default`, `CheckExpression`, `ComputedExpression`, `FilterExpression`, `GenerationExpression`, and similar expression-bearing properties are also rewritten through the same source-schema substitution |

The rewriter handles three identifier forms for the source schema:

- SQL Server: `[tenant_acme].[Customers]`, `tenant_acme.Customers`, `"tenant_acme"."Customers"`
- PostgreSQL: `"tenant_acme"."Customers"`, `tenant_acme.Customers`

All collapse to `{{SchemaName}}.<name>`, preserving the original quoting style on the object name. References to any other schema are left untouched.

> **Note:** The rewriter masks single-quoted string literals before substitution, so a source-schema name embedded inside a `'...'` literal is preserved verbatim. PostgreSQL dollar-quoted blocks (`$tag$...$tag$`) are not recognized as literals -- content inside them is treated as ordinary SQL and is subject to rewriting. This is a known limitation; the unqualified-identifier audit (below) surfaces affected files for review.

### Unqualified ids

Unqualified identifiers in extracted SQL bodies are left untouched. SchemaTongs cannot safely determine whether `Foo` refers to `tenant_acme.Foo` or to a shared schema without semantic analysis of the full SQL context.

After extraction, SchemaTongs emits one warning listing every `.sql` file containing unqualified object references, along with 1-based line numbers:

```
The following extracted files contain unqualified object references. Review them
and add `{{SchemaName}}.` qualification where the reference targets the iteration
schema. Unqualified references that target a shared schema (built-in functions,
shared lookup tables) can stay as-is — make that decision deliberately, because
the deployed engine does not set a default schema per iteration.

  - Procedures/UpdateCustomer.sql: lines 14, 27, 43
  - Functions/CalculateTotal.sql: lines 8
```

This is a punch list, not a blocker. References that target a shared schema (`dbo`, `public`, a global lookup) can stay unqualified. References that target the tenant schema need `{{SchemaName}}.` prepended. The decision is yours -- SchemaTongs never auto-prefixes.

### Generated stub

When `Source:Schema` is set and no `Template.json` exists yet at the output path, SchemaTongs creates a ready-to-use schema-template stub. For a SQL Server extraction with `Template.Name = "TenantBody"` and `Source.Schema = "tenant_acme"`:

```json
{
  "Name": "TenantBody",
  "DatabaseIdentificationScript": "SELECT [Name] FROM master.sys.databases WHERE [Name] = '{{TenantBodyDb}}'",
  "ScriptFolders": [ ... ],
  "ScriptTokens": {},
  "SchemaIdentificationScript": "-- TODO: replace with a query returning the active iteration schemas.\n-- Placeholder uses the seed schema as a single-row example.\nSELECT 'tenant_acme' AS SchemaName"
}
```

The stub's `SchemaIdentificationScript` returns the source schema as a single row. This means you can run SchemaQuench immediately against the extracted package -- it deploys back to `tenant_acme` and validates the round-trip -- without writing any discovery SQL. Replace the stub with your real query when you are ready to fan out to all tenants.

The four schema-template fan-out fields -- `RequireAtLeastOneTarget`, `CreateSchemaIfMissing`, `AllowParallel`, and `ContinueOnSchemaFailure` -- are not emitted by the stub because each one matches its default (`true`, `false`, `true`, `true` respectively). The serializer omits default-value properties, and `Template.Load` reads the absent values back as their defaults. To override any of them, add the property explicitly to `Template.json`.

If `Template.json` already exists at the output path, SchemaTongs does not overwrite it. Your edits survive re-extraction.

### ShouldCast flags

Four `ShouldCast` flags are forced off in schema-template extraction mode because the object types they control are database-scoped, not schema-scoped. They cannot fan out per-schema iteration and must live in a regular template earlier in `TemplateOrder`.

**SQL Server:**

| Flag | Forced | Warning message |
|---|---|---|
| `ShouldCast:Schemas` | `false` | "schema objects ARE the iteration unit" |
| `ShouldCast:DDLTriggers` | `false` | "DDL triggers are database-scoped" |
| `ShouldCast:Catalogs` | `false` | "full-text catalogs are database-scoped" |
| `ShouldCast:StopLists` | `false` | "full-text stop lists are database-scoped" |

**PostgreSQL:**

| Flag | Forced | Warning message |
|---|---|---|
| `ShouldCast:Schemas` | `false` | "schema objects ARE the iteration unit" |

The warning fires when the flag was `true` (or at its default-true value). A quiet skip would be too surprising -- if you have `ShouldCast:Schemas: true` in your config and nothing appears under `Schemas/`, you deserve an explanation.

All other `ShouldCast` flags -- `Tables`, `Views`, `Functions`, `Procedures`, `TableTriggers`, and the PostgreSQL-specific type flags -- are honored in schema-template mode exactly as in regular mode.

### Round-trip

Once a schema template is extracted, re-running SchemaTongs against the same source schema with the same `Source.Schema` setting produces the same output. The extraction is deterministic: the same source objects, same rewriting rules, same filename conventions. This means you can automate re-extraction as part of your schema governance cycle without worrying about drift between runs.

The round-trip property also drives the integration test suite: extract `tenant_seed`, drop and recreate it empty, quench the extracted package with `SchemaIdentificationScript` returning `tenant_seed`, and compare the rebuilt schema to the original structurally. Every CI run exercises this path end to end on both SQL Server and PostgreSQL.

For the end-to-end walkthrough, see [Migrating from manual duplication](../guide/10-multi-tenant-deployments.md#migrating-from-manual-duplication) in the Multi-Tenant Deployments guide.

### Current limitations

- **One schema per run.** `Source.Schema` targets a single schema. To convert multiple canonical schemas, run SchemaTongs once per schema with different `Template.Name` values.
- **No auto-qualification.** The conservative audit warning is intentional. Unqualified identifiers are flagged for review; SchemaTongs never auto-prefixes them with `{{SchemaName}}.`.
- **No multi-schema refs.** Stored procedures referencing three or more schemas with conditional logic may need manual review after the rewrite pass. The audit warning surfaces those files.

---

## Related Documentation

- [Configuration Reference](configuration.md) -- Shared configuration system, CLI switches, settings file format, environment variable mapping
- [Schema Packages Reference](schema-packages.md) -- Package structure, default folders, JSON formats
- [Custom Properties](custom-properties.md) -- The `Extensions` carrier and how it's preserved through re-extraction
- [SchemaQuench Reference](schemaquench.md) -- Deployment tool that consumes the packages SchemaTongs creates
- [DataTongs Reference](datatongs.md) -- Reference data extraction
