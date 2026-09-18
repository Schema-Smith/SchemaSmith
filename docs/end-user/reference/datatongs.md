# DataTongs Reference

Lookup tables, configuration rows, seed data -- every database has reference data that needs to travel with the schema. DataTongs grips that data from a live database and produces self-contained synchronization scripts that bring it into any target. Point it at a source, list the tables you care about, and it produces one SQL file per table -- ready to drop into a schema package's `Table Data` folder or run directly against any compatible instance.

DataTongs supports **SQL Server**, **PostgreSQL**, **MySQL**, and **MariaDB**. The script syntax adapts per platform: SQL Server and PostgreSQL use `MERGE`, while MySQL and MariaDB use `INSERT ... ON DUPLICATE KEY UPDATE` (paired with a conditional `DELETE WHERE NOT EXISTS` step when full delete sync is enabled). The configuration shape and the workflow are identical across all four.

---

## Invocation

DataTongs ships as part of the SchemaSmith distribution — see the [Installation guide](../guide/installation.md) for how to get the binary on your PATH. Run it from the directory containing your `DataTongs.settings.json` configuration file:

```bash
DataTongs
```

To use a configuration file in a different location:

```bash
DataTongs --ConfigFile:path/to/DataTongs.settings.json
```

DataTongs reads configuration from multiple sources, merged in this precedence order (highest priority last):

1. **Configuration file** -- `DataTongs.settings.json` in the current working directory (or the file specified by `--ConfigFile`)
2. **User secrets** (debug builds only)
3. **Environment variables** with the `SmithySettings_` prefix
4. **Command-line switches** (highest precedence)

For the full list of CLI switches shared by all SchemaSmith tools, see the [Configuration Reference](configuration.md#cli-switch-format).

---

## Configuration Reference

A complete `DataTongs.settings.json`:

```json
{
  "Source": {
    "Server": "localhost",
    "Port": "",
    "User": "",
    "Password": "",
    "Database": "ReferenceData",
    "Platform": "SqlServer",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    }
  },
  "ContentPath": "./Templates/Main/data",
  "ScriptPath":  "./Templates/Main/Table Data",
  "Tables": [
    { "Name": "dbo.Country",  "KeyColumns": "CountryCode" },
    { "Name": "dbo.Currency", "KeyColumns": "CurrencyCode", "Filter": "IsActive = 1" }
  ],
  "ShouldCast": {
    "DisableTriggers": false,
    "MergeUpdate": true,
    "MergeDelete": true
  }
}
```

### Source connection

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `Source:Platform` | string | _(required)_ | One of `"SqlServer"`, `"PostgreSQL"`, `"MySQL"`, or `"MariaDb"`. Selects the script generator. |
| `Source:Server` | string | _(required)_ | Database server hostname or IP. |
| `Source:Port` | string | platform default | TCP port. SQL Server `1433`, PostgreSQL `5432`, MySQL `3306`. |
| `Source:User` | string | _(empty)_ | Login username. SQL Server allows blank for Windows authentication. |
| `Source:Password` | string | _(empty)_ | Login password. |
| `Source:Database` | string | _(required)_ | Source database to extract data from. |
| `Source:ConnectionProperties` | object | `{}` | Arbitrary key-value pairs appended to the connection string. Platform-specific keys -- see the [Configuration Reference](configuration.md#connection-configuration). |

The `--ConnectionString` switch bypasses all `Source` settings and passes the provided value directly to the platform-appropriate driver. `Source:Platform` is still required so the right script generator is used.

### Output paths

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `ScriptPath` | string | `"."` | Directory where the generated `Populate <schema>.<table>.sql` merge scripts are written. Created automatically if it doesn't exist. |
| `ContentPath` | string | `"."` | Directory where the sibling `.tabledata` files (raw JSON row data) are written. When `--ConfigureDataDelivery` is on, this path is also used to locate the enclosing template -- DataTongs walks upward from `ContentPath` looking for a `Template.json`. |

The typical placement inside a schema package is `ScriptPath` pointing at `Templates/<Name>/Table Data` and `ContentPath` pointing at `Templates/<Name>/data`. That layout makes the script files visible to SchemaQuench's `TableData` quench slot and the `.tabledata` files reachable from each table's `DataDelivery` block.

### Tables array

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Name` | string | _(required)_ | Table name in `schema.table` format. If no schema prefix is given, the platform default is assumed (`dbo` on SQL Server, `public` on PostgreSQL, the connection database on MySQL). |
| `KeyColumns` | string | _(auto-detected)_ | Comma-separated column names for the row-matching key. When blank, auto-detected from the table's primary key or best unique index. Prefix a column with `*` for NULL-safe comparison on nullable keys. |
| `SelectColumns` | string | _(all columns)_ | Comma-separated column names to extract. When blank, every non-excluded column is extracted. Use to narrow extraction to a specific subset. |
| `Filter` | string | _(empty)_ | SQL `WHERE` clause (without the `WHERE` keyword) to filter which rows are extracted. Also applied to the delete clause when `MergeDelete` is enabled. |
| `MergeType` | string | _(derived)_ | Per-table override for the `DataDelivery:MergeType` default. Values: `None`, `Insert`, `Insert/Update`, `Insert/Update/Delete`. When blank, derived from global `ShouldCast:MergeUpdate` + `ShouldCast:MergeDelete`. |

### Script generation flags (ShouldCast)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `ShouldCast:OutputScripts` | bool | `true` | Generate merge scripts. When `false`, only `.tabledata` files are written. Also skipped, per table, when `--ConfigureDataDelivery` actually configured delivery for that table -- see [Delivery takes precedence over merge scripts](#delivery-takes-precedence-over-merge-scripts). |
| `ShouldCast:OutputContentFiles` | bool | `true` | Write raw data to sibling `.tabledata` content files. Set to `false` to skip writing content files (and, transitively, to skip `ConfigureDataDelivery` since the configurator needs a content file path to record). |
| `ShouldCast:DisableTriggers` | bool | `false` | Wraps the generated script with platform-appropriate trigger disable/enable. |
| `ShouldCast:MergeUpdate` | bool | `true` | Includes the update branch (matched rows whose data has changed). |
| `ShouldCast:MergeDelete` | bool | `true` | Includes the delete branch (target rows missing from the source). On all four platforms: SQL Server / PostgreSQL emit `WHEN NOT MATCHED BY SOURCE THEN DELETE` inside the `MERGE`; MySQL and MariaDB emit an `INSERT ... ON DUPLICATE KEY UPDATE` followed by a separate `DELETE WHERE NOT EXISTS` step (because they have no `MERGE`). |
| `ShouldCast:MergeType` | string | `Insert/Update` | Default `DataDelivery:MergeType` for tables that don't set it explicitly. Values: `None`, `Insert`, `Insert/Update`, `Insert/Update/Delete`. Used when writing `DataDelivery` blocks via `--ConfigureDataDelivery`. |
| `ShouldCast:ConfigureDataDelivery` | bool | `false` | After extraction, write a `DataDelivery` block into each matching table's JSON file. See [--ConfigureDataDelivery](#--configuredatadelivery). |
| `ShouldCast:TokenizeScripts` | bool | `true` | **All source engines.** Embeds each table's extracted row data in the merge script as a `{{<table>.tabledata}}` content token instead of inline JSON/XML text, matching SchemaTongs' tokenization behavior. DataTongs wires the matching `ScriptTokens` entry automatically -- see [Content-file tokenization](#tokenizescripts). Set to `false` to inline the literal row data in generated scripts instead. |
| `ShouldCast:DeliveryEncoding` | string | `Json` | `Xml` extracts each table's content in the XML delivery encoding (so a package can deploy on a legacy-compatibility SQL Server, below the `OPENJSON` cliff), on any source engine, and stamps `"ContentEncoding": "Xml"` on the configured `DataDelivery`. Also settable per run with `--DeliveryEncoding=Xml`. See [Delivery encoding](#delivery-encoding-xml-for-legacy-sql-server). **Not the same setting as `Source:CompatEncoding`** — see the next row. |
| `Source:CompatEncoding` | string | `auto` | SQL Server only. Forces the form of the catalog queries DataTongs builds its delivery script from — `legacy` uses the pre-`STRING_AGG` form a SQL Server 2008–2016 source needs, `modern` the current one. `auto` detects it. This does **not** change the content files' format; that is `DeliveryEncoding` above. See [Forcing the encoding](schemaquench.md#forcing-the-encoding). |
| `ShouldCast:DisableRules` | bool | `false` | **PostgreSQL only.** Wraps the delivery in `ALTER TABLE ... DISABLE RULE` / `ENABLE RULE`. |
| `ShouldCast:UpdateDescendents` | bool | `true` | **PostgreSQL only.** When `false`, the generated `MERGE` uses `MERGE INTO ... ONLY` so partitioned-table writes do not propagate to descendant tables. |

For environment variable mapping, see [Configuration Reference -- Environment Variables](configuration.md#environment-variables). The `Tables` array can't be configured via environment variables -- use the JSON configuration file for table definitions.

---

## Generated Script Anatomy

What DataTongs writes to disk depends on the platform. The data extraction step is the same: query the source table in deterministic key order and serialize each row to JSON. The substitution into a sync script is platform-specific.

### SQL Server

SQL Server uses `MERGE` with `OPENJSON` to parse the embedded data:

```sql
DECLARE @v_json NVARCHAR(MAX) = '[
{"CompanyName":"Speedy Express","Phone":"(503) 555-9831","ShipperID":1},
{"CompanyName":"United Package","Phone":"(503) 555-3199","ShipperID":2},
{"CompanyName":"Federal Shipping","Phone":"(503) 555-9931","ShipperID":3}
]';

ALTER TABLE [dbo].[Shippers] DISABLE TRIGGER ALL;     -- if DisableTriggers
SET IDENTITY_INSERT [dbo].[Shippers] ON;               -- if identity column exists

MERGE INTO [dbo].[Shippers] AS Target
USING (
  SELECT [CompanyName],[Phone],[ShipperID]
    FROM OPENJSON(@v_json)
    WITH (
      [CompanyName] NVARCHAR(40),
      [Phone] NVARCHAR(24),
      [ShipperID] INT
    )
) AS Source
ON Source.[ShipperID] = Target.[ShipperID]

WHEN MATCHED AND (<change detection>) THEN             -- if MergeUpdate
  UPDATE SET
    [CompanyName] = Source.[CompanyName],
    [Phone] = Source.[Phone]

WHEN NOT MATCHED THEN
  INSERT ([CompanyName], [Phone], [ShipperID])
  VALUES (Source.[CompanyName], Source.[Phone], Source.[ShipperID])

WHEN NOT MATCHED BY SOURCE THEN                        -- if MergeDelete
  DELETE
;

SET IDENTITY_INSERT [dbo].[Shippers] OFF;              -- if identity column exists
ALTER TABLE [dbo].[Shippers] ENABLE TRIGGER ALL;       -- if DisableTriggers
```

### PostgreSQL

PostgreSQL 15 and later support `MERGE` natively. DataTongs generates a `MERGE` against a `jsonb` literal source:

```sql
MERGE INTO "public"."shippers" AS "Target"
USING (
  SELECT "company_name", "phone", "shipper_id"
    FROM jsonb_to_recordset('[
      {"company_name":"Speedy Express","phone":"(503) 555-9831","shipper_id":1},
      {"company_name":"United Package","phone":"(503) 555-3199","shipper_id":2}
    ]') AS x("company_name" varchar(40), "phone" varchar(24), "shipper_id" integer)
) AS "Source"
ON "Source"."shipper_id" = "Target"."shipper_id"

WHEN MATCHED AND (...change detection...) THEN
  UPDATE SET "company_name" = "Source"."company_name",
             "phone"        = "Source"."phone"

WHEN NOT MATCHED THEN
  INSERT ("company_name", "phone", "shipper_id")
  VALUES ("Source"."company_name", "Source"."phone", "Source"."shipper_id")

WHEN NOT MATCHED BY SOURCE THEN
  DELETE
;
```

The `MERGE INTO ... ONLY` clause is used when partitioned tables should not propagate to descendants.

### MySQL

MySQL doesn't have a `MERGE` statement. DataTongs generates the appropriate idiom based on the configured behavior:

- **Insert + Update (no delete)** -- `INSERT ... ON DUPLICATE KEY UPDATE`
- **Insert only (seed)** -- `INSERT IGNORE`
- **Insert + Update + Delete** -- `INSERT ... ON DUPLICATE KEY UPDATE` followed by `DELETE WHERE NOT EXISTS` to remove rows not present in the source data

```sql
INSERT INTO `northwind`.`shippers` (`company_name`, `phone`, `shipper_id`) VALUES
  ('Speedy Express',   '(503) 555-9831', 1),
  ('United Package',   '(503) 555-3199', 2),
  ('Federal Shipping', '(503) 555-9931', 3)
ON DUPLICATE KEY UPDATE
  `company_name` = VALUES(`company_name`),
  `phone`        = VALUES(`phone`);
```

The MySQL generator infers the right idiom from `MergeUpdate` and `MergeDelete`.

---

## Table Configuration

### Table name

Specify tables in `schema.table` format. If you omit the schema, the platform default is assumed:

```json
{ "Name": "dbo.Country" }                  // SQL Server: defaults to dbo
{ "Name": "config.feature_flags" }         // PostgreSQL: explicit schema
{ "Name": "Products" }                     // SQL Server: dbo.Products
```

DataTongs validates that each table exists in the source database before attempting extraction. Tables that don't exist are skipped with an error message.

### Key columns

Key columns define the row-matching predicate -- the `ON` clause on platforms that support `MERGE`, the unique-key match on MySQL.

**Auto-detection:** When `KeyColumns` is blank, DataTongs queries the table's indexes and selects the primary key if one exists. If there's no primary key, it falls back to the first available unique index. Nullable columns in the detected key are automatically prefixed with `*` for NULL-safe comparison. This handles the vast majority of tables without any manual configuration.

**Manual override:** When you specify `KeyColumns`, DataTongs uses your list instead of auto-detection. Separate multiple columns with commas:

```json
{ "Name": "dbo.OrderLine", "KeyColumns": "OrderID, LineNumber" }
```

**Nullable key columns:** If a key column allows NULLs, prefix it with `*`. This generates NULL-safe matching:

```json
{ "Name": "dbo.Mapping", "KeyColumns": "SourceCode, *TargetCode" }
```

The generated `ON` clause becomes:

```sql
ON Source.[SourceCode] = Target.[SourceCode]
   AND (Source.[TargetCode] = Target.[TargetCode]
        OR (Source.[TargetCode] IS NULL AND Target.[TargetCode] IS NULL))
```

When auto-detection discovers a nullable column in a unique index, it automatically applies the `*` behavior.

**No key available:** If a table has no primary key, no unique index, and no `KeyColumns` configured, DataTongs skips the table with an error message.

### Filter

The `Filter` field accepts a SQL `WHERE` clause (without the `WHERE` keyword). It controls two things:

1. **Which rows are extracted** from the source database
2. **Which target rows are eligible for deletion** when `MergeDelete` is enabled (every engine -- see [`MergeDelete`](#mergedelete) for the per-engine form)

```json
{ "Name": "dbo.FeatureFlags", "KeyColumns": "FlagName", "Filter": "IsActive = 1" }
```

This extracts only active flags and, if `MergeDelete` is on, only deletes active flags that no longer exist in the source. Inactive flags in the target are untouched.

---

## ShouldCast Options

### DisableTriggers

When enabled, DataTongs wraps each generated script with platform-appropriate trigger control. Use this when the target table has audit or notification triggers whose side effects are undesirable during a bulk data refresh. The generated scripts are plain SQL files -- you can edit them to disable specific triggers rather than all of them when fine-grained control is needed.

### MergeUpdate

When enabled, the script updates existing rows whose data has changed. When disabled, the script only inserts new rows and (on MERGE platforms, if `MergeDelete` is on) deletes missing rows -- existing rows are left untouched regardless of whether their data differs.

### MergeDelete

When enabled, the script deletes rows from the target that don't exist in the source data. When a `Filter` is configured, the delete clause respects the filter so that rows outside the filter are never removed. When disabled, the script only inserts and updates -- no rows are ever deleted from the target.

**Per-platform implementation:**

- **SQL Server / PostgreSQL:** The delete is a `WHEN NOT MATCHED BY SOURCE THEN DELETE` clause inside the same `MERGE` statement.
- **MySQL:** Because MySQL has no `MERGE`, the generated script is two statements -- an `INSERT ... ON DUPLICATE KEY UPDATE` for the upsert plus a separate `DELETE WHERE NOT EXISTS` that removes target rows missing from the source. (The retired `REPLACE INTO` idiom isn't used: it deletes and re-inserts every matched row, which breaks `ON DELETE RESTRICT` foreign keys.)

### Delivery scenarios

These settings compose into three practical patterns:

| Scenario | MergeUpdate | MergeDelete | Effect |
|----------|-------------|-------------|--------|
| **Full sync** (default) | `true` | `true` | Insert missing, update changed, delete removed. Target matches source exactly. |
| **Add and update, no deletes** | `true` | `false` | Insert missing, update changed, leave extra rows alone. Good when targets have environment-specific additions. |
| **Seed only** | `false` | `false` | Insert missing rows only. Existing rows untouched, nothing deleted. Good for seed data without overwriting local customizations. |

The demo products use insert+update with no deletes (`MergeDelete: false`) and `DisableTriggers: true`. They deliver data for every table, so if delete were enabled, any rows a user added while experimenting would be removed on the next deployment.

---

## Special Type Handling

DataTongs detects column types and applies the correct extraction and restoration strategy for each. The full set varies by platform; the highlights:

### SQL Server

- **Identity columns** -- detected automatically. The script wraps the merge with `SET IDENTITY_INSERT ON`/`OFF` and includes the identity in the `INSERT` clause but excludes it from `UPDATE SET`.
- **Computed columns** -- auto-excluded from extraction and all script sections.
- **Geography** -- full round-trip via WKT (`.ToString()`) plus the SRID captured separately.
- **Geometry** -- extracted as WKT (basic support; restoration may need manual adjustment for complex cases).
- **HierarchyID** -- canonical string round-trip.
- **XML / NTEXT / TEXT / IMAGE** -- mapped through the OPENJSON layer with type-appropriate cast for change detection.
- **Auto-excluded:** `sql_variant`, `rowversion` / `timestamp`, ROWGUIDCOL columns. A warning is logged for each skipped column.

### PostgreSQL

- **Identity columns** -- handled via `OVERRIDING SYSTEM VALUE` where appropriate.
- **Generated columns** -- auto-excluded.
- **JSON / JSONB** -- preserved through the round-trip.
- **Auto-excluded with warning:** `tsvector`, `tsquery`, `money`, geometric types (`box`, `circle`, `line`, `lseg`, `path`), and user-defined composite types. These are skipped because their JSON representation can't reliably round-trip without manual intervention.

### MySQL

- **Auto-increment columns** -- handled at the `INSERT` level so explicit values can be carried.
- **Generated / virtual columns** -- auto-excluded.
- **Spatial types** -- basic support via WKT.
- **TEXT / BLOB families** -- supported through string and binary literals.

When a column is excluded from extraction, DataTongs logs a warning naming the table, the column, and the underlying type so you know exactly what was skipped.

### MariaDB

MariaDB uses the MySQL delivery path with the same behaviors as above. One operational difference: MariaDB's default `max_allowed_packet` is smaller than MySQL 8's 64&nbsp;MB. Because each table's data is delivered as a single JSON payload, delivering a large table can exceed that limit and fail with `Error submitting <N>MB packet; ensure 'max_allowed_packet' is greater than <N>MB`. Raise the server's `max_allowed_packet` (for example `--max-allowed-packet=64M`, or the equivalent in `my.cnf`) to accommodate your largest table's row set.

---

## Source Query

DataTongs extracts data using a deterministic, JSON-shaped query. The exact form differs per platform (SQL Server uses `FOR JSON AUTO`, PostgreSQL uses `jsonb_agg(row_to_json(...))`, MySQL uses `JSON_OBJECT` aggregation), but the principles are the same:

- **Read-friendly hints** -- Where the platform supports a non-blocking read hint (`WITH (NOLOCK)` on SQL Server, no special hint needed on PostgreSQL, MySQL, or MariaDB), DataTongs uses it to avoid blocking production workloads. This is appropriate because DataTongs is extracting a snapshot of reference data, not transactional data requiring strict consistency.
- **Deterministic order** -- Results are ordered by the key columns to produce diff-friendly output. Run DataTongs twice against unchanged data and you get identical files.
- **Empty tables** -- When the query returns no data, DataTongs skips script generation for that table entirely.

---

## Output

### File naming

Each table produces one file:

```
Populate <schema>.<tablename>.sql
```

For example:

| Table | Output File |
|-------|-------------|
| `dbo.Country` | `Populate dbo.Country.sql` |
| `HumanResources.Department` | `Populate HumanResources.Department.sql` |
| `public.feature_flags` | `Populate public.feature_flags.sql` |
| `northwind.shippers` | `Populate northwind.shippers.sql` |

DataTongs also writes a sibling `.tabledata` file (the raw JSON row data) alongside each script, so the extracted content can be consumed by the declarative `DataDelivery` pipeline -- see [--ConfigureDataDelivery](#--configuredatadelivery). When `ShouldCast:OutputScripts` is `false`, the script files are skipped and only the `.tabledata` files are written.

Table and schema names containing characters that are illegal in file names are percent-encoded (see [Schema Packages -- Filesystem-Illegal Character Encoding](schema-packages.md#filesystem-illegal-character-encoding)).

### Output directory

Merge scripts are written to `ScriptPath` and `.tabledata` content files to `ContentPath`. Both directories are created automatically if they don't exist. The typical placement inside a schema package is `ScriptPath` pointing at the template's `Table Data` folder and `ContentPath` pointing at the template's `data` folder:

```json
"ScriptPath":  "C:\\SchemaPackage\\Templates\\Main\\Table Data",
"ContentPath": "C:\\SchemaPackage\\Templates\\Main\\data"
```

When the output lands inside a `Table Data` folder of a schema package, SchemaQuench picks it up automatically on the next deployment via the `TableData` quench slot. Reference data ships alongside schema, in the same package, in the same release.

---

## Change Detection

Nobody wants a script that updates every row just because it can. The update branch of the generated script fires only when the source row actually differs from the target row. DataTongs generates a type-aware comparison for every non-key, non-identity column.

For most columns:

```sql
NOT (Target.[Column] = Source.[Column]
     OR (Target.[Column] IS NULL AND Source.[Column] IS NULL))
```

This NULL-safe comparison treats two NULLs as equal (no update needed) and a NULL versus a non-NULL as different (update needed).

For special types -- geography, XML, large text/binary types -- the comparison is wrapped with the appropriate cast or normalization function (e.g., `.ToString()` for geography on SQL Server, `CAST(... AS NVARCHAR(MAX))` for XML and NTEXT).

This approach means running DataTongs twice against unchanged data produces a script that matches every row but updates none -- the script becomes a no-op for existing data. Diffs are clean. Reviews are honest.

---

## --ConfigureDataDelivery

DataTongs can do more than write scripts -- it can wire the extracted data straight into your schema package's declarative delivery pipeline. Pass `--ConfigureDataDelivery` and DataTongs writes a `DataDelivery` block into each matching table's JSON file after extracting the data.

```bash
DataTongs --ConfigureDataDelivery
DataTongs --ConfigureDataDelivery --TemplatePath:./Templates/Main
```

The switch can also be enabled via configuration:

```json
{
  "ShouldCast": { "ConfigureDataDelivery": true }
}
```

### What it does

For every table that produces output, DataTongs opens `Tables/<schema>.<table>.json` inside the template and writes (or overwrites) a `DataDelivery` block with:

| Property | Value |
|---|---|
| `ContentFile` | Path to the written `.tabledata` file, relative to the template root. |
| `MergeType` | Either the per-table `MergeType` from config, or a default derived from `ShouldCast:MergeUpdate` + `ShouldCast:MergeDelete` (see below). |
| `MatchColumns` | The key columns used during extraction -- either the per-table `KeyColumns`, or the primary key auto-detected from the source database (same logic that drives the generated script). |
| `MergeFilter` | The per-table `Filter`, if one was specified. |
| `MergeDisableTriggers` | Mirrors `ShouldCast:DisableTriggers`. |
| `MergeDisableRules` | **PostgreSQL.** Mirrors `ShouldCast:DisableRules`. |
| `MergeUpdateDescendents` | **PostgreSQL.** Mirrors `ShouldCast:UpdateDescendents` (default `true`). |
| `ContentEncoding` | `Xml` when extracting with `--DeliveryEncoding=Xml`, on any source engine (omitted for the default JSON). See [Delivery encoding](#delivery-encoding-xml-for-legacy-sql-server). |

Tables whose JSON file doesn't exist in the template are skipped with a warning. No file is created from scratch -- this tool configures existing tables, it doesn't scaffold new ones (use SchemaTongs for that).

**Authored arrays of gated deliveries are left alone.** If a table's `DataDelivery` is already an array of two or more deliveries -- hand-authored gating or environment variants, see [Schema Packages -- Multiple Deliveries](schema-packages.md#multiple-deliveries) -- `--ConfigureDataDelivery` does not touch it. There's no single-delivery identity to reconcile extraction output against, so the array survives untouched and a warning is logged noting it was left as-is. Configure the single-object form first, then hand-edit it into a gated array once you need per-target variants or additive slices.

### Default MergeType derivation

When a table's config doesn't set `MergeType` explicitly, the default is derived from the global `ShouldCast` flags:

| `MergeUpdate` | `MergeDelete` | Default `MergeType` |
|:---:|:---:|---|
| `true` | `true` | `Insert/Update/Delete` |
| `true` | `false` | `Insert/Update` |
| `false` | any | `Insert` |

A per-table `MergeType` on the `Tables[]` entry always wins over the derived default.

### Delivery takes precedence over merge scripts

`--ConfigureDataDelivery` and `ShouldCast:OutputScripts` both default to producing output, so without this rule a table would get delivered twice -- once through the `DataDelivery` block, once through a generated merge script carrying the same rows. When delivery is actually configured for a table this run, DataTongs does not also emit that table's merge script.

The check is per table, not a global switch: `--ConfigureDataDelivery` can decline for an individual table (its `Tables/<name>.json` isn't found, or its `DataDelivery` is an authored array with no matching `VariantName`), and a table delivery never covered still gets its script normally. A table whose delivery genuinely was configured -- freshly written or already up to date -- is the only one that loses its script.

DataTongs logs this once at startup, not per table (the per-table `Updated data delivery config for <table>` message already covers the detail):

- If `OutputScripts` is left at its default, an informational line notes that scripts will be suppressed where delivery is configured.
- If `OutputScripts` is set to `true` explicitly alongside `--ConfigureDataDelivery`, that is contradictory configuration -- DataTongs still applies the precedence rule (delivery wins) but logs a warning instead of an info line, since the explicit setting is being overridden.

## Delivery encoding (XML for legacy SQL Server)

By default DataTongs extracts a table's data as a raw JSON array. On SQL Server, SchemaQuench delivers that with `OPENJSON` -- a path that requires **SQL Server compatibility level 130** (SQL Server 2016+). To produce a package that also deploys on an older SQL Server target (compatibility level 100--120), extract in the XML delivery encoding instead:

```bash
DataTongs --DeliveryEncoding=Xml
DataTongs --DeliveryEncoding=Xml --ConfigureDataDelivery
```

or in `DataTongs.settings.json`:

```json
{ "ShouldCast": { "DeliveryEncoding": "Xml" } }
```

With `Xml`, DataTongs writes each `.tabledata` file in the [XML delivery shape](schema-packages.md#content-encoding) -- `<rows><row><c n="Col">value</c>...</row></rows>`, with the database's native text forms (binary base64, geometry WKT + an SRID companion, ISO-8601 dates) and `NULL` columns omitted -- and, when `--ConfigureDataDelivery` is on, stamps `"ContentEncoding": "Xml"` on the reconciled `DataDelivery` entry so the extract → deploy round-trip works against a compatibility-level-100 database.

**Available on every source engine.** SQL Server extracts XML natively. PostgreSQL, MySQL, and MariaDB have no native XML producer, so DataTongs extracts the engine's normal JSON and converts it to the identical shape above -- one delivery XML dialect regardless of source engine, not a per-engine variant. That parity is useful even when the deployment target is never SQL Server: the `.tabledata` file is a standalone artifact, and `Xml` is a reasonable choice whenever a downstream consumer -- a third-party system, a hand-off package -- wants XML rather than JSON, independent of whether SchemaSmith ever reads the file back.

**Known limitation: geometry/geography SRID in data extraction.** Per-row SRID capture is a SQL-Server-only extraction capability — the JSON extraction query captures the SRID (`col.STSrid`) only on SQL Server. PostgreSQL and MySQL JSON extraction capture WKT (`ST_AsText()`) alone, with no per-row SRID companion. XML-encoded extracts inherit this same limitation, since XML is converted from the JSON result — a spatial column extracted as XML from PostgreSQL or MySQL carries WKT without the `.STSrid` companion. On **MySQL**, this no longer loses the reference system for a column whose schema declares a `Srid` restriction (`col POINT SRID 4326` -- see [MySQL column extras](schema-packages.md#mysql-column-extras)): every row in a SRID-restricted column is guaranteed to carry that one reference system, so a deploy target reconstructs it from the schema definition rather than needing the per-row companion. On **PostgreSQL** the same reasoning applies through the column *type* rather than a separate attribute: a typed column (`geometry(Point,4326)`) coerces an incoming SRID-less value to its declared reference system, so that round trip is lossless too. The gap is therefore narrower than it first looks — it remains open for an *unrestricted* MySQL spatial column and an *untyped* PostgreSQL `geometry` column. Those are the cases to watch, and they fail quietly: the WKT arrives intact and the value lands with SRID `0`, so the coordinates look right while the reference system is wrong. Extract spatial data from SQL Server, or type/restrict the destination column, when that matters. All other column types (binary, dates, NULLs, and non-spatial columns) are fully portable in both JSON and XML.

### Template root discovery

`--ConfigureDataDelivery` needs to know where your template lives. It uses this precedence:

1. `--TemplatePath:<path>` CLI switch.
2. `TemplatePath` config key.
3. Auto-discovery: walk upward from `ContentPath` looking for the nearest directory containing a `Template.json`.

If nothing is found, DataTongs logs a warning and skips the configuration step -- extraction still completes normally.

### Typical workflow

1. Use SchemaTongs to extract your schema package (tables, procedures, folder structure).
2. Use DataTongs with `--ConfigureDataDelivery` to pull reference data and write the delivery blocks in one pass:

   ```json
   {
     "Source": { "Platform": "SqlServer", "Server": "prod", "Database": "AppMain" },
     "ContentPath": "./Templates/Main/data",
     "ScriptPath":  "./Templates/Main/Table Data",
     "ShouldCast": {
       "MergeUpdate": true,
       "MergeDelete": false,
       "DisableTriggers": true
     },
     "Tables": [
       { "Name": "dbo.Country" },
       { "Name": "dbo.Currency", "MergeType": "Insert" }
     ]
   }
   ```
3. Commit the generated `.tabledata` files and the updated table JSONs.
4. SchemaQuench deploys them in FK order via the [Table Data Delivery](schemaquench.md#table-data-delivery) step -- no hand-written merge scripts required.

See [Schema Packages -- DataDelivery](schema-packages.md#datadelivery) for the full property reference and the schema validation that catches misconfigurations at authoring time.

---

## Schema-Template Data Extraction

Schema templates need per-iteration data delivery -- each tenant gets the same reference data, scaffolded from a canonical source schema. DataTongs extracts data scoped to a single source schema and writes content files and MERGE scripts that use `{{SchemaName}}` as the destination schema reference, so SchemaQuench can resolve it per iteration at deploy time. The common use: extract seed data from a canonical tenant schema so every future onboarding starts from the same known state.

### Activation

Schema-template mode is detected by two signals -- both must be present:

1. **The target `Template.json` has a non-empty `SchemaIdentificationScript` field.** DataTongs walks up from `ContentPath` to find the nearest `Template.json`, then reads it for this field. A template is a schema template when this field exists and is non-empty.
2. **`Source:Schema` is set in `DataTongs.settings.json`.** This names the source schema DataTongs will extract data from.

When signal 1 is true and signal 2 is absent, DataTongs throws an error:

> Target template is a schema template (has `SchemaIdentificationScript`), but `Source.Schema` is not set in `DataTongs.settings.json`. Set `Source.Schema` to the name of the source schema you are extracting data from (typically a seed-tenant schema), or point DataTongs at a regular template instead.

When signal 1 is false and `Source:Schema` is set anyway, DataTongs logs a warning and proceeds in regular extraction mode -- `Source:Schema` is ignored.

Neither signal set means regular extraction behavior, unchanged.

### Source schema

Add `Schema` to the existing `Source` section in `DataTongs.settings.json`:

```json
{
  "Source": {
    "Server": "localhost",
    "Database": "TenantSeedDB",
    "Schema": "tenant_seed",
    "Platform": "SqlServer"
  },
  "ContentPath": "./Templates/TenantBody/Table Data",
  "ScriptPath":  "./Templates/TenantBody/Table Data",
  "Tables": [
    { "Name": "Customers", "KeyColumns": "CustomerID" },
    { "Name": "Plans" }
  ],
  "ShouldCast": {
    "OutputContentFiles": true,
    "ConfigureDataDelivery": true
  }
}
```

`Source:Schema` is a string naming the single source schema for this extraction run. All entries in the `Tables` array are resolved against this schema -- it is the only schema DataTongs touches in one schema-template run.

### Mode constraints

Schema-template mode enforces two requirements at startup, before any database connection is made:

**Unqualified table names.** Each `Tables[N].Name` must be an unqualified table name -- no schema prefix. The schema comes from `Source:Schema`, not from the individual table entry. A qualified name like `"tenant_seed.Customers"` is rejected:

> Table names in schema-template mode must be unqualified. `Tables[0].Name = "tenant_seed.Customers"`. Use `"Customers"` and let `Source.Schema` specify the source. Cross-schema data extraction (e.g., from `dbo`) goes in a separate DataTongs run targeting a regular template.

**No cross-schema data extraction in one run.** Schema-template mode produces content for one schema's tables. If you need data from both `{{SchemaName}}.Customers` and a shared `dbo.Plans` table, run DataTongs twice -- once in schema-template mode for the per-tenant tables, once in regular mode for the shared ones. Regular-mode scripts use literal schema names and land in the appropriate folder of a non-schema template.

`ConfigureDataDelivery` works in schema-template mode. The configurator writes a `DataDelivery` block into each matching table's JSON file but does not add a `Schema` field -- schema-template table files are schemaless by construction.

### Transformations

Schema-template mode changes the names and content of every output file:

| Output element | Regular mode | Schema-template mode |
|---|---|---|
| Merge script filename | `Populate <schema>.<table>.sql` | `Populate <table>.sql` |
| Content file filename | `<schema>.<table>.tabledata` | `<table>.tabledata` |
| Destination schema in merge body | Literal source schema name | `{{SchemaName}}` |
| Cross-schema refs in merge body | Preserved as-is | Preserved as-is |

The unqualified filenames match the naming convention for table JSON files in a schema template (`Tables/Customers.json`, not `Tables/dbo.Customers.json`). SchemaQuench resolves `{{SchemaName}}` to the active schema when it executes the merge script for each iteration.

### TokenizeScripts

`ShouldCast:TokenizeScripts` (default `true`, all four source engines -- `MergeScriptHelper` is the shared cross-platform helper, not a SQL-Server-specific one) controls how a table's extracted row data reaches the generated merge script:

- **Tokenized (default).** The script embeds a `{{<table>.tabledata}}` content-file token instead of the literal row data, matching SchemaTongs' external-file tokenization convention. DataTongs writes the matching `ScriptTokens` entry into `Template.json` automatically, alongside writing the `.tabledata` file -- a `<*File*>`-tagged, template-relative path pointing at the file it just wrote. Re-running extraction is idempotent: an already-correct entry is left untouched, and a key that already exists with a different, non-DataTongs value (a deliberate hand-authored override) is left alone with a warning rather than silently overwritten. This is the key/filename identity DataTongs relies on: the token key is always identical to the `.tabledata` filename stem, by construction, so the two can never drift apart.
- **Inline (`false`).** The script embeds the literal extracted JSON/XML text directly, with no external token or `ScriptTokens` entry needed.

In schema-template mode, tokenization and the `{{SchemaName}}` destination-schema substitution are orthogonal and both apply: the content token is written unqualified (matching the unqualified `.tabledata` filename schema-template mode produces), and destination refs in the script body use `{{SchemaName}}`.

### Round-trip

Running DataTongs twice against the same source schema and data produces identical output files. The extraction query orders rows by key columns, content files are deterministic JSON, and the merge script body is generated from that fixed row order. Clean diffs, honest reviews -- running DataTongs on an unchanged source is a no-op for both the files and the deployed data.

### Current limitations

**Multi-schema extraction in one run.** Each DataTongs run in schema-template mode targets exactly one source schema. If a product's data spans multiple schemas (e.g., per-tenant tables in `{{SchemaName}}` and shared lookup tables in `dbo`), use two separate DataTongs runs -- one in schema-template mode for the per-tenant tables, one in regular mode for the shared schema. The two output directories land in different template folders and are managed independently.

---

## Related Documentation

- [Configuration Reference](configuration.md) -- Shared configuration system, CLI switches, environment variables
- [Schema Packages Reference](schema-packages.md) -- How `Table Data` scripts fit into a schema package
- [Schema Templates](schema-packages.md#schema-templates) -- `Template.json` field reference for schema-template configuration
- [Multi-Tenant Deployments](../guide/10-multi-tenant-deployments.md) -- Narrative walkthrough of the full schema-template workflow
- [Schema-Template Extraction](schematongs.md#schema-template-extraction) -- The SchemaTongs sibling section; users typically run both tools when migrating from hand-replicated schemas
- [SchemaQuench Reference](schemaquench.md) -- The tool that executes the generated scripts against target databases
- [Custom Properties](custom-properties.md) -- Attach team-defined metadata to the tables DataTongs round-trips
