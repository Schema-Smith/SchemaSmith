# Course 11 — Setup: stand up the Vault

Course 11 is about what SchemaSmith **refuses** to do, and why every one of those refusals is a
feature. The scenario is a compliance archive — financial records under a retention mandate — because
it is the case where silently moving or dropping data would be the worst thing a deploy tool could do.
Every guard in the course is motivated by the database you are about to create.

Run this once before Module 1.

```bash
./setup-databases.sh          # or:  .\setup-databases.ps1
```

```
SQL Server
  vault_m1                 PASS (+ pf_vault_year / ps_vault_year / ps_vault_year_alt)
  vault_m2                 PASS
  vault_m3                 PASS
  vault_m4                 PASS
PostgreSQL
  vault_m1                 PASS (+ vault_ts tablespace)
  ...
```

Re-running is safe. `--reset` (`-Reset` in PowerShell) drops and recreates the databases empty — and
only ever drops a database the labs created.

## What it creates, and the part that is the lesson

Four empty databases, one per module: `vault_m1` … `vault_m4`.

And then something worth stopping on. It also creates:

- a SQL Server **partition function** (`pf_vault_year`) and **two partition schemes**
  (`ps_vault_year`, `ps_vault_year_alt`)
- a PostgreSQL **tablespace** (`vault_ts`)

**SchemaSmith created none of them, and never would.** Module 1 has your package *name* a partition
scheme and a tablespace — it does not create them, exactly as it never creates a filegroup. Those are
server-side objects a DBA owns, with sizing, storage and placement decisions behind them that no schema
package can see.

That asymmetry is the course's first lesson arriving before the first lesson. Your model gets to say
*put this table there*. It does not get to say what *there* is, or to invent one if it is missing —
which is why a declared scheme that does not exist fails **by name, before any DDL runs**, rather than
being improvised into existence.

So this script is doing the DBA's half of the job, on purpose, so that Module 1 can do yours.

## How it makes the PostgreSQL tablespace

Worth reading if you ever need to do this yourself. `CREATE TABLESPACE` needs a server-side directory
that already exists — PostgreSQL will not create one for you. Rather than assume a path is there, the
script has the server make it over the same connection:

```sql
COPY (SELECT 1) TO PROGRAM 'mkdir -p /var/lib/postgresql/vault_ts';
CREATE TABLESPACE vault_ts LOCATION '/var/lib/postgresql/vault_ts';
```

`COPY … TO PROGRAM` is superuser-only, which costs nothing here because `CREATE TABLESPACE` is too.

Both script twins **confirm every object exists** after creating it rather than assuming the create
worked, and guard `CREATE TABLESPACE` with an existence check rather than discarding the error — a
swallowed failure and an already-present tablespace must not look the same. A setup that reports PASS
over a missing object is worse than one that fails.

## What each module uses

| Database | Module | Carries |
| --- | --- | --- |
| `vault_m1` | 1 · Applied at CREATE | `LedgerEntry`, partitioned by year — the create-time-only roster |
| `vault_m2` | 2 · State you never described | `AuditTrail` + a schema-bound view — the rebuild guards and `DropSchemaBoundDependents` |
| `vault_m3` | 3 · The refusal you did not ask for | A partitioned table and the drop-by-absence guard |
| `vault_m4` | 4 · Unset means unmanaged | `RetainedDocument` — placement declared vs left alone |

## Cleanup

```bash
./setup-databases.sh --reset      # empty them again
```

To remove the server-side objects the script created:

```bash
../lab-sql.sh postgres postgres "DROP TABLESPACE IF EXISTS vault_ts"
../lab-sql.sh sqlserver vault_m1 "DROP PARTITION SCHEME ps_vault_year_alt; DROP PARTITION SCHEME ps_vault_year; DROP PARTITION FUNCTION pf_vault_year"
```

(Both only succeed once nothing is using them — which is itself the same posture the course teaches.)
