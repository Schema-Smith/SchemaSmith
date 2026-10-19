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
- **Run course9-setup first** ([`../course9-setup/README.md`](../course9-setup/README.md)) so the `orders`, `catalog`, and `sessions` databases exist. This module deploys one new table per service into those existing databases.
- Module 1 does not need to have been deployed first. The new tables have no foreign keys to Module 1's tables.
- **This module is not additive.** Each Module 2 package is the same product as its Module 1 package (`Orders`, `Catalog`, `Sessions`), and it declares only the one new table. To SchemaQuench, Module 1's tables are now tables removed from the product, so deploying Module 2 over Module 1 drops them. If you deployed Module 1, you will see:

  ```text
  SQL Server:  [localhost,11433].[orders]         Dropping table [dbo].Customer
               [localhost,11433].[orders]         Dropping table [dbo].OrderItem
               [localhost,11433].[orders]         Dropping table [dbo].SalesOrder
               [localhost,11433].[orders]         Dropping table [dbo].OrderStatus
  PostgreSQL:  [localhost].[catalog]       Drop tables removed from the product
               [localhost].[catalog]         Table public.product no longer in product
               [localhost].[catalog]         Table public.category no longer in product
               [localhost].[catalog]         Table public.product_status no longer in product
  MySQL:       [localhost].[sessions]       Drop inbound FK: ALTER TABLE `sessions`.`Event` DROP FOREIGN KEY `FK_Event_Session`
               [localhost].[sessions]       Drop table: Event
               [localhost].[sessions]       Drop table: EventCategory
               [localhost].[sessions]       Drop table: Session
  ```

  That is ownership working as designed: one product, one declared set of tables. Each module in this course is its own snapshot of the service, not a layer on the last one.
- `schemaquench --version` answers **2.7.0** or later.

## Steps

Each engine has a single package — deploy it once. There is no baseline/after sequence here; this module teaches declaration, not change.

---

### Orders — SQL Server

Deploy from the `sqlserver` folder.

macOS / Linux:
```bash
# from course9-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```powershell
# from course9-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `[dbo].[OrderEvent]` is created in `orders` with `[EventId]` as `INT IDENTITY(1, 1)` and `[Detail]` as `NVARCHAR(MAX)`.

---

### Catalog — PostgreSQL

Deploy from the `postgres` folder.

macOS / Linux:
```bash
# from course9-module-02/postgres
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```powershell
# from course9-module-02/postgres
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `price_history` is created in `catalog` with `history_id` as `int4 GENERATED ALWAYS AS IDENTITY`, `attributes` as `jsonb`, and `tags` as `text[]`.

---

### Sessions — MySQL

Deploy from the `mysql` folder.

macOS / Linux:
```bash
# from course9-module-02/mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows:
```powershell
# from course9-module-02/mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Exit 0. `` `PageHit` `` is created in `sessions` with `` `HitId` `` as `int AUTO_INCREMENT`, `` `Meta` `` as `json`, and `` `Channel` `` as `enum('web','ios','android')`.

---

## What to notice

The run log names the table it created (`Adding new table [dbo].[OrderEvent]` on SQL Server, `Create new table public.price_history` on PostgreSQL, ``Create table `PageHit` `` on MySQL) but does not print the DDL. Ask each engine what it built instead, from the lab folder:

```bash
# from course9-module-02
../lab-sql.sh sqlserver orders "SELECT c.name, TYPE_NAME(c.user_type_id) AS type_name, c.max_length, c.is_identity FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.OrderEvent') ORDER BY c.column_id"
../lab-sql.sh postgres catalog "SELECT column_name, udt_name, is_identity, identity_generation FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'price_history' ORDER BY ordinal_position"
../lab-sql.sh mysql sessions "SELECT COLUMN_NAME, COLUMN_TYPE, EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'sessions' AND TABLE_NAME = 'PageHit' ORDER BY ORDINAL_POSITION"
```

The identity column is engine-native in every case: an identity `int` on SQL Server, `GENERATED ALWAYS AS IDENTITY` in PostgreSQL, `auto_increment` in MySQL. The package JSON described it at the right level of abstraction for each engine — not a single cross-platform property that gets translated.

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
