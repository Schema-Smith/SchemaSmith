# Course 5 — Database Setup

These scripts create and **seed** the Course 5 migration-track databases on the sandbox engines —
25 databases in total. Most are the *post-migration state* a source tool would have left behind:
the shared e-commerce **shop schema** (`Customer`, `Product`, `SalesOrder`, `OrderItem`) plus that
tool's own **bookkeeping table**. You do not run Flyway, Liquibase, or EF Core yourself — the setup
hands you the database in the exact state those tools produce, so each lab can go straight to the
"extract it into SchemaSmith" step. Module 6 gets a dev/prod pair instead: the shop schema alone in
`shop_brownfield_dev`, and in `shop_brownfield_prod` the shop schema plus the drift production
collects.

## Prerequisite

The shared sandbox must be running. See [`Demos/Learn/README.md`](../README.md) for how to start it
and verify it is healthy before continuing.

## Run the setup

**macOS / Linux**

```bash
# from course5-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
# from course5-setup
.\setup-databases.ps1
```

Both scripts print `PASS` or `FAIL` for each database (`PASS` only after a shop table is confirmed):

```
SQL Server
  shop_from_flyway           PASS
  shop_from_liquibase        PASS
  shop_from_efcore           PASS
  shop_from_scripts          PASS
  shop_from_dacpac           PASS
  shop_brownfield_dev        PASS
  shop_brownfield_prod       PASS
PostgreSQL
  shop_from_flyway           PASS
  ...
MySQL
  shop_from_flyway           PASS
  ...
MariaDB
  shop_from_flyway           PASS
  ...

All 25 databases are seeded and ready (7 SQL Server, 6 PostgreSQL, 6 MySQL, 6 MariaDB).
```

## Databases created

Every database carries the identical four-table shop schema. The migration-track ones differ only in the bookkeeping
table the source tool leaves behind — the table each lab teaches you to leave *out* of the extract.

| Module | Database | Source tool | Bookkeeping table left behind |
| ------ | -------- | ----------- | ----------------------------- |
| 1 — Flyway      | `shop_from_flyway`    | Flyway     | `flyway_schema_history` |
| 2 — Liquibase   | `shop_from_liquibase` | Liquibase  | `DATABASECHANGELOG`, `DATABASECHANGELOGLOCK` |
| 3 — EF Core     | `shop_from_efcore`    | EF Core    | `__EFMigrationsHistory` |
| 4 — SSDT/DACPAC | `shop_from_dacpac`    | SSDT/DACPAC | *(none — DACPAC keeps no runtime history table)* |
| 5 — hand-rolled | `shop_from_scripts`   | hand-rolled scripts | `schema_version` |
| 6 — brownfield  | `shop_brownfield_dev`, `shop_brownfield_prod` | *(none)* | *(none — `prod` adds a column, a check, an index and a table)* |

Engine coverage mirrors the course: Modules 1–3, 5 and 6 are seeded on all four engines; **Module 4
(SSDT/DACPAC) is SQL Server only** — DACPAC is a SQL Server technology, so `shop_from_dacpac` is not
created on PostgreSQL, MySQL, or MariaDB.

| Engine     | Databases |
| ---------- | --------- |
| SQL Server | `shop_from_flyway`, `shop_from_liquibase`, `shop_from_efcore`, `shop_from_scripts`, `shop_from_dacpac`, `shop_brownfield_dev`, `shop_brownfield_prod` |
| PostgreSQL | `shop_from_flyway`, `shop_from_liquibase`, `shop_from_efcore`, `shop_from_scripts`, `shop_brownfield_dev`, `shop_brownfield_prod` |
| MySQL      | `shop_from_flyway`, `shop_from_liquibase`, `shop_from_efcore`, `shop_from_scripts`, `shop_brownfield_dev`, `shop_brownfield_prod` |
| MariaDB    | `shop_from_flyway`, `shop_from_liquibase`, `shop_from_efcore`, `shop_from_scripts`, `shop_brownfield_dev`, `shop_brownfield_prod` |

## Connection details

These are throwaway sandbox credentials — **never reuse them anywhere real.**

| Engine     | Host        | Port    | User       | Password         |
| ---------- | ----------- | ------- | ---------- | ---------------- |
| SQL Server | `localhost` | `11433` | `sa`       | `Learn!Passw0rd` |
| PostgreSQL | `localhost` | `15432` | `postgres` | `Learn!Passw0rd` |
| MySQL      | `localhost` | `13306` | `root`     | `Learn!Passw0rd` |
| MariaDB    | `localhost` | `13307` | `root`     | `Learn!Passw0rd` |

## Re-running is safe

The seed scripts are idempotent — every table is dropped and recreated, so a second run restores the
exact starting state and still reports `PASS` for every database. Run them as often as you like; if a
lab leaves a database in an odd state, re-run the setup to reset it.

## Starting over: `-Reset`

Re-running the setup already restores each table to its starting state, but the database itself is
never touched. `-Reset` goes a step further — it drops and recreates the whole database before
reseeding, useful if a lab has added extra objects alongside the shop schema and you want a truly
empty starting point:

```bash
# from course5-setup
bash setup-databases.sh --reset
```

```powershell
# from course5-setup
.\setup-databases.ps1 -Reset
```

Each database is dropped and recreated, then reseeded, reported as `PASS (reset)`. **Only databases
these scripts created are ever dropped.** On your own server, a database carrying one of these names
that the labs didn't create is refused and left untouched — you'll be told to rename or move it.
Nothing of yours is at risk.
