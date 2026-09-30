# Course 9 · Module 3 — Organizing polyglot services

Three mechanics in one lab: each service is its own repo in production, tables are organized into subfolders so the package layout mirrors your mental model, and connection credentials stay out of every settings file — injected per environment instead.

## What you're learning

**Per-service repos are the production norm.** In a real polyglot deployment each service — `orders`, `catalog`, `sessions` — lives in its own repository with its own `quench.settings.json`, its own deploy cadence, and its own secrets. The lab co-locates all three because that's how lab bundles ship; a real setup would be three separate repos.

**Subfolder organization.** Tables live under `Tables/Core/` (the service's own tables) and `Tables/Reference/` (lookup tables the service reads). SchemaSmith discovers table JSON recursively with `SearchOption.AllDirectories`, so the subfolder structure is purely for humans — deployment is unaffected. Organize however clarifies your package's intent.

**File-less connection config.** Each `quench.settings.json` declares the target database and package path but contains no credentials. Connection details are injected at deploy time via `SmithySettings_` environment variables. The same settings file deploys to dev, staging, and production — only the env vars change.

## Prerequisites

- Sandbox up and `course9-setup` run (creates the `orders`, `catalog`, `sessions` databases).
- `schemaquench --version` >= 2.7.0.

> SchemaSmith converges each database to exactly what the package declares, so this module stands alone — you don't need prior modules deployed first.

## Run the lab

### Set environment variables

**SQL Server (orders)**

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost,11433"
export SmithySettings_Target__User="sa"
export SmithySettings_Target__Password="Learn!Passw0rd"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost,11433"
$env:SmithySettings_Target__User = "sa"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
```

**PostgreSQL (catalog)**

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="15432"
export SmithySettings_Target__User="postgres"
export SmithySettings_Target__Password="Learn!Passw0rd"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "15432"
$env:SmithySettings_Target__User = "postgres"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
```

**MySQL (sessions)**

macOS / Linux:
```bash
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="13306"
export SmithySettings_Target__User="root"
export SmithySettings_Target__Password="Learn!Passw0rd"
```

Windows PowerShell:
```powershell
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "13306"
$env:SmithySettings_Target__User = "root"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
```

### Deploy each service

Run one command from each service directory — once from `sqlserver`, once from `postgres`, once from `mysql`. `$PWD` resolves in both shells.

macOS / Linux:
```bash
cd sqlserver          # then postgres, then mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd sqlserver          # then postgres, then mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Each run exits 0. Inspect the artifacts to confirm SchemaSmith found all tables under both `Core/` and `Reference/` subfolders.

## What to look for

Open `sqlserver/package/Templates/Main/Tables/` — `Core/` holds `dbo.Customer.json`, `dbo.SalesOrder.json`, and `dbo.OrderItem.json`; `Reference/` holds `dbo.OrderStatus.json`. Same pattern across the other two engines. SchemaSmith's artifact output lists all four tables regardless of their subfolder — the tree structure is for you, not the engine.

## Up next

Module 4 takes the same three packages into a CI pipeline: WhatIf gating, per-environment variable injection in GitHub Actions, and a per-service deploy workflow. The organizational foundation you just built is what that pipeline is built on.
