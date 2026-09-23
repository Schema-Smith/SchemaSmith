# Deployment Summary Report

One command fans out across dozens of tenants, and one of them comes back red. Which target? Which phase? Was it the migration script or a modified table? Did the whole run drag because a single database took ninety seconds in one slot? Scrolling an interleaved progress log for those answers is slow work. The Deployment Summary Report is the machine-readable receipt for the run — every target, every timing, every failure, and every verified object change, in one structured file you can read, diff, or feed to a dashboard.

Every quench writes one. You don't ask for it, you don't switch it on — it lands next to your logs on success, on partial failure, and even when the run hard-aborts. When you need to know what a deployment actually *did*, this is the first file to open.

---

## What SchemaQuench writes

The report is two files carrying the same run, in two shapes: a `Summary.json` for machines and a `Summary.md` for humans. Both are produced from the identical in-memory model, so they never disagree — the JSON is the contract, the Markdown is the same facts rendered to read at a glance.

By default both land in the log directory alongside the run's other logs:

```text
SchemaQuench - Summary.json
SchemaQuench - Summary.md
```

They're archived with the rest of the logs when a run finishes, so a report travels with the progress log, the errors log, and the failure roll-up it describes — one bundle per run, nothing to collect separately.

> **Note:** The report is strictly best-effort. Writing it is wrapped so that a failure to assemble or serialize the summary can never disrupt the run's real logging, exit code, or control flow — a broken report never breaks a deployment. If it can't be written, you get a one-line warning in the progress log and the run proceeds exactly as it would have.

> **Emitted on every exit path.** Success, partial failure, and all three hard-abort sites funnel through the same writer, and it's idempotent — an aborting run writes the report once on its way out. A run that died is exactly the run whose report you most want to read, so the report is there for it.

## Redirecting the report

The default location keeps the report with its logs, which is what you want most of the time. But CI pipelines often want the summary at a known path — a build artifact to publish, a file a later step parses — independent of wherever the logs happen to rotate. The `--report` switch pins both files wherever you name them.

```bash
SchemaQuench --report:./artifacts/deploy-summary
```

That writes `./artifacts/deploy-summary.json` and `./artifacts/deploy-summary.md`. You give the path *without* an extension; SchemaQuench appends `.json` and `.md` to the base you provide. Omit the switch and both files fall back to `SchemaQuench - Summary.json` / `.md` in the log directory.

Attach the value with `:` or `=`, as with every other SchemaSmith switch — **not** with a space. A space-separated `--report ./artifacts/deploy-summary` leaves the switch with no value, so the report silently falls back to the default location instead of the path you named.

## Tuning bottleneck detection

A big fan-out has a long tail. Most targets finish in a second or two; a handful crawl. The report's `bottlenecks` list exists to surface exactly those outliers — the individual slot-on-a-target measurements that ran long enough to be worth a look — without you scanning every timing by hand. The cutoff is one setting.

`BottleneckThresholdMs` sets the millisecond bar an individual slot measurement must *exceed* to be listed as a bottleneck. The default is `30000` (30 seconds). Lower it to catch smaller stalls on a fast fleet; raise it on a heavy release where a minute per slot is normal and you only care about the true stragglers.

```bash
SchemaQuench --BottleneckThresholdMs=10000
```

Set it in the settings file (`"BottleneckThresholdMs": 10000`), as an environment variable (`SmithySettings_BottleneckThresholdMs=10000`), or on the command line as above. It only governs which measurements appear in `timing.bottlenecks` — every slot is still timed and rolled up in `bySlot` and `byDatabase` regardless of the threshold.

## Reading Summary.json

The JSON is the frozen contract: camelCase keys, enum values as their names, indented for reading. Here it is end to end for a small two-tenant run, annotated — the field tables below the example define every key.

```jsonc
{
  "schemaVersion": "1.1",           // contract version of this report shape
  "tool": "SchemaQuench",
  "toolVersion": "2.2.0.0",
  "run": {
    "product": "Northwind",
    "platform": "SqlServer",        // SqlServer | PostgreSQL | MySQL | MariaDb
    "startedUtc": "2026-07-09T14:03:11.204Z",
    "finishedUtc": "2026-07-09T14:03:47.881Z",
    "durationMs": 36677,            // run wall-clock
    "mode": "Quench",               // Quench | WhatIf | Validate
    "outcome": "Success",           // Success | PartialFailure | Aborted
    "exitCode": 0,
    "resumedFromCheckpoint": false
  },
  "targets": [
    {
      "server": "primary",
      "database": "TenantA",
      "schema": "sales",            // null when the target has no schema
      "template": "Tenant",
      "outcome": "Success",         // Success | Failed | Skipped
      "durationMs": 14820,
      "databaseSource": "DatabaseIdentificationScript",
      "schemaSource": "TemplateTargets:TenantSchema:Schemas",
      "slots": [
        { "slot": "ModifiedTables", "durationMs": 9120, "scriptsRun": 3 },
        { "slot": "ObjectScripts",  "durationMs": 4110, "scriptsRun": 12 }
      ]
    }
  ],
  "migrationScripts": [
    {
      "path": "MigrationScripts/0007-backfill-region.sql",
      "slot": "MigrationScripts",
      "template": "Tenant",
      "schema": "sales",
      "server": "primary",
      "database": "TenantA",
      "outcome": "Ran"             // always "Ran" — a listed script is one that ran
    }
  ],
  "timing": {
    "totalMs": 36677,
    "bySlot": [
      { "slot": "ModifiedTables", "totalMs": 18240, "targetCount": 2 }
    ],
    "byDatabase": [
      { "database": "TenantA", "totalMs": 14820 }
    ],
    "bottlenecks": [
      { "scope": "[primary].[TenantA] [Schema: sales]", "slot": "ModifiedTables", "durationMs": 31210 }
    ]
  },
  "failures": [],                   // one entry per failed scope; empty on a clean run
  "whatIf": null,                   // populated only for a WhatIf-mode run
  "objectChanges": {
    "instrumented": true,
    "created":  { "tables": 1, "columns": 2, "indexes": 4, "constraints": 2, "foreignKeys": 1, "procedures": 0, "views": 0, "functions": 0 },
    "modified": { "tables": 1, "columns": 3 },
    "dropped":  { "tables": 0, "indexes": 1, "constraints": 0, "foreignKeys": 0 },
    "scriptsRan": 12,
    "details": [
      { "objectType": "table",     "objectName": "sales.Orders",           "action": "created" },
      { "objectType": "column",    "objectName": "sales.Orders.Region",    "action": "modified" },
      { "objectType": "index",     "objectName": "sales.Orders.IX_Region", "action": "dropped" },
      { "objectType": "procedure", "objectName": "Procedures/GetOrders.sql","action": "ran" }
    ]
  },
  "unsupportedDowngrade": {         // null unless a feature was degraded for the target version
    "downgrades": [
      { "feature": "expression statistics (PG14)", "objectName": "public.metric.ST_metric_expr" }
    ]
  }
}
```

### Top-level fields

| Key | Meaning |
| --- | --- |
| `schemaVersion` | Contract version of the report shape — currently `"1.1"`. |
| `tool` | Always `"SchemaQuench"`. |
| `toolVersion` | The CLI version that wrote the report — the same string `--version` prints. |
| `run` | Run-level facts: product, platform, timing, mode, outcome. |
| `targets` | One entry per `(server, database, schema)` target the run touched. |
| `migrationScripts` | One entry per migration script that ran. |
| `timing` | Aggregate timing plus the bottleneck outliers. |
| `failures` | One entry per failed scope — the same content as the failure roll-up log. |
| `whatIf` | The would-apply / would-skip / would-deliver plan; `null` unless the run was `WhatIf` mode. |
| `objectChanges` | Verified DDL changes and object-script runs — its own section below. |
| `unsupportedDowngrade` | Features the package declared that the target version couldn't take, emitted in a degraded form and recorded — its own section below. `null` when nothing was degraded. |

### `run`

| Key | Meaning |
| --- | --- |
| `product` | The product name from `Product.json`. |
| `platform` | `SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`. |
| `startedUtc` / `finishedUtc` | Run start and end, UTC. |
| `durationMs` | Wall-clock milliseconds for the whole run. |
| `mode` | `Quench` (a real deploy), `WhatIf` (a dry run), or `Validate`. |
| `outcome` | `Success`, `PartialFailure` (some targets failed, others succeeded), or `Aborted`. |
| `exitCode` | The process exit code the run returned. |
| `resumedFromCheckpoint` | `true` when the run resumed a prior interrupted deployment. |

### `targets[]`

| Key | Meaning |
| --- | --- |
| `server` / `database` / `schema` | The target's coordinates; `schema` is `null` when the target has no schema. |
| `template` | The template that produced this target. |
| `outcome` | `Success`, `Failed`, or `Skipped`. |
| `durationMs` | Milliseconds spent on this target. |
| `slots[]` | Per-slot timing for this target: `slot`, `durationMs`, `scriptsRun`. |
| `databaseSource` / `schemaSource` | Where each axis of this target came from — the discovery script, or a `TemplateTargets` override naming the template and key. This is the run's source-disclosure audit trail: it answers "was this tenant selected by the script, or overridden in config?" per target. Empty on a run with no source information recorded. |

### `migrationScripts[]`

| Key | Meaning |
| --- | --- |
| `path` | Package-relative path of the migration script. |
| `slot` | The slot it ran in. |
| `template` / `schema` / `server` / `database` | Where it ran; `schema` and `database` are `null` when not applicable. |
| `outcome` | Always `"Ran"` — a script only appears here because it ran. |

### `timing`

| Key | Meaning |
| --- | --- |
| `totalMs` | Run wall-clock, matching `run.durationMs`. |
| `bySlot[]` | Per-slot rollup across all targets: `slot`, `totalMs`, `targetCount`. |
| `byDatabase[]` | Per-database rollup: `database`, `totalMs`. |
| `bottlenecks[]` | Individual slot-on-a-target measurements exceeding `BottleneckThresholdMs`: `scope`, `slot`, `durationMs`. |

### `failures[]`

Empty on a clean run. Each entry mirrors the failure triage roll-up exactly — same content, same backup directory, no new exposure.

| Key | Meaning |
| --- | --- |
| `phase` | The phase the failure occurred in. |
| `scopeKey` | The failed scope — a tenant, a per-server script, or a product-level phase. |
| `error` | The engine's error text for the failure. |
| `contextTail[]` | The captured tail of log lines leading up to the failure. |
| `artifactPath` | Path to the resolved-SQL artifact for the failed scope, when one was written. |

### `whatIf`

`null` for a real quench. On a `WhatIf`-mode run it holds the plan, split three ways — and every entry carries a script *path*, never a SQL body. This block is the *script-level* plan; the engine-generated structural changes a WhatIf run would make (tables, columns, indexes, constraints, foreign keys) preview in [`objectChanges`](#objectchanges--what-actually-changed) with `would*` actions.

| Key | Meaning |
| --- | --- |
| `wouldApply[]` | Changes the run would apply: `scope`, `script`. |
| `wouldSkip[]` | Changes it would skip. |
| `wouldDeliver[]` | Data-delivery scripts it would deliver. |

## objectChanges — what actually changed

Timing tells you where the run spent its seconds; `objectChanges` tells you what it *did to your schema*. This is the section a DBA reads after a release: how many tables were created, which columns were modified, what got dropped. But it draws a hard, honest line between changes SchemaSmith *verified* and scripts it merely *ran* — and understanding that line is the whole point of the section.

**Verified counts.** As the four table-quench procedures run DDL, they record each real change to a session-scoped audit that SchemaSmith drains back in-process. Those captured rows are the `created`, `modified`, and `dropped` counts — genuine, observed structural changes to tables, columns, indexes, constraints, and foreign keys. If the count says one table created and three columns modified, that is what happened, read back from the engine.

**Under WhatIf.** A `WhatIf` run records what it *would* change with the parallel actions `wouldCreate` / `wouldModify` / `wouldDrop`, which roll into the same `created` / `modified` / `dropped` buckets — so a dry run's `objectChanges` previews the structural changes it would make rather than reporting an empty section. The counts read the same as a real run; the report's `mode` field (`WhatIf` vs `Quench`) is how you tell a preview from an applied change.

**Scripts that ran.** Object scripts — your stored procedures, views, and functions — are a different story. SchemaSmith re-applies them idempotently on *every* run, so a procedure script executes whether or not its body changed anything. SchemaSmith refuses to guess. It will not tell you a procedure was "created" or "modified" when all it honestly knows is that the script *ran*. So object scripts never touch the created/modified counts. Instead they contribute to `scriptsRan` (a count) and to `details[]` rows carrying `"action": "ran"`.

> **Why "ran", not "changed".** Reporting a re-applied procedure as "modified" every single run would be a lie that made every report look busy. An honest report says exactly what it knows: verified structural changes as counts, idempotent re-applies as "ran". When you see `scriptsRan: 12`, twelve object scripts executed — the report is not claiming twelve objects changed.

### The count buckets

| Bucket | Object types counted |
| --- | --- |
| `created` | `tables`, `columns` (columns added to an already-existing table — a new table's own columns count under that table's creation, not here), `indexes`, `constraints`, `foreignKeys`, plus `procedures` / `views` / `functions` fields that stay `0` by design (object scripts don't count as created). |
| `modified` | `tables`, `columns`. |
| `dropped` | `tables`, `indexes`, `constraints`, `foreignKeys`. |
| `scriptsRan` | Total object scripts that ran this run. |

### `details[]`

Where the counts are the summary, `details[]` is the itemized list — one row per recorded change or run, each with `objectType`, `objectName`, and `action`. The actions you'll see are `created`, `modified`, `dropped`, and `ran` on a real quench — and their `wouldCreate` / `wouldModify` / `wouldDrop` previews on a `WhatIf` run.

`details[]` also carries object types that have no dedicated count bucket. The verified-change audit records more kinds of object than the count fields cover, and those surface here rather than being dropped:

- **`statistic`** — a statistics object created (SQL Server and PostgreSQL).
- **`xmlIndex`** — an XML index created (SQL Server).
- **`fullTextIndex`** — a full-text index created or dropped (SQL Server, MySQL, MariaDB).
- **`constraint`** — includes exclude constraints (PostgreSQL), which land in the generic constraint type.

These appear as `details[]` rows with their real `objectType` and `action` even though no top-level count field aggregates them — the detail is preserved even where the summary doesn't bucket it.

### `instrumented`

| Value | Meaning |
| --- | --- |
| `true` | The run's engine produced a real audit read; the counts and details are populated. |
| `false` | The engine couldn't read the audit (for example, kindling was suppressed), so every count is `0` and `details[]` is empty. |

> **Note:** `instrumented: false` means *unknown*, not *nothing happened*. A run whose audit couldn't be read reports honestly-empty change data rather than pretending zero changes occurred. Read the progress log for what the run actually did in that case.

## unsupportedDowngrade — features the target couldn't take

When a package declares a feature the detected target version can't support — an expression statistic on PostgreSQL below 14, a `CHECK` constraint on MySQL below 8.0.16, a descending or invisible index on an older MariaDB, a data-masked or temporal column on a pre-2016 SQL Server — the default `Target:UnsupportedFeaturePolicy` (`warn`) emits the object *without* the unsupported aspect and records the fact here. The deploy succeeds; the report is your receipt for exactly what was relaxed. (Set the policy to `fail` and the run aborts instead of degrading — see the [SchemaQuench reference](schemaquench.md).)

These downgrades are recorded **separately** from `objectChanges` — they are not a `details[]` action — because a degraded object is a distinct outcome from a created, modified, or dropped one: the object landed, but not at full fidelity.

```json
"unsupportedDowngrade": {
  "downgrades": [
    { "feature": "expression statistics (PG14)", "objectName": "public.metric.ST_metric_expr" },
    { "feature": "CHECK constraint (MySQL 8.0.16)", "objectName": "Ledger.CK_Ledger_Amount" }
  ]
}
```

| Key | Meaning |
| --- | --- |
| `downgrades[]` | One entry per object emitted in a degraded form. |
| `downgrades[].feature` | The feature that was relaxed, tagged with the version it needs — for example `expression statistics (PG14)`, `INDEX (invisible, MySQL 8.0 / MariaDB 10.6)`. |
| `downgrades[].objectName` | The affected object, `schema.table.item`. |

The whole field is `null` when nothing was degraded. In `Summary.md` the same information renders under an **Unsupported Feature Downgrades** heading, one bullet per feature — the human-readable receipt a reviewer reads after a mixed-version release.

## Cross-platform

The report shape is identical on SQL Server, PostgreSQL, MySQL, and MariaDB — same keys, same nesting, same enum values. The `platform` field tells you which engine produced it, and a few `details[]` object types are engine-specific (statistics on SQL Server and PostgreSQL, XML indexes on SQL Server, full-text indexes on SQL Server / MySQL / MariaDB, exclude constraints on PostgreSQL), but the contract is one shape across all four. A dashboard that parses a SQL Server report parses a MySQL or MariaDB one unchanged.

## What's next

- The `failures[]` block is the structured twin of the failure triage roll-up — for the per-engine error codes and where each engine logs its faults, see [Error Codes & Reporting Channels](error-codes-and-reporting.md).
- For the full set of SchemaQuench switches and settings, see the [SchemaQuench reference](schemaquench.md).
- When a run comes back red and you need the hands-on recovery method, see the [troubleshooting guide](../guide/12-troubleshooting.md).
