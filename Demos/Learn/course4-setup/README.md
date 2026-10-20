# Course 4 — Database Setup

These scripts create the fifteen cookbook databases on every sandbox engine (SQL Server, PostgreSQL,
MySQL, and MariaDB) — 60 databases in total. Each database is dedicated to one recipe so labs stay
hermetic and do not clash with one another.

## Prerequisite

The shared sandbox must be running. See [`Demos/Learn/README.md`](../README.md) for how to start it
and verify it is healthy before continuing.

## Run the setup

**macOS / Linux**

```bash
# from course4-setup
bash setup-databases.sh
```

**Windows (PowerShell)**

```powershell
# from course4-setup
.\setup-databases.ps1
```

Both scripts print `PASS` or `FAIL` for each of the 60 databases:

```
SQL Server
  cookbook_r1_prod           PASS
  cookbook_r1_nonprod        PASS
  cookbook_r2                PASS
  cookbook_r3                PASS
  cookbook_r4                PASS
  cookbook_r5                PASS
  cookbook_r6                PASS
  cookbook_r8                PASS
  cookbook_r9                PASS
  cookbook_r11               PASS
  cookbook_r12               PASS
  cookbook_r13               PASS
  cookbook_r14               PASS
  cookbook_r15               PASS
  cookbook_r16               PASS
PostgreSQL
  cookbook_r1_prod           PASS
  ...
MySQL
  cookbook_r1_prod           PASS
  ...
MariaDB
  cookbook_r1_prod           PASS
  ...

All 60 databases are ready.
```

## Databases created

| Recipe | Database(s) | Description |
| ------ | ----------- | ----------- |
| 1 — environment-aware | `cookbook_r1_prod`, `cookbook_r1_nonprod` | Two side-by-side targets to show prod vs. non-prod |
| 2 — policy enforces itself | `cookbook_r2` | Policy enforcement recipe |
| 3 — package asks the server | `cookbook_r3` | Server-interrogation recipe |
| 4 — assets travel | `cookbook_r4` | Asset-portability recipe |
| 5 — scripts write scripts | `cookbook_r5` | Script-generation recipe |
| 6 — surviving a rebuild | `cookbook_r6` | Rebuild-survival recipe |
| 8 — authoring recyclebin hooks | `cookbook_r8` | Custom drop/restore hook authoring recipe |
| 9 — Extensions as a source of truth | `cookbook_r9` | Data-dictionary-from-metadata recipe |
| 11 — declared, not scripted | `cookbook_r11` | Declared-objects recipe |
| 12 — declared events | `cookbook_r12` | Scheduled-events recipe |
| 13 — index-only templates | `cookbook_r13` | Index-only template recipe |
| 14 — subfolder organization | `cookbook_r14` | Subfolder-layout recipe |
| 15 — expression change detection | `cookbook_r15` | Expression-change recipe |
| 16 — trust the round trip | `cookbook_r16` | Round-trip recipe |

Recipes 7 and 10 bring their own databases, so they have no row here.

Each database is created on all four engines:

| Engine     | Databases |
| ---------- | --------- |
| SQL Server | `cookbook_r1_prod`, `cookbook_r1_nonprod`, `cookbook_r2` … `cookbook_r6`, `cookbook_r8`, `cookbook_r9`, `cookbook_r11` … `cookbook_r16` |
| PostgreSQL | `cookbook_r1_prod`, `cookbook_r1_nonprod`, `cookbook_r2` … `cookbook_r6`, `cookbook_r8`, `cookbook_r9`, `cookbook_r11` … `cookbook_r16` |
| MySQL      | `cookbook_r1_prod`, `cookbook_r1_nonprod`, `cookbook_r2` … `cookbook_r6`, `cookbook_r8`, `cookbook_r9`, `cookbook_r11` … `cookbook_r16` |
| MariaDB    | `cookbook_r1_prod`, `cookbook_r1_nonprod`, `cookbook_r2` … `cookbook_r6`, `cookbook_r8`, `cookbook_r9`, `cookbook_r11` … `cookbook_r16` |

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

Each recipe's database is scoped to that recipe, so labs stay isolated from one another. But if a
lab run leaves one in an odd state — or you just want a recipe's database empty again — reset it:

```bash
# from course4-setup
bash setup-databases.sh --reset
```

```powershell
# from course4-setup
.\setup-databases.ps1 -Reset
```

Each database is dropped and recreated empty, reported as `PASS (reset)`. **Only databases these
scripts created are ever dropped.** On your own server, a database carrying one of these names that
the labs didn't create is refused and left untouched — you'll be told to rename or move it. Nothing
of yours is at risk.
