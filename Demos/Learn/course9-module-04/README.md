# Course 9 · Module 4 — Each service, its own pipeline

Module 3 gave each service a clean, organized package and file-less connection config. This module wires those packages into CI. The result is three independent pipelines — one per service — each path-filtered to its own folder, each deploying on its own cadence, none aware of the others.

## What you're learning

**One shape, three independent pipelines.** Each service gets an identical CI structure: a WhatIf gate on pull request, a real deploy on merge to main, and path filtering so a change to `orders` never triggers a `catalog` or `sessions` run. In production each service is its own repository with its own `.github/workflows/deploy.yml`; the lab co-locates all three to keep the parity visible.

**WhatIf-PR gating.** Every PR that touches a service folder runs a WhatIf deploy driven by a dedicated settings file (`quench.settings.whatif.json`, which sets `"WhatIfONLY": true`). The job generates the full artifact output — exactly what _would_ change — and exits 0 without applying anything. Reviewers see the diff before it hits the database; merge only happens after human review of what the pipeline previewed.

**CI config from secrets.** The same `SmithySettings_Target__*` environment variables from Module 3 are now sourced from GitHub Actions secrets. No credentials in the repo. The settings files for each service are identical between local dev and CI — only the environment changes, and the environment is always injected, never committed.

## Prerequisites

- Sandbox up and `course9-setup` run (creates the `orders`, `catalog`, `sessions` databases).
- `schemaquench --version` **2.7.0** or later on your PATH.

## The pipeline files

Each engine folder contains a `ci/deploy.yml`. These are example files — they live under `<engine>/ci/`, not under `.github/workflows/`, so they don't become active workflows in the labs repo.

Open the three files side by side. The structure is identical:

| | `sqlserver/ci/deploy.yml` | `postgres/ci/deploy.yml` | `mysql/ci/deploy.yml` |
|---|---|---|---|
| **name** | Deploy Orders (SQL Server) | Deploy Catalog (PostgreSQL) | Deploy Sessions (MySQL) |
| **paths filter** | `sqlserver/**` | `postgres/**` | `mysql/**` |
| **secrets prefix** | `ORDERS_` | `CATALOG_` | `SESSIONS_` |
| **Port secret** | — | `CATALOG_DB_PORT` | `SESSIONS_DB_PORT` |

The `whatif` job fires on `pull_request` and runs `--ConfigFile:quench.settings.whatif.json`; the `deploy` job fires on `push` to `main` and runs `--ConfigFile:quench.settings.json`. The `if:` condition on each job enforces the split — GitHub sees both jobs defined in the same file but only one runs per event. Using a WhatIf **settings file** (rather than a command-line flag) means the preview gate behaves identically on every SchemaSmith version — there's no way for a PR job to accidentally run a real deploy.

## Try it locally

Run the WhatIf gate from the command line before CI does it. Set environment variables exactly as in Module 3, then run from each service directory with the WhatIf settings file.

### SQL Server (orders)

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost,11433"
export SmithySettings_Target__User="sa"
export SmithySettings_Target__Password="Learn!Passw0rd"

cd sqlserver
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost,11433"
$env:SmithySettings_Target__User = "sa"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"

cd sqlserver
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD\logs"
```

### PostgreSQL (catalog)

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="15432"
export SmithySettings_Target__User="postgres"
export SmithySettings_Target__Password="Learn!Passw0rd"

cd postgres
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "15432"
$env:SmithySettings_Target__User = "postgres"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"

cd postgres
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD\logs"
```

### MySQL (sessions)

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="13306"
export SmithySettings_Target__User="root"
export SmithySettings_Target__Password="Learn!Passw0rd"

cd mysql
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "13306"
$env:SmithySettings_Target__User = "root"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"

cd mysql
schemaquench --ConfigFile:quench.settings.whatif.json --LogPath:"$PWD\logs"
```

Each run exits 0 and writes artifacts showing what would change. Nothing is applied. To run the real deploy, use the normal settings file instead:

macOS / Linux:
```bash
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

## What to look for

- **Path filtering is the independence proof.** Edit a file under `sqlserver/` — only the Orders pipeline triggers. The Catalog and Sessions pipelines don't run. That's the boundary.
- **WhatIf artifacts are the review surface.** Open `logs/` after a WhatIf run. The artifact files show the exact SQL that would execute. In CI these upload as a GitHub Actions artifact so reviewers can download and inspect them before approving merge.
- **Secret names encode the service boundary.** `ORDERS_DB_SERVER`, `CATALOG_DB_SERVER`, `SESSIONS_DB_SERVER` — three separate secrets, three separate databases, three separate teams who can rotate them independently.

## Up next

Module 5 is the capstone: an independent release for one service and a cross-service reference-data dependency handled correctly — what to do when `sessions` needs a lookup table that `catalog` owns, and how to deploy that dependency without coupling their pipelines.
