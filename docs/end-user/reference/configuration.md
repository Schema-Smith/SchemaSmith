# Configuration Reference

Applies to: SchemaQuench, SchemaTongs, DataTongs — SQL Server, PostgreSQL, MySQL, and MariaDB.

---

Every SchemaSmith CLI tool shares the same configuration spine -- one consistent system for settings files, environment variables, and command-line switches. Learn it once and it works the same way whether you're casting schemas, quenching databases, or extracting data, and whether your target is SQL Server, PostgreSQL, MySQL, or MariaDB. Tool-specific settings live on each tool's own reference page; this page covers the shared foundation.

The **platform** each operation runs against is not a CLI switch or an environment variable -- it's a property of the schema package itself. `Product.json` declares `Platform` (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`), and the tools adapt their behavior accordingly. Each package is bound to its declared platform -- a `MariaDb` package deploys to MariaDB, not MySQL, and vice versa -- so the four package types are distinct even where MySQL and MariaDB share an engine family. One tool, four engines, same muscle memory.

> **MariaDB and MySQL.** MariaDB is a first-class platform with its own `Platform: MariaDb` package type, and it runs on SchemaSmith's MySQL engine. Wherever this documentation shows a MySQL example, idiom, or behavior, it applies to MariaDB as well — the two diverge only where explicitly called out (for example JSON handling, collation defaults, and the accepted `MinimumVersion` format). The packages themselves are **not** interchangeable: point a `MariaDb` package at a MariaDB target and a `MySQL` package at a MySQL target, never the reverse.

---

## CLI Switch Format

SchemaSmith is flexible about how you pass switches -- pick whichever style feels natural. All switches accept either a double-dash (`--`) or forward-slash (`/`) prefix. Separate the switch name from its value with `:` or `=`. A single leading dash (`-`) also works.

```
--switch:value
--switch=value
/switch:value
-switch:value
```

Switch names are **case-insensitive** -- `--logpath`, `--LogPath`, and `--LOGPATH` all do the same thing.

Values that contain spaces must be quoted:

```
--ConfigFile:"C:\configs\my config.json"
/LogPath:"C:\My Logs\SchemaSmith"
```

Flags that take no value (like `--version` or `--help`) are specified without a separator.

---

## Common Switches

Every SchemaSmith CLI tool recognizes these switches. They're processed before any configuration is loaded.

| Switch | Aliases | Description |
|---|---|---|
| `--version` | `-v`, `--ver` | Print the tool name and version number, then exit. |
| `--help` | `-h`, `-?` | Print the available command-line switches, then exit. |
| `--ConfigFile:<path>` | | Path to the settings file. Overrides the default `<ToolName>.settings.json`. |
| `--LogPath:<path>` | | Directory for log files and backup subdirectories. Defaults to the tool's executable directory. |
| `--ConnectionString:<connstr>` | | Full ADO.NET / Npgsql / MySqlConnector connection string appropriate to the target platform. When provided, this bypasses all individual connection settings (`Server`, `Port`, `User`, `Password`, `ConnectionProperties`). |
| `--Encrypt` / `--NoEncrypt` | | Force transport encryption on or off using the right property per engine (SQL Server `Encrypt`, PostgreSQL `SSL Mode`, MySQL/MariaDB `SslMode`). Wins over `ConnectionProperties`. Applies to SchemaQuench, SchemaTongs, and DataTongs. |

### Examples

```bash
# Deploy a schema package with a custom config and log directory
SchemaQuench --ConfigFile:production.json --LogPath:C:\Logs

# Extract a schema using a specific config
SchemaTongs --ConfigFile:extract-config.json

# Export data merge scripts, overriding the connection entirely (SQL Server)
DataTongs --ConnectionString:"Data Source=myserver;Initial Catalog=mydb;User ID=sa;Password=secret;TrustServerCertificate=True;"

# Same thing, PostgreSQL
DataTongs --ConnectionString:"Host=myserver;Port=5432;Database=mydb;Username=deploy;Password=secret;"

# Same thing, MySQL
DataTongs --ConnectionString:"Server=myserver;Port=3306;Database=mydb;User=deploy;Password=secret;"

# Check which version is installed
SchemaQuench --version

# See available switches
SchemaTongs --help
```

---

## Configuration Hierarchy

SchemaSmith layers configuration so you can set sensible defaults in a file and override just the pieces that change per environment. Later sources override earlier ones. The full chain, from lowest to highest priority:

1. **Settings file** -- `<ToolName>.settings.json`
2. **User secrets** -- .NET user secrets (debug builds only, not present in release builds)
3. **Environment variables** -- prefixed with `SmithySettings_`
4. **CLI switches** -- the named switches (`--ConnectionString`, `--ConfigFile`, `--LogPath`) plus the `--Key=value` override form described below

This means a value set in the settings file can be overridden by an environment variable, and a CLI switch always wins.

### Override example

Suppose your `SchemaQuench.settings.json` sets the server:

```json
{
    "Target": {
        "Server": "dev-server"
    }
}
```

You can override just the server for a single run using an environment variable:

```bash
export SmithySettings_Target__Server=staging-server
SchemaQuench
```

Or override the entire connection from the command line:

```bash
SchemaQuench --ConnectionString:"Host=prod-server;Database=mydb;Username=deploy;Password=s3cret;"
```

The `--ConnectionString` switch bypasses all individual connection settings -- `Server`, `Port`, `User`, `Password`, and `ConnectionProperties` are all ignored when a full connection string is provided.

### Overriding any setting

Beyond the named switches, **any** configuration option can be set or overridden from the command line with a `--Key=value` switch. This is the same override reach the `SmithySettings_` environment variables give you, without touching a file or exporting a variable -- ideal for CI and one-off runs.

The rule mirrors the environment-variable grammar exactly: an `=` separates the key from the value, and a double underscore (`__`) nests into the configuration hierarchy. Whatever you'd write after `SmithySettings_` you write after `--`:

| Command-line switch | Maps to config key | Environment-variable equivalent |
|---|---|---|
| `--MinimumVersion=16` | `MinimumVersion` | `SmithySettings_MinimumVersion` |
| `--Target__Server=prod-db` | `Target:Server` | `SmithySettings_Target__Server` |
| `--Source__Password=s3cret` | `Source:Password` | `SmithySettings_Source__Password` |
| `--Target__ConnectionProperties__Encrypt=true` | `Target:ConnectionProperties:Encrypt` | `SmithySettings_Target__ConnectionProperties__Encrypt` |

```bash
# Override just the server and skip cert validation for a single run
SchemaQuench --Target__Server=staging-db --Target__ConnectionProperties__TrustServerCertificate=true
```

A `--Key=value` override sits at the top of the [configuration hierarchy](#configuration-hierarchy) -- it wins over the settings file, user secrets, and environment variables.

Two things to keep in mind:

- The `=` is required for an override. The named switches (`--LogPath`, `--ConfigFile`, `--ConnectionString`) accept a `:` separator, but nesting into arbitrary settings needs the `__`/`=` form so a value containing a colon (a Windows path, a `host:port`) is never mistaken for a key boundary.
- `--ConnectionString` still bypasses the individual connection settings. If you pass both `--ConnectionString` and a `--Source__Server=` / `--Target__Server=` override, the full connection string wins for connecting.

---

## Settings Files

Each tool looks for its own settings file by name:

| Tool | Default settings file |
|---|---|
| SchemaQuench | `SchemaQuench.settings.json` |
| SchemaTongs | `SchemaTongs.settings.json` |
| DataTongs | `DataTongs.settings.json` |

The tool searches for the file in two locations, in order:

1. The **current working directory** (where you run the command)
2. The **tool's executable directory** (where the binary lives)

If the file is found in the current directory, that copy is used. If not, the tool falls back to the executable directory. If neither location has the file, the tool starts with an empty configuration (any required values must come from environment variables or CLI switches).

To use a different file entirely, pass the `--ConfigFile` switch:

```bash
SchemaQuench --ConfigFile:C:\configs\production.json
```

The path can be absolute or relative to the current working directory.

### Unrecognized settings are reported

A mistyped setting is otherwise invisible. `Target:Sever` binds nothing, so the run proceeds exactly as though you had never set it -- and a deployment that silently ignores half your configuration is worse than one that refuses to start.

Each tool therefore checks the settings it was given against the settings it actually reads, and warns about anything it does not recognize:

```
WARN  Configuration key 'Target:Sever' is not read by SchemaQuench and will have no effect. Check for a typo.
```

This is the same treatment `--NoSuchSwitch` already gets on the command line, and it covers every configuration source -- settings file, `SmithySettings_` environment variables, and CLI overrides all land in the same configuration and are all checked.

Three things are deliberately **not** reported:

- **Sections the tool does not own.** SchemaQuench says nothing about a `Source:` section, and neither Tongs tool comments on `Target:`. A file may serve more than one tool, or carry settings for a version you have not installed yet.
- **Open sections**, where you choose the names: `ScriptTokens`, `Target:ConnectionProperties`, `Source:ConnectionProperties`, `Target:TemplateTargets`, and `FolderMapping`. Anything beneath these is your value, not a setting name.
- **Array elements** such as `Target:Databases:0`.

It is a warning, not an error -- the run continues. Treat one as a typo until proven otherwise.

### The settings surface is enforced, not just documented

The set of settings each tool reads is data the tool checks itself against at startup, not a list maintained separately in prose. That is why an unrecognized key is reported rather than silently ignored: the reference above and the check the tool performs come from the same source, so they cannot drift apart.

---

## Environment Variables

Environment variables give you a clean way to inject configuration without touching files on disk -- exactly what you need in CI/CD pipelines and containers. SchemaQuench, SchemaTongs, and DataTongs read environment variables prefixed with `SmithySettings_`. The prefix is stripped, and double underscores (`__`) map to hierarchy separators in the configuration structure.

### Mapping rules

| Environment variable | Maps to config key | JSON equivalent |
|---|---|---|
| `SmithySettings_Target__Server` | `Target:Server` | `{ "Target": { "Server": "..." } }` |
| `SmithySettings_Target__Port` | `Target:Port` | `{ "Target": { "Port": "..." } }` |
| `SmithySettings_Target__User` | `Target:User` | `{ "Target": { "User": "..." } }` |
| `SmithySettings_Target__Password` | `Target:Password` | `{ "Target": { "Password": "..." } }` |
| `SmithySettings_Target__ConnectionProperties__TrustServerCertificate` | `Target:ConnectionProperties:TrustServerCertificate` | `{ "Target": { "ConnectionProperties": { "TrustServerCertificate": "..." } } }` |
| `SmithySettings_SchemaPackagePath` | `SchemaPackagePath` | `{ "SchemaPackagePath": "..." }` |
| `SmithySettings_WhatIfONLY` | `WhatIfONLY` | `{ "WhatIfONLY": "..." }` |

SchemaTongs and DataTongs use `Source` instead of `Target` for their connection section, so the equivalent variables start with `SmithySettings_Source__`:

```bash
# SchemaQuench connection
export SmithySettings_Target__Server=myserver
export SmithySettings_Target__Password=s3cret

# SchemaTongs / DataTongs connection
export SmithySettings_Source__Server=myserver
export SmithySettings_Source__Password=s3cret
```

Environment variables are especially useful in CI/CD pipelines and containers where you don't want secrets in files on disk.

---

## Connection Configuration

Each tool has one connection section. SchemaQuench uses a `Target` section (it writes to the server), while SchemaTongs and DataTongs use a `Source` section (they read from the server). The structure is the same either way, and the same keys work for every supported platform -- the adapter under the hood routes the call to the right client library based on the product's declared platform.

### Individual connection settings

```json
{
    "Target": {
        "Server": "myserver",
        "Port": "",
        "User": "deploy",
        "Password": "s3cret",
        "ConnectionProperties": {
            "TrustServerCertificate": "True"
        }
    }
}
```

| Key | Purpose |
|---|---|
| `Server` | Database server hostname or IP address. |
| `Port` | TCP port. Leave blank for the platform default (SQL Server `1433`, PostgreSQL `5432`, MySQL `3306`). |
| `User` | Login username. |
| `Password` | Login password. |
| `IntegratedSecurity` | SQL Server only. Set to `true` to force Windows Authentication, superseding any configured `User`/`Password`. |
| `Database` | Database name. Used by SchemaTongs and DataTongs. SchemaQuench reads its target databases from the schema package instead. |
| `ConnectionProperties` | Dictionary of additional connection string properties. Each key-value pair is appended to the built connection string. |
| `UnsupportedFeaturePolicy` | What to do when the package declares a feature the detected target version cannot support (e.g. `NULLS NOT DISTINCT` on PostgreSQL &lt; 15, Always Encrypted on SQL Server &lt; 2016, a CHECK constraint on MySQL &lt; 8.0.16). Applies on SQL Server, PostgreSQL, MySQL, and MariaDB. `warn` (default) emits the object without the unsupported aspect and records it under **Unsupported Feature Downgrades** in the deployment summary; `fail` aborts the run with a message naming the feature and the version it requires. See [SchemaQuench — Version-adaptive code generation](schemaquench.md#version-adaptive-code-generation). |

**Windows authentication (SQL Server only):** Leave both `User` and `Password` blank, *or* set `IntegratedSecurity` to `true`. Either way the tool connects using the identity of the process. Prefer `IntegratedSecurity=true` when you are layering environment-variable or command-line overrides over a settings file that already carries a `User`/`Password`: an override can add or change a value but cannot *clear* one -- on Windows, setting an environment variable to empty deletes it, so the file's credential would otherwise remain in force. The key lives in whichever connection section the tool owns, per the section note above: `Target:IntegratedSecurity` for SchemaQuench, `Source:IntegratedSecurity` for SchemaTongs and DataTongs. It is settable from any shell (for example `SmithySettings_Target__IntegratedSecurity=true`, or `SmithySettings_Source__IntegratedSecurity=true` for the Tongs tools). PostgreSQL, MySQL, and MariaDB require explicit credentials.

**Platform-specific connection properties:**

- *SQL Server* — `TrustServerCertificate`, `Encrypt`, `ApplicationIntent`, etc.
- *PostgreSQL* — `SslMode`, `Pooling`, `Timeout`, etc. (Npgsql keys)
- *MySQL* — `SslMode`, `ConnectionTimeout`, `AllowPublicKeyRetrieval`, etc. (MySqlConnector keys)

Whatever you put in `ConnectionProperties` is appended to the connection string for that platform's client library. Consult the corresponding driver documentation for the exhaustive list.

**Transport encryption.** SchemaSmith connects to SQL Server with `Encrypt=True` declared explicitly (matching the Microsoft.Data.SqlClient default) — connections are encrypted unless you opt out. PostgreSQL and MySQL/MariaDB follow their own driver defaults. To change the posture for a run:

- **`--NoEncrypt`** turns encryption off; **`--Encrypt`** forces it on. The switch sets the correct property for the target engine (SQL Server `Encrypt`, PostgreSQL `SSL Mode`, MySQL/MariaDB `SslMode`) and wins over `ConnectionProperties`. Use `--NoEncrypt` for an older or hardened SQL Server instance that classic `sqlcmd` reaches unencrypted but whose TLS handshake the modern driver cannot complete.
- Or set it explicitly through `ConnectionProperties` (`Encrypt` for SQL Server, `SslMode`/`SSL Mode` for MySQL/PostgreSQL), or with the equivalent `--Target__ConnectionProperties__Encrypt=false` override.

### Full connection string override

Instead of individual settings, you can provide a complete connection string for the target platform:

```bash
# SQL Server
SchemaQuench --ConnectionString:"Data Source=myserver;Initial Catalog=mydb;User ID=sa;Password=s3cret;TrustServerCertificate=True;"

# PostgreSQL
SchemaQuench --ConnectionString:"Host=myserver;Port=5432;Database=mydb;Username=deploy;Password=s3cret;"

# MySQL
SchemaQuench --ConnectionString:"Server=myserver;Port=3306;Database=mydb;User=deploy;Password=s3cret;"
```

When `--ConnectionString` is provided, all individual connection settings (`Server`, `Port`, `User`, `Password`, `ConnectionProperties`) are bypassed.

---

## Sensitive value masking

Your credentials stay out of the logs. When a tool logs its active configuration at startup, its resolved command-line switches, and (for SchemaQuench) its product and template script tokens, it scrubs any value whose name matches a built-in sensitive-name set, so a log is safe to attach to a support ticket, paste into a CI artifact, or drop into a screenshot.

The default sensitive-name patterns (case-insensitive, substring match) are `Password`, `Pwd`, `Secret`, `ApiKey`, `Token`, `ConnectionString`, and `Credential`. A matched value renders as `***` while its name still prints, so you can confirm the setting exists without exposing it. An embedded `Password=` / `Pwd=` inside a connection-string value is stripped even when the surrounding setting or token is not sensitively named -- one leaked connection string is one too many.

**A credential inside a URL is masked too.** A value shaped like `scheme://user:secret@host/path` has its password replaced with `***`, again regardless of what the setting is called. This matters because a URL rarely lives under a sensitively-named setting -- it is a webhook, a custom endpoint, or a URL inside a map-typed token -- and the password sits in the middle of the value rather than after a `Password=` key, so the connection-string rule above never saw it. The username, host, port, and path are all preserved, so a scrubbed line is still diagnosable.

Everything else is logged as-is, so you can still audit the active configuration from the log. The two exceptions above are matched on the *shape* of the value rather than the name of the setting, so they apply even where you have not told SchemaSmith the value is sensitive.

Example log output:

```
Configuration:
    Server: myserver
    Port: 5432
    User: deploy
    Password: ***
    ConnectionProperties:
      SslMode: Prefer
```

### Tuning the scrubbing

An optional `LogHygiene` block in any tool's `*.settings.json` tunes the behavior. With no block present, the defaults above apply.

```jsonc
"LogHygiene": {
  // Suppress the token-logging section entirely -- one notice line, no token
  // names and no values. For products with hundreds of tokens. Default: true.
  "LogTokens": true,

  // Scrub these exact token names too, beyond the default patterns.
  "ScrubTokens": [ "Handshake", "TenantSeed" ],

  // Scrub names matching these extra patterns (contains-match; the * is optional).
  "ScrubPatterns": [ "*Salt*", "*PrivateKey*" ],

  // Opt a false positive back out -- log this name verbatim even though it
  // matches a default pattern (e.g. a column literally named "Token").
  "AllowTokens": [ "PublicToken" ]
}
```

When a token name appears in both `AllowTokens` and a scrub rule, `AllowTokens` wins and the value is logged verbatim -- but an embedded connection-string password is still stripped.

---

## Logging

Good logs are the difference between a quick diagnosis and a long night. SchemaSmith gives you detailed, automatic logging out of the box.

### Framework

All CLI tools use [Apache Log4Net](https://logging.apache.org/log4net/). Each tool ships with an embedded Log4Net configuration that is loaded automatically at startup -- there's nothing to configure.

### Log files

Each tool writes two log files per run:

| Tool | Progress log | Error log |
|---|---|---|
| SchemaQuench | `SchemaQuench - Progress.log` | `SchemaQuench - Errors.log` |
| SchemaTongs | `SchemaTongs - Progress.log` | `SchemaTongs - Errors.log` |
| DataTongs | `DataTongs - Progress.log` | `DataTongs - Errors.log` |
| SchemaShears | `SchemaShears - Progress.log` | `SchemaShears - Errors.log` |

The **progress log** receives all informational output: the startup banner, active configuration, per-object progress, and completion status. Everything written to the progress log also appears on the console in real time.

The **error log** receives only error-level entries (such as SQL execution errors) and does **not** echo to the console.

Both log files are overwritten at the start of each run. Previous runs are preserved through the backup rotation described below.

### Console output

The console is a live mirror of the progress log, not a separate channel. Watching the console while a quench runs lets you follow startup, per-object progress, and completion in real time without tailing a file. Log4Net colorizes the console stream by level -- informational entries in green, warnings in yellow, errors in red -- so trouble catches your eye the moment it appears.

When a script fails, you see a short error summary on the console and in the progress log (red, so it's hard to miss): the failing script path and the engine's error message, along with a `Resolved SQL written to:` pointer to the resolved-SQL artifact. That's enough to know what failed and where to look. The **full detail** -- the exception line numbers and the complete SQL batches SchemaSmith submitted -- lands in the error log only, so the console and progress stream don't drown in multi-KB failed-batch text during a rough deployment. When a run fails, the progress log tells you *what* broke; the error log tells you *exactly what SQL was sent* when it broke.

> **Tip:** CI agents that capture stdout get the progress stream for free. If your pipeline step only saves stdout, you still have a readable transcript of successes and error summaries; archive the error log separately to keep the failed-batch detail.

### Failure triage roll-up

When a run finishes with one or more failures, SchemaQuench writes a third log alongside the progress and error logs — `SchemaQuench - Failures.log` — a consolidated, phase-grouped summary of *which* targets failed, so a single failure among many parallel targets isn't buried in one interleaved stream. Each failure is recorded with its scope, the engine error, the `Resolved SQL written to:` artifact path, and a captured tail of the lines leading up to it; a loud `*** FAILED` banner also marks each failure live on the console. A clean run writes nothing extra. The full behavior is described in [Error Codes & Reporting](error-codes-and-reporting.md#failure-triage-roll-up).

`FailureContextLines` (a SchemaQuench setting, default `25`) controls how many lines of lead-up context each failure captures. Set it to `0` to disable context capture entirely — the roll-up still lists the failed scopes and their errors. Set it in the settings file (`"FailureContextLines": 25`), as an environment variable (`SmithySettings_FailureContextLines=40`), or on the command line (`--FailureContextLines=40`).

### Log file location

By default, logs are written to the tool's executable directory. Override this with `--LogPath`:

```bash
SchemaQuench --LogPath:C:\Logs
SchemaTongs --LogPath:/var/log/schemasmith
DataTongs --LogPath:D:\BuildLogs
```

### Startup configuration dump

Immediately after loading configuration, every tool logs the resolved command-line switches followed by its complete active configuration to the progress log. This includes the tool name and version number, every switch passed on the command line, and every configuration key and its value (with sensitive values masked). This makes it straightforward to verify both the effective command line and the settings that were in effect for any given run.

Sensitive switches are scrubbed the same way configuration values are -- a `--Target__Password=...` or `--ConnectionString=...` renders its value as `***` while the switch name still prints, honoring the same `LogHygiene` rules described in [Sensitive value masking](#sensitive-value-masking):

```
Command line:
  ConfigFile: production.json
  Target__Server: prod-db
  Target__Password: ***

Configuration:
    Server: prod-db
    Password: ***
```

### Log backup rotation

When a tool finishes (whether successfully or after an error), it backs up its log files before the process exits:

1. Determines the log directory (`--LogPath` value, or the executable directory if not specified).
2. Creates a numbered subdirectory: `<ToolName>.0001`. If that directory already exists, it increments: `.0002`, `.0003`, and so on.
3. Copies all files matching `<ToolName> - *.log` into the new subdirectory.

The base log files in the log directory are **not deleted** after backup. Each run overwrites the base files and writes a copy into a new numbered subdirectory. This preserves the history of every run while keeping the base files current with the latest.

Example after three SchemaQuench runs:

```
C:\Tools\
    SchemaQuench - Progress.log      (latest run)
    SchemaQuench - Errors.log        (latest run)
    SchemaQuench.0001\               (first run backup)
    SchemaQuench.0002\               (second run backup)
    SchemaQuench.0003\               (third run backup)
```

### Failure artifacts

When any script fails during a SchemaQuench deployment -- a user or migration script, a generated table-quench procedure, a product-level `Before`/`After` script, a validation script (`BaselineValidationScript`, `VersionStampScript`), or a data-delivery merge -- SchemaQuench writes the exact token-expanded SQL the server rejected to a re-runnable `.sql` artifact file. Every surface reports it the same way in the progress log:

```
Unable to quench 'Before/01-seed-config.sql': Invalid column name 'Region'.
    Resolved SQL written to: C:\deploy\SchemaQuench - Failed 01-seed-config prod-db.TargetDB.sql
```

Every artifact is a `.sql` file with a comment header (server/database/schema, the failing script name, the error message), every batch the engine received, separated by `GO`, with the last-attempted batch marked `-- >>> FAILING BATCH (#N) >>>`. The failing-batch marker is a best-effort hint -- the engine marks the last batch it attempted, which is usually the one that caused the error.

**Artifacts are raw by default** -- all token values are already expanded to their real values, so you can open the file, connect to the target, and reproduce the failure immediately without any further substitution.

> **Note:** Artifacts land in the `ArtifactPath` directory (default: current working directory), not the log directory. This is intentional: raw artifacts may contain expanded sensitive values and should not be automatically swept into log archives or CI artifacts.

#### `ArtifactPath`

Directory where SchemaQuench writes resolved-SQL failure artifacts and generated-SQL debug files.

```json
{ "ArtifactPath": "C:\\deploy\\debug" }
```

| Default | Behavior |
|---|---|
| *(not set)* | Artifacts land in the current working directory (where SchemaQuench was launched). |
| A directory path | Artifacts land in the specified directory. Relative paths are resolved from the current working directory. |

Setting `ArtifactPath` is useful when you want artifacts in a consistent location regardless of where SchemaQuench is invoked -- a CI agent's workspace directory, for example, or a dedicated debug folder outside the log path. The directory is created automatically if it does not exist.

#### `ScrubArtifacts`

Controls whether sensitive values are redacted in failure artifacts before writing.

```json
{ "ScrubArtifacts": true }
```

| Value | Behavior |
|---|---|
| `false` (default) | Artifacts contain real expanded values. Re-runnable immediately -- open in a query tool and reproduce the failure without restoring secrets. |
| `true` | Sensitive token values (names matching `*Password*`, `*Secret*`, `*ApiKey*`, `*Token*`, etc., per `LogHygiene` rules) and inline connection-string passwords are redacted to `***`. Safe to attach to a CI artifact or support ticket. To reproduce, restore the real values first. |

Leave `ScrubArtifacts` off for local debugging -- raw artifacts are immediately re-runnable. Turn it on for CI environments or when attaching an artifact to a support ticket. The `LogHygiene` block governs which token names are considered sensitive; see [Sensitive value masking](#sensitive-value-masking) for the full set of rules.

For a step-by-step walkthrough of working a failed deployment from artifact to fix, see [Troubleshooting -- My deployment failed](../guide/12-troubleshooting.md#my-deployment-failed).

### Debug SQL files

When SchemaQuench runs one of its generated procedures against your target database, it dumps the exact SQL it sent to a companion `.sql` file -- honoring `ScrubArtifacts` the same as any other failure artifact. If the procedure throws, the progress log surfaces the file path via the same `Resolved SQL written to:` line described in [Failure artifacts](#failure-artifacts) above. Open it in your query tool of choice, re-run the SQL by hand, and reproduce or narrow the problem without guessing what SchemaSmith actually executed.

Generated procedures cover missing tables and columns, modified tables, indexes, foreign keys, materialized views, indexed views, and the table-JSON parse step. Debug files follow the pattern `SchemaQuench - <operation> <server>.<database>.sql`:

```
SchemaQuench - Quench Missing Tables And Columns prod-db.NorthwindClone.sql
SchemaQuench - Quench Modified Tables prod-db.NorthwindClone.sql
SchemaQuench - Quench Indexes prod-db.NorthwindClone.sql
SchemaQuench - Quench Foreign Keys prod-db.NorthwindClone.sql
SchemaQuench - Quench Materialized Views prod-db.NorthwindClone.sql
SchemaQuench - Quench Indexed Views prod-db.NorthwindClone.sql
SchemaQuench - Parse Table Json prod-db.NorthwindClone.sql
```

Each run overwrites the debug files for the operations it actually performed. Operations that don't apply to your platform (for example, `Indexed Views` on PostgreSQL or `Materialized Views` on MySQL) produce no file. Debug files land in the `ArtifactPath` directory (default: current working directory); `--LogPath` controls the progress and error logs, not artifact or debug SQL.

### Engine notices

SchemaSmith surfaces the database engine's informational output -- notices, prints, and server-side status messages -- into the progress log so you can see what the engine is telling you. The wiring differs per platform because each driver exposes that stream differently:

- **SQL Server** -- `PRINT` output and severity-10-or-lower errors arrive through the `InfoMessage` event. By default SchemaSmith promotes only severity-above-10 errors and `RAISERROR ... WITH STATE 100` notifications to the progress log; set `VerboseLogging: true` to include every `PRINT` and informational message.
- **PostgreSQL** -- `RAISE NOTICE` and `RAISE WARNING` output arrives through the Npgsql `Notice` event and lands in the progress log. SchemaSmith filters out the `"... does not exist, skipping"` and `"... already exists, skipping"` notices that `DROP ... IF EXISTS` and `CREATE ... IF NOT EXISTS` produce during normal runs, so your log stays readable.
- **MySQL and MariaDB** -- the MySQL connector doesn't fire info-message events for long-running stored procedures, so SchemaSmith uses a table-based status channel. A `SchemaSmith_StatusMessages` table in the target database (created automatically during kindling) holds per-session progress rows; the generated quench procedures `INSERT` into it as they work, and a background poller on a separate connection reads the new rows every 200ms and writes them to the progress log. `SessionId` is scoped to `CONNECTION_ID()` so concurrent runs don't cross-talk, and the monitor deletes its rows on shutdown. There's no `VerboseLogging` dial on MySQL or MariaDB -- what you see is whatever the procedures chose to publish.

`VerboseLogging` is a SchemaQuench setting and applies only to SQL Server's `InfoMessage` stream. The PostgreSQL, MySQL, and MariaDB paths already behave the way `VerboseLogging: true` behaves on SQL Server -- SchemaSmith surfaces every engine-side notice (PostgreSQL) or procedure-emitted status message (MySQL and MariaDB) by default.

---

## Exit Codes

| Code | Condition | Recommended action |
|---|---|---|
| `0` | Normal completion | None -- the operation succeeded. |
| `2` | One or more database quenches failed (SchemaQuench only) | Check the progress and error logs for details on which databases failed and why. Fix the failing scripts and re-run. |
| `3` | Unhandled exception | An unexpected error occurred. The exception is logged to both the progress and error logs before exit. Report the error with the log contents if the cause isn't obvious. |
| `4` | Log backup failure | The tool completed its main work but couldn't back up the log files. Check directory permissions and disk space in the log directory. The base log files may still be readable even though the backup failed. |

---

## Quick Reference

```bash
# Run with defaults (settings file in current directory or tool directory)
SchemaQuench

# Custom config file
SchemaQuench --ConfigFile:staging.json

# Custom log directory
SchemaQuench --LogPath:/var/log/quench

# Override connection via environment
export SmithySettings_Target__Server=prod-db
export SmithySettings_Target__User=deploy
export SmithySettings_Target__Password=s3cret
SchemaQuench

# Override connection via CLI (platform-appropriate connection string)
SchemaQuench --ConnectionString:"Host=prod-db;Database=mydb;Username=deploy;Password=s3cret;"
```

---

## DropSchemaBoundDependents

**SQL Server only.** Controls whether SchemaQuench drops a `SCHEMABINDING` view or function that is
blocking a column change, so the change can be applied and the module recreated afterwards. Three tiers
compose to produce the effective value, resolved environment → product → template.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropSchemaBoundDependents` in `SchemaQuench.settings.json` (or `SmithySettings_DropSchemaBoundDependents` environment variable) | `false` |
| Product | `DropSchemaBoundDependents` in `Product.json` | `false` |
| Template | `DropSchemaBoundDependents` in `Template.json` | (inherit) |

### The problem it solves

SQL Server refuses to alter a column while a schema-bound module references it, failing with error 4922 —
"one or more objects access this column" — which names neither the module nor what to do about it. Left
off, SchemaQuench reports the same refusal but names the blocking module, the column it blocks, and the
remedy. Turned on, it drops the module instead, applies the column change, and the after-tables object
pass recreates the module **from your package**.

### Your scripts must be in the after-tables slot

SchemaSmith does not save and replay the definition it found on the server. Your package is the authority
on what the module should be; the copy on the server is only whatever happens to be deployed. So the
recreate comes from your own script, and that script has to run **after** the table work.

Put schema-bound modules in a folder on the `AfterTablesObjects` slot. The default templates already
include `SchemaBound Views/` and `SchemaBound Functions/` for this, and **SchemaTongs writes schema-bound
views and functions into those folders automatically on extraction** — whether or not this setting is on
— so an extracted package is already shaped correctly the day you turn it on. A module left in the
ordinary `Views/` or `Functions/` folder is recreated before the table work rather than after it.

### Dropping discards permissions

**A dropped view or function loses every `GRANT` on it, and SchemaSmith does not put them back.**
SchemaSmith does not manage permissions on any object, so it has nothing to restore them from. Re-grant
in the recreating script itself, or from whatever process you already use to manage permissions — if you
grant on these objects at all, you almost certainly have one.

### What it will not do

An **encrypted** module (`WITH ENCRYPTION`) is refused even with the setting on, and refused *before*
anything is dropped. `OBJECT_DEFINITION` returns `NULL` for one, so it cannot be scripted from the server
by SchemaSmith, by SchemaTongs, or by you — dropping it would be unrecoverable. Remove the encryption or
the `SCHEMABINDING` to proceed.

Indexed views are unaffected either way. Those are already dropped and recreated around column changes.

```json
// Template.json — this template's schema-bound modules are safe to cycle
{ "DropSchemaBoundDependents": true }
```
---

## DropTablesRemovedFromProduct

Controls whether SchemaQuench drops tables that exist in the database but no longer appear in the schema package. Three tiers compose to produce the effective value, resolved environment → product → template.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropTablesRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropTablesRemovedFromProduct` environment variable) | `true` |
| Product | `DropTablesRemovedFromProduct` in `Product.json` | `true` |
| Template | `DropTablesRemovedFromProduct` in `Template.json` | (inherit) |

A `false` at any tier is sticky — it locks the effective value to `false` for all lower tiers and cannot be re-enabled by a more-specific setting. Absent (not set) inherits from the tier above. A `true` at a lower tier overrides an inherited `true` but never an ancestor's `false`.

```json
// SchemaQuench.settings.json — turn off auto-drop for this environment
{ "DropTablesRemovedFromProduct": false }
```

```json
// Product.json — package declares its tables must never be auto-dropped
{ "DropTablesRemovedFromProduct": false }
```

```json
// Template.json — suppress auto-drop for this template only
{ "DropTablesRemovedFromProduct": false }
```

> **Note:** All tiers absent preserves existing behavior — if you haven't set any, table-drop-by-absence works exactly as it always has.

For full guidance — environment advice, the rollback-friendly removal pattern, and the recyclebin alternative — see [DropTablesRemovedFromProduct](schemaquench.md#droptablesremovedfromproduct) in the SchemaQuench reference.

---

## PreventDrop

Protects a single table from drop-by-absence, and makes that protection *sticky* — it survives the table leaving the package. Unlike the `Drop…RemovedFromProduct` cascade flags (which only take effect while a table's definition is still present), `PreventDrop` is a per-table boolean set in the table's `.json` and persisted in SchemaSmith's ownership tracking, so a protected table that is later removed from the package is skipped rather than dropped.

| Scope | Where to set | Default |
|---|---|---|
| Table | `PreventDrop` in a table's `.json` file | `false` |

```json
// Tables/dbo.Orders.json — never drop this table by absence, even after it leaves the package
{ "Name": "[Orders]", "PreventDrop": true }
```

The marker is refreshed to match the package value on every run while the table is still declared. To un-protect: set `PreventDrop: false` and re-deploy while the table is still in the package (this clears the sticky marker), then remove it — or drop the table explicitly with a migration script. Ownership is reconciled against the live catalog each run, so a table dropped out-of-band has its marker pruned automatically.

> **Note:** `PreventDrop` is persisted per engine — a `PreventDrop` extended property on SQL Server, a `PreventDrop` column on the `ProductOwnership` table for PostgreSQL, MySQL, and MariaDB — but behaves identically on all four.

For full guidance — the persistence model, inbound-foreign-key preservation, and the deliberate two-step un-protect — see [PreventDrop](schemaquench.md#preventdrop) in the SchemaQuench reference.

---

## RebuildPolicy

Controls when SchemaQuench replaces a table wholesale — building it fresh, copying the rows across and
swapping it in — instead of altering it in place. **Off by default.** A table that does not ask for a
rebuild never gets one.

Two situations make a rebuild the better move:

- **Cost.** A wide table with several pending column-type changes pays one table rewrite per change.
  A rebuild collapses those into a single copy.
- **Column order.** Reordering existing columns is not something any supported engine can do in place.
  A rebuild is the only mechanism that can make a deployed table match the order its package declares.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `RebuildPolicyMode` / `RebuildPolicyThreshold` / `RebuildPolicyOnOrderMismatch` in `SchemaQuench.settings.json` | (inherit) |
| Product | `RebuildPolicy` in `Product.json` | (inherit) |
| Template | `RebuildPolicy` in `Template.json` | (inherit) |
| Table | `RebuildPolicy` in a table’s `.json` file | (inherit) |

A tier that declares nothing defers to the nearest level above that does. `{ "Mode": "NEVER" }` is the
floor only when **no** tier declares a policy — a table that declares nothing does not get `NEVER`, it
gets whatever its template, product or environment declared. When a level does declare one, it wins
whole; see *The levels replace, they do not blend* below.

```json
// Tables/dbo.Orders.json — rebuild once three or more column modifications pile up
{
  "Name": "[Orders]",
  "RebuildPolicy": { "Mode": "THRESHOLD", "Threshold": 3 }
}
```

| `Mode` | Behaviour |
|---|---|
| `NEVER` | **Default.** Always alter in place. |
| `ALWAYS` | Rebuild whenever a column modification or removal is detected for the table. |
| `THRESHOLD` | Rebuild when the number of pending **column modifications** reaches `Threshold`. |

`OnOrderMismatch` is a separate switch that composes with any `Mode`, so `{ "Mode": "THRESHOLD",
"Threshold": 3, "OnOrderMismatch": true }` means *rebuild if three modifications pile up **or** if the
deployed column order has drifted from the package*. Pairing it with the default `NEVER` —
`{ "OnOrderMismatch": true }` — asks for a rebuild on order drift and nothing else.

The comparison is of **relative** order across the columns the package and the table have in common, not
of absolute positions. Two consequences are worth knowing:

- **A dropped column does not cause a rebuild.** Removing a column from the middle of a table leaves a
  permanent gap in the engine's own column numbering. As long as the remaining columns are still in the
  declared sequence relative to one another, nothing is elected — otherwise a table that had ever lost a
  column would be rebuilt on every deploy.
- **A column added in the middle of the file does.** Every supported engine can only append a new column
  to the end of a table, so a package that declares one between two existing columns is genuinely out of
  order until the table is rebuilt.

**Only column modifications count toward `Threshold`** — the passes a rebuild actually eliminates.
Column additions, column drops, and index or constraint changes do not, because a rebuild does not save
that work: the indexes are rebuilt on the new table either way.

> **The levels replace, they do not blend.** The most specific level that sets a `RebuildPolicy` defines
> it completely. A table declaring `{ "Mode": "ALWAYS" }` does **not** inherit a product-level
> `Threshold` — it takes its own policy whole. This is deliberately unlike the `Drop…RemovedFromProduct`
> cascade, where the levels combine.

### What a rebuild costs

Stated plainly, because the policy is opt-in precisely so that you decide rather than SchemaSmith
guessing:

- It needs roughly **twice the table’s space** while the copy exists.
- It **holds a lock for the duration of the copy**. On a large table that is not a short operation.
- Objects SchemaSmith does not manage — manual grants, triggers you did not declare — live on the old
  table and do **not** survive. An in-place alter would have preserved them. Declared objects are
  rebuilt normally.

> **MySQL and MariaDB:** those engines cannot roll DDL back, so a rebuild there wants a quiesced table.
> SchemaQuench makes the swap itself atomic and verifies the row count immediately before it, so the
> ordinary case fails safely with the original intact — but a row written by another session *during*
> the copy can still be missed. SQL Server and PostgreSQL do not have this exposure.

### When SchemaQuench refuses

Some tables carry state that a copy cannot reconstruct, so a rebuild is **refused outright** rather than
silently destroying it. The deploy stops and names the reason and the table; use Before/After migration
scripts for these.

| Engine | Refused when the table… |
|---|---|
| SQL Server | is system-versioned (temporal), or has Change Data Capture, Change Tracking, or replication |
| PostgreSQL | belongs to a logical replication publication, inherits or is inherited from, or is partitioned |
| MariaDB | is system-versioned, or has an application-time period |

The refusal fires under `WhatIfONLY` too, so a preview shows the impossibility rather than leaving it to the
real run.

A rebuild is also **skipped** — quietly, and the table is altered in place instead — when this run has
promised not to remove anything by absence: `PreventDrop` is on for the environment, or the table has a
column the package no longer declares whose drop `DropColumnsRemovedFromProduct` is suppressing. A
rebuild builds the replacement from the declared definition alone, so it would remove that column
anyway; declining the rebuild costs an in-place alter, taking it costs the data the protection existed
to keep.

`--Validate` reports `SS-TBL-001` when a policy sets `Mode: "THRESHOLD"` without a `Threshold` — that
policy cannot be evaluated, so it is an error rather than a setting that quietly does nothing.

---

## System-versioned tables (MariaDB)

MariaDB can keep a table's own history: every update leaves the superseded row behind, queryable
through `FOR SYSTEM_TIME`. SchemaSmith reads that state and round-trips it.

```json
{
  "Name": "`Account`",
  "IsSystemVersioned": true,
  "Columns": [ ... ]
}
```

`IsSystemVersioned` is detected from the table's catalog type, which is the only signal that answers
for **both** ways of writing one — whether you declare the period columns yourself or let the engine
keep them hidden. Those engine-owned row-start and row-end columns are deliberately left out of the
extracted package: the engine maintains them, and a package that listed them would have SchemaSmith
trying to manage columns that are not yours to manage.

> **MariaDB only, and not an omission elsewhere.** MySQL has no system versioning at any version, so
> the property does not exist in a MySQL package or its editor schema. SQL Server's equivalent is its
> temporal-table support, which is configured separately.

### SystemVersioningAlterHistory

Changing a column on a system-versioned table is not an ordinary alter. MariaDB refuses it outright
unless `@@system_versioning_alter_history` is `KEEP` — and `KEEP` does not simply permit the change,
it applies the change to **the stored history as well**. Rows that were recorded years ago are
rewritten into the new shape, so the history stops being a record of what the table actually looked
like at the time.

That is a decision about your data retention, not a detail of syntax, so SchemaSmith will not make it
for you.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `SystemVersioningAlterHistory` in `SchemaQuench.settings.json` (or the `SmithySettings_SystemVersioningAlterHistory` environment variable, or `--SystemVersioningAlterHistory=KEEP` on the command line) | _unset_ |

| Setting | Effect |
|---|---|
| _unset_ (default) | The engine refuses the column change, exactly as it does today. |
| `KEEP` | The change proceeds and the stored history is rewritten to match. |

```json
// SchemaQuench.settings.json — an environment setting, not a package property
{
  "SystemVersioningAlterHistory": "KEEP"
}
```

> **Note:** this is an environment setting only. It has no product, template or table tier, so it does
> not belong in `Product.json`, `Template.json`, or a table's `.json` — the surrounding examples in this
> section are table JSON, and this one is not. Since v2.6.0 the generated `.json-schemas` reject it as an
> unexpected property, so putting it in a package fails `--Validate` with `SS-JSON-001` and exit `2`.

Leaving it unset costs nothing on a healthy deploy: the refusal only fires when a change genuinely
requires rewriting history, never on a re-deploy where the table already matches its definition. If
you do not want the history rewritten, the alternative is to drop system versioning, make the change,
and re-enable it — accepting the gap deliberately rather than discovering it later.



### Application-time periods

A period names a pair of columns that describe the interval a row is valid **for** — the dates a price
applied, or an assignment ran. That is a different question from system versioning, which records when
a row was *stored*. A table can declare both, and the values in an application-time period are the
application's to set.

```json
{
  "Name": "`Rate`",
  "Periods": [
    { "Name": "Validity", "StartColumn": "ValidFrom", "EndColumn": "ValidTo" }
  ]
}
```

> **Extraction has a version blind spot — and it is not the version you would expect.** Periods work
> from MariaDB **10.4.3**, but the catalog that reports them only arrives in **11.4**. Extracting from
> anything in between returns no periods even where the table plainly has them, so a package
> round-tripped through such a server loses them. Deploying a declared period to those versions works
> normally; only the read is blind. If you extract from MariaDB below 11.4, check your periods survived.

The `SYSTEM_TIME` period is not listed here. MariaDB reports it alongside application periods, but the
table already declares that state through `IsSystemVersioned` — carrying it in both places would let a
package contradict itself.

#### DropPeriodsRemovedFromProduct

A period on the table that the package no longer declares is only removed if you ask for it.

| Setting | Effect |
|---|---|
| _unset_ (default) | The period stays, even though the package does not mention it. |
| `true` | The period is dropped. |

**This defaults to off, unlike most of the `Drop…RemovedFromProduct` family, and the reason matters.** A package
that has no `Periods` entry is not necessarily saying "this table has no period" — it may simply never
have been able to say otherwise. Packages written before periods were supported have no such entry, and
neither does one extracted from MariaDB 10.4.3–11.3, where the server cannot report periods at all.
Defaulting to drop would delete a period on the strength of a silence that means nothing.

Turn it on when your package is genuinely the authority on the table's periods. It can also be set on a
single table, which overrides the environment setting for that table alone.

> **Dropping a period does not touch your data.** The columns it spanned, and everything in them,
> remain. What is removed is the period itself and the check constraint MariaDB uses to enforce it.

---

## DropColumnsRemovedFromProduct

Controls whether SchemaQuench drops columns that exist in the database but no longer appear in the table JSON. Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropColumnsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropColumnsRemovedFromProduct` environment variable) | `true` |
| Product | `DropColumnsRemovedFromProduct` in `Product.json` | (inherit) |
| Template | `DropColumnsRemovedFromProduct` in `Template.json` | (inherit) |
| Table | `DropColumnsRemovedFromProduct` in a table's `.json` file | (inherit) |

A `false` at any tier is sticky — it locks the effective value to `false` for all lower tiers and cannot be re-enabled by a more-specific setting. Absent inherits from the tier above. A `true` at a lower tier overrides an inherited `true` but never an ancestor's explicit `false`. The table tier can only tighten (set its own `false` to protect its columns); a table `true` cannot re-enable column drops that a higher tier suppressed.

```json
// SchemaQuench.settings.json — suppress column drops for all products in this environment
{ "DropColumnsRemovedFromProduct": false }
```

```json
// Product.json — package declares its columns must never be auto-dropped
{ "DropColumnsRemovedFromProduct": false }
```

```json
// Template.json — suppress column drops for this template only
{ "DropColumnsRemovedFromProduct": false }
```

```json
// Tables/dbo.AuditLog.json — protect this table's columns regardless of higher-tier settings
{ "Name": "AuditLog", "DropColumnsRemovedFromProduct": false }
```

> **Note:** All tiers absent preserves existing behavior — if you haven't set any, column-drop-by-absence works exactly as it always has (default `true`).

For full guidance — environment advice, the four-tier cascade detail, and the rollback-friendly removal pattern — see [DropColumnsRemovedFromProduct](schemaquench.md#dropcolumnsremovedfromproduct) in the SchemaQuench reference.

---

## DropForeignKeysRemovedFromProduct

Controls whether SchemaQuench drops foreign keys that exist in the database but no longer appear in the table JSON. Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropForeignKeysRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropForeignKeysRemovedFromProduct` environment variable) | `true` |
| Product | `DropForeignKeysRemovedFromProduct` in `Product.json` | (inherit) |
| Template | `DropForeignKeysRemovedFromProduct` in `Template.json` | (inherit) |
| Table | `DropForeignKeysRemovedFromProduct` in a table's `.json` file | (inherit) |

Same explicit-false-sticky semantics as the other drop-control flags: a `false` at any tier locks the effective value for all lower tiers, and a table can tighten to `false` but never re-enable a higher-tier suppression. Only by-absence removal is gated — a foreign key whose definition merely changed is always dropped and recreated. On MySQL and MariaDB this flag also decouples foreign-key cleanup from `DropUnknownIndexes`, matching SQL Server and PostgreSQL.

> **Note:** All tiers absent preserves existing behavior (default `true`).

For full guidance, see [DropForeignKeysRemovedFromProduct](schemaquench.md#dropforeignkeysremovedfromproduct) in the SchemaQuench reference.

---

## DropCheckConstraintsRemovedFromProduct

Controls whether SchemaQuench drops table-level CHECK constraints that exist in the database but no longer appear in the table JSON. Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropCheckConstraintsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropCheckConstraintsRemovedFromProduct` environment variable) | `true` |
| Product | `DropCheckConstraintsRemovedFromProduct` in `Product.json` | (inherit) |
| Template | `DropCheckConstraintsRemovedFromProduct` in `Template.json` | (inherit) |
| Table | `DropCheckConstraintsRemovedFromProduct` in a table's `.json` file | (inherit) |

Same explicit-false-sticky semantics as the other drop-control flags: a `false` at any tier locks the effective value for all lower tiers, and a table can tighten to `false` but never re-enable a higher-tier suppression. Only by-absence removal is gated (a modified check still reconciles), and column-level `CheckExpression` checks are out of scope. With the flag on, SQL Server, MySQL, and MariaDB now drop orphaned table-level checks by absence, matching PostgreSQL.

> **Note:** All tiers absent preserves existing behavior (default `true`).

For full guidance, see [DropCheckConstraintsRemovedFromProduct](schemaquench.md#dropcheckconstraintsremovedfromproduct) in the SchemaQuench reference.

---

## DropExcludeConstraintsRemovedFromProduct

Controls whether SchemaQuench drops EXCLUDE constraints that exist in the database but no longer appear in the table JSON. EXCLUDE constraints are a **PostgreSQL** feature, so this flag belongs in a PostgreSQL package. Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropExcludeConstraintsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropExcludeConstraintsRemovedFromProduct` environment variable) | `true` |
| Product | `DropExcludeConstraintsRemovedFromProduct` in `Product.json` — **PostgreSQL packages only** | (inherit) |
| Template | `DropExcludeConstraintsRemovedFromProduct` in `Template.json` — **PostgreSQL packages only** | (inherit) |
| Table | `DropExcludeConstraintsRemovedFromProduct` in a table's `.json` file — PostgreSQL only | (inherit) |

> **Warning:** since v2.6.0 the generated `.json-schemas` are filtered per engine, and the SQL Server, MySQL
> and MariaDB `products.*` / `templates.*` schemas no longer carry this property. Leaving it in a
> non-PostgreSQL `Product.json` or `Template.json` is no longer harmless: `--Validate` reports it as an
> unexpected property (`SS-JSON-001`, Error) and exits `2`, which fails a CI gate. Deployment itself is
> unaffected — the package still loads — so this bites validation, not a running deploy. Delete the
> property from those packages; it never did anything on those engines. The environment tier lives in the
> settings file, which these package schemas do not cover, and the table tier still carries the property
> on all four engines — annotated `PostgreSQL only.`

Same explicit-false-sticky semantics as the other drop-control flags; only by-absence removal is gated (a modified exclude constraint still reconciles).

> **Note:** All tiers absent preserves existing behavior (default `true`).

For full guidance, see [DropExcludeConstraintsRemovedFromProduct](schemaquench.md#dropexcludeconstraintsremovedfromproduct) in the SchemaQuench reference.

---

## DropStatisticsRemovedFromProduct

Controls whether SchemaQuench drops user-created statistics objects that exist in the database but no longer appear in the table JSON. Applies to **SQL Server and PostgreSQL** (MySQL and MariaDB have no separate statistics objects). Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropStatisticsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropStatisticsRemovedFromProduct` environment variable) | `true` |
| Product | `DropStatisticsRemovedFromProduct` in `Product.json` | (inherit) |
| Template | `DropStatisticsRemovedFromProduct` in `Template.json` | (inherit) |
| Table | `DropStatisticsRemovedFromProduct` in a table's `.json` file | (inherit) |

Same explicit-false-sticky semantics as the other drop-control flags; only by-absence removal is gated (a modified statistics object still reconciles), and auto-created statistics are never touched. With the flag on, SQL Server now drops orphaned user-created statistics by absence, matching PostgreSQL.

> **Note:** All tiers absent preserves existing behavior (default `true`).

For full guidance, see [DropStatisticsRemovedFromProduct](schemaquench.md#dropstatisticsremovedfromproduct) in the SchemaQuench reference.

---

## DropIndexesRemovedFromProduct

Controls whether SchemaQuench drops a **product-owned** index (one SchemaSmith created and tracks) that no longer appears in the table JSON. Distinct from [DropUnknownIndexes](#dropunknownindexes), which targets out-of-band indexes SchemaSmith never managed. Four tiers compose to produce the effective value, resolved environment → product → template → table.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropIndexesRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropIndexesRemovedFromProduct` environment variable) | `true` |
| Product | `DropIndexesRemovedFromProduct` in `Product.json` | (inherit) |
| Template | `DropIndexesRemovedFromProduct` in `Template.json` | (inherit) |
| Table | `DropIndexesRemovedFromProduct` in a table's `.json` file | (inherit) |

Same explicit-false-sticky semantics as the other drop-control flags; a primary key is never dropped by this path. All four engines gate the removed-from-product index drop directly through this flag — MySQL and MariaDB previously coupled it to `DropUnknownIndexes` and are now at parity with SQL Server and PostgreSQL (a product-owned index removed from the definition drops by default).

> **Note:** All tiers absent preserves existing behavior (default `true`).

For full guidance, see [DropIndexesRemovedFromProduct](schemaquench.md#dropindexesremovedfromproduct) in the SchemaQuench reference.

---

## DropUnknownIndexes

Controls whether SchemaQuench drops indexes on managed tables that aren't defined in the schema package — *out-of-band* indexes SchemaSmith never created (distinct from [DropIndexesRemovedFromProduct](#dropindexesremovedfromproduct), which handles product-owned indexes removed from the definition). Three tiers compose to produce the effective value, resolved environment → product → template. Identical on all four engines — MySQL and MariaDB previously never dropped an out-of-band index and are now at parity with SQL Server and PostgreSQL.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropUnknownIndexes` in `SchemaQuench.settings.json` (or `SmithySettings_DropUnknownIndexes` environment variable) | `false` |
| Product | `DropUnknownIndexes` in `Product.json` | (inherit) |
| Template | `DropUnknownIndexes` in `Template.json` | (inherit) |

A `false` at any tier is sticky — it locks the effective value to `false` for all lower tiers and cannot be re-enabled by a more-specific setting. Absent inherits from the tier above. A `true` at a lower tier overrides an inherited `true` (or default `false`) but never an ancestor's explicit `false`.

The environment tier is new in this release. Previously `DropUnknownIndexes` was settable only in `Product.json` and `Template.json`. It can now be set or suppressed in `SchemaQuench.settings.json` (or via environment variable) as a deployment-wide guardrail.

```json
// SchemaQuench.settings.json — suppress index drops for all products in this environment
{ "DropUnknownIndexes": false }
```

```json
// SchemaQuench.settings.json — enable index drops for all products in this environment
{ "DropUnknownIndexes": true }
```

> **Note:** Default `false` at all tiers preserves existing behavior — if you haven't set any tier, index-drop-by-absence is off, consistent with prior releases. The environment tier adds an opt-in or opt-out guardrail without changing the per-package default.

For package-side configuration and adoption guidance, see [DropUnknownIndexes](schema-packages.md#properties) in the Schema Packages reference.

---

## DropEventsRemovedFromProduct

Controls whether SchemaQuench drops a scheduled event that exists on the server but no longer appears in the package. Scheduled events are a **MySQL and MariaDB** feature. Unlike the rest of the `Drop…RemovedFromProduct` family, this one has a single tier: an event is a template-scoped object, so there is no per-table or per-object level for it to cascade from.

| Scope | Where to set | Default |
|---|---|---|
| Environment | `DropEventsRemovedFromProduct` in `SchemaQuench.settings.json` (or `SmithySettings_DropEventsRemovedFromProduct` environment variable) | `false` |

It defaults to off for the same reason `DropPeriodsRemovedFromProduct` does: a package with no `Events` entry is not necessarily saying "this database has no events" — it may simply predate declared events. Removal also reaches only events SchemaSmith created; one made by hand, or by a scripted `Events/` file, is never touched. Under the no-drop protection tier ([`PreventDrop`](schemaquench.md#preventdrop) set for the environment) the flag is forced off along with every other drop-by-absence pass.

> **Note:** this is an environment setting only. `DropEventsRemovedFromProduct` is not a package property and appears in no generated `.json-schemas` file, so leaving it in a `Product.json` or `Template.json` does not quietly do nothing: the package still loads and the deploy is unaffected, but `--Validate` reports it as an unexpected property (`SS-JSON-001`, Error) and exits `2`, which fails a CI gate. Set it in `SchemaQuench.settings.json` (or via the environment variable) instead.

For the package side of declared events, see [Scheduled Event JSON Format](schema-packages.md#scheduled-event-json-format-mysql--mariadb) in the Schema Packages reference.

---

## Related Documentation

- [SchemaQuench Reference](schemaquench.md) -- Deployment engine settings and behavior
- [SchemaTongs Reference](schematongs.md) -- Schema extraction settings
- [DataTongs Reference](datatongs.md) -- Data extraction settings
- [Script Tokens Reference](script-tokens.md) -- Token replacement via ScriptTokens config
- [CI/CD Integration](../guide/07-cicd-integration.md#configuration-via-environment-variables) -- Practical guide to environment variable configuration in pipelines
