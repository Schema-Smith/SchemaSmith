# Module 1 — Install & connect (lab)

Goal: get the SchemaSmith CLI installed and prove it connects to each sandbox engine by
**kindling the forge** — connecting and installing SchemaSmith's helper routines into a target
database. You'll run a tiny starter package that has no tables yet; Module 2 picks up the same
package and adds your first table.

## Before you start

- **Install the CLI.** See the [Installation guide](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/installation.md) — `choco install schemasmith` on Windows, or `curl -fsSL https://schemasmith.com/dl/install.sh | sh` on Linux/macOS.
- **Get a `learn` database — two ways:**
  - **Docker sandbox (throwaway).** From [`Demos/Learn/docker`](../docker), run `docker compose up -d` and confirm all four engines report `PASS` with `./verify-sandbox.sh` (or `verify-sandbox.ps1`). The sandbox provisions a `learn` database on SQL Server, PostgreSQL, MySQL, and MariaDB.
  - **Your own server (no Docker).** Already run one of these engines? From [`Demos/Learn`](..), point the helper at it and it creates (or cleanly resets) the empty `learn` database for you — the database-level equivalent of the Docker sandbox, minus the containers:

    ```bash
    ./deploy-to-endpoint.sh --engine postgres --server your-host --port 5432 --user you --password '…'
    # engines: sqlserver | postgres | mysql | mariadb   (Windows: deploy-to-endpoint.ps1 -Engine … -Server …)
    ```

    It needs that engine's command-line client on your `PATH` (`sqlcmd` / `psql` / `mysql` / `mariadb`) and only ever touches a database it stamped — an existing `learn` it didn't create is refused, never dropped. Then set each engine folder's `connect.settings.json` to your host/port/credentials. Full walkthrough: [Use your own server](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/use-your-own-server.md).

## Step 1: Confirm the install

```bash
schemaquench --version
```

Expected:

```
SchemaQuench - Version: 2.7.1.0
```

Your exact version may be later — anything from `2.7.0.0` up is fine. The lab packages need 2.7.0, so an older version line means upgrade before you go on. If the command isn't found, revisit the install guide.

## Step 2: Connect and kindle each engine

Each engine has its own folder with a ready-to-run settings file and a minimal package
(`Package/`) whose template targets the sandbox's `learn` database. From this directory:

```bash
cd sqlserver    # or: postgres  |  mysql  |  mariadb
schemaquench --ConfigFile:connect.settings.json
```

Expected (engine name and host vary):

```
localhost,11433 (…) connection succeeded
Validate Server
Quenching Template: Main
Locate Databases To Quench
[localhost,11433].[learn] Begin Quench
[localhost,11433].[learn]   Kindling the forge
[localhost,11433].[learn] Successfully Quenched
Completed quench of LearnConnect
```

That's the proof: SchemaQuench connected, validated the server, found the `learn` database, and
kindled the forge. There are no tables in the package yet, so nothing else is deployed.

Run the other engine folders the same way. Same flow, different connection details:

| Engine     | Folder       | Server (in `connect.settings.json`) | User       |
| ---------- | ------------ | ----------------------------------- | ---------- |
| SQL Server | `sqlserver/` | `localhost,11433`                   | `sa`       |
| PostgreSQL | `postgres/`  | `localhost` + port `15432`          | `postgres` |
| MySQL      | `mysql/`     | `localhost` + port `13306`          | `root`     |
| MariaDB    | `mariadb/`   | `localhost` + port `13307`          | `root`     |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

## What you just proved

Two things have to be true before SchemaQuench can deploy: the CLI is **installed and on your
PATH**, and it has a **working connection** (host, port, credentials) to the target. You've now
confirmed both on every engine you ran — and kindled a forge ready for Module 2.
