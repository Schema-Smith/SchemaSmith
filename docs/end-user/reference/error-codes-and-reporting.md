# Error Codes & Reporting Channels

When a deployment fails, the *same* logical problem — an orphaned foreign key, a duplicate value, a null in a required column — surfaces differently on SQL Server, PostgreSQL, MySQL, and MariaDB. The error text differs, the numeric code differs, and even *which log file* carries the detail differs. This page is the lookup table: how each engine reports back, where to read it, and what the codes mean across all four platforms.

---

## The three reporting channels

SchemaSmith runs the same convergence engine on every database, but each engine hands progress and errors back through a different mechanism. Knowing which one you're reading saves time when a deploy stops. For the structured, machine-readable receipt of the whole run — every target, timing, and failure in one file — see the [Deployment Summary Report](deployment-summary-report.md).

| Engine | Channel | How it works |
| --- | --- | --- |
| **SQL Server** | `InfoMessage` events | The engine raises messages on the connection; SchemaSmith promotes them to the log. |
| **PostgreSQL** | `Notice` stream | Server notices flow to the log; routine "already exists, skipping" / "does not exist, skipping" noise is filtered out. |
| **MySQL** | `SchemaSmith_StatusMessages` sidecar | Progress is written to a table and polled — MySQL has no async message event. |

> **MariaDB:** MariaDB reports through the same channel as MySQL — the `SchemaSmith_StatusMessages` sidecar table polled on a second connection — with MySQL-compatible error codes. Everywhere this page names MySQL, MariaDB behaves the same. Four platforms, three reporting channels.

> **MySQL:** MySQL runs the whole deployment on a single connection and its driver has no equivalent of the SQL Server `InfoMessage` or PostgreSQL `Notice` event, so it can't push a message while that connection is busy doing work. Instead, the engine writes progress rows to a `SchemaSmith_StatusMessages` table and a separate polling connection flushes them to the log every few hundred milliseconds. The rows are cleaned up when the run ends, so the durable record is the progress log, not the table.

## Where errors are logged

SchemaSmith writes two logs per run — a progress log (everything that happened) and an errors log (the faults). Which one carries the fault detail depends on the engine, and this catches people out.

> **SQL Server:** the errors log is populated — each fault lands there with its message and the line number (`at Line: N`), in addition to the progress log. It's the fastest place to read what went wrong.

> **PostgreSQL, MySQL, and MariaDB:** the errors log is **empty**. The fault detail — including the SQLSTATE on PostgreSQL — is in the *progress* log only. Don't go looking in the errors log; it won't be there.

## Failure triage roll-up

When a run has failures, SchemaQuench writes a third log — `SchemaQuench - Failures.log` — that answers "*which* targets failed?" at a glance. A deployment fans out to many `(server, database, schema)` targets in parallel and shares one interleaved progress log; the roll-up pulls every failure back into one consolidated, phase-grouped list.

Each entry names the failed **scope** — a tenant (`[server].[db] [Schema: x]`), a per-server `Before`/`After` product script (`[server]`), or a product-level `Validate` phase — along with the engine error, the resolved-SQL artifact path, and a captured tail of the log lines leading up to the failure. A loud `*** FAILED` banner also marks each failure live in the progress stream, so you can grep `*** FAILED` to jump straight to the failed scopes; the roll-up block echoes to the console at the end of the run.

A failed **user script** now reads with the same detail as a mechanical failure. The `Error:` line carries the specific per-script error — `Unable to quench '<path>': <error>` (with a `(+N more)` tail when several scripts in the scope failed) — and the `Debug SQL:` line points at that script's resolved-SQL artifact when [`WriteDebugSql`](configuration.md#writedebugsql) is on, or says to rerun with `--WriteDebugSql` when it is off. Previously a user-script failure showed only a generic "unable to quench all scripts" message with `n/a` for the artifact; now the roll-up sends you straight to the offending script and the SQL it produced.

It's always on and adds nothing to a clean run (no banner, no roll-up, an empty `Failures.log`). The captured-context depth is the `FailureContextLines` setting — default `25`, or `0` to disable context capture while still listing the failed scopes and their errors. See [configuration.md](configuration.md#failure-triage-roll-up).

## VerboseLogging

`VerboseLogging` is a top-level configuration switch (a boolean, default `false`) that affects **SQL Server and PostgreSQL**. Scripts commonly emit `PRINT` statements and low-severity warnings (SQL Server) or `RAISE NOTICE` output (PostgreSQL); SchemaSmith suppresses that chatter from your scripts by default so the deployment log stays readable, and `VerboseLogging` is the dial that brings it back when you want to see it. SchemaSmith's own progress messages always appear.

With it off (the default), a `PRINT` or a low-severity `RAISERROR` from one of your scripts is filtered out of the log. Turn it on and those messages surface. It affects **your scripts' output only** — SchemaSmith's own phase progress always shows regardless (the engine emits its progress with an always-surface flag), so turning `VerboseLogging` on won't reveal any hidden SchemaSmith progress; it only unmutes your scripts.

> **PostgreSQL, MySQL, and MariaDB:** the switch has no effect. PostgreSQL surfaces its `RAISE NOTICE` output by default, and MySQL and MariaDB user scripts have no progress channel at all — so there's nothing for the dial to gate on those engines.

Set it in the settings file (`"VerboseLogging": true`), as an environment variable (`SmithySettings_VerboseLogging=true`), or on the command line:

```bash
SchemaQuench --VerboseLogging=true
```

> **Note:** Use the `=` form on the command line. `--VerboseLogging=true` is the config-override syntax and takes effect; the colon form `--VerboseLogging:true` is silently ignored for configuration settings (it applies only to the tool's named switches like `--ConfigFile` and `--LogPath`). The settings-file and environment-variable forms always work.

## Per-platform error codes

The same failure carries a different code on each engine. SQL Server usually prints the *message* (the number isn't always shown); PostgreSQL prints the SQLSTATE literally; MySQL and MariaDB print the classic MySQL-family error message. MariaDB inherits MySQL's error-number space, so the two share codes. Use this table to recognize the same fault across platforms.

| Failure | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| Foreign-key violation (orphan) | `547` | `23503` | `1452` | `1452` |
| NOT NULL violation | `515` | `23502` | `1048` | `1048` |
| Duplicate / unique-key | `1505` (index build) / `2601` / `2627` | `23505` | `1062` | `1062` |
| String or binary truncation | `8152` | `22001` | `1406` | `1406` |
| Type / conversion mismatch | `245` / `8115` | `22P02` | `1366` | `1366` |
| Deadlock (retried automatically) | `1205` | `40P01` | — (message-matched) | — (message-matched) |

> **Note:** Deadlocks are retried for you — SchemaSmith detects the deadlock code and re-runs the operation with backoff, so a transient lock collision resolves itself rather than failing the deploy.

For the hands-on method — locating the failing phase, reading the artifact it leaves behind, and choosing a recovery — see the troubleshooting guide.
