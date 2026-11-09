---
name: deploy
description: Deploy a SchemaSmith package to a database with SchemaQuench, only after a preview and only to the server and database the user confirms. Use when the user asks to deploy, apply or quench a package.
---

# Deploy a package

This is the only skill that changes a user's database. Read `${CLAUDE_PLUGIN_ROOT}/reference/settings.md`, `${CLAUDE_PLUGIN_ROOT}/reference/outputs.md` and `${CLAUDE_PLUGIN_ROOT}/reference/exit-codes.md` first.

1. Run `SchemaQuench --version`. If it does not answer, use the `install` skill.
2. **Preview first.** If this conversation has not shown a `--WhatIf` of this package against this server and database, use the `preview` skill now and show its plan.
3. **Name the target and wait for a yes.** Show the exact command, `SchemaQuench --LogPath:./schemasmith-logs`, and say which server and which database or databases it will change, from the settings in effect. Ask the user to confirm. A yes covers this server and these databases only; a different target needs its own preview and its own yes.
4. Do not change a safety setting to get a deployment through. When the preview shows a change that `Target:DestructiveChangePolicy` would refuse, or a table drop the drop settings would make, say which setting governs it and let the user decide.
5. Run the command.
6. Read the exit code and `./schemasmith-logs/SchemaQuench - Summary.json`, and say per target whether it succeeded and what changed: created, modified and dropped objects (`objectChanges`), the scripts that ran, and the data delivered.
   - Exit `0`: done.
   - Exit `1`: it finished, but something was not applied; name each item from the summary (`unsupportedDowngrade`) or the progress log.
   - Exit `2`: one or more targets failed. Use the `explain-run` skill. A failed deployment is resumed by running it again after the cause is fixed; `--ResumeQuench` skips what the failed run finished.

Done when the outcome is stated for every target.
