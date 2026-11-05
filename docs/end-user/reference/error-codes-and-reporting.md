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

> **MySQL:** The MySQL connector has no equivalent of the SQL Server `InfoMessage` or PostgreSQL `Notice` event, so a stored procedure cannot push a message to the client while it runs. Instead SchemaSmith's procedures write progress rows to a `SchemaSmith_StatusMessages` table (created during kindling), keyed by the writing session's `CONNECTION_ID()`, and a second polling connection reads the new rows every 200 ms and writes them to the progress log. After each step the rows are also read through the session's own connection, so nothing is lost when the poller cannot connect -- you get one `Live progress from the database is unavailable` warning and the messages arrive after each step instead of live. The session's rows are deleted when its work unit finishes, so the durable record is the progress log, not the table. Work units still run in parallel under `MaxThreads`, each on its own connection with its own poller.

## Where errors are logged

SchemaSmith writes two logs per run — a progress log (everything that happened) and an errors log (the faults). Most of what reaches the errors log is the same on every engine. The one thing that differs is the engine's own error text for a failed statement, and that difference is what catches people out.

On every engine the errors log records, each with its full exception:

- a run refused before it starts — an unrecognized argument, a missing or unreadable settings file, a package that cannot load, a failed validation or product script, a version check, a refused target (the `FAILED: …` line the console shows, exit `2`);
- a connection that could not be opened (`Unable to connect to <server>`, `Could not connect to <database> on <server>`) or that dropped mid-run (`Lost connection during …`);
- a database or schema discovery that failed, a `Target` filter rejection, and a folder `ShouldApplyExpression` that threw;
- an unhandled exception (exit `3`);
- with [`WriteDebugSql`](configuration.md#writedebugsql) on, one line per failed script: `Unable to quench '<path>': <error> — resolved SQL: <file>`.

> **SQL Server:** on top of that, every error the engine raises on the deploy connection — a failed script, a failed quench step — lands in the errors log with its message and line number (`at Line: N`), as well as in the progress log. It's the fastest place to read what went wrong.

> **PostgreSQL, MySQL, and MariaDB:** the engine's error text for a failed script or quench step is in the *progress* log (and in the failure roll-up), under `Unable to quench '<path>': …` or `FAILED to quench:`; on PostgreSQL that text leads with the SQLSTATE. It reaches the errors log only through the `WriteDebugSql` line above. The SQL itself is never in either log: it is in the debug file that line names.

## Failure triage roll-up

When a run has failures, SchemaQuench writes a third log — `SchemaQuench - Failures.log` — that answers "*which* targets failed?" at a glance. A deployment fans out to many `(server, database, schema)` targets in parallel and shares one interleaved progress log; the roll-up pulls every failure back into one consolidated, phase-grouped list.

Each entry names the failed **scope** — a tenant (`[server].[db] [Schema: x]`), a per-server `Before`/`After` product script (`[server]`), or a product-level `Validate` phase — along with the engine error, the resolved-SQL artifact path, and a captured tail of the log lines leading up to the failure. A loud `*** FAILED` banner also marks each failure live in the progress stream, so you can grep `*** FAILED` to jump straight to the failed scopes; the roll-up block echoes to the console at the end of the run.

A failed **user script** reads with the same detail as a mechanical failure. The `Error:` line carries the specific per-script error — `Unable to quench '<path>': <error>` (with a `(+N more)` tail when several scripts in the scope failed) — and the `Debug SQL:` line points at that script's resolved-SQL artifact when [`WriteDebugSql`](configuration.md#writedebugsql) is on, or says to rerun with `--WriteDebugSql` when it is off, so the roll-up sends you straight to the offending script and the SQL it produced.

It's always on and adds nothing to a clean run (no banner, no roll-up, an empty `Failures.log`). The captured-context depth is the `FailureContextLines` setting — default `25`, or `0` to disable context capture while still listing the failed scopes and their errors. See [configuration.md](configuration.md#failure-triage-roll-up).

## VerboseLogging

`VerboseLogging` is a SchemaQuench setting (a boolean, default `false`) that decides whether the informational messages **your own scripts** raise reach the progress log. Scripts commonly `PRINT` as they go, or `RAISE NOTICE` to say what they did; SchemaSmith mutes that chatter by default so a deployment log stays readable, and `VerboseLogging` is the dial that brings it back when you want it. SchemaSmith's own progress always appears and the engine's errors are always logged, whatever the dial says — it never hides a failure and never reveals hidden SchemaSmith progress; it only unmutes your scripts. What that means on each engine:

| Engine | Always logged | Only with `VerboseLogging: true` | Never logged |
| --- | --- | --- | --- |
| **SQL Server** | Every error of severity above 10; the messages SchemaSmith's own procedures raise with `RAISERROR … WITH STATE 100` | `PRINT` output and any other message of severity 10 or lower from your scripts (a `RAISERROR` with severity 10 or lower and a state other than 100) | — |
| **PostgreSQL** | Notices and warnings raised from inside a `"SchemaSmith"` routine | `RAISE NOTICE`, `RAISE WARNING` and `RAISE INFO` from your own scripts, `DO` blocks and functions | The `… does not exist, skipping` and `… already exists, skipping` notices that `DROP … IF EXISTS` and `CREATE … IF NOT EXISTS` produce |
| **MySQL and MariaDB** | The rows SchemaSmith's procedures write to `SchemaSmith_StatusMessages` | Nothing — the setting has no effect | A script's own output: the connector gives it no channel to the client |

Set it in the settings file (`"VerboseLogging": true`), as an environment variable (`SmithySettings_VerboseLogging=true`), or on the command line with either separator — `--VerboseLogging=true` or `--VerboseLogging:true` both set the key, like any other [configuration override](configuration.md#overriding-any-setting).

## Per-platform error codes

The same failure carries a different code on each engine. SQL Server usually prints the *message* (the number isn't always shown); PostgreSQL prints the SQLSTATE literally; MySQL and MariaDB print the classic MySQL-family error message. MariaDB inherits MySQL's error-number space, so the two share codes. Use this table to recognize the same fault across platforms.

| Failure | SQL Server | PostgreSQL | MySQL | MariaDB |
| --- | --- | --- | --- | --- |
| Foreign-key violation (orphan) | `547` | `23503` | `1452` | `1452` |
| NOT NULL violation | `515` | `23502` | `1048` | `1048` |
| Duplicate / unique-key | `1505` (index build) / `2601` / `2627` | `23505` | `1062` | `1062` |
| String or binary truncation | `8152` | `22001` | `1406` | `1406` |
| Type / conversion mismatch | `245` / `8115` | `22P02` | `1366` | `1366` |
| Deadlock (retried automatically) | `1205` | `40P01` | `1213` | `1213` |

> **Note:** Deadlocks are retried for you — SchemaSmith recognizes the deadlock by its code on every engine (SQL Server `1205`, PostgreSQL `40P01`, MySQL and MariaDB `1213`, so a server running in another language is classified the same way), falls back to the word `deadlock` in the message only when the typed code is missing, and re-runs the operation with a jittered backoff, up to 20 attempts; each retry logs `Transient contention from a parallel iteration; retrying (attempt N of 20)`. A lock-wait timeout (SQL Server `1222`, MySQL and MariaDB `1205`, PostgreSQL `55P03`) and the transient catalog races a parallel fan-out provokes are retried the same way. The retry applies only to SchemaSmith's own idempotent convergence procedures — the table, index, constraint, foreign-key and view quenches, which recompute desired-versus-existing from scratch on every call — never to a migration script or object script of yours, which fails if it is chosen as the victim, because re-running it is not known to be safe.

For the hands-on method — locating the failing phase, reading the artifact it leaves behind, and choosing a recovery — see the [troubleshooting guide](../guide/12-troubleshooting.md).

## Log messages

A deploy says what it is doing in plain sentences, and the one you are searching for is usually the one that explains why the run did less, or more, than you expected. This is the index: the lines a user is most likely to grep for, the engines that write them, what each means, and where the behavior behind it is documented. The strings are quoted as the tools write them (`<…>` marks a value), so a search for the fixed part finds the line.

| Message | Engine | What it means | Read more |
| --- | --- | --- | --- |
| `Not recognized: <argument>. Nothing was run.` | All | An argument the tool will never read — a misspelled switch, or a value written with a space instead of `:` or `=`. Exit `2`. | [CLI Switch Format](configuration.md#cli-switch-format) |
| `Configuration key '<key>' is not read by <Tool> and will have no effect. Check for a typo.` | All | A settings key in a section the tool owns that it does not recognize. The run continues. | [Unrecognized settings are reported](configuration.md#unrecognized-settings-are-reported) |
| `FAILED: <message>` | All | The run stopped for a reason you can fix — a failed validation or product script, a version check, a refused target. Exit `2`. | [Exit Codes](configuration.md#exit-codes) |
| `EXCEPTION - See the error log:` | All | An unhandled exception, a defect in the tool. Exit `3`; please report it. | [Exit Codes](configuration.md#exit-codes) |
| `*** FAILED [Template:<name>] ***` | All | A target failed. Grep for it to find each failed scope; the roll-up lists them together. | [Failure triage roll-up](#failure-triage-roll-up) |
| `Unable to quench '<path>': <error>` | All | A script failed with the engine's error. | [Debug SQL files](configuration.md#debug-sql-files) |
| `Resolved SQL written to: <file>` / `Rerun with --WriteDebugSql to write the resolved SQL of this step to <directory>.` | All | Where the failed step's token-expanded SQL is, or how to get it. | [Debug SQL files](configuration.md#debug-sql-files) |
| `Could not write debug SQL artifact '<name>': <error>` / `Could not write resolved-SQL artifact for '<path>': <error>` | All | The debug file could not be written (usually `ArtifactPath` is not writable). The step itself is unaffected. | [ArtifactPath](configuration.md#artifactpath) |
| `Lost connection to <server> … — the server stopped responding mid-deploy` | All | The connection dropped during the run; nothing applied is rolled back. | [Troubleshooting](../guide/12-troubleshooting.md) |
| `Would APPLY: <path>` / `Would SKIP (unchanged): <path>` | All | The WhatIf plan for a script: it would run, or it is unchanged since its last apply and would be left alone. | [WhatIf Mode](schemaquench.md#whatif-mode) |
| `Unchanged, not re-run: <path>` | All | The object script's text and the module the server renders are both as they were after the last apply, so it was not re-run. | [Unchanged Object Scripts](schemaquench.md#unchanged-object-scripts) |
| `Could not record the script stamp, so object scripts will run again next time: <error>` | All | The deploy login cannot write SchemaSmith's `ScriptStamp` table; the script applied, but every object script runs again on the next deploy. Logged once per target. | [Unchanged Object Scripts](schemaquench.md#unchanged-object-scripts) |
| `Skipping (previously quenched) <path>` | All | A run-once migration script already recorded for this database and slot. | [Migration Script Tracking](schemaquench.md#migration-script-tracking) |
| `Skipping (previously quenched per checkpoint) <path>` | All | A resumed run skipping a script its checkpoint shows as done. | [Checkpoint and Resume](schemaquench.md#checkpoint-and-resume) |
| `Skipped (ShouldNotApply): <path>` | All | The script raised the `SHOULD NOT APPLY` sentinel; it is recorded as done and not run again. | [Script-Level Runtime Skip](schemaquench.md#script-level-runtime-skip) |
| `Cannot read which <slot> scripts have already run from <table>.` / `Cannot read or record which <slot> scripts have already run:` | All | The deploy login lacks `SELECT` (SQL Server: `SELECT` and `INSERT`) on `CompletedMigrationScripts`; nothing in the slot was run. | [Datafix Role Grants](datafix-role-grants.md) |
| `The script applied, but could not be recorded as run, so the next deploy would run it again:` | All | The script's data change stands, but its tracking row could not be inserted; the script is reported as failed. | [Datafix Role Grants](datafix-role-grants.md) |
| `Skipping folder '<folder>' … — ShouldApplyExpression evaluated false` | All | A folder gate turned the folder off for this target (or, for a product-level folder, this server). | [ShouldApplyExpression](schemaquench.md#shouldapplyexpression-and-conditional-deployment) |
| `Skipping data delivery for <table> — ShouldApplyExpression evaluated false` | All | A `DataDelivery` gate evaluated false for this target. | [Table Data Delivery](schemaquench.md#table-data-delivery) |
| `Invalid baseline for this release` | All | The template's or product's `BaselineValidationScript` returned false; the deploy stops. | [Database Quench Sequence](schemaquench.md#database-quench-sequence) |
| `Skipping <feature> on <object> (downgraded)` (SQL Server also: `<feature> skipped: requires <version> … (downgraded)`) | All | Under `UnsupportedFeaturePolicy: warn` the object was emitted without a feature the target version lacks. The summary lists each one; exit `1`. | [Engine Version Compatibility](schemaquench.md#engine-version-compatibility) |
| `Re-baselined <n> recorded expression(s): …` / `Re-baselined <n> recorded materialized view definition(s) …` | All / PostgreSQL | After an engine upgrade the stored canonical text of recorded expressions was refreshed. No object was changed. | [Expression change detection](schemaquench.md#expression-change-detection) |
| `Handing table <name> to SchemaSmith.CustomTableDrop (no longer in product)` (MySQL and MariaDB: `SchemaSmith_CustomTableDrop`) | All | A drop-by-absence was routed to your custom drop hook instead of a plain `DROP TABLE`. | [DropTablesRemovedFromProduct](schemaquench.md#droptablesremovedfromproduct) |
| `Partitioned table(s) skipped by drop-by-absence guard; drop manually or mark PreventDrop.` | MySQL, MariaDB | A table no longer in the package is partitioned. The modified-tables step fails with this error rather than drop it by absence: drop it yourself, or mark it `PreventDrop`. | [DropTablesRemovedFromProduct](schemaquench.md#droptablesremovedfromproduct) |
| `Drop index not declared for this deploy: <table>.<index>` | MySQL, MariaDB | An index on a managed table that the package does not declare for this target — removed from the product, or never declared — is being dropped. | [DropIndexesRemovedFromProduct](configuration.md#dropindexesremovedfromproduct), [DropUnknownIndexes](configuration.md#dropunknownindexes) |
| `… (not covered by OnlineApply)` | SQL Server, PostgreSQL | A change `OnlineApply` has no form for on that engine ran as written, under `Prefer` or `Require` too. | [OnlineApply](configuration.md#onlineapply) |
| `… (online, after this pass)` | PostgreSQL | A `CONCURRENTLY` statement that runs on its own once the step that needs it has finished. | [OnlineApply — PostgreSQL](configuration.md#postgresql) |
| `PreventDrop is ENABLED — this environment will skip every drop-by-absence …` | All | The no-drop protection tier is on; suppressed drops are listed in the summary. | [PreventDrop](schemaquench.md#preventdrop) |
| `<object> was dropped for a column change and this run did not re-create it, so its grants were not put back.` | SQL Server | A schema-bound module was dropped under `DropSchemaBoundDependents` and no script in the run recreated it. | [DropSchemaBoundDependents](configuration.md#dropschemabounddependents) |
| `Transient contention from a parallel iteration; retrying (attempt <n> of 20)` | All | A convergence procedure was a deadlock or lock-timeout victim and is being re-run. | [Per-platform error codes](#per-platform-error-codes) |
| `Live progress from the database is unavailable (<reason>); its messages are logged after each step instead.` / `The database's step messages cannot be read (<reason>); this run's log has none of them.` | MySQL, MariaDB | The status-message poller could not open its second connection, or the status table could not be read at all. | [The three reporting channels](#the-three-reporting-channels) |
| `Deployment summary written: <path>` / `Could not write deployment summary: <error>` | All | Where the run's `Summary.json` and `Summary.md` went, or why they could not be written (the run is unaffected). | [Deployment Summary Report](deployment-summary-report.md) |
| `ScrubArtifacts is deprecated and does nothing …` | All | A deprecated setting is still in the settings file. Remove it. | [ScrubArtifacts](configuration.md#scrubartifacts) |
