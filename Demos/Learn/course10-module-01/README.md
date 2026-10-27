# Course 10 · Module 1 — Let the engine adapt

<!-- TRAINING-RELEASE-PIN 2.8.0 (warn downgrade exits 1; --ExitNonZeroOnWarning; PostgreSQL Skipping line; newest tier; {{ServerMajorVersion}} in MySQL/MariaDB table and column expressions, #598): delete the "On 2.7.x" callouts -->

You author one modern feature per engine and deploy the same package to a **floor-version**
target that predates it. SchemaSmith detects the target version, emits the degraded form the
old engine can take, and records what it couldn't in the `downgraded` manifest — surfaced in
the deployment summary under **Unsupported Feature Downgrades**. The `Target:UnsupportedFeaturePolicy`
setting is your one knob: `warn` (default) degrades and records; `fail` refuses outright.

Then SQL Server plays the honest contrast: a would-degrade feature deployed to a **compatibility
level 100** database on the modern 2022 binary lands at **full fidelity, zero downgrades** —
because SQL Server feature support tracks the server binary, not the database's compat level.

## What each engine authors, and what happens at the floor

| Engine | Floor target | Authored feature | At the floor |
| --- | --- | --- | --- |
| PostgreSQL | `localhost:15433` (PG 12), db `learn` | expression statistics — `"StatisticsColumns": "(\"Id\" / 1)"` (a PG 14 feature) | **warn** skips the statistic + records a downgrade; **fail** aborts with "Expression statistics require PostgreSQL 14" |
| MySQL | `localhost:13316` (MySQL 5.7), db `learn` | a table `CHECK` constraint (MySQL 8.0.16 feature) | **warn** emits the table *without* the check + records a downgrade |
| MariaDB | `localhost:13317` (MariaDB 10.2), db `learn` | a **descending** index key part (MariaDB 10.8) + an **invisible** index (MariaDB 10.6) | **warn** stores the key part ascending + creates the index visible; records one downgrade each |
| SQL Server | `localhost,11433` → db `learn_2008` (compat 100) | dynamic **data masking** — `"DataMaskFunction": "email()"` (a SQL Server 2016 feature) | **full fidelity, zero downgrades** — the 2022 binary supports it regardless of compat level |

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the floor-version fleet is
  standing and every target database exists (`learn` on PostgreSQL 12 / MySQL 5.7 / MariaDB 10.2,
  and `learn_2008` at compatibility level 100 on the SQL Server instance). This module deploys
  into those databases — it does not create them. See that lab's ports-and-tiers table.
- `schemaquench --version` answers **2.7.0** or later. All four engine floors and the
  `UnsupportedFeaturePolicy` knob shipped in 2.4.0.

## Steps

Each engine is self-contained. Deploy in any order.

---

### PostgreSQL — the degrade you can flip

Run these from the `postgres` folder.

**Warn (default) — degrade and record:**

macOS / Linux:
```bash
# from course10-module-01/postgres
schemaquench --ConfigFile:quench.settings.warn.json --LogPath:"$PWD/logs"
```

Windows:
```powershell
# from course10-module-01/postgres
schemaquench --ConfigFile:quench.settings.warn.json --LogPath:"$PWD\logs"
```

Deploys, exits 1. The `metric` table lands, but the expression statistic `ST_metric_expr` cannot exist
on PostgreSQL 12 (it needs 14). SchemaSmith skips it and records a `downgraded` row, and a run
that downgraded something finishes as incomplete — exit 1, not 0. The log says so as it happens:

```
[localhost].[learn]   Skipping expression statistics (PG14) on public.metric.ST_metric_expr (downgraded)
```

Then read the deployment summary's **Unsupported Feature Downgrades** section.

> **On 2.7.x** the same run exits 0 and prints no `Skipping` line; the statistic is still skipped and the
> downgrade still recorded in the summary.

Add `--ExitNonZeroOnWarning`, the switch that turns a `0` into a `1` when a run logged a warning:

```bash
# from course10-module-01/postgres
schemaquench --ConfigFile:quench.settings.warn.json --LogPath:"$PWD/logs" --ExitNonZeroOnWarning
```

Still exit 1, for the same reason. A downgrade is not a warning: the run already exits 1, and the switch
only ever changes a `0`. So the exit code alone can't tell a downgraded run from a warned one — the
deployment summary's `outcome` can: `Incomplete` here.

> **On 2.7.x** the switch does not exist: the run logs a line starting `Ignored - not recognized: --ExitNonZeroOnWarning` and exits 0.

**Fail — refuse instead:**

macOS / Linux:
```bash
# from course10-module-01/postgres
schemaquench --ConfigFile:quench.settings.fail.json --LogPath:"$PWD/logs"
```

Windows:
```powershell
# from course10-module-01/postgres
schemaquench --ConfigFile:quench.settings.fail.json --LogPath:"$PWD\logs"
```

Non-zero exit. Same package, same target — but now SchemaSmith aborts with a message naming the
feature and the version it requires, rather than deploying a silently-degraded schema.

---

### MySQL — a CHECK the floor can't keep

```bash
# from course10-module-01/mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Deploys, exits 1. The `Ledger` table lands, but MySQL 5.7 parses-and-ignores `CHECK` constraints (they
need 8.0.16), so SchemaSmith emits the table without the check and records a downgrade — and the
downgrade makes the run finish as incomplete:

```
[localhost].[learn]       Skipping check constraint (requires MySQL 8.0.16 - downgraded): Ledger.CK_Ledger_Amount
```
 Deploy
the identical package to MySQL 8.0 and the check is created — same package, target decides.

> **On 2.7.x** the same run exits 0; the check is still left out and the downgrade still recorded.

To see the refusal instead, flip the knob for one run:
```bash
# from course10-module-01/mysql
SmithySettings_Target__UnsupportedFeaturePolicy=fail schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

---

### MariaDB — two downgrades in one deploy

```bash
# from course10-module-01/mariadb
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Deploys, exits 1. `Ledger` lands, but MariaDB 10.2 is below both features it declares: the **descending**
index key part (10.8) is stored ascending, and the **invisible** index (10.6) is created visible.
Each records its own `downgraded` row — two lines in the summary — and the run finishes as incomplete:

```
[localhost].[learn]       Descending index key part stored ascending (requires MySQL 8.0 / MariaDB 10.8 - downgraded): Ledger.IX_Ledger_Amount_Desc
[localhost].[learn]       Invisible index stored visible (requires MySQL 8.0 / MariaDB 10.6 - downgraded): Ledger.IX_Ledger_Region_Hidden
```

> **On 2.7.x** the same run exits 0; both indexes are still downgraded the same way.

---

### SQL Server — the honest contrast (no degrade)

```bash
# from course10-module-01/sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Exit 0. The `Account` table's `Email` column declares dynamic data masking — a SQL Server 2016
feature — and the target is `learn_2008`, a **compatibility level 100** database. On the other
engines that mismatch degrades. Here it does **not**: the `Unsupported Feature Downgrades` section
is absent, and the mask is applied. SQL Server feature support gates on the server *binary*
(this instance is 2022), not the database's compatibility level. The 100-level database changes
how SchemaSmith *encodes* its model on the wire (Module 4's topic), not which features it can
deploy — and that gap between compat level and server version is exactly the footgun Module 2 turns on.

## The newest tier — a feature your current server doesn't have yet

Everything above runs a modern feature down to an old server. The same gate faces the other way too: the day a new
release adds something, *your current server* is the old one. SQL Server 2025 adds JSON indexes over a native `json`
column; MariaDB 11.7 adds `VECTOR` indexes over a `VECTOR` column. The sandbox's main engines are SQL Server 2022 and
MariaDB 11.4, so this section brings up the newest releases beside them and deploys one package to both.

There is one wrinkle the floor beats above did not have. There, only the *index* was too new. Here the *column type*
is too new as well — a SQL Server 2022 server has no `json` type, and MariaDB 11.4 has no `VECTOR` — so no
downgrade can save a package that declares them unconditionally. The answer is a **column variant**: the same column
declared twice, each gated on the server version, the native type where the server has it and a plain one where it
doesn't.

```json
{ "Name": "[Payload]", "DataType": "JSON",          "Nullable": true, "VariantName": "Native", "ShouldApplyExpression": "{{ServerMajorVersion}} >= 17" },
{ "Name": "[Payload]", "DataType": "NVARCHAR(MAX)", "Nullable": true, "VariantName": "Text",   "ShouldApplyExpression": "{{ServerMajorVersion}} < 17" }
```

`NVARCHAR(MAX)` holds the same JSON text on 2022, so the application keeps working; only the index is missing. MariaDB's
package does the same with `VECTOR(3)` above 11.7 and `VARBINARY(12)` below it (`{{ServerMajorVersion}}` reads `1108`
on MariaDB 11.8 and `1104` on 11.4). The index itself is declared once, unconditionally — and that is the point: where
the server has the feature it is built, and where it doesn't, the `UnsupportedFeaturePolicy` downgrade you met above
records it instead of failing.

### Bring up the newest tier

The `latest-tier` profile adds `learn-sqlserver-2025` (`localhost,11434`) and `learn-mariadb-118` (`localhost:13308`).
SQL Server is another ~2 GB, capped at 4 GB; its first boot can take several minutes.

```bash
# from docker
docker compose --profile latest-tier up -d sqlserver-latest mariadb-latest
```

When both report healthy (`docker ps`), give SQL Server 2025 its `learn` database (MariaDB's image creates its own).
`LEARN_CONTAINER` points `lab-sql` at a container other than the engine's default:

```bash
# from course10-module-01
LEARN_CONTAINER=learn-sqlserver-2025 ../lab-sql.sh sqlserver master "IF DB_ID('learn') IS NULL CREATE DATABASE learn"
```

### SQL Server — a JSON index on 2025, recorded on 2022

```bash
# from course10-module-01/newest/sqlserver
schemaquench --ConfigFile:quench.settings.2025.json --LogPath:"$PWD/logs"
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD/logs"
```

The 2025 deploy exits `0`:

```
  Adding new table [dbo].[Event]
  Creating JSON index [dbo].[Event].[JX_Event_Payload]
```

The 2022 deploy exits `1` — the `warn` policy's "finished, but something was relaxed":

```
  JSON index skipped: requires SQL Server 2025 - the table deploys without it (downgraded): [dbo].[Event].[JX_Event_Payload]
```

and its summary's **Unsupported Feature Downgrades** names it: `JSON index (SQL Server 2025): [dbo].[Event].[JX_Event_Payload]`.
Ask each server what it built:

```bash
# from course10-module-01
LEARN_CONTAINER=learn-sqlserver-2025 ../lab-sql.sh sqlserver learn "SELECT TYPE_NAME(user_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Event') AND name = 'Payload'"
# → json
../lab-sql.sh sqlserver learn "SELECT TYPE_NAME(user_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Event') AND name = 'Payload'"
# → nvarchar
LEARN_CONTAINER=learn-sqlserver-2025 ../lab-sql.sh sqlserver learn "SELECT name, type_desc FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.Event') AND type_desc = 'JSON'"
# → JX_Event_Payload JSON
```

Same package, two servers, each with the most it can hold. The day the 2022 server is upgraded, the next deploy picks
the `Native` variant there too — but a column changing from `nvarchar(max)` to `json` is a real type change on a real
table, so plan that deploy like one.

### MariaDB — a VECTOR index on 11.8, recorded on 11.4

```bash
# from course10-module-01/newest/mariadb
schemaquench --ConfigFile:quench.settings.118.json --LogPath:"$PWD/logs"
schemaquench --ConfigFile:quench.settings.114.json --LogPath:"$PWD/logs"
```

11.8 exits `0` with ``Create index: Item.vx_item_embedding``; 11.4 exits `1` with
``Skipping VECTOR index (requires MariaDB 11.7, MySQL unsupported - downgraded): Item.vx_item_embedding``. On 11.8 the
index does the job it exists for — nearest neighbours by cosine distance:

```bash
# from course10-module-01
LEARN_CONTAINER=learn-mariadb-118 ../lab-sql.sh mariadb learn "INSERT INTO Item VALUES (1, 'anvil', VEC_FromText('[1,0,0]')), (2, 'hammer', VEC_FromText('[0.9,0.1,0]')), (3, 'apron', VEC_FromText('[0,0,1]'))"
LEARN_CONTAINER=learn-mariadb-118 ../lab-sql.sh mariadb learn "SELECT GROUP_CONCAT(Name ORDER BY VEC_DISTANCE_COSINE(Embedding, VEC_FromText('[1,0.05,0]'))) FROM Item"
# → anvil,hammer,apron
../lab-sql.sh mariadb learn "SELECT COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'learn' AND TABLE_NAME = 'Item' AND COLUMN_NAME = 'Embedding'"
# → varbinary(12)
```

### What the sandbox can't show: edition degrades

One more family of downgrades is gated by **edition**, not version. Before SQL Server 2016 SP1, table compression and
columnstore indexes were Enterprise features: on Express, Standard and Web they were not there at all. SchemaSmith
treats that like any other missing feature — under `warn` the table deploys without the compression or the
columnstore index and records a downgrade; under `fail` the deploy is refused before anything changes. (On 2012 and
2014 a nonclustered columnstore index makes its table read-only, so SchemaSmith skips it there until the server is
2016, rather than deploy a table nobody can write to.)

The sandbox cannot run it for you, and the reason is worth knowing rather than glossing over: SQL Server's container
images begin at 2017, and from 2016 SP1 onward every edition has compression and columnstore. The servers where this
downgrade fires — 2012, 2014 or 2016 RTM, on a non-Enterprise edition — exist only as Windows installs. If your fleet
still has one, this is the downgrade it will show you.

### Clean up the newest tier

```bash
# from docker
docker compose rm -sfv sqlserver-latest mariadb-latest
```

## What each folder is

| Path | Purpose |
| --- | --- |
| `postgres/package/` | `metric` table with an expression statistic (PG 14). |
| `postgres/quench.settings.warn.json` | Deploys to PG 12 with policy `warn` — degrade + record. |
| `postgres/quench.settings.fail.json` | Same target with policy `fail` — refuse. |
| `mysql/package/` | `Ledger` table with a CHECK constraint (MySQL 8.0.16). |
| `mariadb/package/` | `Ledger` table with a descending index (10.8) and an invisible index (10.6). |
| `sqlserver/package/` | `Account` table with a data-masked column (SQL Server 2016). |
| `<engine>/quench.settings*.json` | Targets that engine's floor tier; carries `UnsupportedFeaturePolicy`. |
| `newest/sqlserver/` | `Event` with a `json` / `NVARCHAR(MAX)` column variant and a JSON index; settings for SQL Server 2025 (11434) and 2022 (11433). |
| `newest/mariadb/` | `Item` with a `VECTOR(3)` / `VARBINARY(12)` column variant and a `VECTOR` index; settings for MariaDB 11.8 (13308) and 11.4 (13307). |

## Up next

Module 2 — **gate it yourself.** The engine adapts the DDL *it* generates; it will not rewrite
the scripts *you* wrote. `ShouldApplyExpression` is the seam, and the compatibility-level footgun
you just met on SQL Server is where it starts.
