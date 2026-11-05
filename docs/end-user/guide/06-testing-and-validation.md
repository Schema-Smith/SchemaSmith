# Testing and Validation

The best time to find a deployment problem is before it reaches production. A schema change that passes review but breaks on deploy wastes time, burns trust, and makes the next deployment scarier. SchemaSmith gives you multiple layers of defense -- from local Docker testing to CI validation pipelines -- so you catch issues early and deploy with confidence. This is the Strengthen pillar in action: fortifying your process so problems never reach production.

## Local testing with Docker

The demo included with SchemaSmith ships Docker Compose files that stand up complete environments from nothing -- database servers, databases created from scratch, and full schema deployments -- all in one command. This isn't just a convenience for trying the demo. It's a pattern you can adopt for your own projects, on any supported platform.

```bash
cd Demos/SqlServer && ./run-demo.sh
```

That single command does everything: publishes SchemaQuench from source (the launcher needs the .NET 10 SDK), starts a database server (SQL Server, PostgreSQL, MySQL, or MariaDB depending on which `Demos/` folder you launched), waits for it to be healthy, then deploys the matching schema package. When it finishes, you have a fully quenched database running locally. Swap `Demos/SqlServer` for `Demos/PostgreSQL`, `Demos/MySQL`, or `Demos/MariaDB` to target a different engine.

Here's how the compose pattern is structured:

**db server** starts a database container with a health check that polls readiness. Nothing else starts until the server reports healthy. Environment variables configure the credentials, and the port maps to a non-default value on the host so it doesn't collide with any existing local database.

**quench** builds SchemaQuench (or uses a prebuilt image), mounts the schema package as a volume at `/metadata`, and deploys it to the database server. All configuration flows through environment variables -- target server, credentials, package path, and any tokens the package expects. It depends on the database container with a `service_healthy` condition, so it waits for the server to be ready before attempting deployment.

**completed** is a lightweight marker container that runs only if every earlier step succeeds. If any step fails, `completed` never runs and the exit code tells you something broke.

The key patterns to carry into your own projects:

- **Environment variables configure everything.** Server, credentials, tokens, and package paths all flow through env vars, making the same compose file work across environments.
- **Health checks enforce readiness.** The server health check prevents SchemaQuench from connecting before the database is ready to accept connections.
- **Volume-mounted packages.** The schema package is mounted into the container, not baked into the image. Change files on disk, run compose again, see the results.
- **Sequential dependencies.** `depends_on` with `condition: service_healthy` and `condition: service_completed_successfully` guarantees deployment order.
- **One compose file per platform, or one per product.** Your team can test SQL Server, PostgreSQL, MySQL, and MariaDB deployments independently or side-by-side depending on what your real environments look like.

The testing workflow becomes a tight loop: make changes to your schema files, run `docker compose up`, verify success, and tear down with `docker compose down -v` to reset completely. Every run starts from zero, which means you're testing the full deployment path -- not just incremental changes against a database that might have drifted.

## Schema validation in CI

Before a schema package ever reaches a database, SchemaQuench can tell you whether it is sound. `--Validate` loads the package from disk and lints it -- no connection, no target, no credentials -- so it runs on every pull request in seconds:

```bash
schemaquench --Validate --SchemaPackagePath=./my-product
```

It exits `0` when the package is clean or carries only warnings, and `2` on any error (with `ExitNonZeroOnWarning` on, a warnings-only run exits `1` instead, for teams that want warnings to fail the check too). What it catches, in one pass:

- **Package load** -- `Product.json`, every `Template.json` and every table file parse and bind, and the product declares its `Platform`; a file that would crash the deploy mid-run is reported as a finding instead.
- **Dangling references** -- two files declaring the same object, a foreign key or index naming a column that does not exist, a foreign key pointing at a table the package does not declare.
- **Tokens** -- a `{{Token}}` used in a script that no `ScriptTokens` entry defines.
- **Schema conformance** -- every file validated against the `.json-schemas/` the package carries, and the schemas themselves checked against the CLI that generated them, so an edited package cannot drift past what the editor was told is valid.

The CI step is the same shell command on any platform -- a script step in Azure DevOps, GitHub Actions, GitLab CI, Jenkins, TeamCity, or anything else that runs a command and reads an exit code:

```bash
schemaquench --Validate --SchemaPackagePath=./my-product || { echo "Package validation failed"; exit 1; }
```

The `.json-schemas/` folder in each package is generated from the live engine types for the exact platform the package targets (each file carries a `.sqlserver.`, `.postgresql.`, `.mysql.`, or `.mariadb.` infix), and every package file carries a `$schema` reference to its own. That is what gives you autocomplete and inline errors in the editor with nothing to configure -- and `--Validate` is the same check, run against the whole package at once. For every rule, its code, and how to read the output, see the [`--Validate` reference](../reference/validate.md).

## Custom Extensions validation

If your team has authored a custom JSON Schema fragment for the `Extensions` bag on tables (for example, to require a `DataClassification` field on every table or to constrain `OwningTeam` to a known list), that fragment rides inside the same `.schema` files and `--Validate` checks it with everything else -- no extra step. The schema generation process preserves your custom Extensions fragment through regeneration, so your team's governance rules are enforced automatically on every pull request.

See [Custom Properties -- JSON Schema Validation](../reference/custom-properties.md#json-schema-validation) for the fragment format, a real demo that carries table-level and column-level Extensions governance, and step-by-step instructions for constraining the `Extensions` shape in your own `.schema` files.

## WhatIf as a validation gate

Schema validation catches structural problems in your JSON files. But valid JSON can still produce invalid SQL. A column referencing a type that doesn't exist, a foreign key pointing to a table that was renamed, a token that was never defined -- these pass schema validation but fail at deployment time. WhatIf mode catches them.

WhatIf runs the full SchemaQuench deployment logic -- validation scripts, token replacement, DDL generation, dependency resolution -- without executing any changes against the database. It's a complete dry run.

```bash
SchemaQuench --WhatIf
```

In WhatIf mode, SchemaQuench:

- **Executes validation scripts normally.** Server validation and baseline validation still run, because they're read-only checks that need real answers.
- **Generates table quench SQL without applying it.** The SQL that would create, alter, or drop tables is generated and logged but never executed.
- **Reports migration script status.** For each migration script, WhatIf reports whether it would be applied or skipped (because it was already tracked in a previous deployment).

The output tells you exactly what SchemaQuench would do -- every table change, every script execution, every migration -- without touching a single row.

The pattern for PR pipelines: spin up a disposable database container, deploy the base branch schema to establish the current state, then run SchemaQuench in WhatIf mode against the PR branch. If WhatIf succeeds, the PR is deployable. If it fails, the PR check fails and the author knows exactly what broke. This catches real SQL execution issues, not just JSON structure problems.

For the full details on WhatIf output, debug SQL files, and configuration options, see the [SchemaQuench Reference -- WhatIf Mode](../reference/schemaquench.md#whatif-mode).

## Validation scripts as deployment gates

The final layer runs at deployment time itself. The `ValidationScript` property in `Product.json` executes before SchemaQuench deploys anything -- am I on the right server? -- and `BaselineValidationScript` asks the second question: is this environment at the state this package expects? If either returns false, deployment stops before anything is touched. Nothing changed. Nothing broken. Exactly how a safety gate should work.

The scripts, the version-handshake pattern that pairs `BaselineValidationScript` with `VersionStampScript`, and the per-engine forms are all in [Power Workflows -- Validation gates](09-power-workflows.md#validation-gates); this chapter only places them in the layering.

### Three layers, three classes of problems

Each validation layer catches problems the others can't:

| Layer | What it catches | When it runs | Database required? |
|-------|----------------|--------------|-------------------|
| **`--Validate`** | Malformed JSON, missing properties, duplicates, dangling references, undefined tokens, JSON-schema and Extensions contract violations | PR time | No |
| **WhatIf mode** | Invalid SQL, missing tokens, dependency failures | PR or pre-deploy | Yes (disposable) |
| **Validation scripts** | Wrong server, wrong state, wrong version | Deploy time | Yes (target) |

`--Validate` is fast and cheap -- run it on every PR. WhatIf is thorough but needs a database -- run it on PRs that touch schema files. Validation scripts are your last line of defense -- they run on every deployment, every time, automatically. Three layers. Three stages. Problems caught early, kept far from production.

---

Testing and validation give you confidence that your schema changes will deploy correctly. The next chapter shows how to wire these checks into your CI/CD pipeline so they run automatically on every change. [CI/CD Integration](07-cicd-integration.md)
