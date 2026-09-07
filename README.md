# SchemaSmith Community Edition

*Terraform for SQL Server, Postgres, MySQL, and MariaDB databases*

> **SchemaSmith v2.5.0 released.** Five new object types across SQL Server, PostgreSQL and MariaDB, new column-level features on every engine, and 43 fixes — plus SQL Server 2016 deploys again. [Read the v2.5.0 announcement](https://github.com/Schema-Smith/SchemaSmith/discussions/396) · [Release notes](https://github.com/Schema-Smith/SchemaSmith/releases/tag/v2.5.0)

![Build](https://github.com/Schema-Smith/SchemaSmith/actions/workflows/continuous-integration.yml/badge.svg)
[![Latest Release](https://img.shields.io/github/v/release/Schema-Smith/SchemaSmith)](https://github.com/Schema-Smith/SchemaSmith/releases/latest)
[![Chocolatey](https://img.shields.io/chocolatey/v/schemasmith)](https://community.chocolatey.org/packages/schemasmith)
[![License: SSCL v2.0](https://img.shields.io/badge/license-SSCL%20v2.0-blue)](LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/Schema-Smith/SchemaSmith)](https://github.com/Schema-Smith/SchemaSmith/stargazers)

**Featured in:** [![Mentioned in Awesome MariaDB](https://awesome.re/mentioned-badge.svg)](https://github.com/Vettabase/awesome-mariadb)

SchemaSmith is a state-based database schema management toolset for SQL Server, PostgreSQL, MySQL, and MariaDB. Define your desired database state as metadata — tables, views, procedures, indexes, constraints, data — and SchemaSmith transforms any target server to match. Same toolset, same package format, four engines — no migration scripts to author or order.

Self-contained, single-file executables for Windows, Linux, and macOS. No .NET runtime install needed.

⭐ **Find SchemaSmith useful?** [Star the repo](https://github.com/Schema-Smith/SchemaSmith/stargazers) — it helps other database teams discover it.

## Tools

- **SchemaTongs** — Extracts databases into schema packages across all four platforms. Pure SQL extraction with no external SDKs, orphan detection with cleanup-script generation, post-extraction script validation, and subfolder preservation so your repository organization survives re-extraction.
- **SchemaQuench** — Deploys schema packages to SQL Server, PostgreSQL, MySQL, and MariaDB. 9 execution slots, conditional deployment via `ShouldApplyExpression`, secondary-server fan-out, FK-aware data delivery, checkpoint/resume, WhatIf analysis, indexed views (SQL Server), materialized views (PostgreSQL), and a token system that reaches every script.
- **DataTongs** — Extracts table data and generates platform-aware MERGE scripts for SQL Server, PostgreSQL, MySQL, and MariaDB. Auto primary-key detection, complex type support (geometry, hierarchyid, binary), and full token resolution including MySQL.
- **SchemaShears** — Carves an object-level patch (subset) package from a full product via a manifest. Emitted patches suppress drop-by-absence so omitted objects are preserved.

For the complete feature reference, see [docs/FEATURE_LIST.md](docs/FEATURE_LIST.md).

## Platform Support

### Operating systems

| OS | x64 | ARM64 |
|----|-----|-------|
| Windows | win-x64 | win-arm64 |
| Linux | linux-x64 | linux-arm64 |
| macOS | osx-x64 | osx-arm64 |

### Supported database versions

These are the minimum engine versions SchemaSmith supports. The floor is enforced automatically — a below-floor target aborts the run with a clear message before anything is deployed.

| Engine | Minimum supported |
|--------|-------------------|
| SQL Server | 2008 (major version 10) |
| PostgreSQL | 12 |
| MySQL | 5.7 |
| MariaDB | 10.2 |

The floor is enforced from the detected server version — below it, the run aborts before any change. SchemaSmith reaches these floors by generating version-correct SQL for each target rather than demanding a uniform version: newer-version features are taken by an equivalent path (or degraded through a policy with a downgrade manifest) instead of refused. For SQL Server the target database's `compatibility_level` is also checked (100+). Full detail — the compatibility-level floor, the per-version feature adaptations, and how to raise the floor per product — is in the [Engine Version Compatibility](docs/end-user/reference/schemaquench.md#engine-version-compatibility) reference.

## Installation

### Chocolatey (Windows)

```powershell
choco install schemasmith
```

Installs `schemaquench`, `schematongs`, and `datatongs` onto your PATH as a single combined package. Binaries are Authenticode-signed via Azure Trusted Signing — no SmartScreen warnings.

### winget (Windows)

```powershell
winget install SchemaSmith.SchemaSmith
```

Installs all four CLI commands (`SchemaQuench`, `SchemaTongs`, `DataTongs`, `SchemaShears`) onto your PATH from the Authenticode-signed release zip.

### Linux / macOS (install script)

```bash
curl -fsSL https://schemasmith.com/dl/install.sh | sh
```

Installs the CLI tools (`schemaquench`, `schematongs`, `datatongs`, `schemashears`) from the official release binaries — to `/usr/local/bin` as root, otherwise `~/.local/bin`. Resolves the latest release automatically; pin a version with `INSTALL_VERSION=x.y.z` or redirect with `INSTALL_DIR=<path>`. Targets glibc-based Linux and macOS (Alpine/musl isn't supported — use the `.deb` / `.rpm` packages or a glibc base image).

### GitHub Releases

Download self-contained ZIP packages from the [latest release](https://github.com/Schema-Smith/SchemaSmith/releases/latest). Extract and run — no .NET runtime required.

### Arch Linux (AUR)

```bash
yay -S schemasmith-bin
```

Installs the CLI tools (`schemaquench`, `schematongs`, `datatongs`, `schemashears`) from the official release binaries. Works with any AUR helper.

### Build from Source

```bash
dotnet build SchemaSmith.sln
```

For self-contained publishing of the CLI tools:
```bash
# Windows
.\build-schemaquench.cmd

# Linux/macOS
./build-schemaquench.sh
```

### Docker

SchemaQuench ships as a container image on Docker Hub and GHCR — run a deploy with no .NET install:

```bash
# Docker Hub
docker run --rm \
  -e SmithySettings_SchemaPackagePath=/pkg \
  -e SmithySettings_Target__Server=db.example.com \
  -e SmithySettings_Target__User=deploy \
  -e SmithySettings_Target__Password="$DB_PASSWORD" \
  -v "$PWD/schema:/pkg" \
  schemasmithyfree/schemaquench:latest

# GHCR (reliable pulls behind corporate NAT / Docker Hub's anonymous rate limit)
docker run --rm -v "$PWD/schema:/pkg" \
  -e SmithySettings_SchemaPackagePath=/pkg \
  ghcr.io/schema-smith/schemaquench:2.6.0 --Validate
```

Tags: `latest`, `X.Y.Z` (immutable), `X.Y`, `X`. Multi-arch (`linux/amd64` + `linux/arm64`). Configure via `SmithySettings_` environment variables (`__` denotes nesting) or a mounted `SchemaQuench.settings.json`; append `--Key:value` overrides as needed.

## GitHub Action (CI/CD)

Run SchemaQuench in a workflow with the **SchemaSmith Deploy** action — WhatIf on pull requests, deploy on merge:

```yaml
- name: WhatIf on PR
  if: github.event_name == 'pull_request'
  uses: Schema-Smith/SchemaSmith@v2.6.0
  with:
    mode: whatif
    product-path: ./schema
    server: ${{ secrets.DB_SERVER }}
    user: ${{ secrets.DB_USER }}
    password: ${{ secrets.DB_PASSWORD }}

- name: Deploy on merge
  if: github.ref == 'refs/heads/main'
  uses: Schema-Smith/SchemaSmith@v2.6.0
  with:
    mode: deploy
    product-path: ./schema
    server: ${{ secrets.DB_SERVER }}
    user: ${{ secrets.DB_USER }}
    password: ${{ secrets.DB_PASSWORD }}
```

The action fetches the matching self-contained binary for the runner OS at run time — no runtime install. **Pinning a release tag (`@v2.6.0` above) is recommended for production** — a tag pins both the action and the CLI version it runs; `@main` tracks the latest action instead (the `version` input defaults from the ref).

- **Inputs:** `version`, `mode` (`deploy` / `whatif` / `validate` / `test-connection` / `preview-targets`), `product-path`, `server`, `user`, `password` (passed via env, never on the command line), `extra-args` (raw `--Key:value` passthrough — port, template/database/schema filters, `Drop*` toggles, connection properties, and more).
- **Outputs:** `exit-code`, `log-dir`, `summary-path` (the `SchemaQuench - Summary.md`/`.json` — e.g. post a WhatIf summary as a PR comment).

## Quick Start

Pick a platform and run the matching `run-demo` script:

```bash
# SQL Server
cd Demos/SqlServer && ./run-demo.sh

# PostgreSQL
cd Demos/PostgreSQL && ./run-demo.sh

# MySQL
cd Demos/MySQL && ./run-demo.sh

# MariaDB
cd Demos/MariaDB && ./run-demo.sh
```

Each script builds SchemaQuench from source (if not already built), starts a containerized database server, and deploys the AdventureWorks, Chinook, Northwind, and Sakila demo products. Use `run-demo.cmd` on Windows. Connection details for each platform live in the `.env` file inside the platform folder.

## Running Tests

Integration tests run against all four supported platforms in parallel and expect database servers on these local ports:

| Platform   | Host      | Port |
|------------|-----------|------|
| SQL Server | 127.0.0.1 | 1440 |
| PostgreSQL | 127.0.0.1 | 5432 |
| MySQL      | 127.0.0.1 | 3306 |
| MariaDB    | 127.0.0.1 | 3317 |

MariaDB runs on the MySQL engine but binds its own port (3317), so all four containers run side by side.

The simplest way to bring them up is to run the demo for each platform — the same containers serve as the integration-test backends:

```bash
cd Demos/SqlServer  && ./run-demo.sh
cd Demos/PostgreSQL && ./run-demo.sh
cd Demos/MySQL      && ./run-demo.sh
cd Demos/MariaDB    && ./run-demo.sh
```

Then run tests:

```bash
dotnet test SchemaSmith.sln
```

Integration tests for a platform whose container isn't running will be skipped or fail — start only the platforms you need to exercise locally.

## Demo Products

Four sample databases ship across all four platforms:

- **AdventureWorks** (71 tables) — Microsoft's reference OLTP schema
- **Chinook** — digital media store, common across DB tutorials
- **Northwind** (13 tables) — classic small-business sample
- **Sakila** — DVD rental store, originally a MySQL reference

See [Demos/README.md](Demos/README.md) for the per-platform layout, credentials, and SQL Server tutorials.

## Security

To report a vulnerability, see [SECURITY.md](SECURITY.md). For how the tools handle
credentials and network access, how releases are built and signed, and answers to common
security-review questions, see [SECURITY-POSTURE.md](SECURITY-POSTURE.md).

## License

SchemaSmith Community Edition is licensed under [SSCL v2.0](LICENSE). Use it freely to manage databases for your own products and services — SQL Server, PostgreSQL, MySQL, or MariaDB — with no restrictions on organization size, revenue, database size, or environment count. Not permitted: redistributing SchemaSmith as a standalone product, bundling it as a component of another product marketed to third parties, or offering it as a hosted or managed service. See the [LICENSE](LICENSE) for the full terms.

For SBOM and license-scanning tools, SSCL v2.0 is declared as the SPDX custom identifier `LicenseRef-SSCL-2.0` (SSCL is not on the SPDX License List).

## Contributors

External contributors:

- [@noctelvirei](https://github.com/noctelvirei) / [@zacnaloen](https://github.com/zacnaloen)

For the full list see the [GitHub contributors page](https://github.com/Schema-Smith/SchemaSmith/graphs/contributors), and [CONTRIBUTING.md](CONTRIBUTING.md) for how to get involved.

## Additional Resources

- [End-User Documentation](docs/end-user/README.md) — guide chapters and reference docs
- [Documentation Site](https://schemasmith.com/) — multi-platform documentation
