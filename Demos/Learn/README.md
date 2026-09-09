# SchemaSmith Learn — lab sandbox

The hands-on labs for the [Learn SchemaSmith](https://learn.schemasmith.com) course run against a
throwaway four-engine database sandbox — SQL Server, PostgreSQL, MySQL, and MariaDB, all at once. Spin
it up, work through the labs, tear it down.

## Which lab comes next — the course map

The labs follow the [Learn SchemaSmith](https://learn.schemasmith.com) courses **in order**: work a
course's lessons on the site, run its lab folder here, then move to the next course. Each lab folder
maps to a course like this:

| Course | Lab folders | What it covers |
| ------ | ----------- | -------------- |
| **Course 1 · First deployment** | `module-01` … `module-04` | Install & connect, first package, WhatIf, extract an existing schema |
| **Course 2 · Going deeper** | `level2-module-01` … `level2-module-06` | Products, template fan-out, conditional deployment, script tokens, data delivery, custom metadata |
| **Course 3 · Ship it / operate it** | `course3-module-01` … `course3-module-05` | Team workflow, CI/CD gating, rollback, capstone (full dev→prod lifecycle + recyclebin recovery) |
| **Course 4 · Recipes** | `course4-recipe-01` … `course4-recipe-12` | A cookbook of task-focused recipes |
| **Course 5 · Switching tools** | `course5-module-01` … `course5-module-05` | Migrating from another migration tool |
| **Course 6 · Production & least privilege** | `course6-module-01` … `course6-module-06` | Operating in CI under least privilege |
| **Course 7 · Fleet fan-out** | `course7-module-01` … `course7-module-06` | One schema across a fleet of per-tenant databases |
| **Course 8 · Troubleshooting & recovery** | `course8-module-01` … `course8-module-06` | The when-a-deploy-fails playbook |
| **Course 9 · Polyglot shop** | `course9-module-01` … `course9-module-05` | All engines at once, each service independently deployable |
| **Course 10 · Mixed-version fleet** | `course10-module-01` … `course10-module-05`, plus the `course10-xml-data-delivery` bonus recipe | One package across servers that are not all on the same version |

> **Heads-up on the folder names** — they use three conventions. Course 1's labs are the bare
> `module-*` folders; **Course 2's are the `level2-*` folders** (the site calls it "Course 2 · Going
> deeper" — the `level2` prefix is historical); Courses 3–10 are `courseN-*`. So the lab **after
> `module-04`** (the last Course 1 lab) is **`level2-module-01`**, then on through `course3-*` and up.

Courses 3–10 each include a `courseN-setup` folder — run it once before that course's modules.

## Prerequisites

- [Docker](https://docs.docker.com/get-docker/) (Desktop or Engine) with Compose v2 — for the throwaway sandbox. Already run one of these engines? Skip Docker entirely: see [Use your own server](#use-your-own-server-instead-no-docker) below.

## Start the sandbox

```bash
cd docker
docker compose up -d
```

The first run pulls four database images — give it a few minutes.

## Verify it's ready

```bash
./verify-sandbox.sh        # macOS / Linux
.\verify-sandbox.ps1       # Windows PowerShell
```

Each engine reports `PASS` once it has finished warming up. If one says it isn't healthy yet, wait a
minute and run it again — SQL Server in particular takes a little while on first boot.

## Connection details (throwaway — sandbox only)

| Engine     | Host        | Port    | User       | Password         | Database |
| ---------- | ----------- | ------- | ---------- | ---------------- | -------- |
| SQL Server | `localhost` | `11433` | `sa`       | `Learn!Passw0rd` | `learn`  |
| PostgreSQL | `localhost` | `15432` | `postgres` | `Learn!Passw0rd` | `learn`  |
| MySQL      | `localhost` | `13306` | `root`     | `Learn!Passw0rd` | `learn`  |
| MariaDB    | `localhost` | `13307` | `root`     | `Learn!Passw0rd` | `learn`  |

All four engines come up with a `learn` database ready to go. PostgreSQL, MySQL, and MariaDB create it
from environment variables; SQL Server's image can't, so a one-shot `sqlserver-init` service creates it
once the engine is healthy, then exits.

These credentials are intentionally simple and public. The sandbox is disposable — **never reuse
them anywhere real.** The ports are offset (`11433` / `15432` / `13306`) so the sandbox won't collide
with a default SQL Server, PostgreSQL, or MySQL you may already be running locally.

## Tear it down

```bash
cd docker
docker compose down -v     # -v also removes the data volumes
```

## Use your own server instead (no Docker)

Already run SQL Server, PostgreSQL, MySQL, or MariaDB? You don't need Docker for any of it. Every
course works against a server you already have.

You need that engine's command-line client on your `PATH` (`sqlcmd` / `psql` / `mysql` / `mariadb`)
and a login that can create databases. Then, from this directory, **source** the activation script
once per shell:

```bash
. ./use-my-server.sh --engine postgres --server your-host --port 5432 --user you --password '…'
```

```powershell
. .\use-my-server.ps1 -Engine postgres -Server your-host -Port 5432 -User you -Password '…'
```

That's the whole setup. **Every lab command in every course then works exactly as written** — you
don't edit a single settings file. SchemaSmith layers `SmithySettings_*` environment variables over
each lab's settings, and the activation script sets them; the lab helper scripts read the matching
`LEARN_*` values so their database creation and catalog checks land on your server too. It's the
same override mechanism Course 3 teaches, pointed at you.

Then work the courses normally. Each course has a setup script that creates that course's databases
— run it before that course's labs, exactly as the sandbox path does:

```bash
cd course3-setup && ./setup-environments.sh
```

A few things are worth knowing:

- **One engine at a time.** The activation is global to your shell, so a course runs against the
  engine you activated. Working through a course on a second engine? Source the script again with
  the other engine's details. (Course 9 is the exception — it runs three services on three engines
  together, so the sandbox is the easier path for that one.)
- **Per shell.** A new terminal needs the activation sourced again. `--off` / `-Off` returns you to
  the sandbox.
- **Windows Authentication works on SQL Server (2.5.0+).** Omit *both* `-User` and `-Password` and the
  PowerShell helper connects as you:
  ```powershell
  . .\use-my-server.ps1 -Engine sqlserver -Server 'localhost\SQLEXPRESS'
  ```
  Name the instance rather than using a loopback address and port — Windows Authentication has no SPN to
  resolve for `127.0.0.1,1433` and fails with *"Cannot generate SSPI context"* before SchemaSmith is
  reached. The helper checks for that combination and tells you. Every other engine still needs a login,
  and so does the `.sh` helper, since Windows Authentication needs a Windows logon session.
- **Your databases are safe.** Setup scripts stamp what they create and will never drop or deploy
  into a same-named database they didn't create — they stop and tell you to rename or move it. The
  `--reset` switch each setup script carries honours the same rule.
- **Older or hardened SQL Server?** If the modern driver can't complete the TLS handshake your server
  offers, the connection fails before any deploy starts. Add `--NoEncrypt` to the `schemaquench`
  command — it forces transport encryption off (setting the right property per engine), and it's the
  escape hatch for exactly the older or hardened SQL Server that classic `sqlcmd` reaches but the
  modern driver refuses. Full detail: the [configuration reference](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/configuration.md)
  under `--Encrypt` / `--NoEncrypt`.

### Will these labs run on my server?

**Using the Docker sandbox? Skip this — it already meets every floor.** This section is only for
the own-server path above.

SchemaSmith itself supports SQL Server 2008+, PostgreSQL 12+, MySQL 5.7+, and MariaDB 10.2+. Some
labs need more than that, because they demonstrate a feature that needs a newer engine — most often
automatic data delivery, which needs `OPENJSON` (SQL Server 2016) or `JSON_TABLE` (MySQL 8.0).

| Your engine | Runs every lab at | Below that |
|---|---|---|
| **SQL Server** | **2016** | Most labs still run; the ones that need more stop at pre-flight and name the version |
| **MySQL** | **8.0** | Data delivery has no fallback on 5.7, so those labs stop rather than deploy a schema with no rows |
| **PostgreSQL** | **12** | Every lab runs at PostgreSQL's floor |
| **MariaDB** | **10.2** | Every lab runs at MariaDB's floor |

Labs that need a newer engine say so under **Before you start**. If your server is below one, the
run stops at pre-flight naming the version it needs — it won't deploy half a schema and leave you
guessing. Nothing here applies to PostgreSQL or MariaDB: both do everything these labs ask at their
floors.

A few SQL Server labs go one better and ship *two* variants of the same object, picking one from the
detected version — you get the same result either way, and the lab says so. That is the same
`ShouldApplyExpression` gate Course 3 teaches, used on the labs themselves.

Want just the `learn` database from Courses 1–2 without the rest? `deploy-to-endpoint` still does
exactly that:

```bash
./deploy-to-endpoint.sh --engine postgres --server your-host --port 5432 --user you --password '…'
```

Full walkthrough, including how to install each engine's client:
[Use your own server](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/use-your-own-server.md).

## How this relates to the other demos

The per-engine demos under `Demos/SqlServer`, `Demos/PostgreSQL`, and `Demos/MySQL` stand up a full
demo: they build the CLI and auto-deploy the sample databases. This sandbox is deliberately lighter
— it stands up empty engines so you install the CLI yourself (Module 1) and run it by hand, the way
you would against your own database. It reuses the same engine versions and the MySQL
function-trust flag those demos rely on, with throwaway credentials and offset ports.
