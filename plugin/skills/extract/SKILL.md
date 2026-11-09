---
name: extract
description: Extract an existing database into a new SchemaSmith schema package with SchemaTongs, then validate it. Use when the user wants to start a package from a database they already have.
---

# Extract a database into a package

Read `${CLAUDE_PLUGIN_ROOT}/reference/settings.md` and `${CLAUDE_PLUGIN_ROOT}/reference/passwords.md` first.

1. Run `SchemaTongs --version` and `SchemaQuench --version`. If either does not answer, use the `install` skill.
2. Ask for the platform, server, port if not the default, user, the database to extract, and the folder to write the package to. Never ask for the password.
3. The folder must be new or empty. If it already holds a `Product.json`, stop and ask: extracting into an existing package updates it, and the user should decide that knowingly.
4. Write `SchemaTongs.settings.json` with `Source:Platform`, `Source:Server`, `Source:Port`, `Source:Database`, `Source:User`, `Product:Name` and `Product:Path`.
5. Unless it is Windows authentication on SQL Server, ask the user to set `SmithySettings_Source__Password` in this shell and wait.
6. Show the command, then run it: `SchemaTongs --LogPath:./schemasmith-logs`. Extraction reads the database; it installs SchemaSmith's helper objects in it on a writable database, as the tools always do, and changes nothing else.
7. Exit `0`: list what the package holds (the templates under `Templates/`, and per template the folders of tables, views, procedures and so on). Exit `1`: some objects were left out; quote the progress log's reasons. Exit `2`: read the logs and say what failed.
8. Validate it: `SchemaQuench --Validate --LogPath:./schemasmith-logs` with `SmithySettings_SchemaPackagePath` set to the package folder (or `SchemaPackagePath` in `SchemaQuench.settings.json`). Report each finding with its code. `--Validate` connects to nothing.

Done when the package folder exists and `--Validate` reports no errors.

After any edit to a package, run `--Validate` again.
