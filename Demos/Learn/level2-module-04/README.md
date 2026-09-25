# Course 2, Module 4 — Script tokens: adjustable dyes (lab)

Goal: deploy **one package to two environments** without editing a single file between them. You'll
define values as tokens, ship the package with dev defaults, and override them for prod from a
settings file. You'll also see a token that computes its own value live against the target server.
Proof is a `DeploymentLog` table that ends up with two rows — one stamped `Development / 2.4.0-dev`,
one stamped `Production / 2.4.0` — written by the identical package.

A **script token** is `{{TokenName}}` in your script. Its value is resolved per run, in layers, with
the most specific winning:

- **`Product.json` → `ScriptTokens`** — the package's baseline defaults.
- **Settings file → `ScriptTokens`** — per-run overrides, no package edit.
- **Environment variables** (`SmithySettings_ScriptTokens__TokenName`) — the CI hook, same mechanism.

Overrides can only change tokens the package already declares — the package owns the contract, the
environment fills in the values.

This lab ships one product, `BillingService`, on all four engines. Each engine folder
(`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) has the full `Package/` plus **two** settings files —
`dev.settings.json` (rides the package defaults) and `prod.settings.json` (overrides two tokens).

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (`./verify-sandbox.sh` /
  `.\verify-sandbox.ps1` — all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Look at the token contract

Open `<engine>/Package/Product.json`. The `ScriptTokens` block declares three tokens (SQL Server
shown):

```json
"ScriptTokens": {
  "Environment": "Development",
  "ReleaseVersion": "2.4.0-dev",
  "EngineVersion": "<*Query*>SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128))"
}
```

`Environment` and `ReleaseVersion` are plain string defaults. `EngineVersion` is a **`<*Query*>`
token** — its value isn't fixed; it's the result of running that SQL against the target at deploy
time.

Now compare the two settings files. `dev.settings.json` has no `ScriptTokens` section — it uses the
package defaults. `prod.settings.json` overrides the two static tokens:

```json
"ScriptTokens": {
  "Environment": "Production",
  "ReleaseVersion": "2.4.0"
}
```

The after-script `Templates/Main/After Scripts/Stamp Deployment [ALWAYS].sql` stamps all four
resolved values (`{{Environment}}`, `{{ProductName}}`, `{{ReleaseVersion}}`, `{{EngineVersion}}`)
into `DeploymentLog`. The `[ALWAYS]` tag re-runs it every quench; an idempotency guard keeps it from
duplicating a row for an environment that's already stamped.

## Step 2: Deploy with the dev settings

```bash
cd <engine>
schemaquench --ConfigFile:dev.settings.json
```

SchemaQuench echoes the resolved tokens up front, then creates the table and runs the stamp script:

```
  Product Script Tokens:
    Environment: Development
    ReleaseVersion: 2.4.0-dev
    ProductName: BillingService

Resolving Product Level Query Tokens
[localhost,11433].[learn]         Adding new table [dbo].[DeploymentLog]
[localhost,11433].[learn]     Quenching .\Package\Templates\Main\After Scripts\Stamp Deployment [ALWAYS].sql
[localhost,11433].[learn] Successfully Quenched
```

`Resolving Product Level Query Tokens` is the `<*Query*>` firing — SchemaQuench asked the server for
its version before any script ran.

## Step 3: Deploy the SAME package with the prod settings

No file edits. Just a different settings file:

```bash
schemaquench --ConfigFile:prod.settings.json
```

```
  Product Script Tokens:
    Environment: Production
    ReleaseVersion: 2.4.0
    ProductName: BillingService
```

## Step 4: Prove the dye took

Read the table back — two rows, written by one package:

```bash
# SQL Server (from a SQL client):
#   SELECT Environment, ProductName, ReleaseVersion, EngineVersion FROM dbo.DeploymentLog ORDER BY Environment;
../lab-sql.sh postgres learn "SELECT environment, product_name, release_version, engine_version FROM public.deploymentlog ORDER BY environment"
```

SQL Server result:

```
Environment    ProductName       ReleaseVersion    EngineVersion
-------------  ----------------  ---------------   -------------
Development    BillingService    2.4.0-dev         16.0.4260.1
Production     BillingService    2.4.0             16.0.4260.1
```

`Environment` and `ReleaseVersion` differ because the settings file overrode those tokens.
`EngineVersion` is identical here because both runs hit the same sandbox engine — in real life,
pointed at a dev server and a prod server, the query token would report each server's own version.
Re-run either deploy and no new row appears: the stamp is idempotent per environment.

## Step 5 (optional): override from an environment variable

Same package, no settings edit — override a token from the shell, the way a CI pipeline would:

```bash
# Linux/macOS
export SmithySettings_ScriptTokens__ReleaseVersion="2.4.0-ci.99"
schemaquench --ConfigFile:dev.settings.json
```

```powershell
# Windows (PowerShell)
$env:SmithySettings_ScriptTokens__ReleaseVersion = "2.4.0-ci.99"
schemaquench --ConfigFile:dev.settings.json
```

The stamp is **insert-once per `Environment`** (`WHERE NOT EXISTS` on the environment name), so
overriding `ReleaseVersion` on its own does **not** rewrite the existing `Development` row — the
override only lands on a row that isn't there yet. To watch `2.4.0-ci.99` stamp in, clear the
`Development` row first (or point at a fresh `Environment` label) and re-run. Unset the variable when
you're done. Whether a re-deploy overwrites, ignores, or *appends* a history row is a choice your
stamp script makes, not the token engine's — this demo deliberately keeps one row per environment.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Engine-version query | `SERVERPROPERTY('ProductVersion')` → `16.0.4260.1` | `split_part(current_setting('server_version'),' ',1)` → `16.13` | `VERSION()` → `8.0.45` |
| New-table wording | `Adding new table [dbo].[DeploymentLog]` | `Create new table public.deploymentlog` | ``Create table `DeploymentLog` `` |
| Identifier case | mixed-case, bracketed | folded to lowercase | backticked |
| Idempotent stamp | `INSERT … WHERE NOT EXISTS` | `INSERT … WHERE NOT EXISTS` | `INSERT IGNORE` (MySQL forbids referencing an insert's target table in a subquery, so idempotency rides on the `Environment` primary key) |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The token mechanics are identical across all four: declare in `Product.json`, override per run, and
let `<*Query*>` compute live values against the target. Only the SQL dialect inside the tokens and the
engine's DDL wording differ.

## The principle

One package, many environments. A script token is a name you write once and a value you resolve per
target — from the package's defaults, a settings-file override, or an environment variable, with the
most specific winning. Static tokens swap a database name or a version stamp without editing a file;
a `<*Query*>` token computes its value live against the server it's deploying to, so the answer is
always current. The package owns which tokens exist; the environment owns what they become. Three
copies of a deploy that drift apart become one package that fits every forge you carry it to.
