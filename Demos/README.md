# SchemaSmith Demos

Demo database schema packages for SQL Server, PostgreSQL, MySQL, and MariaDB, deployed by [SchemaSmith](https://github.com/Schema-Smith/SchemaSmith) (SchemaQuench).

## Demo Products

| Product | SQL Server | PostgreSQL | MySQL | MariaDB |
|---------|:---:|:---:|:---:|:---:|
| AdventureWorks | Done | Done | Done | Done |
| Chinook | Done | Done | Done | Done |
| Northwind | Done | Done | Done | Done |
| Sakila | Done | Done | Done | Done |
| TenantCRM | Done | Done | n/a¹ | n/a¹ |

¹ Schema templates (schema-per-tenant fan-out) are SQL Server + PostgreSQL only.

TenantCRM is a hand-authored multi-tenant CRM showcasing the **schema-per-tenant** pattern — one schema-template definition fanned out across an arbitrary number of tenant schemas inside a single database. See the [SQL Server](SqlServer/TenantCRM/README.md) or [PostgreSQL](PostgreSQL/TenantCRM/README.md) demo READMEs for the walkthrough.

## Conditional Deployment Demos

Demonstrations of `ShouldApplyExpression` (conditional deployment) on real engines. See [`Conditional/`](Conditional/) for the five demos:

- [`Conditional/PostgreSQL-VersionGate`](Conditional/PostgreSQL-VersionGate) — PG15 ↔ PG18, gating a virtual generated column on PG18+
- [`Conditional/MySQL-VersionGate`](Conditional/MySQL-VersionGate) — MySQL 8.0 ↔ MySQL 9, gating a `VECTOR(384)` column on MySQL 9+
- [`Conditional/MariaDB-VersionGate`](Conditional/MariaDB-VersionGate) — MariaDB 10.6 ↔ 11.4, gating a native `UUID` column on 10.7+. Shows the **mid-major** case: a major-only comparison is wrong, so the gate tests major *and* minor
- [`Conditional/SqlServer-CompatLevelGate`](Conditional/SqlServer-CompatLevelGate) — one SQL Server 2022 instance, two databases at compatibility level 130 and 160, swapping a whole view implementation by folder-level gating. Shows why `SERVERPROPERTY('ProductMajorVersion')` is the **wrong** gate for syntax on SQL Server
- [`Conditional/SqlServer-RollingRollout`](Conditional/SqlServer-RollingRollout) — SQL Server 2022 × 3 tenant databases, rolling out a nonclustered columnstore index one tenant per maintenance window via a `RolloutControl` table

These demos use a different docker layout than the products above (engine pairs, or one instance with several databases) and live in `Conditional/` rather than per-platform subdirectories.

## Working Alongside Other Tools

- [`MySQL-PtOsc-Coexistence`](MySQL-PtOsc-Coexistence) — MySQL with 2.5 million rows. SchemaQuench declares the change and prints the exact `ALTER` clause under WhatIf, `pt-online-schema-change` applies it online, and the next deploy finds the swapped table matching the package. Use it for a change MySQL can't make without blocking writes, such as a column type change, where `OnlineApply` has no lower-lock form to ask for.

## Quick Start

Choose a platform and run the matching launcher.

Windows (cmd):

```cmd
:: SQL Server
cd SqlServer && run-demo.cmd

:: PostgreSQL
cd PostgreSQL && run-demo.cmd

:: MySQL
cd MySQL && run-demo.cmd

:: MariaDB
cd MariaDB && run-demo.cmd
```

macOS / Linux (bash):

```bash
# SQL Server
cd SqlServer && ./run-demo.sh

# PostgreSQL
cd PostgreSQL && ./run-demo.sh

# MySQL
cd MySQL && ./run-demo.sh

# MariaDB
cd MariaDB && ./run-demo.sh
```

The launcher pulls the SchemaQuench image published with every release (`ghcr.io/schema-smith/schemaquench:latest`), then runs `docker compose up --build -d` to start the database server and deploy the demo schemas. Docker is all you need.

- **Pin a release:** set `SCHEMAQUENCH_IMAGE` first, e.g. `SCHEMAQUENCH_IMAGE=ghcr.io/schema-smith/schemaquench:2.8.0 ./run-demo.sh`.
- **Run your own changes:** `./run-demo.sh --from-source` (or `run-demo.cmd --from-source`) publishes SchemaQuench from this checkout and runs the demo on that build. This needs the .NET SDK.

Each platform folder contains:
- Demo schema packages (product folders)
- A `docker-compose.yml` that spins up the database server and runs SchemaQuench to deploy the demo schemas
- A `run-demo.sh` / `run-demo.cmd` launcher
- A `.env` file with default credentials

### Credentials and ports

Every demo signs in as `TestUser`; the password, the host port, and the engine image are the variables in that platform folder's `.env` (`MSSQL_*`, `PostGreSQL_*`, `MYSQL_*`, `MARIADB_*`). The default host ports are SQL Server `1440`, PostgreSQL `5432`, MySQL `3306`, and MariaDB `3317`, chosen so all four stacks run side by side. Change a value in `.env` before launching to point the demo elsewhere.

### Cleaning up

Each stack is torn down from its own folder:

```bash
docker compose down -v
```

`-v` removes any data volume the engine image created along with the containers, so the next `run-demo` starts from an empty server rather than from whatever the last run left behind.

### Engine version (optional)

Every demo lets you point its container at a different engine version, so you can exercise the demo against the version you actually run in production. Each variable lives in that demo's `.env` and falls back to the default shown:

| Demo | Variable | Default |
| --- | --- | --- |
| [`SqlServer/`](SqlServer) | `MSSQL_IMAGE` | `mcr.microsoft.com/mssql/server:2022-latest` |
| [`PostgreSQL/`](PostgreSQL) | `POSTGRES_IMAGE` | `postgis/postgis:latest` |
| [`MySQL/`](MySQL) | `MYSQL_IMAGE` | `mysql:8.0` |
| [`MariaDB/`](MariaDB) | `MARIADB_IMAGE` | `mariadb:11.4` |
| [Learn sandbox](Learn/docker) | `MSSQL_IMAGE`, `POSTGRES_IMAGE`, `MYSQL_IMAGE`, `MARIADB_IMAGE` | as above (`postgres:16` for PG) |
| [`Conditional/PostgreSQL-VersionGate`](Conditional/PostgreSQL-VersionGate) | `PG_OLD_IMAGE`, `PG_NEW_IMAGE` | `postgres:15`, `postgres:18` |
| [`Conditional/MySQL-VersionGate`](Conditional/MySQL-VersionGate) | `MYSQL_OLD_IMAGE`, `MYSQL_NEW_IMAGE` | `mysql:8.0`, `mysql:9` |
| [`Conditional/MariaDB-VersionGate`](Conditional/MariaDB-VersionGate) | `MARIADB_OLD_IMAGE`, `MARIADB_NEW_IMAGE` | `mariadb:10.6`, `mariadb:11.4` |
| [`Conditional/SqlServer-CompatLevelGate`](Conditional/SqlServer-CompatLevelGate) | `MSSQL_IMAGE` (must stay 2022+) | `mcr.microsoft.com/mssql/server:2022-latest` |
| [`Conditional/SqlServer-RollingRollout`](Conditional/SqlServer-RollingRollout) | `MSSQL_IMAGE` | `mcr.microsoft.com/mssql/server:2022-latest` |

Three things to know before you override:

- **The version-gate demos need both sides.** They exist to show the *contrast* between two engine versions, so they take an old and a new image. Keep the old one below the gate and the new one at or above it, or both sides behave identically and the demo stops demonstrating anything. The compose service names (`pg15`, `pg18`, `mysql8`, `mysql9`) reflect the defaults, not your override.
- **The PostgreSQL demo must stay PostGIS-enabled.** It deploys spatial columns, so use a `postgis/postgis:<pgMajor>-<postgisVersion>` tag rather than a plain `postgres:<n>` one.
- **SchemaSmith enforces its own floor first.** Point a demo below the supported floor and SchemaQuench refuses at pre-flight with a clear message rather than failing partway through a deploy. That is the guard working, not the demo breaking.

**SQL Server first-boot cost.** A fresh SQL Server container runs a one-time system-database upgrade on first boot — a few seconds on a fast disk, but many minutes on a slow or resource-constrained Docker backend, and the amount of work varies a lot by version:

| `MSSQL_IMAGE` | First boot | Pick it when |
| --- | --- | --- |
| `mcr.microsoft.com/mssql/server:2019-latest` | fastest (~⅓ the upgrade work of 2022) | your Docker backend is slow, or you run SQL Server 2019 |
| `mcr.microsoft.com/mssql/server:2022-latest` *(default)* | slowest | you run SQL Server 2022 |
| `mcr.microsoft.com/mssql/server:2025-latest` | ~½ of 2022 | you want the current release, or you run SQL Server 2025 |

All three are tested end-to-end — every demo package, including the AdventureWorks full-text catalog and indexes, deploys cleanly.

### Run on your own server (no Docker)

Already have a server? Skip Docker and deploy the demo databases straight onto your instance with the `deploy-to-endpoint.ps1` (Windows) / `deploy-to-endpoint.sh` (macOS/Linux) helper in the engine's demo folder — `SqlServer/`, `PostgreSQL/`, `MySQL/`, or `MariaDB/`. It resets and redeploys the same demo set behind a confirmation, and refuses to touch any same-named database it didn't create. Requires the engine's command-line client on your `PATH` (`sqlcmd` / `psql` / `mysql` / `mariadb`). Full walkthrough: [Use your own server](../docs/end-user/guide/use-your-own-server.md).

## Sources & Licensing

Each extracted product folder contains a `PROVENANCE.md` documenting the canonical source, license, and extraction notes. AdventureWorks, Chinook, Northwind, and Sakila are extracted from open-source sample databases using the SchemaSmith toolset. TenantCRM is hand-authored as a schema-template feature demo and has a tutorial `README.md` in place of a `PROVENANCE.md`.

## Additional Resources

- [SchemaSmith Website](https://schemasmith.com) -- documentation and getting started guides
