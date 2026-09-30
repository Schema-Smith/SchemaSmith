# Course 10 · Module 1 — Let the engine adapt

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

```
cd postgres
```

**Warn (default) — degrade and record:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.warn.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.warn.json --LogPath:"$PWD\logs"
```

Exit 0. The `metric` table deploys, but the expression statistic `ST_metric_expr` cannot exist
on PostgreSQL 12 (it needs 14). SchemaSmith skips it and records a `downgraded` row. Read the
deployment summary's **Unsupported Feature Downgrades** section.

**Fail — refuse instead:**

macOS / Linux:
```
schemaquench --ConfigFile:quench.settings.fail.json --LogPath:"$PWD/logs"
```

Windows:
```
schemaquench --ConfigFile:quench.settings.fail.json --LogPath:"$PWD\logs"
```

Non-zero exit. Same package, same target — but now SchemaSmith aborts with a message naming the
feature and the version it requires, rather than deploying a silently-degraded schema.

---

### MySQL — a CHECK the floor can't keep

```
cd mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Exit 0. The `Ledger` table deploys, but MySQL 5.7 parses-and-ignores `CHECK` constraints (they
need 8.0.16), so SchemaSmith emits the table without the check and records a downgrade. Deploy
the identical package to MySQL 8.0 and the check is created — same package, target decides.

To see the refusal instead, flip the knob for one run:
```
SmithySettings_Target__UnsupportedFeaturePolicy=fail schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

---

### MariaDB — two downgrades in one deploy

```
cd mariadb
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Exit 0. `Ledger` deploys, but MariaDB 10.2 is below both features it declares: the **descending**
index key part (10.8) is stored ascending, and the **invisible** index (10.6) is created visible.
Each records its own `downgraded` row — two lines in the summary.

---

### SQL Server — the honest contrast (no degrade)

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Exit 0. The `Account` table's `Email` column declares dynamic data masking — a SQL Server 2016
feature — and the target is `learn_2008`, a **compatibility level 100** database. On the other
engines that mismatch degrades. Here it does **not**: the `Unsupported Feature Downgrades` section
is absent, and the mask is applied. SQL Server feature support gates on the server *binary*
(this instance is 2022), not the database's compatibility level. The 100-level database changes
how SchemaSmith *encodes* its model on the wire (Module 5's topic), not which features it can
deploy — and that gap between compat level and server version is exactly the footgun Module 3 turns on.

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

## Up next

Module 2 — **gate it yourself.** The engine adapts the DDL *it* generates; it will not rewrite
the scripts *you* wrote. `ShouldApplyExpression` is the seam, and the compatibility-level footgun
you just met on SQL Server is where it starts.
