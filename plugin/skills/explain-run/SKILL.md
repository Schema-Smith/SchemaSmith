---
name: explain-run
description: Explain the result of the last SchemaSmith run from its exit code, deployment summary and logs, and point to the documentation that covers it. Use when a run failed, exited non-zero, or the user asks what happened.
---

# Explain a run

Read `${CLAUDE_PLUGIN_ROOT}/reference/exit-codes.md` and `${CLAUDE_PLUGIN_ROOT}/reference/outputs.md` first.

1. Find the run's log folder: the `--LogPath` it was given (the other skills use `./schemasmith-logs`), else the tool's install folder.
2. Start from the exit code and what it means.
3. For SchemaQuench, read `SchemaQuench - Summary.json`: `run.outcome`, then each target's `outcome`, and for each failed target its entry in `failures[]` (`error`, and `contextTail`, the lines before it). `Summary.md` has the same in prose.
4. Read `SchemaQuench - Failures.log` (or `<Tool> - Errors.log`, and the end of `<Tool> - Progress.log`) for the detail the summary does not carry.
5. Say what happened: which target, which step or script, and the engine's error, quoted. If the cause is clear, name it and the setting or file involved. If it is not, quote the lines and say so; do not guess.
6. Point to the page that covers it: troubleshooting, https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/guide/12-troubleshooting.md, and the error reference, https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/error-codes-and-reporting.md

Done when the cause is named or the relevant log lines are quoted. A script that failed is fixed in the package by the user; after any edit to a package, run `SchemaQuench --Validate`.
