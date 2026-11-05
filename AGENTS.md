# AGENTS.md — Working in this Repo with an AI Coding Assistant

Quick reference for AI coding agents (Claude Code, Cursor, Copilot, Gemini, etc.) working in this repo. For the full human-readable contribution story, read [`CONTRIBUTING.md`](CONTRIBUTING.md).

## Hard rules — agents must follow these without exception

- **Tests first.** Write the failing test before the implementation. PRs without tests for new behavior will be rejected. See CONTRIBUTING.md "Test-Driven Development."
- **Don't suppress warnings.** `Directory.Build.props` sets `TreatWarningsAsErrors=true`. Fix the underlying issue, never `#pragma warning disable` to silence CI.
- **OS portability.** Code shipped in the CLI tools runs on Windows, Linux, and macOS, on x64 and ARM64. Watch for hard-coded path separators, case-sensitive filesystems, line-ending assumptions, and culture-dependent string formatting.
- **Database Platform parity.** Code that touches SQL Server, PostgreSQL, MySQL, or MariaDB must work on the others where the feature applies. Don't ship single-platform changes without either parity in the same PR or a tracked follow-up issue for the missing platforms.
- **Mock at the right boundary.** Unit tests mock the database (and other dependencies) to stay fast and focused on the unit under test. Integration tests that validate SQL behavior — query plans, type coercion, MERGE semantics, error codes, transaction semantics — MUST run against the real database platforms via Docker; no DB mocking when SQL behavior is the thing being tested. Integration tests for non-DB concerns (file access, deserialization, isolator behavior) can mock the DB to avoid combinatorial runs across all four platforms.
- **Copyright header on new files.** New `.cs` files: `// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.` New `Schema/Scripts/*.sql` files: same text as a `--` comment. Not required on user-facing templates (`TestProducts/`, `Demos/`, anything under `MigrationScripts/` directories).

## Quick reference

- **Build:** `dotnet build SchemaSmith.sln`
- **Test:** `dotnet test SchemaSmith.sln`
- **Target framework:** `net10.0` (single target, set in `Directory.Build.props`)
- **Style:** `.editorconfig` at the repo root encodes naming + formatting rules — let your IDE apply them on save.

## Where things live

- `SchemaQuench/`, `SchemaTongs/`, `DataTongs/`, `SchemaShears/` — the four CLI tools.
- `Schema/` — shared library (domain model, data access, delivery, isolators, embedded scripts). Referenced by the four CLI projects in this repository.
- `TestProducts/`, `Demos/` — schema-package fixtures used by integration tests and demos.
- `docs/end-user/` — user-facing documentation; rendered at schemasmith.com.
- `packaging/` — release packaging assets.

Each tool project has companion `*.UnitTests` and `*.IntegrationTests` projects (e.g., `SchemaQuench.UnitTests`, `SchemaQuench.IntegrationTests`). Tests live next to the project they test.

## Conventions you'll encounter

- **TDD discipline** — see CONTRIBUTING.md "Test-Driven Development."
- **Coverage target >85%** — see CONTRIBUTING.md "Code Coverage." Reductions need a stated reason in the PR description.
- **Conventional commit prefixes** — `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `perf`, `ci`. See CONTRIBUTING.md "Commit Messages."
- **One concern per PR** — see CONTRIBUTING.md "Pull Request Guidelines."
- **Self-review checklist** — walk through it before marking a PR ready. See CONTRIBUTING.md "Self-Review Checklist."

## Exercising the product is a test layer

Building examples and demos, writing or updating docs, and running the CLI end-to-end exercise SchemaSmith through paths the unit and integration suites don't — and regularly turn up real bugs that had no test coverage. Treat that kind of work as a genuine test pass, not just output: where it applies, run it against every supported engine (SQL Server, PostgreSQL, MySQL, MariaDB), and when something behaves unexpectedly, open an issue rather than quietly working around it in the example or doc. Surprising behavior found while actually using the tools is some of the most valuable feedback the project gets.

## What's NOT here

This file deliberately doesn't duplicate `CONTRIBUTING.md`. For:

- The full code-review philosophy → CONTRIBUTING.md "Code Review"
- The Definition of Done → CONTRIBUTING.md "Definition of Done"
- Contributor recognition policy → CONTRIBUTING.md "How Contributors Are Recognized"
- Security disclosure → [`SECURITY.md`](SECURITY.md)
- Code of conduct → [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md)
