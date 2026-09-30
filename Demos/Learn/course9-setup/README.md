# Course 9 — Setup: stand up the three services

Northwind Commerce runs three services, each on its own engine and each managed
by its own **native** SchemaSmith package:

| Service | Engine | Database |
| --- | --- | --- |
| Orders | SQL Server | `orders` |
| Catalog | PostgreSQL | `catalog` |
| Sessions / Events | MySQL | `sessions` |

This lab creates the three empty service databases the rest of Course 9 deploys
into. The packages are native and separate — an Orders package is a SQL Server
package, a Catalog package is a PostgreSQL package, a Sessions package is a MySQL
package. Each service is independently deployable; there is no single package that
spans engines and no combined deploy step. The consistency across the three
services is in the tooling and the workflow, not in a shared artifact. In
production each service would live in its own repository; they sit together here
only because that is how lab bundles ship.

## Prerequisites

- The three-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.0** or later on your PATH. New to the
  CLI? Install it in [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the service databases

**macOS / Linux**

```bash
cd Demos/Learn/course9-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
cd Demos\Learn\course9-setup
.\setup-databases.ps1
```

Prints `PASS` per engine once its service database exists. Re-running is safe —
each database is only ever created once.

On your own server ([Use your own server](../README.md#use-your-own-server-instead-no-docker)),
only your single activated engine is set up — Course 9 needs SQL Server, PostgreSQL,
and MySQL together, so at most one of the three services can run there at a time.

## What the script does

| Engine | Database created |
| --- | --- |
| SQL Server | `orders` |
| PostgreSQL | `catalog` |
| MySQL | `sessions` |

No schema is deployed here. Module 1 deploys the first native package into each
of these databases.

## Starting over: `--reset`

Any time you want a clean slate for one of the services — say a module's deploy
failed partway and left it in a state a later module doesn't expect — reset it:

```bash
bash setup-databases.sh --reset
```

```powershell
.\setup-databases.ps1 -Reset
```

Each database is dropped and recreated empty, reported as `PASS (reset)`. **Only
databases this script created are ever dropped.** On your own server, a database
carrying one of these names that the labs didn't create is refused and left
untouched — you'll be told to rename or move it. Nothing of yours is at risk.

Re-run the module you're returning to from its first step; everything it needs,
it creates.

Next: **Module 1 — Same change, three engines**, where you apply one identical
schema evolution to all three services — as three independent native packages —
and feel the workflow stay the same even as the DDL goes native per engine.
