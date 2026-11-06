# Course 12 — Database Setup

These scripts create the eleven module databases on every sandbox engine (SQL Server, PostgreSQL,
MySQL, and MariaDB), 44 databases in total. Each database is dedicated to one module so labs stay
hermetic and do not clash with one another.

## Prerequisite

The shared sandbox must be running. See [`Demos/Learn/README.md`](../README.md) for how to start it
and verify it is healthy before continuing.

## Run the setup

**macOS / Linux**

```bash
# from course12-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
# from course12-setup
.\setup-databases.ps1
```

Both scripts print `PASS` or `FAIL` for each of the 44 databases:

```
SQL Server
  declared_m1                PASS
  declared_m2                PASS
  declared_m3                PASS
  declared_m4                PASS
  declared_m5                PASS
  declared_m6                PASS
  declared_m7                PASS
  declared_m8                PASS
  declared_m9                PASS
  declared_m10               PASS
  declared_m11               PASS
PostgreSQL
  declared_m1                PASS
  ...
MySQL
  declared_m1                PASS
  ...
MariaDB
  declared_m1                PASS
  ...

All 44 databases are ready.
```

## Databases created

| Module | Database | What it holds |
| ------ | -------- | ------------- |
| 1 · Declared, not scripted | `declared_m1` | PostgreSQL types and sequences, MariaDB sequences |
| 2 · Declared scheduled events | `declared_m2` | MySQL and MariaDB events |
| 3 · Index-only templates | `declared_m3` | A vendor table you index but don't own |
| 4 · Subfolder organization | `declared_m4` | Procedures filed by domain |
| 5 · Expression change detection | `declared_m5` | Check constraints and computed columns |
| 6 · Trust the round trip | `declared_m6` | Extract, rebuild, re-extract |
| 7 · Search you declare | `declared_m7` | Full-text indexes |
| 8 · History the database keeps | `declared_m8` | Temporal tables and system versioning |
| 9 · History you can't rewrite | `declared_m9` | SQL Server ledger tables |
| 10 · Indexes beyond the B-tree | `declared_m10` | SQL Server spatial, selective XML and ordered columnstore indexes; PostgreSQL GiST and GIN |
| 11 · The security layer, declared | `declared_m11` | SQL Server row-level security, Always Encrypted keys, signed modules, trigger state and order |

Each database is created on all four engines, including the ones a module doesn't use, so the setup is the same
everywhere.

## Connection details

These are throwaway sandbox credentials — **never reuse them anywhere real.**

| Engine     | Host        | Port    | User       | Password         |
| ---------- | ----------- | ------- | ---------- | ---------------- |
| SQL Server | `localhost` | `11433` | `sa`       | `Learn!Passw0rd` |
| PostgreSQL | `localhost` | `15432` | `postgres` | `Learn!Passw0rd` |
| MySQL      | `localhost` | `13306` | `root`     | `Learn!Passw0rd` |
| MariaDB    | `localhost` | `13307` | `root`     | `Learn!Passw0rd` |

## Re-running is safe

The scripts are idempotent — running them a second time makes no changes and still reports `PASS`
for every database. You can run them as many times as you like.

## Starting over: `-Reset`

Each module's database is scoped to that module, so labs stay isolated from one another. But if a
lab run leaves one in an odd state — or you just want a module's database empty again — reset it:

```bash
# from course12-setup
bash setup-databases.sh --reset
```

```powershell
# from course12-setup
.\setup-databases.ps1 -Reset
```

Each database is dropped and recreated empty, reported as `PASS (reset)`. **Only databases these
scripts created are ever dropped.** On your own server, a database carrying one of these names that
the labs didn't create is refused and left untouched — you'll be told to rename or move it. Nothing
of yours is at risk.
