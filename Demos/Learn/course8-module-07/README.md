# Course 8 · Module 7 — Before you share the log

<!-- TRAINING-RELEASE-PIN 2.8.0 (--ExitNonZeroOnWarning): delete the "On 2.7.x" callouts -->
<!-- TRAINING-RELEASE-PIN #576 (ScrubArtifacts): re-check Step 6 when the issue is closed -->
<!-- TRAINING-RELEASE-PIN #577 (LogHygiene echo masks itself): delete the Step 3 note and show the echo listing DeployKey -->

The last module of Course 8. Every module so far ended with a log you read. This one ends with a log you
**hand to someone else** — attached to a ticket, uploaded as a CI artifact, pasted into a chat. Before it
leaves your machine, you want to know what's in it. SchemaSmith masks a lot on its own. This module shows
you what it masks, the secret it lets through, how to close that gap, and the one file it never masks on
purpose.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.7.3 or later** on your PATH.

## The package

Each engine folder holds the same small product, `Settings`: one table, `app_setting`, and an After script
that writes the app's integration settings into it on every deploy. The values come from the product's
`ScriptTokens`:

```json
"ScriptTokens": {
  "Region": "us-east",
  "DeployKey": "dk-7f3a91c2",
  "ReportingApiKey": "rk-55aa01be",
  "WebhookUrl": "https://deploybot:hunter2@hooks.shop.test/deploys",
  "AuditDsn": "Server=audit.shop.test;User Id=auditor;Password=Aud1t-Only;"
}
```

Four of those five are secrets. Keep the four values in mind — `dk-7f3a91c2`, `rk-55aa01be`, `hunter2` and
`Aud1t-Only` — because the honest test of a log is searching it for them.

## Step 1 — create the sandbox database

**macOS / Linux**

```bash
# from course8-module-07
bash setup-databases.sh --reset
```

**Windows (PowerShell)**

```powershell
# from course8-module-07
.\setup-databases.ps1 -Reset
```

`--reset` (`-Reset`) drops `diag_hygiene` and recreates it empty. Prints `PASS (reset)` per engine. Only a
database the labs created is ever dropped.

## Step 2 — deploy, then read what the log kept

Change into your engine's folder (`sqlserver`, `postgres`, `mysql`, or `mariadb`); every `schemaquench`
below runs there, and every run writes to the **same** `logs` folder:

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

Exit `0`. Open `logs/SchemaQuench - Progress.log` and find the token block near the top:

```
  Product Script Tokens:
    Region: us-east
    DeployKey: dk-7f3a91c2
    ReportingApiKey: ***
    WebhookUrl: https://deploybot:***@hooks.shop.test/deploys
    AuditDsn: Server=audit.shop.test;User Id=auditor;Password=***;
    ProductName: Settings
```

Same block on all four engines. Read it line by line:

- **`ReportingApiKey: ***`** — masked **by name**. Any name containing `Password`, `Pwd`, `Secret`,
  `ApiKey`, `Token`, `ConnectionString` or `Credential` (any case) prints as `***`.
- **`WebhookUrl`** — masked **by shape**. The name means nothing, but the value is a URL with a password in
  it, so the password goes and the user, host and path stay.
- **`AuditDsn`** — also by shape: a `Password=` inside a connection-string value is stripped whatever the
  setting is called.
- **`Region`** — in clear, and that's right.
- **`DeployKey: dk-7f3a91c2`** — in clear, and that's the leak. `Key` alone isn't one of the patterns.
  SchemaSmith masks what *looks* secret; it can't know what your names mean.

Don't trust your eyes on a long log. Search it for the values you know:

```bash
# from course8-module-07/<engine>
grep -rl "dk-7f3a91c2" logs
```

```powershell
# from course8-module-07/<engine>
Get-ChildItem logs -Recurse -File | Select-String "dk-7f3a91c2" -List | Select-Object -ExpandProperty Path
```

```
logs/SchemaQuench - Progress.log
logs/SchemaQuench.0001/SchemaQuench - Progress.log
```

Two files, not one (more, if you ran Step 2 again). Every run also keeps a numbered copy of its logs (`SchemaQuench.0001`, `.0002`, …) in
the same folder. Remember that for the next step.

## Step 3 — name the secret

`quench.settings.scrub.json` adds one block:

```json
"LogHygiene": {
  "ScrubTokens": [ "DeployKey" ]
}
```

Deploy with it, into the same `logs` folder:

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.scrub.json --LogPath:"$PWD/logs"
```

Exit `0`, and the token block now reads `DeployKey: ***`. Search again:

```bash
# from course8-module-07/<engine>
grep -rl "dk-7f3a91c2" logs
```

```
logs/SchemaQuench.0001/SchemaQuench - Progress.log
```

(On Windows, the `Select-String` line from Step 2 with the same value.) The current log is clean. The copy from Step 2's run is not. **Fixing the setting fixes the next log, never
the last one.** So share the current run's files, not the whole folder — or clear the folder and re-run
before you attach it.

`LogHygiene` has three lists. `ScrubTokens` names exact tokens; `ScrubPatterns` adds patterns of your own
(`"*Salt*"`); `AllowTokens` lets a false positive back out (a `PublicToken` that holds nothing
secret). Name your secrets once, in the settings file the deploy already uses.

> **Note:** the configuration echo at the top of the log prints this block as `ScrubTokens: ***` — the
> setting's own name contains `Token`, so it masks itself (#577). Check the token block, not the echo, to
> see the setting worked.

## Step 4 — a block the tools can't read

`quench.settings.oops.json` makes the most common mistake — one value where a list belongs:

```json
"LogHygiene": {
  "ScrubTokens": "DeployKey"
}
```

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.oops.json --LogPath:"$PWD/logs"
```

Exit `0`, and the log says so plainly:

```
LogHygiene:ScrubTokens is a single value, not a list, and is ignored. Write it as [ "DeployKey" ].
```

Ignored means ignored: the token block is back to `DeployKey: dk-7f3a91c2`. A misspelled key gets the same
treatment — `"ScrubToken"` logs `LogHygiene:ScrubToken is not a setting and is ignored. The settings are LogTokens, ScrubTokens, ScrubPatterns and AllowTokens.` That warning line is
the only signal, and an exit `0` buries it. If your pipeline should stop on it, add `--ExitNonZeroOnWarning`:

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.oops.json --LogPath:"$PWD/logs" --ExitNonZeroOnWarning
```

Exit `1`: the run finished and logged a warning. The switch never changes a failure code; it only turns a
`0` that logged a warning into a `1`.

> **On 2.7.x** the switch does not exist: the run logs a line starting `Ignored - not recognized: --ExitNonZeroOnWarning` and exits 0.

## Step 5 — when the token list is the problem

A product with hundreds of tokens turns the top of every log into a wall. `quench.settings.quiet.json` sets
`"LogTokens": false`:

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.quiet.json --LogPath:"$PWD/logs"
```

Exit `0`. The whole token block becomes one line:

```
  Product Script Tokens: (suppressed via LogHygiene.LogTokens=false)
```

No names, no values. The tokens still reach your scripts — confirm the After script wrote them:

```bash
# from course8-module-07
../lab-sql.sh sqlserver diag_hygiene "SELECT setting_name FROM dbo.app_setting ORDER BY setting_name"
../lab-sql.sh postgres diag_hygiene "SELECT setting_name FROM app_setting ORDER BY setting_name"
../lab-sql.sh mysql diag_hygiene "SELECT setting_name FROM app_setting ORDER BY setting_name"
../lab-sql.sh mariadb diag_hygiene "SELECT setting_name FROM app_setting ORDER BY setting_name"
```

Three rows: `deploy_key`, `region`, `webhook_url`.

## Step 6 — the file the log points at

Now a deploy that fails. `broken/` adds a script that rotates the reporting key and then writes to a
`key_rotation_log` table nobody added to the package:

```bash
# from course8-module-07/<engine>
schemaquench --ConfigFile:quench.settings.broken.json --LogPath:"$PWD/logs"
```

Exit `2`. You know this shape from Module 1 — the error, then the pointer:

```
Resolved SQL written to: ./artifacts\SchemaQuench - Failed 02_rotate_report_key <server>.diag_hygiene.sql
Unable to quench '.\broken\Templates\Main\After Scripts\02_rotate_report_key.sql': <the engine's "no such table" error>
```

(`<server>` is `localhost,11433` on SQL Server and `localhost` elsewhere; paths print with your system's
separator.) Search both folders for the
reporting key:

```bash
# from course8-module-07/<engine>
grep -rl "rk-55aa01be" logs artifacts
```

```
artifacts/SchemaQuench - Failed 02_rotate_report_key <server>.diag_hygiene.sql
```

(On Windows: `Get-ChildItem logs, artifacts -Recurse -File | Select-String "rk-55aa01be" -List | Select-Object -ExpandProperty Path`.) The logs are clean. The artifact holds the key in clear — `VALUES ('report_api_key', 'rk-55aa01be')` — and
says so in its own header:

```
-- Contains expanded values — may be sensitive. Local debugging; scrub before sharing.
```

That's on purpose. The artifact exists so you can paste it into your client and re-run the failure exactly
as the deploy did (Module 1), and it can only do that with the real values in it. So the rule is short:
**logs travel, artifacts stay on your machine.** Attach the log to the ticket; keep the artifact local.

Don't reach for `ScrubArtifacts` to make one shareable. It replaces each secret with `***`, and a `***`
inside quotes is still valid SQL — re-run that file and it writes `***` into your table (#576). Masked is
safe to read, not safe to run.

## Before you attach a log

1. **Share the run, not the folder.** Older runs' copies don't get any cleaner when you fix a setting.
2. **Search for every secret you know** — the values, not the names. The tools mask what looks secret; only
   you know the rest.
3. **Leave the artifacts behind.** They hold real values by design.

## Cleanup

```bash
# from course8-module-07/<engine>
rm -rf logs artifacts checkpoints
```

```powershell
# from course8-module-07/<engine>
Remove-Item -Recurse -Force logs, artifacts, checkpoints -ErrorAction SilentlyContinue
```

## Course 8 complete

You can now locate any failure's phase, read its artifact, name whose problem it is, recover it, and hand
the log to someone else without handing over your secrets — on SQL Server, PostgreSQL, MySQL, or MariaDB.
For the masking rules in full, see the configuration reference's *Sensitive value masking* section. That's
the course.
