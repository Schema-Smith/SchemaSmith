# Course 3, Module 2 — CI/CD (lab)

Goal: run a schema change through a real pipeline shape — **base -> WhatIf-on-PR -> staging -> prod** —
entirely on the sandbox, and prove the teaching point: **one built artifact deploys to every
environment, and the only thing that changes per environment is a handful of `SmithySettings_*`
environment variables.** No per-environment settings files. The commands the pipeline runs ARE the
contract; the CI YAML is just a wrapper.

The change under review is one small additive edit on top of Module 1's package: a **non-unique index
on `Customer.Email`** (`IX_Customer_Email`). That keeps the WhatIf delta clean — a single
`Create index ...` line — so you can see exactly what the PR check reports.

## Layout

```
course3-module-02/
  starter/<engine>/      current production state — the Module 1 package (Customer WITH LoyaltyTier)
  solution/<engine>/     the PR under review — starter + IX_Customer_Email on Customer.Email
  pipeline/<engine>/     pipeline.sh + pipeline.ps1 — runnable twins that drive the whole flow
```

`<engine>` is `sqlserver`, `postgres`, `mysql`, or `mariadb`. Each `starter/` and `solution/` carries its own
`Package/` plus a single shared **`base.settings.json`** — connection details and the package-path
default. Everything that varies per environment is injected as an env var at run time.

## How per-environment config works (the teaching point)

The package is **environment-agnostic**. Two pieces make that possible:

- **Target database via a script token.** `Product.json` declares a `TargetDb` script token
  (defaulting to `ordersservice_dev`), and `Template.json`'s `DatabaseIdentificationScript` selects
  that token instead of a hardcoded name — e.g. on SQL Server
  `SELECT name FROM sys.databases WHERE name = '{{TargetDb}}'`. The `ValidationScript` references the
  same token. Override it per environment with `SmithySettings_ScriptTokens__TargetDb`.
- **Preview vs. apply via `WhatIfONLY`.** The PR check sets `SmithySettings_WhatIfONLY=true` to run
  the full deploy logic without changing anything; the deploy steps leave it `false`.

Environment-variable mapping follows the standard convention: prefix `SmithySettings_`, and `__`
(double underscore) for nesting. So `Target:Server` -> `SmithySettings_Target__Server`,
`ScriptTokens:TargetDb` -> `SmithySettings_ScriptTokens__TargetDb`, `WhatIfONLY` ->
`SmithySettings_WhatIfONLY`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and all four engines are healthy.
- The target databases exist. Run the Course 3 setup once (idempotent — safe to re-run):

  ```bash
  pwsh ../course3-setup/setup-environments.ps1     # or: ../course3-setup/setup-environments.sh
  ```

  It creates the twelve `ordersservice_{dev,staging,prod}` databases and reports `PASS` for each.
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Run the pipeline

Pick your engine and shell. The scripts are real twins — same flow, native to each shell:

```bash
cd pipeline/<engine>      # sqlserver | postgres | mysql | mariadb
./pipeline.sh             # bash (Git Bash, Linux, macOS)
```

```powershell
cd pipeline\<engine>
.\pipeline.ps1            # PowerShell
```

The script runs four steps:

1. **Deploy the base (starter) to `ordersservice_staging`** — establishes the current production
   state the PR branches from.
2. **WhatIf the solution against staging** — the PR check. `SmithySettings_WhatIfONLY=true` runs the
   full deploy logic and prints the index it *would* create, then applies nothing. The script proves
   it by reading the catalog right after: the index is `ABSENT`.
3. **Deploy the solution to staging** — WhatIf passed, so apply it for real.
4. **Promote the same artifact to `ordersservice_prod`** — identical package and command; only
   `SmithySettings_ScriptTokens__TargetDb` changed.

At the end the script confirms `IX_Customer_Email` is present in both staging and prod.

## What you'll see — the WhatIf gate (Step 2)

The PR check runs the full deploy logic and reports the change without touching the database. On SQL
Server, the preview includes the exact DDL:

```
[localhost,11433].[ordersservice_staging]   Creating index [dbo].[Customer].[IX_Customer_Email]
[localhost,11433].[ordersservice_staging]   CREATE NONCLUSTERED INDEX [IX_Customer_Email] ON [dbo].[Customer] ([Email]) WITH (DATA_COMPRESSION=NONE);
[localhost,11433].[ordersservice_staging] Successfully Quenched
```

The per-engine WhatIf wording differs, but each one previews the same single change:

| Engine     | WhatIf preview line |
| ---------- | ------------------- |
| SQL Server | `CREATE NONCLUSTERED INDEX [IX_Customer_Email] ON [dbo].[Customer] ([Email]) ...` |
| PostgreSQL | `CREATE INDEX "ix_customer_email" ON "public"."Customer" USING btree ("Email") ...` |
| MySQL      | ``CREATE INDEX `IX_Customer_Email` ON `ordersservice_staging`.`Customer` (Email) USING BTREE`` |
| MariaDB    | ``CREATE INDEX `IX_Customer_Email` ON `ordersservice_staging`.`Customer` (Email) USING BTREE`` |

*MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native
package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics
(invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for
you.*

Immediately after the WhatIf step the script checks the catalog and prints `ABSENT` — WhatIf ran the
whole deploy path and changed nothing. That's the gate: if the change can't be planned cleanly, the
PR check fails *here*, before anything merges.

## Confirm it by hand

Want to verify the catalog yourself at any point:

```bash
# SQL Server
../lab-sql.sh sqlserver ordersservice_prod "SELECT name FROM sys.indexes WHERE name='IX_Customer_Email'"

# PostgreSQL
../lab-sql.sh postgres ordersservice_prod "SELECT indexname FROM pg_indexes WHERE indexname='ix_customer_email'"

# MySQL
../lab-sql.sh mysql ordersservice_prod "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='ordersservice_prod' AND INDEX_NAME='IX_Customer_Email'"

# MariaDB
../lab-sql.sh mariadb ordersservice_prod "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='ordersservice_prod' AND INDEX_NAME='IX_Customer_Email'"
```

After a full run, each returns the index name on both `ordersservice_staging` and
`ordersservice_prod`.

## Re-run safely

Run the whole pipeline again. Step 1 re-establishes the base (which drops the index, since the
starter package doesn't declare it), Step 2's WhatIf shows it would be re-added, and Steps 3–4 add it
back. The final state is identical: index present on staging and prod. Every quench converges to the
declared state and stops — that idempotence is exactly what makes promoting the *same* artifact across
environments safe.

## The principle

The pipeline isn't magic — it's the four commands above, each one a `schemaquench` invocation whose
behavior is shaped entirely by `SmithySettings_*` environment variables. Build the package once,
review the change with WhatIf on the PR, then deploy that same package to staging and prod by changing
nothing but the env vars. One artifact, every environment, gated by a preview that runs the full
deploy logic without touching a row.
