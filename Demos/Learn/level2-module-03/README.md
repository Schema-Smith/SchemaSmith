# Course 2, Module 3 — Conditional deployment: the right thing in the right place (lab)

Goal: deploy **one package to two environments** and watch it adapt per target at **three
granularities**. You'll deploy `OrderService` with `dev.settings.json` and `prod.settings.json`
(which differ only by overriding the `{{Environment}}` token) and see three gates make different calls:

1. **Object-level `ShouldApplyExpression`** — an analytics index that deploys only in Production.
2. **Folder-level `ShouldApplyExpression`** — a `TestData` folder that runs only in Development.
3. **Per-script sentinel** — a backfill migration that decides at runtime to skip itself.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` plus
`dev.settings.json` and `prod.settings.json`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later
  — folder-level `ShouldApplyExpression` token resolution shipped in v2.1.0).

## Step 1: Look at the three gates

- **Object** — `Tables/…Orders.json`: the `IX_Orders_Region` index carries
  `"ShouldApplyExpression": "'{{Environment}}' = 'Production'"` (a boolean predicate).
- **Folder** — `Templates/Main/Template.json`: the `ScriptFolders` array gates the `TestData` folder
  with `"ShouldApplyExpression": "SELECT CASE WHEN '{{Environment}}' = 'Development' THEN 1 ELSE 0 END"`
  (a scalar SELECT). Note `After Scripts` is listed explicitly too — a non-empty `ScriptFolders`
  **replaces** the default folder set, so every folder you want must be declared.
- **Script** — `After Scripts/Backfill Legacy Regions.sql`: a run-once migration that raises the
  `SCHEMASMITH: SHOULD NOT APPLY` sentinel when there's no legacy table to back-fill.

## Step 2: Deploy to dev

```bash
cd <engine>
schemaquench --ConfigFile:dev.settings.json
```

```
Adding new table [dbo].[Orders]
Skipped (ShouldNotApply): ...After Scripts\Backfill Legacy Regions.sql
Successfully Quenched
```

What happened, per tier:

- **Object:** `IX_Orders_Region` is **not** created (`'Development' = 'Production'` is false).
- **Folder:** `TestData` **runs** — `Orders` gets 3 sample rows.
- **Script:** the backfill finds no `LegacyOrders` table and raises the sentinel — logged
  `Skipped (ShouldNotApply)`, recorded as a completed run-once migration (won't retry).

Verify:

```bash
# SQL Server: SELECT COUNT(*) FROM dbo.Orders;  -- 3
#             index IX_Orders_Region is absent
cd ..                                  # back to the lab folder
../lab-sql.sh postgres learn "SELECT count(*) FROM public.orders"
cd <engine>                            # back to the engine folder
```

## Step 3: Deploy the SAME package to prod

```bash
schemaquench --ConfigFile:prod.settings.json
```

```
Skipping folder 'TestData' — ShouldApplyExpression evaluated false
Creating index [dbo].[Orders].[IX_Orders_Region]
Successfully Quenched
```

Now the calls flip:

- **Object:** `IX_Orders_Region` **is** created (`'Production' = 'Production'`).
- **Folder:** `TestData` is **skipped** — logged, no sample rows added.
- **Script:** the backfill was already recorded complete on the dev run (run-once tracking is
  per-database), so it isn't re-attempted here.

Verify the index now exists:

```bash
# SQL Server: index IX_Orders_Region is present on dbo.Orders
cd ..                                  # back to the lab folder
../lab-sql.sh postgres learn "SELECT indexname FROM pg_indexes WHERE tablename='orders' AND indexname='ix_orders_region'"
```

(Both settings target the same `learn` database, so the dev sample rows persist into the prod
observation — in real life dev and prod are separate servers. The point is which gate fires where,
shown by the per-deploy log and the index appearing only after the prod run.)

## Which gate, when

- **One object, decision is a SQL expression** → object `ShouldApplyExpression` (boolean predicate).
- **A whole folder of scripts** → folder `ShouldApplyExpression` (scalar SELECT).
- **The decision needs the script to look around first** → the runtime sentinel.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Object predicate | `'{{Environment}}' = 'Production'` | same | same |
| Folder gate | `SELECT CASE WHEN … THEN 1 ELSE 0 END` | same | same |
| Sentinel raise | `RAISERROR('SCHEMASMITH: SHOULD NOT APPLY', 16, 1)` (sev ≥ 11; add `RETURN`) | `RAISE EXCEPTION 'SCHEMASMITH: SHOULD NOT APPLY'` in a `DO $$ … $$` block | `SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'SCHEMASMITH: SHOULD NOT APPLY'` |
| Runtime branching | inline in the batch | a `DO` block | inside a short stored routine — MySQL can't branch in a plain batch, so the backfill creates a throwaway procedure, `CALL`s it, and drops it |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The three mechanisms are identical across engines; only the dialect's way of writing a predicate, a
scalar SELECT, and a conditional raise differs.

## The principle

One package adapts to every target instead of forking into a package per environment. Gate at the
size that fits: a single object with a `ShouldApplyExpression` predicate, a whole folder with a
folder-level `ShouldApplyExpression`, or an individual script that inspects the target at runtime and
raises the `SCHEMASMITH: SHOULD NOT APPLY` sentinel. The analytics index lands only where it earns
its keep, the test data only where it belongs, the backfill only when there's something to move —
all from one source of truth, no drift between environments.
