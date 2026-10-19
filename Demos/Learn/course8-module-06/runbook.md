# SchemaSmith deploy — team diagnostic runbook

*A fill-in template from Course 8. Copy it into your own repo, keep the reference tables, and fill the
"Our incidents" log as you go. When a deploy fails at 2am, start at the top and work down.*

---

## 1. The core loop

**Locate the phase → read the artifact → pick the recovery.** Every SchemaSmith failure fits this.

1. Open `SchemaQuench - Progress.log`. Find the last `Quenching <phase>` line before the error — that's
   **where** it stopped.
2. Read the **per-item line** (below) to learn **whose** problem it is.
3. Open the artifact it names, reproduce if needed, apply the recovery (section 6).

## 2. The quench phase map

The order SchemaSmith runs, per database:

```
Kindling → ValidateBaseline → object scripts → [parse tables] → Before →
ModifiedTables → BetweenTablesAndKeys → Indexes & Constraints → AfterTable →
TableDataDelivery → ForeignKeys → [Materialized/Indexed Views] → After → VersionStamp
```

Foreign keys run **after** data delivery. A changed index is dropped in ModifiedTables and rebuilt at
Indexes & Constraints (so it fails one phase "later" than you edited it).

## 3. Whose problem is it — the per-item line

| The log says… | It's a… | Artifact |
| --- | --- | --- |
| `FAILED to quench:` + engine error, no item named | **mechanical phase** (the engine's computed DDL) | copy-runnable `EXEC`/`CALL <proc>` — no batch marker |
| `Unable to quench '<path>': …` | **user script** | `Failed <script> ….sql` with a `>>> FAILING BATCH (#N)` marker |
| `Error delivering <table>: …` | **data delivery** | `Failed DataDelivery <table>#N ….sql` — the MERGE, marked |

(User-input failures also print a trailing generic `FAILED to quench: / Unable to quench all scripts`
— ignore it; the per-item line above is the tell.)

## 4. Where to look, per engine

Each database hands errors back through a different door:

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| **Channel** | `InfoMessage` events | `Notice` stream | `SchemaSmith_StatusMessages` sidecar table (polled) |
| **Fault detail** | ProgressLog **and Errors.log** (with `at Line: N`) | ProgressLog (SQLSTATE) | ProgressLog |
| **Errors.log** | populated | **empty** | **empty** |
| **`VerboseLogging`** | surfaces your scripts' `PRINT`/low-severity output (suppressed by default); use `=true` on the CLI | no effect | no effect |

**Rule of thumb:** on SQL Server, `Errors.log` gives you the fault + line fast. On PostgreSQL and
MySQL, read the `Progress.log` — `Errors.log` will be empty.

## 5. Per-platform error codes

The same failure, three codes:

| Failure | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Foreign-key orphan | `547` | `23503` | `1452` |
| NOT NULL violation | `515` | `23502` | `1048` |
| Duplicate / unique key | `2601` / `2627` | `23505` | `1062` |
| String/binary truncation | `2628` (`8152` below compatibility level 150) | `22001` | `1406` |
| Type / cast mismatch | `245` / `8115` | `22P02` | `1366` |

(SQL Server prints the *message*, not always the number; PostgreSQL prints the SQLSTATE literally;
MySQL prints the classic message.)

## 6. Recovery — which tool

| Situation | Tool |
| --- | --- |
| A script bug you fixed in the package | **fix + plain re-run** (the default — discards the checkpoint, re-converges) |
| Same fix, but don't redo expensive completed phases | **`--ResumeQuench`** (keeps the checkpoint, resumes at the failure; Kindling + missing-tables still re-run) |
| You fixed the problem **by hand**, outside the package | **mark-done** — `INSERT` a row into `CompletedMigrationScripts` so the run-once script is skipped |

The checkpoint (`%TEMP%/schemaquench-checkpoints`, or `--CheckpointDirectory:`) is your map: `[Completed
Steps]` + per-slot completed scripts. Preserved on failure, deleted on success.

## 7. Our incidents

*Fill this in as your team hits them — it becomes the fastest section over time.*

| Date | Phase | Engine | Code | Root cause | Fix (tool) |
| --- | --- | --- | --- | --- | --- |
|  |  |  |  |  |  |
|  |  |  |  |  |  |

---

*Full reference: the SchemaSmith end-user docs (error codes & reporting channels). Method + worked
examples: Course 8, Modules 0–6.*
