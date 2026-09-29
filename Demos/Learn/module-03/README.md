# Module 3 — Change it and redeploy (lab)

Goal: evolve the `Widget` table you built in Module 2 — widen a column and add a new one — then
**preview the exact change with WhatIf before applying it**, deploy it, and confirm SchemaSmith
converged the existing table to your new declared state. No hand-written `ALTER`.

Each engine folder has a `starter/` (the `Widget` package as Module 2 left it) and a `solution/`
(the evolved package, plus a `whatif.settings.json` preview config). The `solution/` is what your
edited `starter/` should look like when you're done.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and you completed [Module 2](../module-02),
  so the `Widget` table already exists on `learn`.
- The CLI is on your PATH (`schemaquench --version` answers **2.7.0 or later**).

> If you're starting fresh, deploy a `starter/` first to lay down the original `Widget`:
> `cd <engine>/starter && schemaquench --ConfigFile:deploy.settings.json`.

## Step 1: Make the change

Open your table file under `<engine>/starter/Package/Templates/Main/Tables/` and edit two things —
widen `Name` from 100 to 200, and add a nullable `Price` column. On **SQL Server** (`dbo.Widget.json`):

```json
{
  "Schema": "dbo",
  "Name": "Widget",
  "Columns": [
    { "Name": "WidgetId", "DataType": "BIGINT" },
    { "Name": "Name", "DataType": "NVARCHAR(200)" },
    { "Name": "Quantity", "DataType": "INT", "Nullable": true },
    { "Name": "Price", "DataType": "DECIMAL(10,2)", "Nullable": true }
  ],
  "Indexes": [
    { "Name": "PK_Widget", "PrimaryKey": true, "Clustered": true, "IndexColumns": "WidgetId" }
  ]
}
```

The other engines are the same edit with their own type names — the finished files are in each
engine's `solution/Package/.../Tables/`:

| Engine     | File name            | Type names                                          |
| ---------- | -------------------- | --------------------------------------------------- |
| SQL Server | `dbo.Widget.json`    | `NVARCHAR(200)`, `DECIMAL(10,2)`                    |
| PostgreSQL | `public.Widget.json` | `VARCHAR(200)`, `DECIMAL(10,2)` (reported `NUMERIC`) |
| MySQL      | `Widget.json`        | `VARCHAR(200)`, `DECIMAL(10,2)`                     |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

## Step 2: Preview with WhatIf

The `solution/` folder ships a `whatif.settings.json` — identical to `deploy.settings.json` but with
`"WhatIfONLY": true`. It computes the difference and prints the SQL it *would* run, without applying
anything. Add `--report` so the run also writes a structured receipt of the same preview:

```bash
cd <engine>/solution
schemaquench --ConfigFile:whatif.settings.json --report:./out/whatif-summary
```

Expected (SQL Server shown):

```
[localhost,11433].[learn]       ALTER TABLE [dbo].[Widget] ADD [Price] DECIMAL(10,2) NULL;
[localhost,11433].[learn]       ALTER TABLE [dbo].[Widget] ALTER COLUMN [Name] NVARCHAR(200) NOT NULL;
[localhost,11433].[learn] Successfully Quenched
```

(These print among the run's `[WhatIf]` marker lines; WhatIf applies none of them.)

PostgreSQL prints `ADD "Price" NUMERIC(10,2)` and `ALTER COLUMN "Name" SET DATA TYPE VARCHAR(200)`;
MySQL prints `ADD COLUMN \`Price\` DECIMAL(10,2) NULL` and `MODIFY COLUMN \`Name\` VARCHAR(200) NOT NULL`.

### The same preview, as data

The `[WhatIf]` markers above are for a human watching the log scroll by. `--report` writes the identical
preview as two files a program can read instead. Open `out/whatif-summary.md` first — the human receipt:

```text
## Object Changes
- Created: tables=0, columns=1, indexes=0, constraints=0, foreignKeys=0, procedures=0, views=0, functions=0
- Modified: tables=0, columns=1
- Dropped: tables=0, indexes=0, constraints=0, foreignKeys=0
- Ran (object scripts): 0
```

Now `out/whatif-summary.json` — the machine-readable twin a CI gate reads, with the mode that marks it a
preview and the two changes itemized:

```json
"run": { "mode": "WhatIf", "outcome": "Success", "exitCode": 0 },
"whatIf": { "wouldApply": [], "wouldSkip": [], "wouldDeliver": [] },
"objectChanges": {
  "instrumented": true,
  "created":  { "tables": 0, "columns": 1, "indexes": 0, "constraints": 0, "foreignKeys": 0, "procedures": 0, "views": 0, "functions": 0 },
  "modified": { "tables": 0, "columns": 1 },
  "dropped":  { "tables": 0, "indexes": 0, "constraints": 0, "foreignKeys": 0 },
  "scriptsRan": 0,
  "details": [
    { "objectType": "column", "objectName": "[dbo].[Widget].[Name]",  "action": "wouldModify" },
    { "objectType": "column", "objectName": "[dbo].[Widget].[Price]", "action": "wouldCreate" }
  ]
}
```

Those two `details[]` rows are the exact machine-readable twin of the two `ALTER TABLE` lines above —
`wouldModify` for the widened `Name`, `wouldCreate` for the new `Price` column. `mode: WhatIf` and the
empty `whatIf.wouldApply` are the tell that nothing behind these entries was actually applied.

`--report:` takes a base path with no extension — SchemaSmith writes both `.json` and `.md`. This lab's
`out/` folder is scratch output: remove `solution/out/` once you've looked, or leave it — nothing later
in this lab reads it back.

## Step 3: Confirm WhatIf changed nothing

The preview is read-only. The table is still in its Module 2 shape:

```bash
# SQL Server (from a SQL client): in the `learn` DB —
#   SELECT name, max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Widget');
cd ../..                              # back to the lab folder
../lab-sql.sh postgres learn "SELECT column_name, data_type, character_maximum_length FROM information_schema.columns WHERE table_schema='public' AND table_name='Widget' ORDER BY ordinal_position"
../lab-sql.sh mysql learn "SELECT column_name, column_type FROM information_schema.columns WHERE table_schema='learn' AND table_name='Widget' ORDER BY ordinal_position"
cd <engine>/solution                  # back to the engine folder
```

`Name` is still 100, and there's no `Price` yet — WhatIf looked but didn't touch.

## Step 4: Deploy the change

```bash
schemaquench --ConfigFile:deploy.settings.json
```

```
[localhost,11433].[learn]         Adding 1 new columns to [dbo].[Widget]
[localhost,11433].[learn]         Altering Column [dbo].[Widget].[Name]
[localhost,11433].[learn] Successfully Quenched
Completed quench of LearnConnect
```

Exactly the two changes WhatIf promised.

## Step 5: Prove it converged

Re-run the catalog query from Step 3 — now `Name` is 200 and `Price` is `DECIMAL(10,2)` / `NUMERIC(10,2)`,
nullable. The primary key never moved. Run `schemaquench --ConfigFile:deploy.settings.json` once more and
nothing happens: the declared state matches the database, so the difference is zero. Compare your edited
`starter/` against `solution/` if anything differs.
