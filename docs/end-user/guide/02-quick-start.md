# Quick Start

In the next 15 minutes, you'll extract a real database into version-controlled files, deploy them to a fresh empty database, make a schema change, and watch SchemaSmith compute the exact DDL needed to bring the target in line. By the end, you'll have completed the full SchemaSmith cycle -- and you'll understand why teams stop writing migration scripts once they've seen this.

This walkthrough uses **SQL Server** as the primary example, but the exact same workflow works against **PostgreSQL** and **MySQL**. Every platform ships the same four demo products (AdventureWorks, Chinook, Northwind, Sakila) -- pick whichever engine your team runs.

## Prerequisites

You need three things:

**SchemaSmith tools.** Pick the fastest path for your platform:

- **Windows:** `choco install schemasmith`
- **Linux / macOS:** `curl -fsSL https://schemasmith.com/dl/install.sh | sh`
- **Debian/Ubuntu, RHEL/Fedora, manual ZIP, signature verification, all the rest:** see [Installation](installation.md).

**A database server.** Either an existing instance or Docker (recommended). The repository includes a Docker Compose file that starts the demo servers for you.

**The demo files.** Clone the repository or download just the `Demos/` folder:

```bash
git clone https://github.com/Schema-Smith/SchemaSmith.git
cd SchemaSmith
```

## Step 1: Start the Demo Environment

Each platform has its own demo folder under `Demos/` with a `run-demo` launcher, a `docker-compose.yml`, and a `.env` credentials file. From the repository root, launch the SQL Server demo:

**macOS / Linux:**

```bash
cd Demos/SqlServer
./run-demo.sh
```

**Windows:**

```cmd
cd Demos\SqlServer
run-demo.cmd
```

The launcher runs `build-schemaquench.sh` to compile the SchemaQuench binary (requires the .NET SDK on the host), then `docker compose up --build` packages that binary into a local Docker image, starts a SQL Server instance on port `1440`, and deploys the demo databases using SchemaQuench. The launcher blocks until the `completed` service exits, which signals the databases are ready. Credentials live in `Demos/SqlServer/.env`:

| Setting  | Value |
|----------|-------|
| SQL Server | `localhost,1440` |
| User     | `TestUser` |
| Password | _(see `Demos/SqlServer/.env`)_ |

> **PostgreSQL, MySQL, or MariaDB?** Swap the platform folder: `Demos/PostgreSQL` (port `5432`), `Demos/MySQL` (port `3306`), or `Demos/MariaDB` (port `3317`). Each platform has its own `run-demo.sh` / `run-demo.cmd` launcher, `.env` credentials file, and the same four demo databases (AdventureWorks, Chinook, Northwind, Sakila). On MySQL and MariaDB the demo databases are stored in lowercase (`adventureworks`, `chinook`, `northwind`, `sakila`) because their Linux containers default to `lower_case_table_names=1`; use the lowercase form in your `Database` / `NorthwindDb` config values when the platform is MySQL or MariaDB.

> **Already have a SQL Server?** You can skip Docker entirely — the same demo databases deploy straight onto your own instance. See [Use your own server](use-your-own-server.md).

## Step 2: Cast with SchemaTongs

Now let's go the other direction. Pretend you already have a database and want to bring it under SchemaSmith management. SchemaTongs grips your live schema and casts it into structured files. Create a SchemaTongs configuration file called `tongs-extract.json`:

```json
{
  "Source": {
    "Platform": "SqlServer",
    "Server": "localhost,1440",
    "User": "TestUser",
    "Password": "your-password-here",
    "Database": "Northwind",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    }
  },
  "Product": {
    "Path": "./my-northwind",
    "Name": "Northwind"
  },
  "Template": {
    "Name": "Northwind"
  },
  "ShouldCast": {
    "Tables": true,
    "Schemas": true,
    "UserDefinedTypes": true,
    "Functions": true,
    "Views": true,
    "Procedures": true,
    "TableTriggers": true,
    "Catalogs": true,
    "StopLists": true,
    "DDLTriggers": true,
    "XMLSchemaCollections": true,
    "IndexedViews": true
  }
}
```

> **PostgreSQL, MySQL, or MariaDB?** Set `"Platform"` to `"PostgreSQL"`, `"MySQL"`, or `"MariaDb"` and adjust the connection string. The `ShouldCast` flags you don't need are simply ignored. Schemas, sequences, materialized views, exclude constraints, full-text indexes, events -- all the per-platform object types are toggled from the same flag list.

Run the extraction:

```bash
SchemaTongs --ConfigFile:tongs-extract.json
```

Look at what appeared in the `my-northwind/` folder. You now have a complete schema package:

```
my-northwind/
  Product.json                     ← Platform, templates, tokens
  .json-schemas/                   ← JSON Schema validation, generated on the fly
  Templates/
    Northwind/
      Template.json
      Tables/
        dbo.Categories.json
        dbo.Customers.json
        dbo.Orders.json
        ... (14 tables, including recyclebin.Registry)
      Views/
        dbo.Alphabetical list of products.sql
        ...
      Procedures/
        dbo.CustOrderHist.sql
        ...
      Schemas/
      Functions/
      Triggers/
      Table Data/
```

Every table is a JSON file describing its columns, indexes, and constraints. Every stored procedure and view is a plain SQL file. This is your entire database -- materialized as readable, diffable, reviewable source files. Commit this to source control and you have a complete history of every schema change from this point forward. For the full set of extraction options, see the [SchemaTongs Reference](../reference/schematongs.md).

That's the cast. A live database, captured in files you can read, review, and version.

## Step 3: Explore the Package

No GUI editor needed -- the schema package is designed to read clearly in any editor or IDE. Open `my-northwind/` in VS Code, JetBrains Rider, Visual Studio, or whatever your team uses. The `.json-schemas/` folder holds a JSON Schema per file type, generated from the live engine types. Point your editor at them once -- a `json.schemas` mapping in VS Code, or the equivalent in your IDE -- and you get autocomplete, type checking and inline errors while editing. They are also what a CI job validates against; see [Testing and validation](06-testing-and-validation.md).

Click through `Tables/dbo.Categories.json`. You'll see the column definitions, the primary key, and any indexes. Open a stored procedure in `Procedures/` and you see the full SQL definition ready to diff in a pull request. This is what schema review looks like when the files are designed for it: structured enough for tools, readable enough for humans, diff-friendly enough for code review.

Take a minute to click around. Every table, every view, every procedure in the Northwind database is here, structured and browsable. No server connection needed to explore.

## Step 4: Quench with SchemaQuench

Here's where it gets powerful. You just extracted the live Northwind schema into `my-northwind/`. Now let's quench that package back at the same database to prove the round-trip — declared state in, target verified against it, exact-delta DDL out. Create a SchemaQuench configuration file called `quench-deploy.json`:

```json
{
  "Target": {
    "Server": "localhost,1440",
    "User": "TestUser",
    "Password": "your-password-here",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    }
  },
  "WhatIfONLY": false,
  "SchemaPackagePath": "./my-northwind",
  "ScriptTokens": {
    "NorthwindDb": "Northwind"
  }
}
```

Notice the `ScriptTokens` section: SchemaQuench resolves the `{{NorthwindDb}}` token in the package's scripts to `Northwind`, the database we want to manage. Tokens parameterize a package so the same source files can deploy to dev, staging, and production by changing one value at deploy time — point a token at a different database name to spin up a clone for testing without touching another file. You'll learn more about tokens in [Core Concepts](03-core-concepts.md), and the full token system is documented in the [Script Tokens Reference](../reference/script-tokens.md).

Run the deployment:

```bash
SchemaQuench --ConfigFile:quench-deploy.json
```

SchemaQuench reads the schema package, connects to the live `Northwind` database, and compares your declared state to what's actually there. Since you just extracted this package from that exact database, the two are in sync — no DDL is emitted on this first run. That's the point: SchemaSmith only changes what's actually different. The diff comes alive in the next step.

One package. One command. A target verified against your declared source of truth. That's what quenching looks like.

## Step 5: Make a Change

Now for the payoff. Let's modify the schema and watch SchemaSmith figure out exactly what needs to change.

Open `my-northwind/Templates/Northwind/Tables/dbo.Shippers.json`. The file declares the current state of the table: columns, indexes, the primary key. Add a new column for tracking email addresses. Insert this entry into the `Columns` array after the `[Phone]` column:

```json
{
  "Name": "[Email]",
  "DataType": "NVARCHAR(100)",
  "Nullable": true,
  "OldName": ""
}
```

You just declared the desired state: "the Shippers table should have an Email column." You didn't write an ALTER TABLE script. You didn't check whether the column already exists. You described what the table should look like. You decide the shape. The forge handles the rest.

Now let's see what SchemaSmith will do -- without actually touching the database. Run SchemaQuench in WhatIf mode by setting `"WhatIfONLY": true` in a copy of your settings file:

```json
{
  "Target": {
    "Server": "localhost,1440",
    "User": "TestUser",
    "Password": "your-password-here",
    "ConnectionProperties": {
      "TrustServerCertificate": "True"
    }
  },
  "WhatIfONLY": true,
  "SchemaPackagePath": "./my-northwind",
  "ScriptTokens": {
    "NorthwindDb": "Northwind"
  }
}
```

Save this as `quench-whatif.json` and run:

```bash
SchemaQuench --ConfigFile:quench-whatif.json
```

In the output, you'll see `[WhatIf]` entries showing the computed changes. SchemaQuench compared the declared state (your JSON with the new Email column) against the live `Northwind` database (which has no Email column) and determined that an `ALTER TABLE ... ADD` is needed. No changes were applied -- WhatIf mode is read-only. Preview before you commit. Confidence before you deploy.

Rather than scan the full log, read the summary: every run writes a human-readable **`SchemaQuench - Summary.md`** next to the logs, and in WhatIf mode it lists exactly what *would* be applied, skipped, or delivered -- the at-a-glance view of the plan. Its twin `SchemaQuench - Summary.json` is the same facts as a stable, machine-readable contract for CI. Both are always on; see the [Deployment Summary Report reference](../reference/deployment-summary-report.md).

Now apply it for real. Re-run the original `quench-deploy.json` from Step 4 -- it already has `WhatIfONLY: false`, so this run actually writes the change:

```bash
SchemaQuench --ConfigFile:quench-deploy.json
```

SchemaQuench connects to the `Northwind` database, sees that `dbo.Shippers` is missing the `[Email]` column, and adds it. Every other table, view, and procedure is already in sync, so nothing else changes. Exactly the right delta, computed automatically. No more, no less.

## Step 6: Verify the Change

Connect to the database with whatever SQL client you prefer and look at the Shippers table:

```sql
SELECT TOP 1 [CompanyName], [Phone], [Email] FROM [dbo].[Shippers];
```

The `Email` column is there, right where you declared it. The file is the truth, the database matches, and your next commit records the change for posterity.

## The Cycle Is Complete

You just completed the full SchemaSmith workflow:

1. **Cast** -- SchemaTongs gripped a live database and cast it into structured, version-controlled files
2. **Review** -- The JSON and SQL files are human-readable and diff-friendly, ready for pull requests
3. **Quench** -- SchemaQuench built a complete database from those files, reproducibly
4. **Change** -- You edited a JSON file to declare a new column
5. **Preview** -- WhatIf mode showed you the computed change without touching the database
6. **Apply** -- SchemaQuench made the target match the declared state, changing only what needed to change

No migration scripts. No ordered chains of ALTERs. No guessing what the target looks like. You declare the state you want, and SchemaSmith gets you there. Every time.

> ⭐ **Did this click?** If SchemaSmith just saved you a migration script, [give it a star on GitHub](https://github.com/Schema-Smith/SchemaSmith/stargazers) — it helps other database teams find it.

## What's Next

You just did state-based schema management. Now let's understand what is happening under the hood -- products, templates, tokens, and the deployment pipeline that makes it all work. Same concepts across all four platforms.

Next: [Core Concepts](03-core-concepts.md)
