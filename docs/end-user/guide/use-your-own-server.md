# Use Your Own Server (No Docker)

Already run SQL Server, PostgreSQL, MySQL, or MariaDB? You don't need Docker to try SchemaSmith. Point one helper script at any reachable instance and it stands up the entire demo — the same databases the Docker demo builds, minus the containers. It's the database-level equivalent of `docker compose up`, for people who already have infrastructure.

Every supported engine ships the same helper, side by side with its Docker launcher:

| Engine | Helper folder | Client needed |
| --- | --- | --- |
| SQL Server | `Demos/SqlServer/` | `sqlcmd` |
| PostgreSQL | `Demos/PostgreSQL/` | `psql` |
| MySQL | `Demos/MySQL/` | `mysql` |
| MariaDB | `Demos/MariaDB/` | `mariadb` |

## Two ways to run the demo

- **Docker** — throwaway, zero setup. `run-demo` spins up a disposable server, deploys the demo, and you tear it all down when you're done. Reach for this when you *don't* already have a server. See [Quick Start](02-quick-start.md).
- **Your own server** — this page. A local instance, a shared QA box, a container you manage — the `deploy-to-endpoint` helper resets and deploys the demo databases directly onto it, no Docker involved.

Either path lands you in the same place: the AdventureWorks, Northwind, Sakila, and Chinook demo databases (SQL Server and PostgreSQL add TenantCRM), ready to explore, plus one or two small scaffolding databases. `TestMain` is the control database every demo product's `Initialize` template connects to first -- it checks whether the product's database exists and creates it from there, so `TestMain` only has to exist and is otherwise empty. `TestSecondary` (SQL Server only) is a second empty database of the same kind; the demo packages never deploy into it, but the SQL Server demo container creates it for the integration tests that share these containers, so the helper creates it too and the two paths match.

## Prerequisite: the engine's command-line client

The helper uses your engine's command-line client to create, stamp, and drop the demo databases, so that client must be on your `PATH`. None are bundled — install the one for your engine and re-open your shell.

> **SQL Server — `sqlcmd`.** `winget install Microsoft.Sqlcmd` (Windows) · `brew install sqlcmd` (macOS) · `mssql-tools18` per the [Microsoft docs](https://learn.microsoft.com/sql/tools/sqlcmd/sqlcmd-utility) (Linux). Verify with `sqlcmd -?`.

> **PostgreSQL — `psql`.** macOS: `brew install libpq` (client only) or `postgresql`. Linux: `apt-get install postgresql-client` / `dnf install postgresql`. Windows: take the [binary ZIP](https://www.enterprisedb.com/download-postgresql-binaries) and put its `bin\` on your `PATH` — or install the whole server with `winget install PostgreSQL.PostgreSQL.17` (the version suffix is required; the bare id doesn't exist). Verify with `psql --version`.

> **MySQL — `mysql`.** macOS: `brew install mysql-client`. Linux: `apt-get install mysql-client` / `dnf install mysql`. Windows: the [ZIP archive](https://dev.mysql.com/downloads/mysql/) gives you `bin\mysql.exe` with no server; `winget install Oracle.MySQL` also works but installs the full server. Verify with `mysql --version`.

> **MariaDB — `mariadb`.** macOS: `brew install mariadb`. Linux: `apt-get install mariadb-client` / `dnf install MariaDB-client`. Windows: take the [ZIP package](https://mariadb.org/download/) and use its `bin\mariadb.exe`; `winget install MariaDB.Server` is server-and-all. Verify with `mariadb --version`. (`mysql` is a legacy symlink being phased out — the helper calls `mariadb`.)

> **A client is not a server.** On Windows the vendors mostly ship the client inside the server package, so a `winget install` will put a database service on your machine — bound to a default port, which is exactly where your existing server or containers already live. The ZIP archives above hold the same executables with nothing to install and nothing to collide with. The MariaDB package also drops a `mysql.exe` alias; it is *not* a MySQL client and cannot authenticate to MySQL 8.

Each helper checks for its client and stops with these same instructions if it can't find it.

## Provision the demo

Give the helper your server and a login that can create and drop databases. SQL Server takes the port comma-joined to the server; the other engines take a separate `--port` (shown with each engine's default).

**SQL Server (PowerShell / bash):**

```powershell
cd Demos\SqlServer
.\deploy-to-endpoint.ps1 -Server 'localhost,1433' -User sa -Password 'YourPassword'
```

```bash
cd Demos/SqlServer
./deploy-to-endpoint.sh --server your-server --user sa --password 'YourPassword'
```

> **PowerShell quoting.** Quote the server: `-Server 'localhost,1433'`. Unquoted, PowerShell reads the comma as a list separator and `host,port` becomes two values instead of one — the quotes keep it a single string.

**PostgreSQL (PowerShell / bash):**

```powershell
cd Demos\PostgreSQL
.\deploy-to-endpoint.ps1 -Server localhost -Port 5432 -User postgres -Password 'YourPassword'
```

```bash
cd Demos/PostgreSQL
./deploy-to-endpoint.sh --server localhost --port 5432 --user postgres --password 'YourPassword'
```

**MySQL / MariaDB (PowerShell / bash):**

```powershell
cd Demos\MySQL          # or Demos\MariaDB
.\deploy-to-endpoint.ps1 -Server localhost -Port 3306 -User root -Password 'YourPassword'
```

```bash
cd Demos/MySQL          # or Demos/MariaDB
./deploy-to-endpoint.sh --server localhost --port 3306 --user root --password 'YourPassword'
```

> **Windows Authentication (SQL Server only).** Omit the user and password — `-Server 'localhost,1433'` on its own (PowerShell) or `--server your-server` on its own (bash) — and the helper connects as your current Windows identity: SchemaQuench builds an `Integrated Security` connection and `sqlcmd` uses `-E`. PostgreSQL, MySQL, and MariaDB always need credentials.

On every run the helper does three things:

1. **Resets** — drops the demo databases it previously created (and *only* those — see below), so you always start from a known-clean state.
2. **Bootstraps** — creates the shared `TestMain` control database (and `TestSecondary` on SQL Server).
3. **Deploys** — runs the shipped SchemaQuench against each demo package, exactly as the Docker demo does.

Before dropping anything, it prints the exact list of databases it will replace and waits for you to type `yes`. Pass `-Force` (PowerShell) or `--force` (bash) to skip the prompt in automation.

> **Note:** the helper reuses the shipped SchemaQuench and the demo packages unchanged — it adds no new tool and alters nothing about how deployment works. It's only the orchestration the Docker demo gives you for free, unbundled from Docker.

## If a name collides

The demo uses friendly names like `Northwind` and `AdventureWorks` — and you might already have a real `Northwind` on that server. The helper will not touch it.

When it creates a database, it stamps it with an ownership marker, and it only ever drops databases carrying that stamp. If a demo name already exists *without* the stamp — your real database — the helper stops and refuses, naming the collision:

```
These databases already exist on your-server but were NOT created by this helper:
  Northwind
```

> **Where the stamp lives.** The marker is per engine, but the safety rule is identical everywhere: SQL Server uses a `SchemaSmith_DemoProvisioned` extended property, PostgreSQL a database comment (`COMMENT ON DATABASE`), and MySQL / MariaDB a small `SchemaSmith_DemoProvisioned` marker table inside each provisioned database.

To proceed, rename the demo's copy so it no longer collides. Open the engine's `demo-databases.manifest` and change the `NAME` column for that row — for example `Northwind` → `SS_Northwind`:

```
product|SS_Northwind|NorthwindDb|Northwind
```

Re-run the helper. It now deploys the same package to a database named `SS_Northwind`, and your real `Northwind` is never touched. Only the `NAME` column changes — leave `TOKEN` and `PACKAGE` as they are.

> **Full-Text Search is optional (SQL Server).** The AdventureWorks demo includes a full-text catalog, three full-text indexes, and a search procedure. If your server doesn't have Full-Text Search installed, those objects are skipped automatically — you'll see `Skipping folder ... ShouldApplyExpression evaluated false` in the log, telling you exactly what was left out and why — and the rest of AdventureWorks deploys normally. Install Full-Text Search on the server if you want the full-text objects.

## The Learn labs on your own server

The training labs have their own helper, because they provision many small databases per course rather than one demo set. From `Demos/Learn`, **source** the activation script once per shell:

```bash
. ./use-my-server.sh --engine postgres --server your-host --port 5432 --user you --password '…'
```

```powershell
. .\use-my-server.ps1 -Engine postgres -Server your-host -Port 5432 -User you -Password '…'
```

Every lab command in every course then works exactly as written — no settings file is edited. Each course's setup script creates that course's databases on your server, and stamps them, so a database it didn't create is never dropped or deployed into. Full walkthrough: [`Demos/Learn/README.md`](https://github.com/Schema-Smith/SchemaSmith/blob/main/Demos/Learn/README.md).

> **Windows Authentication on SQL Server.** Omit *both* `-User` and `-Password` and the PowerShell helper connects as you — `Target:IntegratedSecurity` supersedes any credential the lab settings file declares, which is what makes this possible through an environment override at all:
>
> ```powershell
> . .\use-my-server.ps1 -Engine sqlserver -Server 'localhost\SQLEXPRESS'
> ```
>
> Name the instance instead of using a loopback address and port: Windows Authentication has no SPN to resolve for `127.0.0.1,1433` and fails with *"Cannot generate SSPI context"* before SchemaSmith is involved. Other engines still need a login, and so does the `.sh` helper — Windows Authentication needs a Windows logon session.

## Bring your own database instead

Want to point SchemaSmith at a database you *already* have, rather than the demo set? That's SchemaTongs' job — it grips your live schema and casts it into a version-controlled package. No helper needed; it's a connection-settings change. Walk through it in [Quick Start → Cast with SchemaTongs](02-quick-start.md#step-2-cast-with-schematongs).
