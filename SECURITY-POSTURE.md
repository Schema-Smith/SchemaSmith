# Security Posture

SchemaSmith is a set of command-line tools for declarative database schema
management. This document describes how the tools behave in your environment, how they
are built and released, and how to reach us about a security issue.

For vulnerability reporting and the threat model, see [SECURITY.md](SECURITY.md).

## Delivery model

SchemaQuench, SchemaTongs, SchemaShears, and DataTongs are operator-run command-line
tools. You download them, and they execute on infrastructure you control — a workstation,
a build agent, or a deployment runner inside your own network. They connect only to the
databases named in the configuration you supply.

There is no hosted service, no account, and no server-side component. Nothing about
running these tools creates a relationship in which we hold, process, or transmit your
data.

## Data handling

**What the tools read.** Schema definitions from your schema package (table JSON,
`Project.json`, configuration), and database catalog metadata from the targets you point
them at.

**Credentials.** Connection settings come from your configuration files or from
environment variables prefixed `SmithySettings_`. Credentials are redacted in log output.
We recommend the `SmithySettings_` environment-variable path for CI/CD pipelines, and
your platform's strongest non-password authentication where available — see
[SECURITY.md](SECURITY.md) for per-engine specifics.

**What the tools do not do.** SchemaQuench, SchemaTongs, SchemaShears, and DataTongs make
no outbound network connections other than to the databases you configure. There is no
telemetry, no usage reporting, no update check, and no license check. The tools contain no
HTTP client and open no sockets of their own.

## Secure development

- **Change review.** Product code reaches the default branch through pull requests.
- **Static analysis.** CodeQL runs on a weekly schedule using the extended query suite,
  covering C#, GitHub Actions workflows, and Python.
- **Secret scanning.** Enabled, with push protection, on the public repository.
- **Test gate.** Unit tests and a full integration suite run against SQL Server,
  PostgreSQL, MySQL, and MariaDB in containers on every push and pull request, including a
  supported-floor version leg for PostgreSQL and MariaDB alongside the current release.
- **Dependency monitoring.** Dependency and GitHub Actions updates are tracked
  automatically, with security advisories surfaced against the repository's dependency
  graph.

## Release integrity

- **Build provenance and reproducibility.** Releases are built by a GitHub Actions
  workflow from a tagged commit in the public repository. Every action used in the build
  is pinned to a specific commit SHA rather than a mutable tag.
- **Release gate.** A release cannot be produced unless the following checks are green on
  the release commit: `Build & Unit Tests`, `Integration Tests (SQL Server)`,
  `Integration Tests (PostgreSQL latest)`, `Integration Tests (PostgreSQL 14)`,
  `Integration Tests (MySQL)`, `Integration Tests (MariaDB 11.4)`,
  `Integration Tests (MariaDB 10.6)`, `Coverage Gate`, and `check-headers`.
- **Windows code signing.** Windows binaries (`win-x64`, `win-arm64`) are Authenticode
  signed via Azure Trusted Signing.
- **Checksums.** Every release publishes a `SHA256SUMS` file covering all release assets.
- **Runtime contents.** Binaries are published self-contained: each archive bundles the
  .NET 10 runtime alongside the tool executables. The bundled runtime is Microsoft's, not
  ours, and is updated by taking a newer runtime and cutting a new release.
- **Software bill of materials.** Every release publishes a CycloneDX SBOM
  (`SchemaSmith-<version>.cdx.json`) enumerating the third-party packages the tools
  depend on, with resolved licenses. It covers declared dependencies, not the bundled
  .NET runtime described above.
- **Build provenance.** Release archives and packages carry a signed provenance
  attestation binding each artifact to the workflow, repository, and commit that produced
  it. Verify any release asset with the GitHub CLI:

  ```
  gh attestation verify SchemaSmith-<version>-linux-x64.tar.gz --repo Schema-Smith/SchemaSmith
  ```

## Vulnerability disclosure

Report security issues to **security@schemasmith.com**. Do not open a public issue. You
will receive an acknowledgment within 48 hours. Full process, scope, and threat model:
[SECURITY.md](SECURITY.md).

## Licensing

SchemaSmith is distributed under the SchemaSmith Community License v2.0. For
SBOM and license-scanning tools, declare it as the SPDX custom identifier
`LicenseRef-SSCL-2.0`. The authoritative text is in [LICENSE](LICENSE); the repository
carries a machine-readable [REUSE](https://reuse.software) declaration.

## Common intake questions

**Do you store, process, or transmit our data?**
No. The tools run on your infrastructure and connect only to databases you configure. We
operate no service that receives your data.

**Is there multi-tenancy or shared infrastructure?**
Not applicable. There is no hosted component.

**Who are your subprocessors?**
None, for the operation of these tools. Nothing is transmitted to a third party.

**Where does our data reside?**
Wherever you run the tools and host your databases. No data leaves your environment.

**Do you have a SOC 2 report or ISO 27001 certification?**
No. Both attest to the controls of a *service organization* — an entity that holds,
processes, or transmits customer data on its customers' behalf. These tools run entirely
within your infrastructure and we operate no service that processes your data, so there is
no service boundary such a report would cover.

What we provide instead is evidence about the software itself: a published threat model,
static analysis, secret scanning, a signed and checksummed release pipeline built from
public source, and a documented disclosure process. Those are listed above.

**Has the software had a third-party penetration test?**
No. Static analysis (CodeQL, extended query suite) runs continuously, and identified
issues are remediated in the public repository where the history is auditable. Given the
operator-run model — no network service, no exposed endpoint, no attacker-reachable
surface we host — a penetration test has limited applicability.

**Can you provide an SBOM?**
Yes. Every release includes a CycloneDX SBOM as a downloadable asset, listing declared
third-party dependencies with resolved licenses. SchemaSmith itself is declared
as `LicenseRef-SSCL-2.0`.
