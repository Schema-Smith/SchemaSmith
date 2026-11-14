---
name: connect
description: Write a SchemaSmith settings file for a database server and prove the connection with --TestConnection. Use when the user wants to point SchemaSmith at a server or check that it can connect.
---

# Connect to a server

Read `${CLAUDE_PLUGIN_ROOT}/reference/settings.md` and `${CLAUDE_PLUGIN_ROOT}/reference/passwords.md` first.

1. Run `SchemaQuench --version`. If it does not answer, use the `install` skill.
2. Ask for the platform (`SqlServer`, `PostgreSQL`, `MySQL` or `MariaDb`), the server, the port if it is not the default, the user, and the package folder if the user already has one. Never ask for the password.
3. Write `SchemaQuench.settings.json` in the working folder with `SchemaPackagePath` and `Target:Server`, `Target:Port`, `Target:User`. If a settings file is already there, show what would change and ask before replacing it.
4. Unless the user connects with Windows authentication on SQL Server, ask them to set `SmithySettings_Target__Password` before starting Claude Code, as `passwords.md` describes, and check it arrived without printing it.
5. Show the command, then run it: `SchemaQuench --TestConnection --LogPath:./schemasmith-logs`. `--TestConnection` connects and checks the server's version against the package's minimum; it changes nothing.
6. Exit code `0`: say the connection works. Anything else: read `./schemasmith-logs/SchemaQuench - Progress.log` and say what failed (a refused login, an unreachable server, an untrusted certificate) and which setting to look at. For a certificate the server signs itself, the setting is `Target:ConnectionProperties:TrustServerCertificate` on SQL Server.

Done when `--TestConnection` exits `0`.
