# Course 9 · Module 2 — Where native fidelity diverges

Three engines. The same two concepts — an auto-incrementing surrogate key, a JSON payload — declared three different ways because that is what each engine's native model actually looks like.

SchemaSmith does not flatten those differences into a lowest-common-denominator abstraction. It models them natively, so the JSON you write matches the DDL the engine expects and the artifacts the extractor produces.

## The two fidelity axes

### Auto-incrementing surrogate keys

The concept is identical: a surrogate integer key the database assigns automatically. The encoding is not:

| Engine | Table | Column | How the key is declared |
| --- | --- | --- | --- |
| SQL Server (`orders`) | `[OrderEvent]` | `[EventId]` | `"DataType": "INT IDENTITY(1, 1)"` — identity is encoded directly in the DataType string. There is no separate Identity property. |
| PostgreSQL (`catalog`) | `price_history` | `history_id` | `"DataType": "int4"` + `"Generated": "GENERATED ALWAYS AS IDENTITY"` — a first-class column property alongside DataType. |
| MySQL (`sessions`) | `` `PageHit` `` | `` `HitId` `` | `"DataType": "int"` + `"AutoIncrement": true` — a boolean column property. MySQL requires the AUTO_INCREMENT column be a key, so `HitId` is the PK. |

Open the three table JSON files and compare them side by side. The difference is not a SchemaSmith choice — it reflects how each engine exposes the feature in its own DDL and information schema.

### JSON payloads

Every modern application stores flexible JSON alongside structured columns. How you hold that payload depends entirely on the engine:

| Engine | Column | Type | What it means |
| --- | --- | --- | --- |
| SQL Server (`orders`) | `[Detail]` | `NVARCHAR(MAX)` | SQL Server has no native JSON type. JSON lives in a max-width Unicode string — valid, queryable with JSON functions, and exactly what the extractor captures. |
| PostgreSQL (`catalog`) | `attributes` | `jsonb` | Native binary JSON with indexing support. `tags` alongside it is `text[]` — a native typed array, another PostgreSQL-only construct. |
| MySQL (`sessions`) | `` `Meta` `` | `json` | Native JSON column with constraint validation and path-expression queries. `` `Channel` `` alongside it is `enum('web','ios','android')` — a native MySQL ENUM, which has no equivalent in the other two engines. |

## Prerequisites

- Three-engine sandbox is up (`Demos/Learn/docker`).
- **Run course9-setup first** ([`../course9-setup/README.md`](../course9-setup/README.md)) so the `orders`, `catalog`, and `sessions` databases exist. This module is additive — it deploys one new table per service into those existing databases.
- Module 1 does not need to have been deployed first. The new tables have no foreign keys to Module 1's tables.
- `schemaquench --version` answers **2.7.0** or later.

## Steps

Each engine has a single package — deploy it once. There is no baseline/after sequence here; this module teaches declaration, not change.

---

### Orders — SQL Server

```
cd sqlserver
```

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `[dbo].[OrderEvent]` is created in `orders` with `[EventId]` as `INT IDENTITY(1, 1)` and `[Detail]` as `NVARCHAR(MAX)`.

---

### Catalog — PostgreSQL

```
cd postgres
```

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `price_history` is created in `catalog` with `history_id` as `int4 GENERATED ALWAYS AS IDENTITY`, `attributes` as `jsonb`, and `tags` as `text[]`.

---

### Sessions — MySQL

```
cd mysql
```

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `` `PageHit` `` is created in `sessions` with `` `HitId` `` as `int AUTO_INCREMENT`, `` `Meta` `` as `json`, and `` `Channel` `` as `enum('web','ios','android')`.

---

## What to notice

Look at `artifacts/` in each engine folder after the deploy. The DDL SchemaSmith wrote for the identity column is engine-native in every case: `INT IDENTITY(1, 1)` in T-SQL, `GENERATED ALWAYS AS IDENTITY` appended to the column definition in PostgreSQL, `AUTO_INCREMENT` in MySQL. The package JSON described it at the right level of abstraction for each engine — not a single cross-platform property that gets translated.

The same is true for JSON storage. `NVARCHAR(MAX)` in T-SQL, `jsonb` in PostgreSQL, `json` in MySQL. No translation layer — just native types modeled natively.

## What each folder is

| Path | Purpose |
| --- | --- |
| `sqlserver/package/` | `[OrderEvent]` table — surrogate identity key + NVARCHAR(MAX) JSON column. Deploys to `orders`. |
| `postgres/package/` | `price_history` table — GENERATED ALWAYS AS IDENTITY + jsonb + text[] columns. Deploys to `catalog`. |
| `mysql/package/` | `` `PageHit` `` table — AUTO_INCREMENT key + json + enum columns. Deploys to `sessions`. |
| `<engine>/quench.settings.json` | Points at the package, targets the engine's service database. |

## Up next

Module 3 looks at how to organize polyglot services in practice: per-service repositories, tables grouped into `Core/` and `Reference/` subfolders, and file-less connection config via `SmithySettings_` environment variables.
