# Core Concepts

The [Quick Start](02-quick-start.md) walked you through the full cycle: cast, review, quench, change, redeploy. This chapter explains the mental model behind what you just did, so the patterns make sense as your schema packages grow more complex.

Think of this as the "why it works" behind the "how it works." Everything here applies to **SQL Server**, **PostgreSQL**, **MySQL**, and **MariaDB** -- the concepts are identical across platforms.

## State-based vs migration-based

Most database deployment tools are migration-based. You write ordered scripts that describe *how to change* the database: "add this column, rename that index, drop this constraint." Each migration builds on the one before it, creating a chain. Break one link and everything downstream fails.

SchemaSmith is state-based. You declare *what the database should look like*, and the tool computes the delta. You describe the destination. The forge figures out the route.

Here's what the difference looks like in practice. Suppose the Products table needs a new `DiscountPercent` column.

**Migration approach** -- you write the change steps:

```sql
-- Migration_042_AddDiscountPercent.sql
ALTER TABLE [dbo].[Products]
    ADD [DiscountPercent] DECIMAL(5,2) NULL
        CONSTRAINT [DF_Products_DiscountPercent] DEFAULT (0);
GO
```

This script must run exactly once, in the right order, after every prior migration. If someone already added that column on staging but not dev, the script fails. If you need to undo it, you write another migration. The migration folder becomes a growing ledger of every change ever made, and the question "what does the table look like right now?" requires reading all of them in sequence.

**State-based approach** -- you declare the desired result. You edit the JSON table definition to include the new column:

```json
{
  "Name": "[DiscountPercent]",
  "DataType": "DECIMAL(5,2)",
  "Nullable": true,
  "Default": "0"
}
```

SchemaQuench reads this declaration, queries the target database, sees that `DiscountPercent` doesn't exist, and generates the right DDL itself. Run the same package against dev, staging, and production -- each environment gets exactly the changes it needs, regardless of what state it was in before. Same package, correct results everywhere.

The benefits compound over time:

- **No ordering bugs.** There's no migration chain to break.
- **No drift.** Every deployment converges to the same declared state.
- **Readable reviews.** Pull requests show the table as it will be, not a sequence of mutations to decipher.
- **Repeatable deploys.** Same package, any environment. SchemaQuench computes the right delta for each.

## Products and Templates

A **product** is a deployable unit -- the top-level container for everything SchemaSmith manages. Think of it as the complete blueprint for a deployment. It's defined by a `Product.json` file at the root of your schema package:

```json
{
  "Name": "Northwind",
  "Platform": "SqlServer",
  "ValidationScript": "SELECT CAST(1 AS BIT)",
  "TemplateOrder": [
    "Initialize",
    "Northwind"
  ],
  "ScriptTokens": {
    "NorthwindDb": "Northwind"
  }
}
```

The `Platform` field (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`) is the linchpin -- it tells every SchemaSmith tool which adapter, DDL flavor, and object-type set to use. Everything else -- validation scripts, script tokens, template order -- works the same on every platform.

A **template** targets a specific database (or set of databases). It lives in a subdirectory under `Templates/` and has its own `Template.json`:

```json
{
  "Name": "Northwind",
  "DatabaseIdentificationScript": "SELECT [Name] FROM master.sys.databases WHERE [Name] = '{{NorthwindDb}}'",
  "UpdateFillFactor": true,
  "ScriptTokens": {}
}
```

Templates come in two flavors, and both work through the same execution pipeline. A **regular template** fans out across databases: `DatabaseIdentificationScript` returns one row per database, and SchemaQuench runs the full template against each returned database. A **schema template** fans out across schemas inside a single database: `SchemaIdentificationScript` returns one row per schema, and SchemaQuench runs the full template against each returned schema with the active schema name available as `{{SchemaName}}` everywhere it's needed. One declaration, many iterations, one quench. The [Multi-Tenant Deployments](10-multi-tenant-deployments.md) chapter walks through both patterns end to end with a working demo.

Each platform has its own idiom for the database identification script:

- **SQL Server:** `SELECT [Name] FROM master.sys.databases WHERE [Name] = '{{MyDb}}'`
- **PostgreSQL:** `SELECT datname FROM pg_database WHERE datname = '{{MyDb}}'`
- **MySQL:** `SELECT schema_name FROM information_schema.schemata WHERE schema_name = '{{MyDb}}'`

**The hierarchy:**

```
Northwind/                        ← Product root
  Product.json                    ← Product definition
  Templates/
    Initialize/                   ← First template (creates the DB)
      Before Scripts/
    Northwind/                    ← Main template (the schema)
      Template.json               ← Template definition
      Tables/                     ← JSON table definitions
      Procedures/                 ← SQL procedure files
      Views/                      ← SQL view files
      Functions/                  ← SQL function files
      ...
```

The Northwind demo uses two templates: Initialize (which creates the database if it doesn't exist) and Northwind (which manages all the schema objects). Most real projects follow a similar pattern. Multi-template products come into play when a single deployment needs to touch multiple databases -- for example, a shared reference database alongside the application database.

For the full list of `Product.json` and `Template.json` fields, see the [Schema Packages reference](../reference/schema-packages.md).

## Schema packages

A schema package is the folder structure that holds your entire database definition. The organizing principle is straightforward: **structure is data, behavior is code.**

**Structural objects** -- tables, indexed views (SQL Server), materialized views (PostgreSQL) -- are defined as JSON files. JSON is diffable, mergeable, and machine-readable. SchemaQuench parses these definitions, compares them against the live database, and computes precise DDL statements. You never write ALTERs by hand.

**Behavioral objects** -- stored procedures, functions, views, triggers, rules, and the rest -- are plain `.sql` files containing `CREATE OR ALTER` (or the platform equivalent). They're code: they get deployed as-is, replacing whatever currently exists. There's no diff to compute; the file *is* the definition. SchemaQuench does remember what each script left behind, so a script that hasn't changed -- and whose object nobody has touched on the server -- isn't re-run on the next deployment.

The distinction matters for your workflow. Structural changes -- adding a column, modifying an index -- SchemaSmith hammers into shape for you, computing exactly the right DDL. Behavioral changes -- rewriting a stored procedure -- you author directly in SQL, and SchemaQuench deploys them wholesale.

The default folder set for each template varies by platform (see [Schema Packages -- Default Folders](../reference/schema-packages.md#default-folders) for the full list), but the shape is the same everywhere:

```
Templates/Northwind/
  Template.json
  Tables/                          ← JSON: table definitions (all platforms)
  Indexed Views/                   ← JSON: indexed views (SQL Server)
  Materialized Views/              ← JSON: materialized views (PostgreSQL)
  <platform object-script folders> ← SQL: functions, views, procedures, triggers, ...
  Table Data/                      ← SQL: data sync scripts for reference data
```

You don't need all these folders. Most projects use Tables, Procedures, Views, and Functions. The rest exist when you need them. SchemaTongs creates the standard set automatically when it casts a database, and you can declare your own custom folders via `ScriptFolders` in `Template.json` to fit your team's lifecycle.

For the complete folder reference and file naming conventions, see [Schema Packages](../reference/schema-packages.md).

## The tool lifecycle

The four SchemaSmith tools form a cycle that covers the full schema management workflow:

```
                 Live Database
                /             \
         Cast  /               \ Quench
              /                 \
      SchemaTongs          SchemaQuench
              \                 /
               \               /
             Schema Package (files in git)
                     |
               Review in your IDE,
               code review in PRs

    DataTongs: Live Database ──→ Sync Scripts
    SchemaShears: Full Package + Manifest ──→ Patch Package
```

**SchemaTongs** grips a live database and casts it into a schema package. Tables become JSON files, procedures become SQL files, everything organized into the folder structure described above. This is how you onboard an existing database -- run SchemaTongs once, commit the output, and you have a versioned baseline. Your database's entire definition, captured in files you own.

**SchemaQuench** deploys -- quenches -- a schema package to a database. It reads your declared state, queries the target, computes the delta, and applies the changes. This is the deployment engine -- the tool that makes state-based management work. Same package in, correct database out, every time.

**DataTongs** grips reference data from a live database and extracts it as deployable sync scripts. Lookup tables, configuration rows, seed data -- anything that should travel with the schema. The output goes into the `Table Data/` folder and deploys alongside structural changes. For the full DataTongs configuration and type handling details, see the [DataTongs Reference](../reference/datatongs.md).

**SchemaShears** carves an object-level patch package from a full schema package using a manifest -- a list of the specific files to include. The patch is a valid schema package that SchemaQuench can deploy, but it only updates the objects in scope; drop suppression stamps ensure omitted objects are left untouched on the target. For the full SchemaShears reference, see the [SchemaShears Reference](../reference/schemashears.md).

The tools don't impose a rigid sequence. A typical flow looks like:

1. Cast with SchemaTongs (onboarding or re-baselining)
2. Edit the schema package files directly (day-to-day development)
3. Quench to a test database with SchemaQuench
4. Review in your IDE or via git diff in a pull request
5. Quench to production with SchemaQuench

Once your schema's in files, most daily work is editing JSON and SQL directly -- you don't re-cast every time. The files are yours to shape.

## The deployment model

SchemaQuench follows a clear sequence when quenching a schema package to a database:

1. **Read the declared state** -- parse every JSON table definition and SQL file in the schema package.
2. **Query the current state** -- inspect the target database's actual tables, columns, indexes, keys, and objects.
3. **Compute the delta** -- determine what needs to be created, altered, or dropped to make the database match the declaration.
4. **Apply changes in execution slots** -- run the computed changes in a controlled order.

The execution slots give you precise control over ordering when it matters. SchemaQuench runs each template once per iteration -- where an iteration is one matched database for a regular template, or one matched schema inside a database for a schema template. The slot sequence is the same either way. Within a single iteration, changes execute in this sequence:

1. Programmable objects (schemas, types, functions, views, procedures) -- with dependency retry
2. New tables and columns created
3. Before migration scripts
4. Existing table modifications (column type changes, index updates)
5. Between-tables-and-keys migration scripts
6. Missing indexes and constraints
7. After-tables migration scripts
8. Triggers, DDL triggers, rules
9. Table data (sync scripts)
10. Foreign key constraints
11. Indexed views (SQL Server) / Materialized views (PostgreSQL)
12. After migration scripts

Most of the time you don't think about slots -- tables and objects just deploy correctly. That's by design. The migration script slots exist for cases where you need to run something at a specific point in the sequence, like populating data before a NOT NULL constraint takes effect, or applying a data fixup after indexes exist. The tool handles the complexity so you can focus on the design.

For the full deployment flow, execution slot details, and configuration options, see [SchemaQuench](../reference/schemaquench.md).

## JSON table definitions

Table definitions are where you'll spend most of your editing time. They're the heart of the craft -- where you shape every column, index, and constraint. Here's an actual table from the Northwind demo -- `dbo.Products`:

```json
{
  "Schema": "[dbo]",
  "Name": "[Products]",
  "CompressionType": "NONE",
  "Columns": [
    { "Name": "[ProductID]",     "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
    { "Name": "[ProductName]",   "DataType": "NVARCHAR(40)",       "Nullable": false },
    { "Name": "[CategoryID]",    "DataType": "INT",                "Nullable": true  },
    {
      "Name": "[UnitPrice]",
      "DataType": "MONEY",
      "Nullable": true,
      "Default": "0",
      "CheckExpression": "[UnitPrice]>=(0)"
    },
    { "Name": "[Discontinued]",  "DataType": "BIT",                "Nullable": false, "Default": "0" }
  ],
  "Indexes": [
    { "Name": "[PK_Products]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[ProductID]" },
    { "Name": "[CategoriesProducts]", "IndexColumns": "[CategoryID]" }
  ],
  "ForeignKeys": [
    {
      "Name": "[FK_Products_Categories]",
      "Columns": "[CategoryID]",
      "RelatedTableSchema": "[dbo]",
      "RelatedTable": "[Categories]",
      "RelatedColumns": "[CategoryID]"
    }
  ]
}
```

Reading top to bottom, the whole table is right here:

- **Schema and Name** identify the table. SQL Server uses bracket notation (`[Schema].[Name]`) and MySQL backticks (`` `name` ``) -- SchemaTongs preserves whatever the source used. PostgreSQL names are written bare (`public`, `customer`), because PostgreSQL keeps a double quote as part of a name.
- **Columns** list every column with its data type, nullability, defaults, and check constraints. `"DataType": "INT IDENTITY(1, 1)"` means an auto-incrementing integer (SQL Server syntax; PostgreSQL uses `INTEGER GENERATED ALWAYS AS IDENTITY`, MySQL uses `INT AUTO_INCREMENT`). `"Default": "0"` applies a default constraint.
- **Indexes** define the primary key and any secondary indexes. The primary key is a clustered unique index on `ProductID`. Additional indexes cover the foreign key columns.
- **ForeignKeys** define relationships to other tables. Each entry names the constraint, the local column(s), the related table, and the referential actions.

If you've ever viewed a table's properties in SSMS, pgAdmin, MySQL Workbench, or DBeaver, this is the same information -- columns, indexes, foreign keys -- expressed as a single file you can diff, review in a pull request, and merge without conflicts. One file, one table, complete truth.

Why JSON instead of SQL DDL? Three reasons:

1. **Diffable.** Adding a column is a clean diff -- one new object in the Columns array. In DDL, you would see an entirely rewritten `CREATE TABLE` or an ALTER statement that doesn't show context.
2. **Mergeable.** Two developers adding different columns to the same table produce a clean git merge in JSON. In SQL, they usually produce a conflict.
3. **Machine-readable.** SchemaQuench parses the JSON to compute precise deltas across every supported platform. Parsing arbitrary DDL reliably is much harder.

For the complete field reference covering every column, index, and constraint property -- including the per-platform extensions for materialized views, exclude constraints, full-text indexes, and more -- see [Schema Packages -- Table JSON Format](../reference/schema-packages.md#table-json-format----shared-properties).

---

Now that you understand the model, let's look at how this plays out in daily development. [Defining Your Schema](04-defining-your-schema.md)
