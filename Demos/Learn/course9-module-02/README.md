# Course 9 · Module 2 — Where native fidelity diverges

<!-- TRAINING-RELEASE-PIN 2.8.0 (#519 one file per overload; #433 case-only column rename): delete the "On 2.7.x" callouts -->

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
  SQL Server:  [localhost,11433].[orders]         Dropping table [dbo].[Customer]
               [localhost,11433].[orders]         Dropping table [dbo].[OrderItem]
               [localhost,11433].[orders]         Dropping table [dbo].[SalesOrder]
               [localhost,11433].[orders]         Dropping table [dbo].[OrderStatus]
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

#### PostgreSQL's own object kinds

The same deploy built four kinds of object that only PostgreSQL has, each from its own folder in the package:

| Folder | Script | What it is |
| --- | --- | --- |
| `Trigger Functions/` | `public.stamp_recorded_at.sql` | a `RETURNS trigger` function that fills `recorded_at` when an insert leaves it out |
| `Triggers/` | `public.price_history.price_history_stamp.sql` | the trigger that calls it, `BEFORE INSERT` on `price_history` |
| `Rules/` | `public.price_history.price_history_append_only.sql` | `ON DELETE … DO INSTEAD NOTHING`: price history is append-only |
| `Aggregates/` | `public.tag_list.sql` | a user-defined aggregate that joins values into one list (its step function is in `Functions/`) |

`Functions/` also holds two functions that share a name: `public.price_band(numeric)` and
`public.price_band(numeric, numeric)`. PostgreSQL tells overloads apart by their argument types, so the package
does too — the file names carry the signature. The deploy log names each script it ran:

```
  Quenched .\package\Templates\Main\Trigger Functions\public.stamp_recorded_at.sql
  Quenched .\package\Templates\Main\Aggregates\public.tag_list.sql
  Quenched .\package\Templates\Main\Rules\public.price_history.price_history_append_only.sql
  Quenched .\package\Templates\Main\Triggers\public.price_history.price_history_stamp.sql
```

Put them to work, from the lab folder:

```bash
# from course9-module-02
../lab-sql.sh postgres catalog "INSERT INTO public.price_history (product_ref, attributes, tags) VALUES (7, '{}', ARRAY['sale','new']), (8, '{}', ARRAY['clearance'])"
../lab-sql.sh postgres catalog "DELETE FROM public.price_history"
../lab-sql.sh postgres catalog "SELECT count(*), bool_and(recorded_at IS NOT NULL) FROM public.price_history"
# → 2|t
../lab-sql.sh postgres catalog "SELECT public.tag_list(t ORDER BY t) FROM (SELECT unnest(tags) AS t FROM public.price_history) s"
# → clearance,new,sale
../lab-sql.sh postgres catalog "SELECT public.price_band(42), public.price_band(42, 40)"
# → mid|high
```

Both rows got a `recorded_at` from the trigger, the `DELETE` removed nothing (the rule), the aggregate folded
three tags into one string, and each overload answered for its own signature.

Deploy again and the trigger function, the aggregate, both overloads and the trigger are `Unchanged, not re-run`.
The rule's script runs on every deploy: SchemaSmith stamps views, functions, procedures and triggers, and re-applies
other object scripts — harmless here, since `CREATE OR REPLACE RULE` puts back the same rule.

```bash
# from course9-module-02/postgres
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Now cast the database back out:

```bash
# from course9-module-02/postgres
schematongs --ConfigFile:tongs.settings.json
```

```
  Casting .\extracted\Templates\Main\Functions\public.price_band(numeric, numeric).sql
  Casting .\extracted\Templates\Main\Functions\public.price_band(numeric).sql
  Casting .\extracted\Templates\Main\Trigger Functions\public.stamp_recorded_at.sql
  Casting .\extracted\Templates\Main\Functions\public.tag_list_step.sql
  Casting .\extracted\Templates\Main\Aggregates\public.tag_list.sql
  Casting .\extracted\Templates\Main\Rules\public.price_history.price_history_append_only.sql
  Casting .\extracted\Templates\Main\Triggers\public.price_history.price_history_stamp.sql
```

Every object comes back into the folder it came from, under the name the package already uses — including one
file per overload.

> **On 2.7.x** the two overloads extract into a single `public.price_band.sql` that holds only one of them, so the other is missing from the package.

`Window Functions/` is the one PostgreSQL folder this lab leaves empty: PostgreSQL only lets you write a window
function in C, so the ones you'll meet come from extensions.

The catalog goes further than this lab needs. SchemaTongs also casts range types, text search configurations,
operators, casts and event triggers, each to its own folder (`Range Types/`, `Text Search Configurations/`,
`Operators/`, `Casts/`, `Event Triggers/`), so a package extracted from a database that uses them deploys to an
empty one. An operator's file name carries its symbol and argument types, with characters a file system can't hold
encoded: `<->` on two `numeric`s is `public.%3C-%3E(numeric, numeric).sql`. (On 2.7.x none of the five was
extracted, and a range type's constructor functions came out as ordinary functions in `Functions/`, so such a
package could not rebuild its database.)

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

#### MySQL's own rule for names

Native fidelity covers how an engine compares names, too. To MySQL and MariaDB a column name is
case-insensitive: `Channel` and `channel` are the same column. Put two rows in, then respell the column in the
package — `` `Channel` `` becomes `` `channel` `` in `mysql/package/Templates/Main/Tables/PageHit.json`:

```bash
# from course9-module-02
../lab-sql.sh mysql sessions "INSERT INTO PageHit (SessionRef, OccurredAt, Meta, Channel) VALUES (1, NOW(), '{}', 'ios'), (2, NOW(), '{}', 'web')"
```

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/package/Templates/Main/Tables/PageHit.json" set 'Columns[Name=`Channel`].Name' '"`channel`"' -->
```json
{ "Name": "`channel`", "DataType": "enum('web','ios','android')", ... }
```

```bash
# from course9-module-02/mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```
  Rename column `Channel` to `channel` on `PageHit`
```

```bash
# from course9-module-02
../lab-sql.sh mysql sessions "SELECT COUNT(*) FROM PageHit WHERE channel IS NOT NULL"
# → 2
```

Same column, new spelling, both rows' values intact.

> **On 2.7.x** this deploy logs ``Drop column: ALTER TABLE `sessions`.`PageHit` DROP COLUMN `Channel` `` and exits
> `0`: the comparison was case-sensitive, so the live column looked removed from the package and was dropped with its
> data, and the respelled one was never added (#433). If you follow along on 2.7.x, skip the deploy.

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
