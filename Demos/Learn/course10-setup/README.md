# Course 10 — Setup: stand up the mixed-version fleet

Course 10 deploys **one package across a farm of database servers that are not all on the
same version**. Dev is current; some tenants are a few releases back; one contract-bound
tenant is on the oldest tier SchemaSmith still supports. That mixed fleet is the whole
course, so before Module 1 you stand it up — floor-version engines running *alongside* the
current ones, plus three SQL Server tiers on one instance.

| Tier | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| **Current** | `learn_2022` (compat 160) | `16` | `8.0` | `11.4` |
| **Mid** (SQL Server only) | `learn_2016` (compat 130) | — | — | — |
| **Floor** | `learn_2008` (compat 100) | `12` | `5.7` | `10.2` |

SQL Server is the one engine that does *not* get a second container. Instead, three databases
on the single `learn-sqlserver` instance sit at different **compatibility levels** — `learn_2022`
(160), `learn_2016` (130), `learn_2008` (100). Compatibility level, not the binary, is what
actually gates T-SQL syntax, so this is both the cheaper option (a SQL Server container is
~2 GB of RAM) and the *more accurate* teacher for the footgun Module 3 turns on.

The other three engines each get a floor-version container from the opt-in `mixed-fleet`
compose profile: PostgreSQL 12 beside the current 16, MySQL 5.7 beside 8.0, MariaDB 10.2
beside 11.4.

## Sandbox only — there is no own-server path for this course

Every other course's setup lab offers a "use your own server" path. Course 10 does not, and
the reason is honest: **a single server cannot be several engine versions at once.** The mixed
fleet only exists because the sandbox runs the floor and current engines in parallel
containers. If you want to point one lab at your own server, you can still do that per Module
using [Use your own server](../README.md#use-your-own-server-instead-no-docker) — but the
mixed-version story itself needs the sandbox.

> **Engine floor — the SQL Server tiers need SQL Server 2022 or later.** The three tiers are *compatibility levels on one instance*, and a server only offers the levels its own release supports: 2017 caps at 140 and 2019 at 150, so **`learn_2022` (level 160) cannot be provisioned below 2022**. `provision_sqlserver_tier` issues the `CREATE DATABASE` and the `ALTER … SET COMPATIBILITY_LEVEL` in one batch, so an unsupported level aborts the batch and the database is not created at all — which is the safe outcome, because a lab then fails with *"No database targets discovered"* rather than quietly deploying into a database sitting at the wrong level. Measured: on 2019 and 2017 the tier is absent and every lab targeting it fails; on 2022 the whole fleet provisions and all of Course 10 passes. The sandbox default is 2022, so this only bites if you have overridden `MSSQL_IMAGE` to an older release.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
  This script adds the floor engines on top of it.
- `schemaquench --version` answers **2.7.0** or later on your PATH. All four engine floors and
  the conditional tokens Course 10 uses shipped in 2.4.0. New to the CLI? Install it in
  [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — bring up the fleet and provision the tiers

**macOS / Linux**

```bash
cd Demos/Learn/course10-setup
bash setup-fleet.sh
```

**Windows (PowerShell)**

```powershell
cd Demos\Learn\course10-setup
.\setup-fleet.ps1
```

The script starts the `mixed-fleet` compose profile (`docker compose --profile mixed-fleet up
-d` from `Demos/Learn/docker`), waits for each floor engine — and SQL Server — to report
healthy, then provisions every tier. It prints `PASS` per tier/engine and finishes with a
ports-and-tiers reference table. **Re-running is safe** — every step is idempotent, so a
second run just re-confirms the fleet.

## Ports and tiers

| Engine | Current tier | Floor tier (`--profile mixed-fleet`) |
| --- | --- | --- |
| SQL Server | `localhost,11433` → `learn_2022` (compat 160), `learn_2016` (compat 130) | same instance → `learn_2008` (compat 100) |
| PostgreSQL | `localhost:15432` (16) | `localhost:15433` (12) |
| MySQL | `localhost:13306` (8.0) | `localhost:13316` (5.7) |
| MariaDB | `localhost:13307` (11.4) | `localhost:13317` (10.2) |

All engines use password `Learn!Passw0rd` and (except the SQL Server tiers) the `learn`
database; users and full connection details are in [`../README.md`](../README.md#connection-details-throwaway--sandbox-only).
The SQL Server tiers live in `learn_2022` / `learn_2016` / `learn_2008` on the one instance.

## What the script provisions

| Target | Where | State confirmed |
| --- | --- | --- |
| `learn_2022` | `learn-sqlserver` (11433) | database exists, `compatibility_level = 160` |
| `learn_2016` | `learn-sqlserver` (11433) | database exists, `compatibility_level = 130` |
| `learn_2008` | `learn-sqlserver` (11433) | database exists, `compatibility_level = 100` |
| `learn` | `learn-postgres-12` (15433) | database exists (env-var auto-create verified) |
| `learn` | `learn-mysql-57` (13316) | database exists (env-var auto-create verified) |
| `learn` | `learn-mariadb-102` (13317) | database exists (env-var auto-create verified) |

No schema is deployed here. Module 1 reads the detected version of every target with the
pre-flight pair; the later modules deploy one package across all of them.

## Tearing the floor engines back down

The floor containers stop with the profile flag; the current-tier sandbox stays up:

```bash
cd Demos/Learn/docker
docker compose --profile mixed-fleet down        # stop floor + current
docker compose --profile mixed-fleet down -v      # also drop the data volumes
```

Next: **Module 1 — the mixed-version farm**, where you read each target's detected version and
separate two numbers learners routinely conflate — the floor *you* declare, and the version
*the engine* reports.
