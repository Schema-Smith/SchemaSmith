# Module 2 — Your first schema package (lab)

Goal: declare a table in a schema package and deploy it with SchemaQuench — then run the exact same
command again and watch nothing happen. That no-op is the whole point of state-based deployment.

You'll work from the same package you kindled in Module 1. Each engine folder has a `starter/`
(the package with no tables yet) and a `solution/` (the finished package to compare against).

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and you completed [Module 1](../module-01) (the forge is kindled on `learn`).
- The CLI is on your PATH (`schemaquench --version` answers **2.7.0 or later**).

## Step 1: Look at the starter

Pick an engine and open its `starter/Package/`:

```
starter/Package/
  Product.json                       # Platform + template order
  Templates/Main/Template.json       # targets the `learn` database
```

No tables yet. Let's add one.

## Step 2: Declare a table

Create a table file under `starter/Package/Templates/Main/Tables/`. On **SQL Server**, make
`dbo.Widget.json`:

```json
{
  "Schema": "dbo",
  "Name": "Widget",
  "Columns": [
    { "Name": "WidgetId", "DataType": "BIGINT" },
    { "Name": "Name", "DataType": "NVARCHAR(100)" },
    { "Name": "Quantity", "DataType": "INT", "Nullable": true }
  ],
  "Indexes": [
    { "Name": "PK_Widget", "PrimaryKey": true, "Clustered": true, "IndexColumns": "WidgetId" }
  ]
}
```

The other engines are the same shape with small dialect differences — the exact files are in each
engine's `solution/Package/.../Tables/`:

| Engine     | File name            | Differences from above |
| ---------- | -------------------- | ---------------------- |
| SQL Server | `dbo.Widget.json`    | `NVARCHAR`, clustered PK named `PK_Widget` |
| PostgreSQL | `public.Widget.json` | `Schema: public`, `VARCHAR`, PK named `pk_widget` |
| MySQL      | `Widget.json`        | no `Schema`, `VARCHAR`, PK index **must** be named `PRIMARY` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

## Step 3: Deploy it

```bash
cd <engine>/starter
schemaquench --ConfigFile:deploy.settings.json
```

Expected (SQL Server shown; the wording varies slightly per engine):

```
[localhost,11433].[learn] Begin Quench
[localhost,11433].[learn]         Adding new table [dbo].[Widget]
[localhost,11433].[learn]         Creating constraint [dbo].[Widget].[PK_Widget]
[localhost,11433].[learn] Successfully Quenched
Completed quench of LearnConnect
```

PostgreSQL says `Create new table public.Widget`; MySQL says ``Create table `Widget` ``.

## Step 4: Prove it's there

```bash
# SQL Server (from a SQL client): SELECT name FROM sys.tables WHERE name = 'Widget';
cd ../..                              # back to the lab folder
../lab-sql.sh postgres learn "SELECT to_regclass('public.\"Widget\"')"
../lab-sql.sh mysql learn "SELECT table_name FROM information_schema.tables WHERE table_schema='learn' AND table_name='Widget'"
cd <engine>/starter                   # back to the engine folder
```

## Step 5: Run it again — the "aha"

```bash
schemaquench --ConfigFile:deploy.settings.json
```

This time there's **no** `Adding new table` line. The declared state already matches the database, so
SchemaQuench computes a difference of zero and changes nothing. That's idempotency: the same package
applied twice lands in the same place. Compare your work against `solution/` if anything differs.
