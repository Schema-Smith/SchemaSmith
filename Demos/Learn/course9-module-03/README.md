# Course 9 · Module 3 — Organizing polyglot services

Three mechanics in one lab: each service is its own repo in production, tables are organized into subfolders so the package layout mirrors your mental model, and connection credentials stay out of every settings file — injected per environment instead.

## What you're learning

**Per-service repos are the production norm.** In a real polyglot deployment each service — `orders`, `catalog`, `sessions` — lives in its own repository with its own `quench.settings.json`, its own deploy cadence, and its own secrets. The lab co-locates all three because that's how lab bundles ship; a real setup would be three separate repos.

**Subfolder organization.** Tables live under `Tables/Core/` (the service's own tables) and `Tables/Reference/` (lookup tables the service reads). SchemaSmith discovers table JSON recursively with `SearchOption.AllDirectories`, so the subfolder structure is purely for humans — deployment is unaffected. Organize however clarifies your package's intent.

**File-less connection config.** Each `quench.settings.json` declares the target database and package path but contains no credentials. Connection details are injected at deploy time via `SmithySettings_` environment variables. The same settings file deploys to dev, staging, and production — only the env vars change.

## Prerequisites

- Sandbox up and `course9-setup` run (creates the `orders`, `catalog`, `sessions` databases).
- `schemaquench --version` >= 2.7.0.

> SchemaSmith converges each database to exactly what the package declares, so this module stands alone — you don't need prior modules deployed first. It is the same product as Module 2's package, so if Module 2 is deployed, this deploy drops Module 2's table (`[dbo].OrderEvent`, `public.price_history`, `PageHit`).

## Run the lab

Each service gets its own connection, so set one service's variables and deploy it before moving to the next. The variables share names across engines, and setting the next service's values replaces the last. Go in the order shown: SQL Server sets no `Port`, so run it first (or in a fresh shell). `$PWD` resolves in both shells.

### Orders — SQL Server

macOS / Linux:
```bash
# from course9-module-03/sqlserver
export SmithySettings_Target__Server="localhost,11433"
export SmithySettings_Target__User="sa"
export SmithySettings_Target__Password="Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
# from course9-module-03/sqlserver
$env:SmithySettings_Target__Server = "localhost,11433"
$env:SmithySettings_Target__User = "sa"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

### Catalog — PostgreSQL

macOS / Linux:
```bash
# from course9-module-03/postgres
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="15432"
export SmithySettings_Target__User="postgres"
export SmithySettings_Target__Password="Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
# from course9-module-03/postgres
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "15432"
$env:SmithySettings_Target__User = "postgres"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

### Sessions — MySQL

macOS / Linux:
```bash
# from course9-module-03/mysql
export SmithySettings_Target__Server="localhost"
export SmithySettings_Target__Port="13306"
export SmithySettings_Target__User="root"
export SmithySettings_Target__Password="Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
# from course9-module-03/mysql
$env:SmithySettings_Target__Server = "localhost"
$env:SmithySettings_Target__Port = "13306"
$env:SmithySettings_Target__User = "root"
$env:SmithySettings_Target__Password = "Learn!Passw0rd"
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD\logs"
```

Each run exits 0. Read its output (also kept in the engine folder's `logs/`) to confirm SchemaSmith found the tables under both `Core/` and `Reference/` subfolders:

```text
SQL Server:  [localhost,11433].[orders]         Adding new table [dbo].[Customer]
             [localhost,11433].[orders]         Adding new table [dbo].[OrderItem]
             [localhost,11433].[orders]         Adding new table [dbo].[SalesOrder]
             [localhost,11433].[orders]         Adding new table [dbo].[OrderStatus]
PostgreSQL:  [localhost].[catalog]         Create new table public.category
             [localhost].[catalog]         Create new table public.product
             [localhost].[catalog]         Create new table public.product_status
MySQL:       [localhost].[sessions]       Create table `EventCategory`
             [localhost].[sessions]       Create table `Event`
             [localhost].[sessions]       Create table `Session`
```

(On a re-run the tables already exist and these lines do not appear.)

## What to look for

Open `sqlserver/package/Templates/Main/Tables/` — `Core/` holds `dbo.Customer.json`, `dbo.SalesOrder.json`, and `dbo.OrderItem.json`; `Reference/` holds `dbo.OrderStatus.json`. Same pattern across the other two engines: `postgres/` has `public.category.json` and `public.product.json` in `Core/` and `public.product_status.json` in `Reference/`; `mysql/` has `Session.json` and `Event.json` in `Core/` and `EventCategory.json` in `Reference/`. The run log lists every table regardless of its subfolder — the tree structure is for you, not the engine.

## Up next

Module 4 takes the same three packages into a CI pipeline: WhatIf gating, per-environment variable injection in GitHub Actions, and a per-service deploy workflow. The organizational foundation you just built is what that pipeline is built on.
