# Contributing to SchemaSmith

Thanks for considering a contribution. SchemaSmith is community-driven, and we welcome bug reports, feature suggestions, documentation improvements, and code.

This document is the rules of the road for contributing. We point at it when we push back on a PR — the goal is to make our standards visible up front so reviewers and contributors are calibrated to the same bar before any code gets written. The rigor isn't accidental; it's the price of trust on a tool that touches your production schema.

## Ways to Contribute

- **Report bugs** — Open an issue with detailed steps to reproduce, the platform you hit it on (SQL Server, PostgreSQL, MySQL, or MariaDB — including version), and any relevant logs.
- **Suggest enhancements** — Open an issue describing the use case and the shape of the feature. Larger features benefit from a design conversation in the issue before code lands.
- **Submit code** — Fix bugs, add features, improve docs. For non-trivial changes, open an issue first so we can align on direction before you invest implementation time.
- **Improve docs** — The [documentation site](https://schemasmith.com/), in-repo `docs/` directory, README, and CHANGELOG are all fair game.

## AI-Assisted Contributions

AI-assisted PRs are welcome — many of us use AI tooling in our own workflow. The standards in this document apply regardless of how the patch was authored: tests come first, the same review bar applies, the contributor owns the PR and is responsible for understanding the code they're submitting, defending design choices in review, and addressing follow-up issues.

If you use AI tooling with its own GitHub identity, both your handle and the tool's handle may appear in the commit history. Both are credited in CHANGELOG attribution (see *How Contributors Are Recognized* below); the welcome line for first-time contributors fires on whichever handle opens the PR.

## Before You Start

For anything beyond a typo fix or a one-line bug repro, please skim this whole document before opening a PR. It's shorter than the review you'd otherwise get back. Two specific items contributors most often miss:

- **Tests come first.** We practice TDD. PRs that add behavior without tests, or that lower coverage without a documented reason, will be sent back.
- **OS portability is non-negotiable.** Code that ships in the CLI tools must run correctly on Windows, Linux, and macOS, on x64 and ARM64. CI runs the OS matrix on every push.
- **Database Platform parity is non-negotiable.** Code that touches a database platform must work on SQL Server, PostgreSQL, MySQL, and MariaDB where the feature applies. CI runs all four platforms on every push. (MariaDB runs on the MySQL engine, so parity there is usually automatic — but it has its own CI job and can diverge, e.g. JSON and collation handling.)

## Development Setup

The CLI tools target:

- **.NET:** `net10.0` (single target, set in `Directory.Build.props`)
- **IDEs:** Visual Studio 2026 or JetBrains Rider (both with full ReSharper / built-in analyzer support)
- **Databases tested in CI:** SQL Server 2017, 2019, 2022 and 2025; PostgreSQL 12 through 18; MySQL 5.7.22, 5.7, 8.0.23, 8.0, 8.4, 9.7 and 26; MariaDB 10.2, 10.6, 11.4, 11.8, 12.3 and 13, plus MySQL 8.0 and MariaDB 11.4 with `lower_case_table_names=1`. A pull request runs a subset (the oldest and newest of each); a merge to `main` runs them all. The supported floors are SQL Server 2008 (database compatibility level 100+), PostgreSQL 12, MySQL 5.7.22 and 8.0.23, and MariaDB 10.2.13.

### Running Locally with Docker

The `Demos/` directory has a separate Docker compose stack per platform — `Demos/SqlServer/`, `Demos/PostgreSQL/`, `Demos/MySQL/`. Each stack spins up the database server and runs SchemaQuench to deploy the bundled demo schema packages (AdventureWorks, Chinook, Northwind, Sakila).

Each platform folder has `run-demo.cmd` (Windows) and `run-demo.sh` (Linux/macOS) that build SchemaQuench from your local source and stand the stack up correctly:

```
# Windows
cd Demos\SqlServer    & run-demo.cmd
cd Demos\PostgreSQL   & run-demo.cmd
cd Demos\MySQL        & run-demo.cmd

# Linux / macOS
cd Demos/SqlServer    && ./run-demo.sh
cd Demos/PostgreSQL   && ./run-demo.sh
cd Demos/MySQL        && ./run-demo.sh
```

Default credentials are in each stack's `.env` file. See [`Demos/README.md`](Demos/README.md) for additional details.

Pull the [SchemaSmithDemos](https://github.com/Schema-Smith/SchemaSmithDemos) repository for additional demo products beyond what's bundled here.

### Running Tests

From the repo root:

```
dotnet test SchemaSmith.sln
```

Integration tests require live database containers. The Docker compose stack above is the supported way to run them locally; CI runs against fresh containers for each push.

## Project and Solution Organization

The solution is organized around the three end-user CLI tools and the shared schema library:

- **`SchemaQuench/`** — CLI that deploys a schema package to a target database.
- **`SchemaTongs/`** — CLI that extracts a live database into a clean, source-controllable schema package.
- **`DataTongs/`** — CLI that captures and deploys reference data alongside schema.
- **`Schema/`** — Shared library containing the domain model (`Domain/`, with platform-specific `Domain.SqlServer/`, `Domain.PostgreSQL/`, `Domain.MySQL/` projections), data access (`DataAccess/`), data delivery (`Delivery/`), checkpointing, isolators, embedded SQL scripts (`Scripts/`), and utilities. Referenced by the four CLI projects in this repository.
- **`TestProducts/`** — Schema packages used by integration tests as fixtures.
- **`Demos/`** — Demo schema packages and Docker compose stack for local development.
- **`packaging/`** — Release packaging assets (install scripts, package templates, etc.).
- **`docs/`** — In-repo documentation. The published site at schemasmith.com renders from `docs/end-user/`.

Each tool project has companion `*.UnitTests` and `*.IntegrationTests` projects (e.g., `SchemaQuench.UnitTests`, `SchemaQuench.IntegrationTests`). The `Schema` library follows the same pattern (`Schema.UnitTests`, `Schema.IntegrationTests`). Tests live next to the project they test, not in a parallel `tests/` tree.

`Directory.Build.props` and `Directory.Packages.props` at the repo root centralize target framework, package versions, warnings-as-errors, and copyright metadata. Don't override these in individual `.csproj` files unless there's a specific reason.

## Coding Standards

### Test-Driven Development

Write the test first. The test should fail for the right reason, then implementation makes it pass. Refactor with the test as a safety net. This isn't a stylistic preference — it's how we keep the regression surface bounded on a tool that touches production schemas.

PRs that add behavior without corresponding tests will be sent back. If you genuinely cannot test something (rare — usually means an isolator is missing), call it out in the PR description so we can discuss; don't quietly skip the tests.

**Assert the outcome, not the mechanism.** Ask of every test: if this broke the way a user would notice, would the test fail? A test that checks a function was called, or that a message contains a fragment, passes over the defect it was written to catch. Assert what the user gets — the data that survived, the value that arrived, the exact line that was logged. If a fix makes you change an existing test, check whether that test was pinning the defect, and strengthen it rather than relax it.

**Prove the test bites.** A test written after the code passes for reasons that have nothing to do with the bug surprisingly often: a filter that selects no tests, an assertion that is always true. Before calling a fix done, revert the fix, watch the test fail with a message that says what is wrong, then restore it. Check the test count too — a run that selects nothing is not a pass. Say in the PR that you did.

### Code Coverage

- **Target:** >85% line coverage, aiming close to 100% on new code.
- **Tooling:** `coverlet` collects line coverage during `dotnet test` (configured by `coverage.runsettings`). CI runs collection in every test job — unit and all three database engines — merges the results, and **fails the build** if any project or the solution total falls below its line-coverage threshold. The merged report and a per-project summary are published on every CI run.
- **Thresholds (line %):** DataTongs 92, Schema 92, SchemaQuench 91, SchemaTongs 90, solution 91. These protect the current level rather than the bare 85% floor and are a ratchet — raised over time toward the observed baseline, never lowered to make a red gate green. The fix for a failing gate is added tests.
- **Non-regression:** A PR that reduces coverage — even if the result stays above target — needs an explicit, specific reason in the PR description. "I didn't get to it" is not a reason; "this code path requires an isolator we haven't built and I've filed issue #N to track it" is. **Lowering a gate threshold, or widening the coverage exclusions to drop product code out of the denominator, is a flagged review event** — call it out and investigate; it is never an invisible part of getting CI green. Legitimate exclusions are test assemblies and compiler plumbing only; excluding product code in any form (new `<Exclude>` patterns, `GeneratedCodeAttribute`, or `[ExcludeFromCodeCoverage]` on real code) counts the same as lowering the number.

### OS Portability

Code shipped in the CLI tools runs on Windows, Linux, and macOS, on x64 and ARM64. Common pitfalls to avoid:

- Hard-coded path separators (`\\` or `/`) — use `Path.Combine`, `Path.DirectorySeparatorChar`.
- Case-sensitive filesystems on Linux/macOS — file lookups must match casing exactly.
- Line endings — see `.editorconfig` (CRLF default; LF for shell scripts; trailing whitespace preserved in markdown).
- Process invocation — `cmd` / `pwsh` aren't available everywhere; prefer pure-.NET implementations or guard with platform checks.
- Time zones, current-culture string formatting — be explicit about `CultureInfo.InvariantCulture` for any data that touches storage or comparison.

CI runs the test matrix across the supported operating systems and database platforms; treat a CI failure as the contract.

### Database Behavior — Mock at the Right Boundary

Unit tests should mock the database (and other dependencies) to stay fast and focused on the unit under test. That's the norm and is encouraged — `Schema.UnitTests`, `SchemaQuench.UnitTests`, and friends do exactly this throughout the codebase.

Integration tests that validate SQL behavior — query plans, type coercion, transaction semantics, MERGE behavior, error codes — MUST run against the real database platforms via the Docker matrix. These behaviors can only be validated against the actual database, which is why the integration tests against live SQL Server, PostgreSQL, MySQL, and MariaDB containers exist.

Integration tests for non-DB concerns (file access, deserialization, isolator behavior) can mock the DB to avoid combinatorial test runs across all four platforms — the goal there is to exercise the non-DB code path without paying the multi-platform cost for behavior that's platform-agnostic anyway.

If you find yourself reaching for a DB mock to avoid spinning up a container in a test that IS about SQL behavior, please don't. File an issue if there's a real gap in the integration test infrastructure.

### Test at the Right Boundary

When a capability surfaces through a specific call site, put its test at the layer where the capability lives — not at the call site that happened to exercise it. If `TableQuench` provides primary-key extension as a capability that any caller can depend on, the test belongs in `TableQuench`'s own test suite, not in the test file for whatever feature first needed it.

The question to ask yourself: *if someone else calls into this same code tomorrow with a different shape of input, does my test cover them?* If not, the test is sitting too high — push it down to the capability's own layer, where it protects every caller. Often a test file already exists at that layer; extend it rather than starting a fresh one at the call site.

This is distinct from *mocking* at the right boundary (above): that's about whether a test uses a real database; this is about which layer owns the test.

### Warnings, Style, and Comments

- **Treat warnings as errors.** `Directory.Build.props` sets `TreatWarningsAsErrors=true`. Don't suppress warnings to make the build green — fix the underlying issue, or discuss in the PR if a suppression is genuinely the right call.
- **Follow ReSharper / built-in analyzer recommendations.** Remove unused usings, unused variables, and dead code as you go. The `.editorconfig` file at the repo root encodes the canonical style and naming rules; let your IDE apply them automatically.
- **SonarAnalyzer runs in the build.** `SonarAnalyzer.CSharp` is referenced solution-wide from `Directory.Build.props` (dev-only, `PrivateAssets=all`), so its rules gate every `dotnet build` locally and in CI — no separate job. A curated set is enforced at error; the remaining rules are silenced in `.editorconfig`, each with a per-rule reason. If you hit a Sonar finding, fix it; suppress a rule (with a reason in `.editorconfig`) only after confirming it's a false positive or genuinely inapplicable to this codebase.
- **Comments explain *why*, not *what*.** Well-named identifiers describe what code does; comments are for the non-obvious — a hidden constraint, a workaround for a specific bug, a subtle invariant that would surprise a reader. Don't restate code in prose. Don't add comments that say "added for issue #123" — that belongs in the PR description and rots as the codebase evolves.
- **Prefer descriptive names over comments.** A method called `WaitForReplicaSync` doesn't need a comment explaining what it waits for.

### Naming Conventions

The `.editorconfig` at the repo root encodes the .NET-standard rules: PascalCase for types, methods, properties, events, namespaces, and constants; camelCase for locals and parameters; `_camelCase` for private fields; `I`-prefix for interfaces. These are configured at *suggestion* severity — your IDE highlights deviations but the build doesn't fail on them. Match the existing style of the file you're editing.

File naming:
- One public type per file; the file name matches the type name.
- Test files sit in the corresponding `*.UnitTests` or `*.IntegrationTests` project, mirroring the namespace structure of the type under test.

### SQL Script Conventions

Stored-procedure and function scripts under `Schema/Scripts/{SqlServer,PostgreSQL,MySQL}/` follow the [SQL script conventions](docs/development/sql-script-conventions.md) — dynamic-SQL aggregate-to-string over row-by-row cursor loops, with engine-specific idioms (MySQL's single-statement `PREPARE` limitation, the `INFORMATION_SCHEMA`-snapshot rule, and the cursor/loop allow-list). Reviewers check new SQL against this guide.

### Copyright Headers

New files in the codebase need a copyright header on the first line:

- **`.cs` files:**
  ```
  // Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
  ```
- **`.sql` files in `Schema/Scripts/`:**
  ```
  -- Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.
  ```

Not required on user-facing templates (`TestProducts/`, `Demos/`, anything under `MigrationScripts/` directories).

**BOM tolerance:** Some `Schema/Scripts/*.sql` files have a UTF-8 BOM (`EF BB BF`) before the copyright comment. The CI check strips a leading BOM before comparison; the BOMs are harmless to runtime and helpful for SSMS round-trips. Don't strip them as "cleanup" — that's removing intentional content.

## Workflow

1. **Open or claim an issue.** For non-trivial changes, an issue establishes scope and direction before code lands.
2. **Fork** the repository and create a feature branch off `main`:
   ```
   git checkout -b feature/short-descriptive-name
   ```
3. **Write the test first.** Then the implementation. Then refactor.
4. **Commit** in small, focused units. See commit message format below.
5. **Push** to your fork and open a **pull request** against `main`. Mark it draft if you want early feedback on direction.
6. **Address review feedback** with follow-up commits on the same branch.

### Commit Messages

We use a conventional-commit-style prefix to keep history scannable:

```
type(scope): short summary in present tense

Optional body explaining the why, not the what. Wrap at 72 cols.
References related issues with "Fixes #N" or "Refs #N".
```

Common types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `perf`, `ci`. Scope is the area touched (e.g., `schemaquench`, `schema`, `readme`, `release`). Subject is concise and descriptive — "fix state diff logic for dropped columns" beats "fix bug".

### Pull Request Guidelines

- **One concern per PR.** A 2,000-line PR mixing a feature, a refactor, and a bug fix is harder to review than three smaller PRs.
- **Tests in the same PR as the behavior.** Don't promise "tests in a follow-up."
- **Reference related issues** in the PR description (`Fixes #42`, `Part of #100`).
- **PR description matters.** Summarize the change and the reasoning, list any user-visible behavior changes, call out anything reviewers should pay extra attention to (security implications, performance considerations, OS portability or Database Platform parity concerns, coverage changes).
- **CI must be green** before merge. Don't ask for review on a red PR unless you specifically want help diagnosing the failure.
- **Be prepared to make changes.** Code review is collaborative; pushing back on feedback is fine if you have a reason, but expect a conversation.

## Code Review

Code review is the moment we apply the standards in this document. The checklists below describe what reviewers look for and what contributors should self-check before requesting review. Both sides looking at the same list keeps the conversation focused on substance, not surprise.

### Self-Review Checklist (Contributors)

Before you click "Ready for review," walk through this list against your own diff:

- [ ] Tests added or updated, and they exercise the new behavior — not just call into it.
- [ ] Tests live at the layer where the capability lives, not just at the call site that exercised it.
- [ ] Tests pass locally on `dotnet test SchemaSmith.sln`.
- [ ] Coverage maintained or improved on touched code (or a documented reason for any reduction).
- [ ] No new compiler warnings or analyzer hints introduced.
- [ ] OS portability considered: paths, line endings, case sensitivity, culture-dependent string formatting.
- [ ] Database Platform parity considered if the change touches database-platform-specific code: does the same behavior need a corresponding implementation on SQL Server, PostgreSQL, MySQL, and MariaDB?
- [ ] No unused usings, dead code, or commented-out blocks left behind.
- [ ] Comments explain *why*, not *what*; new identifiers are descriptive enough that the comment isn't needed.
- [ ] Copyright header on any new `.cs` or `Schema/Scripts/*.sql` files.
- [ ] If the domain model or its JSON-schema attributes changed, the committed `.json-schemas/` files under `Demos/`, `TestProducts/` and the `--Validate` test fixtures are regenerated with `--WriteSchemasOnly`; stale ones fail the `SS-STALE-001` check.
- [ ] A new or moved top-level path is listed on the right side of `.github/ci-tested-paths.txt` (does CI build, run or read it?), and the workflow trigger copies match.
- [ ] CHANGELOG updated for any user-visible change (feature, fix, breaking change, removal).
- [ ] End-user docs (under `docs/end-user/`) updated for any user-visible behavior change.
- [ ] README updated if the build process, project structure, or getting-started experience changed.
- [ ] PR description summarizes the change, the reasoning, and anything reviewers should pay extra attention to.

### Reviewer Checklist

When reviewing a PR, we look at:

- **Correctness.** Does the code do what the PR says it does? Are edge cases handled? Are there logic errors hiding behind happy-path tests?
- **Test rigor.** Do the tests actually exercise the behavior, or do they just call into it? Are failure modes tested, not just success modes? For database-touching code, do the tests run against real database platforms?
- **Coverage.** Did this PR raise or lower coverage on the touched code? Reductions need a stated reason. Watch specifically for changes to the coverage gate thresholds or the `coverage.runsettings` exclusions. The only legitimate exclusions are test assemblies and compiler-generated plumbing — anything that drops *product* code out of the denominator (a new assembly or namespace in `<Exclude>`, adding `GeneratedCodeAttribute` to `ExcludeByAttribute`, or `[ExcludeFromCodeCoverage]` on real code) games the gate exactly like lowering the threshold and must be flagged and investigated, not approved silently.
- **OS portability.** Will this code behave the same on Windows, Linux, and macOS? Are paths, line endings, and culture-dependent operations handled correctly?
- **Database Platform parity.** If a feature lands for one database platform, what's the story for the other two? Sometimes "we'll do it next" is the right answer with a tracked issue; sometimes it's a sign the design isn't ready.
- **Backward compatibility.** Does this change affect a public API surface, package format, or generated SQL shape? If so, is the change additive, deprecating, or breaking? Breaking changes need explicit CHANGELOG entries.
- **Style and consistency.** Does the code match the surrounding style? `.editorconfig`'s naming rules, brace style, indentation? No suppressed warnings without justification?
- **Comment hygiene.** Are comments load-bearing (explaining a non-obvious why) or noise (restating the code, referencing the PR/ticket, marking removed code)?
- **Security.** Does the change introduce SQL injection risk, command injection, or unintended secret exposure? Are connection strings, passwords, and tokens handled correctly?
- **Documentation.** If this is user-visible, does the CHANGELOG entry exist? Are end-user docs updated? Are any behavior contracts in the README still accurate?

### When We Push Back

If we ask for changes, the goal is the same standards documented here — we're not negotiating the bar PR by PR. If feedback feels off-base, push back with reasoning; we'd rather have the conversation than merge something neither side is confident in. If you're stuck on what a comment is asking for, ask — review feedback is sometimes terser than it should be.

## Definition of Done

A PR is ready to merge when:

1. All tests pass on CI across the supported platform matrix.
2. Coverage is maintained on touched code (or any reduction has a stated reason in the PR description).
3. CHANGELOG is updated for any user-visible change.
4. End-user docs are updated for any user-visible behavior change.
5. README is updated if the build process, project structure, or getting-started experience changed.
6. Self-review checklist (above) is complete.
7. Reviewer feedback has been addressed.

## How Contributors Are Recognized

Contributors are credited in three places:

- **CHANGELOG entries** — user-visible PRs (anything that generates a CHANGELOG entry) carry a `Thanks to @handle` line crediting all contributor handles that appear in the PR. Doc-only and non-released-tooling PRs that don't generate CHANGELOG entries are still credited via the README contributors section and the auto-generated GitHub contributors page.
- **First-PR welcome** — the first PR a new contributor opens carries an extra welcome line in its CHANGELOG entry (when one exists), or via the README contributors update otherwise.
- **README contributors section** — a running list of external contributors with links to their GitHub profiles, alongside [GitHub's auto-generated contributors page](https://github.com/Schema-Smith/SchemaSmith/graphs/contributors).

For substantial design contributions or beta-testing partnerships, we may credit the contributor by real name in the relevant release notes or feature callout — with explicit consent, separate from the routine handle-credit above.

Maintainer commits don't get per-entry CHANGELOG credit; maintainer authorship is established via project ownership and git history.

## Community & Communication

- Be respectful and constructive in issues, PRs, and discussions.
- Assume positive intent.
- We're all working to make SchemaSmith better.

See [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md) for the full conduct expectations.

## Reporting Security Issues

Please don't open public issues for security vulnerabilities. See [`SECURITY.md`](SECURITY.md) for the disclosure process.

## License

By contributing, you agree that your contributions will be licensed under the [SSCL v2.0](LICENSE), the same license as the project.
