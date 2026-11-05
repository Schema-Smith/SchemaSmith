# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 2.x     | Yes       |
| 1.x     | No        |

v2.x is feature-complete across all four platforms (SQL Server, PostgreSQL, MySQL, MariaDB) and replaces v1 with no upgrade cost. v1.x is no longer maintained.

## Reporting a Vulnerability

**Do not open a public issue for security vulnerabilities.**

Instead, please report security issues by emailing **security@schemasmith.com** with:

- Description of the vulnerability
- Steps to reproduce
- Affected tool(s) and version(s)
- Impact assessment if known

You should receive an acknowledgment within 48 hours. We will work with you to understand the issue and coordinate a fix before any public disclosure.

## Scope

SchemaSmith tools connect to SQL Server, PostgreSQL, MySQL, and MariaDB databases and execute SQL. Security-relevant areas include:

- **Connection credential handling** — passwords are redacted in logs, but configuration files may contain credentials
- **SQL generation** — tools generate and execute dynamic SQL against target databases
- **File handling** — tools read schema packages from disk or ZIP archives

## Threat Model

SchemaSmith is an **operator-run** command-line tool, intended to be run by an operator against databases and schema definitions that operator controls. In the normal case, schema definitions (table JSON, `Project.json`, configuration) and the chosen target connections are trusted inputs.

Hardening therefore focuses on the surfaces where input may originate from a **less-trusted source** than the operator's own authored schema:

- **Identifiers read from a database via introspection** (catalog views / `INFORMATION_SCHEMA`) are bound as query parameters rather than interpolated into SQL, so identifier contents cannot alter the introspection queries.
- **Template and content-file references** that may come from externally-authored or distributed schema packages are constrained to the template's own directory tree during path resolution.

Running SchemaSmith against a schema package or database you do not trust is outside the intended operating model; the hardening above reduces — but does not eliminate — that risk. Treat untrusted schema packages with the same caution you would any untrusted code or configuration.

## Best Practices

- Prefer your platform's strongest non-password authentication over storing credentials in config files: Windows integrated authentication or Azure AD on SQL Server, Kerberos / GSSAPI or SCRAM-SHA-256 on PostgreSQL, socket auth or PAM auth on MySQL and MariaDB
- Restrict file system permissions on configuration files containing credentials
- Use environment variables (`SmithySettings_` prefix) for sensitive settings in CI/CD pipelines
- Review WhatIf output before applying changes to production databases

## Security posture and common questions

For how the tools behave in your environment, how releases are built and verified, and
answers to common security-review questions, see [SECURITY-POSTURE.md](SECURITY-POSTURE.md).
