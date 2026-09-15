# Defining Your Schema

You understand the [core concepts](03-core-concepts.md). Now it's time to give your database form. This chapter covers the workflows you'll reach for every day -- adding tables, shaping columns, writing stored procedures, casting changes from live databases, and bootstrapping new environments from scratch. Each one replaces a manual, error-prone process with something you can trust. And they build on each other naturally.

The examples use SQL Server bracket notation, but the same workflows apply verbatim on **PostgreSQL**, **MySQL**, and **MariaDB** -- swap the quoting style and the data type flavor, and everything else is identical.

## Adding a table

Your team needs a `Promotions` table to track discount campaigns. Here's what you do.

**1. Create the JSON file.** Add `dbo.Promotions.json` to your package's `Tables/` folder:

```json
{
  "Schema": "[dbo]",
  "Name": "[Promotions]",
  "CompressionType": "NONE",
  "Columns": [
    { "Name": "[PromotionID]",   "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
    { "Name": "[PromotionName]", "DataType": "NVARCHAR(100)",      "Nullable": false },
    {
      "Name": "[DiscountPercent]",
      "DataType": "DECIMAL(5,2)",
      "Nullable": false,
      "Default": "0",
      "CheckExpression": "[DiscountPercent]>=(0) AND [DiscountPercent]<=(100)"
    },
    { "Name": "[StartDate]", "DataType": "DATE", "Nullable": false },
    { "Name": "[EndDate]",   "DataType": "DATE", "Nullable": true  },
    { "Name": "[IsActive]",  "DataType": "BIT",  "Nullable": false, "Default": "1" }
  ],
  "Indexes": [
    { "Name": "[PK_Promotions]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[PromotionID]" },
    { "Name": "[IX_Promotions_StartDate]", "IndexColumns": "[StartDate]" }
  ]
}
```

That's the entire table definition. Every column, every constraint, every index -- all in one readable file. For every property available in a table JSON file on every platform, see the [Schema Packages Reference](../reference/schema-packages.md#table-json-format----shared-properties).

**2. Quench it.** Run SchemaQuench against your development database:

```bash
SchemaQuench
```

SchemaQuench reads the JSON, sees that `dbo.Promotions` doesn't exist in the target database, and generates a `CREATE TABLE` statement. One file. One command. Done.

**Compare this to the traditional approach:** write a `CREATE TABLE` script, write a migration file with a sequence number, make sure the sequence number doesn't collide with anyone else's, add an `IF NOT EXISTS` guard, add a corresponding rollback script, update a migrations tracking table. With SchemaSmith, you created one file and ran one command. No migration scripts. No dependency ordering. No collision worries.

### PostgreSQL, MySQL, and MariaDB shape

The same table on PostgreSQL:

```json
{
  "Name": "promotions",
  "Schema": "public",
  "Columns": [
    { "Name": "promotion_id",   "DataType": "INTEGER GENERATED ALWAYS AS IDENTITY", "Nullable": false },
    { "Name": "promotion_name", "DataType": "VARCHAR(100)",                         "Nullable": false },
    { "Name": "discount_percent", "DataType": "NUMERIC(5,2)", "Nullable": false, "Default": "0" },
    { "Name": "start_date", "DataType": "DATE", "Nullable": false },
    { "Name": "end_date",   "DataType": "DATE", "Nullable": true  },
    { "Name": "is_active",  "DataType": "BOOLEAN", "Nullable": false, "Default": "true" }
  ],
  "Indexes": [
    { "Name": "pk_promotions", "PrimaryKey": true, "Unique": true, "IndexColumns": "promotion_id" }
  ],
  "CheckConstraints": [
    { "Name": "ck_promotions_discount_range", "Expression": "discount_percent BETWEEN 0 AND 100" }
  ]
}
```

The MySQL and MariaDB variants use `INT AUTO_INCREMENT`, `VARCHAR(100)`, `DECIMAL(5,2)`, and `TINYINT(1)` or `BOOLEAN` -- they're separate packages (each declares its own `Platform`), even where the DDL shape lines up. The file structure is identical; only the data types and quoting change. Your team can manage all four platforms with the same mental model.

## Modifying a table

The `Promotions` table needs changes. Marketing wants a description field, the discount column needs more precision, and you need an index on the active flag for a dashboard query. All three edits happen in the same JSON file -- you shape the table right where it lives.

**Add a column.** Insert a new entry in the `Columns` array:

```json
{
  "Name": "[Description]",
  "DataType": "NVARCHAR(500)",
  "Nullable": true
}
```

**Change a data type.** Find the `DiscountPercent` column and edit its `DataType`:

```json
"DataType": "DECIMAL(7,4)"
```

**Add an index.** Add a new entry in the `Indexes` array:

```json
{
  "Name": "[IX_Promotions_IsActive]",
  "IndexColumns": "[IsActive]",
  "FilterExpression": "[IsActive] = 1"
}
```

Now preview the changes before touching the database. Run SchemaQuench in WhatIf mode:

```bash
SmithySettings_WhatIfONLY=true SchemaQuench
```

SchemaQuench generates the SQL it *would* execute -- an `ALTER TABLE ... ADD` for the new column, an `ALTER TABLE ... ALTER COLUMN` for the data type change, and a `CREATE INDEX` for the new filtered index -- and logs it all without applying anything. Read the generated SQL, confirm it looks right, then run SchemaQuench normally to quench the changes into your database.

Three changes to one file. One preview. One command. No scripts to write, number, or maintain.

## Adding and updating programmable objects

Stored procedures, functions, views, and triggers work differently from tables. Instead of JSON, they're plain `.sql` files. Each object gets its own file in the matching folder. The exact folder names vary slightly per platform (see [Schema Packages -- Default Folders](../reference/schema-packages.md#default-folders)), but the pattern is the same:

| Object type | Common folder name |
|---|---|
| Stored procedures | `Procedures/` |
| Functions | `Functions/` |
| Views | `Views/` |
| Triggers | `Triggers/` |

Here's a SQL Server stored procedure that returns the order history for a customer. Create `dbo.CustOrderHist.sql` in the `Procedures/` folder:

```sql
SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER PROCEDURE CustOrderHist @CustomerID nchar(5)
AS

SELECT ProductName, Total = SUM(Quantity)
  FROM Products P, [Order Details] OD, Orders O, Customers C
  WHERE C.CustomerID = @CustomerID
    AND C.CustomerID = O.CustomerID AND O.OrderID = OD.OrderID AND OD.ProductID = P.ProductID
  GROUP BY ProductName

GO
```

The key detail: `CREATE OR ALTER`. This is idempotent. It works whether the procedure exists or not. No `IF EXISTS ... DROP` guard. No separate create-vs-alter logic. SchemaQuench runs the script as-is, and the database engine handles the rest. PostgreSQL uses `CREATE OR REPLACE FUNCTION` / `CREATE OR REPLACE PROCEDURE` for the same effect; MySQL uses `DROP ... IF EXISTS` followed by `CREATE ...` as a single idempotent pair. SchemaSmith's helper procedures smooth over the platform differences so your script files stay focused on the business logic.

Views work the same way. Here's a SQL Server view in the `Views/` folder:

```sql
SET ANSI_NULLS ON
SET QUOTED_IDENTIFIER ON
GO

CREATE OR ALTER VIEW "Products Above Average Price" AS
SELECT Products.ProductName, Products.UnitPrice
  FROM Products
  WHERE Products.UnitPrice > (SELECT AVG(UnitPrice) FROM Products);

GO
```

Need to update an existing procedure? Edit the `.sql` file and quench. The `CREATE OR ALTER` (or equivalent) takes care of whether it's new or changed. There's no separate "alter" workflow -- you always declare the full object definition, and SchemaQuench applies it.

## Extracting changes from a live database

Someone changed the database directly. Maybe a DBA added a column in production during an incident. Maybe a developer used SSMS or pgAdmin to tweak an index on staging. The database has drifted from the package.

Cast the current state back into your package with SchemaTongs:

```bash
SchemaTongs
```

SchemaTongs connects to the database, reads every table, procedure, view, function, and trigger -- plus any per-platform extras like sequences, materialized views, exclude constraints, or events -- and writes the current definitions to the package files. Changed objects update in place. New objects get new files. **Any `Extensions` metadata you had previously attached to tables is preserved** through the round-trip. For the full extraction configuration, including filtering by object type and partial extraction, see the [SchemaTongs Reference](../reference/schematongs.md).

Now the power of files shows up. Run `git diff`:

```
$ git diff
--- a/Templates/Northwind/Tables/dbo.Products.json
+++ b/Templates/Northwind/Tables/dbo.Products.json
@@ -48,6 +48,12 @@
     },
+    {
+      "Name": "[BackorderThreshold]",
+      "DataType": "INT",
+      "Nullable": true,
+      "Default": "10"
+    },
```

The diff reads like a sentence: "someone added a BackorderThreshold column to the Products table with a default of 10." Compare that to trying to figure out what changed by comparing two database snapshots or reading through audit logs. The drift is captured. The mystery is over.

## Extraction intelligence

SchemaTongs does more than dump scripts to flat folders. When you cast your database schema, the tool brings real intelligence to the extraction.

**Subfolder preservation.** You can organize scripts by domain -- `Tables/Sales/`, `Tables/HR/`, `Procedures/Reporting/`. When SchemaTongs casts, it preserves existing subfolder locations. If `dbo.Orders.json` already lives in `Tables/Sales/`, the next extraction updates it in place rather than creating a duplicate in the root `Tables/` folder. New objects that haven't been organized yet go to the root folder. Your organization stays intact.

**Extensions preservation.** Custom metadata you've attached to tables via the [`Extensions` carrier](../reference/custom-properties.md) -- data classification tags, ownership, retention policies -- is carried forward on every re-extraction. Your sidecar data survives as long as the underlying object stays (or you use `OldName` to track a rename).

**Orphan detection.** When a database object is dropped, its script file becomes an orphan. SchemaTongs offers three modes for handling this:

| Mode | Behavior |
|---|---|
| `Detect` | Logs orphaned files but takes no action. Default. |
| `DetectWithCleanupScripts` | Logs orphans and generates cleanup scripts you can review and apply. |
| `DetectDeleteAndCleanup` | Deletes orphaned files and generates cleanup scripts automatically. |

**Script validation.** With `ValidateScripts` enabled, SchemaTongs checks each extracted script against the database to verify it parses correctly. Invalid scripts are saved with a `.sqlerror` extension instead of `.sql`, making them visible but excluded from deployment until you fix them.

**CheckConstraintStyle.** Controls whether check constraints are extracted as column-level properties (inside the table JSON) or as table-level named constraints. The default is `ColumnLevel`. If you prefer `TableLevel`, set it in `Product.json` -- but be consistent, because the style is locked to whatever `Product.json` specifies once the product exists.

For the full set of extraction options, filtering, and configuration, see [SchemaTongs Reference](../reference/schematongs.md).

## The Initialize template pattern

Some products need to create their target database from scratch. CI pipelines spin up fresh containers. Docker Compose environments start from nothing. New developers clone the repo and need a working database in one command. The Initialize template pattern handles all of these.

Three pieces work together. Here's how the Northwind demo product sets it up on SQL Server.

**1. The Initialize template identifies itself out of the deployment.** In `Templates/Initialize/Template.json`:

```json
{
  "Name": "Initialize",
  "DatabaseIdentificationScript": "SELECT [Name] FROM master.sys.databases WHERE [Name] = 'master' AND NOT EXISTS (SELECT 1 FROM master.sys.databases WHERE [Name] = '{{NorthwindDb}}')"
}
```

The `DatabaseIdentificationScript` is the key. It returns a result only when the target database doesn't yet exist -- it matches `master` (a database that always exists on the server) but only when `NorthwindDb` is missing. On the first run, this template activates and creates the database. On every subsequent run, the script returns no rows, SchemaQuench skips the template entirely, and deployment proceeds straight to the main template.

**2. A migration script creates the database idempotently.** In `Templates/Initialize/Before Scripts/Create Northwind [ALWAYS].sql`:

```sql
IF NOT EXISTS (SELECT 1 FROM master.sys.databases WHERE [Name] = '{{NorthwindDb}}')
BEGIN
    CREATE DATABASE [{{NorthwindDb}}]
END
```

The `[ALWAYS]` marker tells SchemaQuench to run this script every time the Initialize template is active -- no version tracking needed. The `IF NOT EXISTS` guard makes the script safe to re-run, though in practice it only executes once because the template self-selects out after the database exists.

**3. `Product.json` defines the template order.** In `Product.json`:

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

`TemplateOrder` ensures Initialize runs first. If the database doesn't exist, Initialize creates it, then the Northwind template deploys the full schema. If the database already exists, Initialize is skipped and Northwind deploys any pending changes.

The same pattern works on PostgreSQL, MySQL, and MariaDB -- swap the identification script to use `pg_database` or `information_schema.schemata`, and the `CREATE DATABASE` / `CREATE SCHEMA` statement to match. One `docker compose up` bootstraps everything from an empty server. Subsequent runs skip Initialize automatically and apply only schema changes. Fresh environment or existing environment, same command, same result.

## Advanced engine features

Every engine brings capabilities that go beyond basic columns and indexes. SQL Server has temporal tables and columnstore indexes. PostgreSQL has exclusion constraints and materialized views. MySQL has generated columns with automatic dependency ordering. All of them live in the same JSON you've been writing -- no new tools, no separate configuration layer. You declare the feature; SchemaSmith handles the DDL.

### SQL Server

**Temporal tables.** Audit trails are one of the most common database requirements -- and one of the most tedious to implement correctly. Set `"IsTemporal": true` on any table and SchemaSmith adds the system-time period columns (`ValidFrom`, `ValidTo`), the `PERIOD FOR SYSTEM_TIME` declaration, and `SYSTEM_VERSIONING = ON` pointing at the `<Name>_Hist` history table. You declare neither the period columns nor the history table's DDL -- just the flag, and the engine takes it from there.

```json
{
  "Schema": "[dbo]",
  "Name": "[AuditableOrders]",
  "IsTemporal": true,
  "Columns": [
    { "Name": "[OrderID]", "DataType": "INT IDENTITY(1, 1)", "Nullable": false },
    { "Name": "[Status]", "DataType": "NVARCHAR(20)", "Nullable": false }
  ],
  "Indexes": [
    { "Name": "[PK_AuditableOrders]", "PrimaryKey": true, "Clustered": true, "IndexColumns": "[OrderID]" }
  ]
}
```

When you toggle `IsTemporal` back to `false`, SchemaSmith emits `SET (SYSTEM_VERSIONING = OFF)` -- clean in both directions.

**Columnstore indexes.** Analytic queries that scan millions of rows for aggregations and reports are where row-store indexes struggle. Add `"ColumnStore": true` to any index definition and SchemaSmith creates a columnstore index instead of the default B-tree structure. SQL Server also supports `"CompressionType": "COLUMNSTORE_ARCHIVE"` for maximum compression on cold data. No separate DDL, no separate tooling -- just a flag on the index object you already know.

**Full-text search.** Natural-language search over text columns requires a full-text index -- a catalog-backed structure that SQL Server manages separately from its B-tree indexes. Declare it as a `FullTextIndex` object on the table, specifying the catalog, the unique key index, the columns to index, and the change-tracking mode. SQL Server allows one full-text index per table, but that one index can cover multiple text columns at once. For the full property set, variant rules, and conditional-application patterns, see the [Full-Text Index (SQL Server) reference](../reference/schema-packages.md#full-text-index-sql-server).

**CDC -- brief note.** Set `"EnableCDC": true` on any table to enable Change Data Capture. When you later change that table's columns, SchemaSmith adds a *second* capture instance covering the new column set and leaves the original one in place, holding everything it has captured so far. That is SQL Server's own pattern for changing the shape of a tracked table without losing history. To keep change tables off the default filegroup, add `"CdcFilegroup": "<filegroup>"` to the table or to `Template.json` -- see [Where change tables go](../reference/schema-packages.md#where-change-tables-go).

**Change tracking -- brief note.** Set `"EnableChangeTracking": true` to have SQL Server record *which rows* changed since a given version -- lighter than CDC, and enough when a reader just needs to re-fetch what moved. Add `"TrackColumnsUpdated": true` to record which columns changed too. Both CDC and change tracking have to be switched on at the database level first; SchemaSmith tells you when a table asked for one and the database has not got it, rather than deploying green and leaving it off. See [Change Tracking](../reference/schema-packages.md#change-tracking-sql-server).

> **Action required:** The old capture instance is yours to retire, because only you know when your readers have finished with it. Once they have, drop it with `EXEC sys.sp_cdc_disable_table @source_schema = N'dbo', @source_name = N'YourTable', @capture_instance = N'<name>'` -- the deploy log names the instance for you. SQL Server allows only two capture instances per table, so until you drop it the next column change on that table will be refused, with a message telling you which tables are affected. The deploy stops before touching any column, so nothing is half-applied.

### PostgreSQL

**Exclude constraints.** Unique indexes enforce equality: no two rows can have the same value. Exclusion constraints enforce an operator relationship: no two rows can satisfy a given operator pair. The canonical use case is non-overlapping reservation periods -- a GiST index with the `&&` (overlap) operator guarantees that no two reservations for the same room span the same time.

```json
{
  "Name": "no_overlapping_reservations",
  "AccessMethod": "gist",
  "ExcludeColumns": [
    { "Column": "room_id",         "Operator": "="  },
    { "Column": "reserved_period", "Operator": "&&" }
  ]
}
```

Declare these in the `ExcludeConstraints` array on the table. This is something a unique index simply cannot express -- it requires a different constraint type entirely. For the full property set including deferrable options, see the [Exclude Constraints (PostgreSQL) reference](../reference/schema-packages.md#exclude-constraints-postgresql).

**Materialized views.** Some queries are too expensive to compute on every request -- reporting aggregations, denormalized read models, dashboards pulling from many joined tables. Materialize the result: PostgreSQL computes the query once, stores it as a physical table, and you refresh it on demand. SchemaSmith manages the create/drop lifecycle and the view's own indexes in a `Materialized Views/` folder alongside your regular tables. For the full shape including `WithData`, `Tablespace`, and index declarations, see the [Materialized View reference](../reference/schema-packages.md#materialized-view-json-format-postgresql).

**Generated columns.** A generated column derives its value from an expression over other columns in the same row -- the engine maintains the value for you. By default it's persisted on disk (STORED); set `"Virtual": true` on the column and PostgreSQL recomputes it on every read instead (VIRTUAL). Declare it with `GenerationExpression`:

```json
{ "Name": "full_name", "DataType": "TEXT", "Generated": "ALWAYS", "GenerationExpression": "first_name || ' ' || last_name", "Nullable": false }
```

The engine keeps the value current on every insert and update. No triggers, no application-layer logic.

> **Note:** PostgreSQL also supports `"RowLevelSecurity": true` and `"ForceRowLevelSecurity": true` on the table, together with a `"Policies"` array declaring the policies themselves. **Both halves matter:** a table with row-level security enabled and no policy returns no rows to anyone but its owner, so enabling the flag without declaring a policy locks the table. SchemaSmith creates declared policies that are missing and drops ones the package no longer declares; editing an expression on an existing policy is not detected, because PostgreSQL stores those normalised. See [Row-Level Security Policies](../reference/schema-packages.md#row-level-security-policies-postgresql).

> **PostgreSQL:** Advanced index methods -- GIN for JSONB and arrays, GiST for ranges and geometry, BRIN for append-only time-series -- are available via `"AccessMethod"` on any index definition (e.g., `"AccessMethod": "gin"`). SchemaSmith emits the appropriate `USING` clause.

### MySQL

**Generated columns.** MySQL generated columns work the same conceptually as PostgreSQL's, but with an important constraint: a generated column must be defined *after* all columns whose values it references. For multi-column expressions or complex schemas, getting that order right by hand is error-prone. SchemaSmith resolves the creation order automatically using a topological sort with circular-dependency detection -- declare the expression, and the tool handles where in the DDL sequence the column lands.

```json
{ "Name": "full_name", "DataType": "VARCHAR(255)", "GenerationExpression": "CONCAT(first_name, ' ', last_name)", "Nullable": false }
```

Use `"Generated": "STORED"` or `"Generated": "VIRTUAL"` to control whether the value is persisted on disk or recomputed on every read.

**Full-text indexes.** Unlike SQL Server, MySQL allows multiple full-text indexes per table -- which is why the property is `FullTextIndexes` (an array). Each index can specify a custom parser: `"Parser": "ngram"` for CJK and other non-space-delimited languages, for example. SchemaSmith creates each full-text index with `CREATE FULLTEXT INDEX ... WITH PARSER` when a parser is specified. For the full property set, see the [Full-Text Indexes (MySQL) reference](../reference/schema-packages.md#full-text-indexes-mysql).

**Spatial indexes.** Index geometry columns for spatial queries by setting `"IndexType": "SPATIAL"` on the index definition. SchemaSmith emits the appropriate `CREATE SPATIAL INDEX` DDL. Pair with a geometry-typed column and MySQL's spatial functions for proximity searches, bounding-box queries, and GIS workloads.

---

That's how you shape your schema -- adding tables, modifying columns, writing procedures, casting changes, and bootstrapping new environments. Clean, repeatable, no surprises. When you're ready to bring your team into the process, the next chapter shows how schema-as-files transforms collaboration. [Working with Your Team](05-working-with-your-team.md)
