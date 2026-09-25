# Course 4, Recipe 1 — The environment-aware schema (lab)

Goal: deploy **one package to two databases** and watch it take a different *physical shape* in each —
driven by **custom properties on the objects themselves**, combined with a deploy-time environment token.
This goes past Course 2's conditional-deployment module: there the gate read a single product token;
here each object carries its own `Extensions` metadata and the gate reads *that*.

You'll deploy `CookbookShop` with `prod.settings.json` (→ `cookbook_r1_prod`) and `nonprod.settings.json`
(→ `cookbook_r1_nonprod`) and see one `Customer` table land two ways:

- a **performance index** that exists **only in production**, and
- a **diagnostic column** that exists **only outside production**.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` plus
`prod.settings.json` and `nonprod.settings.json`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The Course 4 databases exist — run [`../course4-setup`](../course4-setup) once (creates `cookbook_r1_prod`
  and `cookbook_r1_nonprod`, among others).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Look at what drives the gates

Open `Tables/…Customer.json`. Two objects carry their own `Extensions`, and each gates itself on a mix
of its **own** custom property and the **deploy** token `{{DeployEnv}}`:

- The index `IX_Customer_Email` carries `"Extensions": { "Purpose": "PerfOnly" }` and
  `"ShouldApplyExpression": "'{{Purpose}}' = 'PerfOnly' AND '{{DeployEnv}}' = 'Production'"`.
  `{{Purpose}}` is the index's *own* bare-name custom property; `{{DeployEnv}}` is the product token.
- The column `DebugPayload` carries `"Extensions": { "Audience": "NonProdOnly" }` and
  `"ShouldApplyExpression": "'{{Audience}}' = 'NonProdOnly' AND '{{DeployEnv}}' <> 'Production'"`.

`{{DeployEnv}}` defaults to `Production` in `Product.json`. `nonprod.settings.json` overrides it to
`NonProd` (and points `{{TargetDb}}` at the non-prod database). Same package, two settings files.

## Step 2: Deploy to production

```bash
cd <engine>
schemaquench --ConfigFile:prod.settings.json
```

```
Begin Quench of CookbookShop
[…].[cookbook_r1_prod] Begin Quench
    Adding new table [dbo].[Customer]
[…].[cookbook_r1_prod] Successfully Quenched
```

The production shape: the perf index **is** built, the diagnostic column is **not**.

```bash
# SQL Server
cd ..            # back to the lab folder
../lab-sql.sh sqlserver cookbook_r1_prod "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.Customer')"
# → CustomerId, Email  (no DebugPayload)
# index IX_Customer_Email is present
```

## Step 3: Deploy the SAME package to non-prod

```bash
cd <engine>       # back into the engine folder
schemaquench --ConfigFile:nonprod.settings.json
```

Now the calls flip — the perf index is **skipped**, the diagnostic column **is** added:

```bash
# PostgreSQL
cd ..            # back to the lab folder
../lab-sql.sh postgres cookbook_r1_nonprod "SELECT column_name FROM information_schema.columns WHERE table_name='customer' ORDER BY ordinal_position"
# → customerid, email, debugpayload
../lab-sql.sh postgres cookbook_r1_nonprod "SELECT indexname FROM pg_indexes WHERE tablename='customer'"
# → pk_customer  (no ix_customer_email)
```

Two databases, two physical shapes, one source package. The metadata that decided each one lives on the
object it governs — not in a second copy of the file.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Object names | bare (`dbo.Customer`) | folded lowercase (`public.customer`) | backticked (`` `Customer` ``) |
| Diagnostic column type | `NVARCHAR(MAX)` | `TEXT` | `TEXT` |
| Database switch | `{{TargetDb}}` in `DatabaseIdentificationScript` | same | same (schema = database) |
| Gate predicate | `'{{Purpose}}' = 'PerfOnly' AND '{{DeployEnv}}' = 'Production'` | same | same |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The mechanism is identical on all four engines — a component's own `Extensions` becomes a bare token in
its `ShouldApplyExpression`, the deploy token rides alongside. Only the dialect's names and types differ.

## The principle

Conditional deployment you met in Course 2 gated on a single product token. Here the *object* carries the
intent — this index is a perf-only index, this column is non-prod-only — as a custom property right where
the object is defined. The deploy token says *which environment*; the object's own metadata says *what it
is*. Put them together in one `ShouldApplyExpression` and the same package hardens into the right shape for
every target, with the decision living on the thing being decided.
