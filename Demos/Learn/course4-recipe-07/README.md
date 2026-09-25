# Course 4, Recipe 7 — Conditional / multi-delivery data delivery (lab)

**Goal:** ship a table's reference data *conditionally* — different rows to different targets from one package — by gating each `DataDelivery` with its own `ShouldApplyExpression`. One deploy fans out to a `_dev` database and a `_main` database, and the gates decide what data lands where: dev-only fixtures, a rich-vs-lean catalog per environment, and additive reference slices. SQL Server, PostgreSQL, MySQL, and MariaDB.

## The scenario

Recipe from Course 2 Module 5: attach a `DataDelivery` block to a table, point it at a `.tabledata` file, and SchemaQuench merges those rows on every deploy. That block delivered the *same* rows everywhere. Real environments aren't the same everywhere — your dev database wants a fat catalog and a pile of test orders; prod wants a lean, curated set and no test data at all. This recipe gates delivery on the target, so one package carries every environment's data and the deploy picks the right rows for each.

The lever is two new fields on `DataDelivery`:

- **`ShouldApplyExpression`** — a SQL predicate evaluated against the target at deploy time. True delivers; false skips (logged); blank/absent always delivers (unchanged behavior).
- **`VariantName`** — a label naming the intent behind a gate. It shows up in the deploy log whether the delivery applies or is skipped, so the run reads like a decision log.

And `DataDelivery` is now either a single object (as before) or an **array** of independently-gated deliveries.

## Before you start

> **Engine floor:** on your own server this lab needs **SQL Server 2016+** or **MySQL 8.0+** — it uses automatic data delivery, which needs `OPENJSON` / `JSON_TABLE`. PostgreSQL and MariaDB run it at any supported version. The Docker sandbox is already above the floor.

- **The sandbox is up.** SQL Server (`localhost,11433`), PostgreSQL (`localhost:15432`), MySQL (`localhost:13306`), MariaDB (`localhost:13307`), all `…/Learn!Passw0rd`.
- **The CLI is on your PATH** — `schemaquench --version` answers **2.7.0** or later.
- **Two databases per engine.** The lab creates `cookbook_r7_dev` and `cookbook_r7_main`; the template's `DatabaseIdentificationScript` targets both, so **one quench deploys to both** and the gates steer the data. Create them first (SQL Server shown):
  ```sql
  CREATE DATABASE cookbook_r7_dev;
  CREATE DATABASE cookbook_r7_main;
  ```
  MariaDB:
  ```bash
  ../lab-sql.sh mariadb information_schema "CREATE DATABASE IF NOT EXISTS cookbook_r7_dev"
  ../lab-sql.sh mariadb information_schema "CREATE DATABASE IF NOT EXISTS cookbook_r7_main"
  ```

## Deploy once, watch the gates decide

```bash
schemaquench --ConfigFile:sqlserver/deploy.settings.json
```

The schema — all three tables — deploys to *both* databases unconditionally. Only the **data** is gated. Watch the delivery step; the log names every decision by its `VariantName`:

```
[localhost,11433].[cookbook_r7_dev]     Skipping data delivery for dbo.ProductCatalog [Lean prod catalog] - ShouldApplyExpression evaluated false
[localhost,11433].[cookbook_r7_dev]     Delivering dbo.ProductCatalog [Rich dev catalog]
[localhost,11433].[cookbook_r7_dev]     Delivering dbo.SampleOrder [Dev/test sample orders]
[localhost,11433].[cookbook_r7_main]    Skipping data delivery for dbo.SampleOrder [Dev/test sample orders] - ShouldApplyExpression evaluated false
[localhost,11433].[cookbook_r7_main]    Delivering dbo.ProductCatalog [Lean prod catalog]
[localhost,11433].[cookbook_r7_main]    Delivering dbo.StatusCode [Core status codes]
[localhost,11433].[cookbook_r7_main]    Delivering dbo.StatusCode [Regional status codes]
```

Count the rows and the gates are plain:

| Table | `cookbook_r7_dev` | `cookbook_r7_main` |
| --- | :---: | :---: |
| `SampleOrder` | 3 | 0 |
| `ProductCatalog` | 6 | 2 |
| `StatusCode` | 0 | 5 |

Three patterns, one per table.

### Pattern 1 — environment-gated fixtures (single gated delivery)

`SampleOrder` carries throwaway test orders you want in dev/test and never in prod. A single `DataDelivery` object with one gate:

```json
"DataDelivery": {
  "ContentFile": "data/dbo.SampleOrder.tabledata",
  "MergeType": "Insert/Update",
  "MatchColumns": "OrderId",
  "ShouldApplyExpression": "DB_NAME() LIKE '%_dev' OR DB_NAME() LIKE '%_test'",
  "VariantName": "Dev/test sample orders"
}
```

The table is created everywhere; its rows only land where the name ends `_dev`/`_test`. In `cookbook_r7_main` the delivery is skipped — the table's there, empty, exactly as prod wants it.

### Pattern 2 — per-environment variants (an array of mutually-exclusive gates)

`ProductCatalog` ships a rich six-row catalog to dev and a lean two-row set to prod — same table, same `MatchColumns`, two content files, two gates that never both fire:

```json
"DataDelivery": [
  { "ContentFile": "data/dbo.ProductCatalog.dev.tabledata",  "MergeType": "Insert/Update", "MatchColumns": "Sku", "ShouldApplyExpression": "DB_NAME() = 'cookbook_r7_dev'",  "VariantName": "Rich dev catalog" },
  { "ContentFile": "data/dbo.ProductCatalog.main.tabledata", "MergeType": "Insert/Update", "MatchColumns": "Sku", "ShouldApplyExpression": "DB_NAME() <> 'cookbook_r7_dev'", "VariantName": "Lean prod catalog" }
]
```

dev gets six rows, main gets two. The gates are the environment switch, and the log tells you which variant won on each database.

### Pattern 3 — authoritative slices (full-sync partitions under one gate)

`StatusCode` builds its prod reference set from two slices that *both* apply — core global codes plus regional codes. Data deliveries aren't "one match wins": **every** delivery whose gate passes applies, in declared order. And each slice is **authoritative** for its partition: `Insert/Update/Delete` with a disjoint `MergeFilter`, so a slice inserts, updates, *and deletes* only the rows it owns.

```json
"DataDelivery": [
  { "ContentFile": "data/dbo.StatusCode.core.tabledata",     "MergeType": "Insert/Update/Delete", "MatchColumns": "Code", "MergeFilter": "Target.Region = 'GLOBAL'",        "ShouldApplyExpression": "DB_NAME() = 'cookbook_r7_main'", "VariantName": "Core status codes" },
  { "ContentFile": "data/dbo.StatusCode.regional.tabledata", "MergeType": "Insert/Update/Delete", "MatchColumns": "Code", "MergeFilter": "Target.Region IN ('EMEA','APAC')", "ShouldApplyExpression": "DB_NAME() = 'cookbook_r7_main'", "VariantName": "Regional status codes" }
]
```

On `cookbook_r7_main` both slices deliver — three core (GLOBAL) rows plus two regional (EMEA/APAC), five total. On dev both skip. `Target` is the row already in the table, so each `MergeFilter` fences a slice's authority to its partition: the core slice governs `Region = 'GLOBAL'`, the regional slice governs `EMEA`/`APAC`, and neither can touch the other's rows.

**Prove the partitioning.** Deploy the five rows, remove `HELD` from `dbo.StatusCode.core.tabledata`, and redeploy — `HELD` is deleted (it fell out of the GLOBAL source) while `EMEA` and `APAC` stay put (a different partition the core slice can't reach). Full-sync gives each slice authority; the disjoint filter keeps that authority in its lane.

> **Portability.** The `MergeFilter` alias is `Target` (the row already in the table) on all four engines — `Target.Region` on SQL Server, MySQL, and MariaDB, `"Target".region` (PostgreSQL folds unquoted names to lowercase). Full-sync delivery uses a partitioned `MERGE`; PostgreSQL handles it on 16 as well as 17 (this lab's sandbox runs 16.13).

## One rule and one caveat

- **An array of two or more deliveries requires a gate on every entry.** An ungated entry beside gated ones would always apply, defeating the point — so loading such an array fails with a clear error before any deployment work begins. A *single* delivery can be ungated (always applies) or carry one gate.
- **A `DataDelivery.ShouldApplyExpression` is not token-resolved.** Unlike a component or folder gate, it does *not* substitute `{{Token}}` placeholders (including `{{SchemaName}}`). Write it against things you can query directly on the target — the database name (`DB_NAME()` / `current_database()` / `DATABASE()`), a catalog lookup — not a token.

## Cross-platform

The gate mechanism is identical on all four engines; only the "which database am I?" predicate is native:

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Current-database predicate | `DB_NAME()` | `current_database()` | `DATABASE()` |
| Identifier form | `[dbo]`, bracketed | lowercase `public` | backtick-quoted, schema-less |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.* MariaDB's current-database predicate is `DATABASE()`, same as MySQL.

`ShouldApplyExpression`, `VariantName`, the object-or-array shape, the all-gates-apply semantics, and the skip logging are the same everywhere — only the predicate dialect changes.
