# SchemaSmith for Claude Code

This plugin operates the SchemaSmith tools from a Claude Code conversation. It installs them, writes their settings files, extracts a database into a schema package, validates a package, previews a deployment, runs one when you say so, checks a database for drift, explains a run's results, and sets up the GitHub Action.

It does not edit schema packages. It deploys only after showing you the preview and the command, and only to the server and database you confirm.

Extraction, preview and deployment install SchemaSmith's own helper objects on a writable target, as the tools always do. Extraction and preview change nothing of yours.

Passwords are read from environment variables. The plugin never asks for one.

## Install

```
/plugin marketplace add Schema-Smith/SchemaSmith
/plugin install schemasmith@schemasmith
```

It works in Claude Code's terminal, the IDE extensions and the desktop app's Code tab.

## Skills

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

## Contributing

Changes that keep the plugin accurate against the tools are welcome. Features that create or change package content, review a schema's design or plan a migration are outside this plugin's scope.
