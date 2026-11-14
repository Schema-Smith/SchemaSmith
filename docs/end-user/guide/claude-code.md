# Using SchemaSmith with Claude Code

If you work in Claude Code, the SchemaSmith plugin walks you from nothing to a first extraction, preview and deployment in one conversation. It knows the tools' switches, settings, exit codes and outputs, and runs them through Claude Code's own shell, asking before each command.

```
/plugin marketplace add Schema-Smith/SchemaSmith
/plugin install schemasmith@schemasmith
```

It works in Claude Code's terminal, the IDE extensions and the desktop app's Code tab. The plugin's version matches the tools': each release of the tools moves it.

## What it does

| Skill | What it does |
|---|---|
| `/schemasmith:install` | Installs the tools for your operating system and checks each answers `--version` |
| `/schemasmith:connect` | Writes a settings file for a server and proves it with `--TestConnection` |
| `/schemasmith:extract` | Extracts a database into a new schema package and validates it |
| `/schemasmith:preview` | Validates a package and previews a deployment with `--WhatIf`, in plain words |
| `/schemasmith:deploy` | Runs a deployment after a preview, once you confirm the server and database |
| `/schemasmith:drift` | Checks whether a database still matches its package |
| `/schemasmith:explain-run` | Reads the last run's exit code, summary and logs, and says what happened |
| `/schemasmith:ci` | Writes a GitHub Actions workflow using the SchemaSmith Deploy action |

You can call a skill by name, or just ask: "extract my Shop database into a package", "what would a deploy change?", "why did that fail?"

## What it does not do

**It does not edit packages.** Writing tables, scripts and templates is yours, with the [package reference](../reference/schema-packages.md) and the JSON schemas your editor reads. After any edit, `SchemaQuench --Validate` checks the package, and the plugin runs it for you.

**It deploys only when you say so.** It shows the preview and the exact command, names the server and database, and waits for your yes. A yes covers that target only. It never changes a safety setting to get a deployment through.

**It never asks for a password.** Passwords come from environment variables you set in the shell (`SmithySettings_Target__Password`, `SmithySettings_Source__Password`), never a settings file or the conversation.

Extraction, preview and deployment install SchemaSmith's own helper objects on a writable target, as the tools always do ([what they are](../reference/schemaquench.md#what-schemasmith-installs-in-your-database)). Extraction and preview change nothing of yours.
