# Course 4, Recipe 5 — Scripts that write scripts (lab)

Goal: a deploy-time script **reads your declared model** (the table's JSON) and **generates DDL from it** —
here, a `ProductSnapshot` table that mirrors `Product`'s columns, plus a copy of the current rows. Add a
column to `Product` and re-quench: the generated table grows to match and the next snapshot includes it. No
second declaration to keep in sync — the generated object is computed from the one source of truth.

This is different from cataloging your metadata (Course 2 reads the model into a table of *rows about* the
schema). Here the script **emits schema** — `CREATE TABLE`, `ALTER TABLE`, `INSERT` — built from the model.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` plus `deploy.settings.json`,
all targeting `cookbook_r5`.

## Before you start

> **Engine note:** on SQL Server 2016 and below this lab deploys a `FOR XML PATH` variant of its snapshot generator instead of `STRING_AGG` (2017+). Same output either way — the version split is the point. No floor is declared; every supported engine runs it.

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup) once — it creates `cookbook_r5`).
- The CLI is on your PATH (`schemaquench --version` → `2.7.0.0` or later).

## Step 1: Look at the generator

In `Templates/Main/Template.json`, a token holds the `Product` table's JSON:

```json
"ScriptTokens": { "ProductTable": "<*SpecificTable*>dbo.Product" }
```

The `[ALWAYS]` after-script reads that JSON, walks the columns (name + type), and builds DDL: a `CREATE TABLE`
for the mirror the first time, an `ALTER TABLE ADD` for any newly-declared columns, and an `INSERT … SELECT`
that snapshots the current rows. It runs that DDL dynamically — so it always reflects the current model.

## Step 2: Deploy — the script generates the mirror

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

`ProductSnapshot` now exists, with `Product`'s columns plus a `SnapshotAt`:

```bash
# SQL Server
cd ..            # back to the lab folder
../lab-sql.sh sqlserver cookbook_r5 "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ProductSnapshot') ORDER BY column_id"
# → SnapshotAt, ProductId, Name, Price
```

## Step 3: Put rows in Product, re-quench — they're captured

```sql
INSERT INTO Product (ProductId, Name, Price) VALUES (1, 'Widget', 9.99), (2, 'Gadget', 19.99);
```

```bash
schemaquench --ConfigFile:deploy.settings.json
# ProductSnapshot now holds a snapshot of those rows
```

## Step 4: Add a column — the generated table follows

Add `Sku NVARCHAR(50)` (or `VARCHAR(50)`) to `Product`'s JSON and re-quench. `ProductSnapshot` gains the `Sku`
column, and the next snapshot includes it — you never touched `ProductSnapshot`'s definition, because it
doesn't have one. It's generated from `Product` every deploy.

```bash
# PostgreSQL
cd ..            # back to the lab folder
../lab-sql.sh postgres cookbook_r5 "SELECT column_name FROM information_schema.columns WHERE table_name='productsnapshot' ORDER BY ordinal_position"
# → snapshotat, productid, name, price, sku
```

## Same lever, for a view

`<*SpecificTable*>` has view-model siblings: `<*SpecificIndexedView*>` (SQL Server) and
`<*SpecificMaterializedView*>` (PostgreSQL). They resolve the same way — the token holds the view's full JSON
model — but a view's model carries a `Definition` and an `Indexes` list rather than a `Columns` array, so the
script it drives is **index-aware**. This package declares a `vProductSummary` view over `Product` and a second
`[ALWAYS]` generator that reads its model:

- **SQL Server** (`Inventory View Indexes [ALWAYS].sql`) — SQL Server maintains indexed views for you, so
  there's nothing to rebuild or refresh. Instead the generator reads the view's declared `Indexes` and keeps a
  governance inventory table (`dbo.IndexedViewInventory`) in sync with what you declared — name, uniqueness,
  clustering, key columns. Declare another index on the view and re-quench: the inventory picks it up. (This is
  the *describe* side of the lever — not every model drives generated DDL; sometimes you keep an honest record.)
- **PostgreSQL** (`Refresh Materialized View [ALWAYS].sql`) — emits `REFRESH MATERIALIZED VIEW CONCURRENTLY`
  when the model declares a unique index (Postgres *requires* one for a concurrent refresh), or a plain
  `REFRESH` when it doesn't. The refresh mode is computed from the declared index model, so it can't drift.
- **MySQL and MariaDB** — neither has an indexed or materialized view type, so no view-model token and no
  view generator here (the `Product` snapshot above still applies).

```json
// SQL Server  — Templates/Main/Template.json
"ScriptTokens": { "ProductSummaryView": "<*SpecificIndexedView*>dbo.vProductSummary" }
// PostgreSQL  — Templates/Main/Template.json
"ScriptTokens": { "ProductSummaryView": "<*SpecificMaterializedView*>public.vproductsummary" }
```

Verify after deploy:

```bash
# SQL Server — the inventory reflects the view's declared indexes
../lab-sql.sh sqlserver cookbook_r5 "SELECT IndexName, IsUnique, IsClustered, KeyColumns FROM dbo.IndexedViewInventory WHERE ViewName='vProductSummary'"
# → CIX_vProductSummary 1 1 Name ;  IX_vProductSummary_Count 0 0 ProductCount

# PostgreSQL — the matview is populated, proving the concurrent refresh ran
../lab-sql.sh postgres cookbook_r5 "SELECT matviewname, ispopulated FROM pg_matviews WHERE matviewname='vproductsummary'"
# → vproductsummary|t
```

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Read the model JSON | `OPENJSON` | `jsonb_array_elements` | `JSON_TABLE` |
| Run generated DDL | `EXEC(@sql)` | `EXECUTE` in a `DO` block | `PREPARE` / `EXECUTE` |
| Snapshot timestamp | `SYSUTCDATETIME()` | `clock_timestamp()` | `CURRENT_TIMESTAMP(6)` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

Same shape on all four — read the model as JSON, build `CREATE`/`ALTER`/`INSERT` from the column list, run
it. Only the dialect's JSON-shredding and dynamic-execution syntax differ.

## The principle

The generated table is computed from the declared model on every deploy, so it can never drift from it. You
don't maintain a parallel definition; you maintain `Product`, and the snapshot table is derived. That's the
move — when an object's shape is a function of your schema, generate it from the schema instead of hand-keeping
a copy. One source of truth, and the tools write the rest.
