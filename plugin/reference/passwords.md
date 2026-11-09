# Passwords

A password never goes into a settings file, a command line or this conversation. The tools read it from an environment variable:

| Tool | Variable |
|---|---|
| SchemaTongs | `SmithySettings_Source__Password` |
| SchemaQuench | `SmithySettings_Target__Password` |

The variable has to be in Claude Code's own environment, so the commands it runs inherit it. Ask the user to set it in the terminal *before* starting Claude Code (or in their shell profile), then start Claude Code again from that terminal:

- PowerShell: `$env:SmithySettings_Target__Password = '...'`
- bash or zsh: `export SmithySettings_Target__Password='...'`

Do not ask for the value, and do not suggest typing the command into this session (a `!` command included): what is typed here is kept in the conversation. To check that it arrived without showing it, run a command that prints only whether it is set, such as `test -n "$SmithySettings_Target__Password" && echo set` or, in PowerShell, `[bool]$env:SmithySettings_Target__Password`.

On SQL Server, a blank user means Windows authentication, and no password is needed.
