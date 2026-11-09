# Where each tool writes

Without `--LogPath`, a tool writes its logs into its own install folder. Always pass `--LogPath:<folder>` so they land where you can read them.

| File | Written by | Holds |
|---|---|---|
| `<Tool> - Progress.log` | every tool | What the run did, line by line, as the console showed it |
| `<Tool> - Errors.log` | every tool | Each error with its full detail |
| `SchemaQuench - Failures.log` | SchemaQuench, when a target failed | Each failed target: its error and the lines leading up to it |
| `SchemaQuench - Summary.json` | SchemaQuench: deploy, `--WhatIf`, `--Drift` | The deployment summary: the run, each target's outcome, what changed or would change, failures, drift. Its `schemaVersion` names the schema it follows |
| `SchemaQuench - Summary.md` | the same runs | The same summary, written to read |

Reading `Summary.json`:

- `run.mode` is `Quench`, `WhatIf` or `Drift`; `run.outcome` is `Success`, `Incomplete`, `PartialFailure` or `Aborted`; `run.exitCode` is the code the run ended with.
- `targets[]` has one entry per server, database and schema, each with its `outcome`.
- `objectChanges.created`, `modified` and `dropped` count tables, columns, indexes, constraints and foreign keys; `objectChanges.details[]` names each change. In a preview the actions are `wouldCreate`, `wouldModify` and `wouldDrop`.
- `whatIf.wouldApply` lists the scripts a deployment would run.
- `destructive.changes[]` lists each change that removes data, and `outcome` says whether it was applied, refused, or would be.
- `failures[]` has each failed target's `error`.
- `drift` (drift checks only): `drifted`, and per target its `status` and `changes`.

The field reference: https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/deployment-summary-report.md
