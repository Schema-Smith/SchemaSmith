# Server and Database Settings

Every DBA has met the server that is almost like the others. Same version, same hardware, same build script -- and a `max degree of parallelism` somebody changed during an incident two years ago, a database still in `FULL` recovery on a box that has no log backups, a trace flag nobody remembers turning on. Nothing breaks, so nobody notices, until the one query that only misbehaves on that server.

SchemaSmith packages already make the schema the same everywhere. They can do the same for the configuration around it: declare the settings a class of server should have, and every deploy either brings each server into line or tells you exactly how it differs.

## Nothing changes until you ask

Settings are opt-in, all the way down. A package without a `ServerSettings.json` or `DatabaseSettings.json` deploys exactly as it always has: no setting is read, no extra permission is needed, and nothing new appears in the log or the deployment summary. A package that has one only ever touches the settings it lists -- everything else on the server stays out of scope.

That makes it safe to start small. Declare one setting you care about, report-only, and see what your fleet looks like.

## Declare what a server should look like

`ServerSettings.json` sits beside `Product.json` and describes each server the product deploys to. `DatabaseSettings.json` sits in a template folder and describes each database that template targets.

```json
{
  "Settings": [
    { "Kind": "Configuration", "Name": "cost threshold for parallelism", "Value": "50", "Fix": true, "OnDrift": "Warn" },
    { "Kind": "Configuration", "Name": "optimize for ad hoc workloads", "Value": "1", "Fix": false, "OnDrift": "Warn" }
  ]
}
```

`Kind` says which catalog the setting lives in -- a SQL Server `Configuration` or `TraceFlag`, a PostgreSQL `Parameter`, a MySQL or MariaDB `Variable` for the server; a SQL Server `Option` or `ScopedConfiguration`, a PostgreSQL `Parameter`, or a MySQL `CHARACTER SET` / `COLLATE` `Option` for a database. The [reference](../reference/schema-packages.md#server-and-database-settings) lists every kind and how each one is compared: `ON`, `1` and `true` are the same value, and a PostgreSQL `128MB` matches the `16384` pages the server reports.

## Pick a policy per setting

Two properties decide what happens when a server differs, and every setting states both -- SchemaSmith never assumes a policy for you.

- **`Fix: true`** changes the setting; **`Fix: false`** only reports it.
- **`OnDrift`** grades whatever is still wrong after the deploy: `Warn` logs a warning, `Error` stops the deploy, `Ignore` logs it as information.

Put together, that covers the cases a DBA actually has:

| You want | `Fix` | `OnDrift` |
|---|---|---|
| To know, without touching anything | `false` | `Warn` |
| A deploy that refuses to run on a misconfigured server | `false` | `Error` |
| The setting enforced, with a heads-up when it cannot be | `true` | `Warn` |
| The setting enforced, or no deploy | `true` | `Error` |

**Errors stop early.** A report-only `Error` setting (`false` / `Error`) that has drifted stops its step before any setting is changed, so a refused deploy never leaves a server half-configured.

**Restarts, spelled out.** Some settings only take effect after a restart. SchemaQuench sets them and says so, with the value the server is running now; under `Error` that stops the deploy, so nothing builds on a setting that is not in effect yet. A change a restart will undo -- `SET GLOBAL` on MySQL 5.7 and MariaDB, a SQL Server trace flag -- is applied with a warning that it reverts, and the next deploy after a restart applies it again.

## One package per class of server

Most fleets have a few kinds of server: OLTP, warehouse, reporting. There are two clean ways to describe them.

**One package per class.** Each package declares the settings for its class, and you deploy the OLTP package to OLTP servers. Simple to read, simple to review.

**Gated variants.** Declare the same setting more than once, each with a `ShouldApplyExpression` that picks the servers it belongs to:

```json
{ "Kind": "Configuration", "Name": "max degree of parallelism", "Value": "8", "Fix": true, "OnDrift": "Error",
  "ShouldApplyExpression": "SELECT CASE WHEN @@SERVERNAME LIKE 'OLTP%' THEN 1 ELSE 0 END", "VariantName": "OLTP class" },
{ "Kind": "Configuration", "Name": "max degree of parallelism", "Value": "0", "Fix": false, "OnDrift": "Warn",
  "ShouldApplyExpression": "SELECT CASE WHEN @@SERVERNAME LIKE 'DW%' THEN 1 ELSE 0 END", "VariantName": "Warehouse class" }
```

A server where no variant applies leaves the setting alone. A server where two apply stops the step and names both, so an overlapping gate is caught the first time it matters, not silently resolved.

## Capture a reference server

You rarely start from a blank file. Point SchemaTongs at the server you trust, turn on `ShouldCast:ServerSettings` and `ShouldCast:DatabaseSettings`, and it writes both files for you -- only the settings that differ from the engine's defaults, and none that name the machine itself, like file paths and ports.

```json
"ShouldCast": {
  "ServerSettings": true,
  "DatabaseSettings": true,
  "SettingsDefaultPolicy": "Report"
}
```

`SettingsDefaultPolicy` sets the starting policy on every captured setting: `Report`, `EnforceWarn` or `EnforceError`. When you want an exact list rather than "whatever is not default", give SchemaTongs an allowlist file with `ShouldCast:SettingsAllowlist`. And a team that would rather review SQL than JSON can set `ShouldCast:SettingsOutput` to `Script`. The [SchemaTongs reference](../reference/schematongs.md#server-and-database-settings) covers each switch.

Commit the files, review them like any other change, and the next extraction from the reference server shows you, as a diff, every setting that moved.

## Jobs, audits and sessions travel too (SQL Server)

A server class is more than its settings. With `ShouldCast:ServerObjects`, SchemaTongs also scripts the server's own objects -- Agent jobs with their steps and schedules, operators and alerts, server and logon triggers, server audits and their specifications, and Extended Events sessions -- into product folders that run on every server the product deploys to.

The scripts are written to be run on every deploy:

- **Jobs keep history.** A job is updated in place, so its history stays attached, and a job that is running when the deploy reaches it is left alone until the next one.
- **Unchanged, untouched.** A job, audit or session that already matches is not modified at all -- no audit switched off and on, no running session restarted.
- **Jobs are yours to edit.** A job's script lists its steps and schedules as values. Change a step's command or a schedule's time in the package, and the next deploy applies it to every server, history intact. An audit or session matches the reference server as it was captured, so change one of those there and extract again.
- **Credentials stay home.** Linked servers, credentials, proxies and Database Mail accounts are never scripted.

## Read the outcome

Every setting that differed gets one line, and the line says what happened -- changed, set but waiting for a restart, reported, failed, or not possible on this server:

```
Server setting Configuration 'cost threshold for parallelism': 5 -> 50
Server setting Configuration 'user connections' set to 500; takes effect after a restart (running value 0)
Database setting Option 'RECOVERY' is FULL, declared SIMPLE (not fixed)
```

Each one is also a row in the [deployment summary](../reference/deployment-summary-report.md), with counts per scope in its `settingOutcomes` block, so a pipeline can tell "the fleet is in line" from "three servers need a restart" without reading the log. Run with `--WhatIf` first and SchemaQuench reads and compares everything, changes nothing, and tells you what a real run would change -- including where it would stop.

**Least privilege works.** Reporting only needs read access to the catalogs. Fixing needs the engine's own rights; without them, the setting is reported as a failed fix with the engine's message, and the rest of the report is complete.
