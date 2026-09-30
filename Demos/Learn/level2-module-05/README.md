# Course 2, Module 5 — Data delivery: ship the rows too (lab)

Goal: deliver **reference data** alongside your schema, idempotently, and see the three tools come
together. You'll attach a `DataDelivery` block to an `IsoCurrency` lookup table, deploy it so the rows
MERGE in, edit a row and re-deploy to watch the update land without duplicating, and finally use
**DataTongs** to cast the live data back out into a content file.

Data delivery declares the rows that belong with a table right in the package:

- **`DataDelivery.ContentFile`** points at a `.tabledata` JSON array of rows (under `data/` at the
  template root).
- **`DataDelivery.MergeType`** chooses how rows reconcile: `Insert` (add missing), `Insert/Update`
  (add + update changed), or `Insert/Update/Delete` (full sync — also deletes rows not in the file).
- **`DataDelivery.MatchColumns`** is the key SchemaSmith matches on to tell "already there" from "new".

SchemaQuench delivers the data on every quench by MERGEing the content file into the table — the same
declarative, idempotent model it uses for schema.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/`, a
`deploy.settings.json`, and a `tongs.settings.json` (for the DataTongs step).

## Before you start

> **Engine floor:** on your own server this lab needs **SQL Server 2016+** or **MySQL 8.0+** — it uses automatic data delivery, which needs `OPENJSON` / `JSON_TABLE`. PostgreSQL and MariaDB run it at any supported version. The Docker sandbox is already above the floor.

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later;
  `datatongs --version` is available too).

## Step 1: Look at the delivery declaration

Open `<engine>/Package/Templates/Main/Tables/…IsoCurrency.json` (SQL Server shown):

```json
"DataDelivery": {
  "ContentFile": "data/dbo.IsoCurrency.tabledata",
  "MergeType": "Insert/Update",
  "MatchColumns": "Code"
}
```

and the content file it points at, `Templates/Main/data/dbo.IsoCurrency.tabledata` — a JSON array of
five currency rows. That's the entire data-delivery surface: a block on the table, a file of rows.

## Step 2: Deploy — the rows ride in with the schema

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

```
[localhost,11433].[learn]         Adding new table [dbo].[IsoCurrency]
[localhost,11433].[learn]   Delivering table data
[localhost,11433].[learn]     Delivering dbo.IsoCurrency
[localhost,11433].[learn] Successfully Quenched
```

One run created the table and delivered its rows.

## Step 3: Verify, and prove idempotency

```bash
# SQL Server (from a SQL client): SELECT COUNT(*) FROM dbo.IsoCurrency;   -- 5
../lab-sql.sh postgres learn "SELECT count(*) FROM public.isocurrency"
../lab-sql.sh mysql learn "SELECT COUNT(*) FROM IsoCurrency"
../lab-sql.sh mariadb learn "SELECT COUNT(*) FROM IsoCurrency"
```

Five rows. Run `schemaquench` again — still five. Delivery MERGEs declared rows against what's there,
so an unchanged row is a no-op. No duplicates, no PK errors.

## Step 4: Edit a row, re-deploy — watch Insert/Update

Change one row in the content file — `JPY` from `Yen` to `Japanese Yen` — and re-deploy:

```bash
schemaquench --ConfigFile:deploy.settings.json
```

```sql
-- SQL Server
SELECT Code, CurrencyName FROM dbo.IsoCurrency WHERE Code = 'JPY';   -- JPY  Japanese Yen
```

Still five rows: the `JPY` row was **updated** (matched on `Code`), not duplicated. The other four
no-op. That's `MergeType: "Insert/Update"`. (Set it to `Insert` and the update would be skipped; set
it to `Insert/Update/Delete` and rows you remove from the file would be deleted from the target.)

## Step 5: Cast the data with DataTongs

You won't hand-type content files for real tables — **DataTongs** casts them out of a source, the way
SchemaTongs casts schema:

```bash
datatongs --ConfigFile:tongs.settings.json
```

```
Casting data for: dbo.IsoCurrency
  Writing contents to : ./cast\dbo.IsoCurrency.tabledata
  Writing merge script to : ./cast\Populate dbo.IsoCurrency.sql
```

Look in the generated `cast/` folder. Two artifacts, and the difference matters:

- **`dbo.IsoCurrency.tabledata`** — the rows. This is what you drop into a package's `data/` folder
  for `DataDelivery` to ship; SchemaQuench builds its own merge from it at deploy time.
- **`Populate dbo.IsoCurrency.sql`** — a standalone MERGE script. Data delivery does **not** use this;
  it's for running the merge by hand, handing to a DBA, dropping into a script folder as an override,
  or using the data outside SchemaQuench entirely.

**Open that script before you hand it to anybody.** You are about to give someone a statement that writes
to their table, and the shape of it is the whole question:

```sql
MERGE INTO [dbo].[IsoCurrency] AS Target
USING ( SELECT [Code],[CurrencyName],[IsoNumber] FROM OPENJSON(@v_json) WITH (...) ) AS Source
ON Source.[Code] = Target.[Code]
WHEN MATCHED AND (...) THEN
  UPDATE SET ...
WHEN NOT MATCHED BY TARGET THEN
  INSERT (...) VALUES (...)
;
```

Two branches: update what matches, insert what is missing. **Nothing deletes.** Rows in the target that
the content file does not mention are left exactly where they are.

That is the default, and it is opt-in for a reason. Add `"MergeDelete": true` to `ShouldCast` and a third
branch appears:

```sql
WHEN NOT MATCHED BY SOURCE THEN
  DELETE
```

Now the script is a **full sync**: anything in the table that is not in your content file is removed. That
is a legitimate thing to want — it is what `MergeType: "Insert/Update/Delete"` asks for on a declared
`DataDelivery` — but it is a very different script to hand a DBA, and the only visible difference is those
two lines. Read the branches before you run it, every time.

(`cast/` is generated output — leave it out of source control.)

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| New-table wording | `Adding new table [dbo].[IsoCurrency]` | `Create new table public.isocurrency` | ``Create table `IsoCurrency` `` |
| Merge idiom | `MERGE` + `OPENJSON` | `MERGE` + `jsonb_to_recordset` | `INSERT … ON DUPLICATE KEY UPDATE` (+ `DELETE` for full sync) |
| Content file | `data/dbo.IsoCurrency.tabledata` | `data/public.isocurrency.tabledata` | `data/IsoCurrency.tabledata` |
| Identifier case | bracketed, mixed-case | folded to lowercase | backticked |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

`MergeType`, `MatchColumns`, and the `.tabledata` shape are identical across engines — SchemaSmith
emits the right merge per engine. PostgreSQL (15+) and SQL Server have native `MERGE`; MySQL uses
`INSERT … ON DUPLICATE KEY UPDATE` plus a companion `DELETE` for full sync.

## The principle

Reference data belongs with the schema it serves — versioned in the same package, deployed by the
same run. A `DataDelivery` block points a table at a `.tabledata` content file; SchemaQuench MERGEs it
in idempotently, with the `MergeType` you choose. DataTongs casts those content files out of a live
source so you never hand-type them. Schema and data, cast from one source of truth and hardened
together — no post-deploy `INSERT` scripts, no drift between the structure and the rows it needs.
