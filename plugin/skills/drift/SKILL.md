---
name: drift
description: Check whether a database still matches its SchemaSmith package with SchemaQuench --Drift, and list what differs per target. Use when the user asks if a database has drifted or been changed outside a deployment.
---

# Check for drift

Read `${CLAUDE_PLUGIN_ROOT}/reference/settings.md` and `${CLAUDE_PLUGIN_ROOT}/reference/exit-codes.md` first.

1. Run `SchemaQuench --version`. `--Drift` needs SchemaQuench 2.9 or later; with an older version, say so and stop.
2. Make sure SchemaQuench has its settings for the package and the target (use the `connect` skill if not). On MySQL and MariaDB a drift check needs one of two things, because their DDL cannot be rolled back: `KindleTheForge: false` when the target was deployed by this SchemaQuench version, or `Target:DriftHelperDatabase` naming a database the drift account owns. If neither is set, say so and ask which the user wants.
3. Show the command, then run it: `SchemaQuench --Drift --LogPath:./schemasmith-logs`. A drift check changes nothing of the user's.
4. Read the exit code and the summary's `drift` section in `./schemasmith-logs/SchemaQuench - Summary.json`:
   - `0`: every target matches its package.
   - `5`: at least one target has drifted. For each target with `status` `drifted`, list its `changes` (object, and whether a deploy would create, modify or drop it).
   - `2`: a target could not be checked; use the `explain-run` skill.
   Targets with `status` `unverifiable` could not be compared for some objects; say which.

Done when the verdict is stated per target. A drifted target is brought back by a deployment (the `deploy` skill), or by extracting the change into the package if the database is right.
