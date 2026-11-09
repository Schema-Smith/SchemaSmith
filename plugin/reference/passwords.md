# Passwords

A password never goes into a settings file, a command line or this conversation. The tools read it from an environment variable:

| Tool | Variable |
|---|---|
| SchemaTongs | `SmithySettings_Source__Password` |
| SchemaQuench | `SmithySettings_Target__Password` |

Ask the user to set it in the shell Claude Code runs commands in, and wait until they say it is set. Do not ask for the value and do not echo it. On Windows PowerShell: `$env:SmithySettings_Target__Password = '...'`; in bash or zsh: `export SmithySettings_Target__Password='...'`. A variable set in another terminal is not seen here.

On SQL Server, a blank user means Windows authentication, and no password is needed.
