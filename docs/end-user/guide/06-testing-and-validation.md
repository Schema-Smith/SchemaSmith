# Testing and Validation

The best time to find a deployment problem is before it reaches production. A schema change that passes review but breaks on deploy wastes time, burns trust, and makes the next deployment scarier. SchemaSmith gives you multiple layers of defense -- from local Docker testing to CI validation pipelines -- so you catch issues early and deploy with confidence. This is the Strengthen pillar in action: fortifying your process so problems never reach production.

## Local testing with Docker

The demo included with SchemaSmith ships Docker Compose files that stand up complete environments from nothing -- database servers, databases created from scratch, and full schema deployments -- all in one command. This isn't just a convenience for trying the demo. It's a pattern you can adopt for your own projects, on any supported platform.

```bash
cd Demos/SqlServer && ./run-demo.sh
```

That single command does everything: starts a database server (SQL Server, PostgreSQL, MySQL, or MariaDB depending on which `Demos/` folder you launched), waits for it to be healthy, then deploys the matching schema package. When it finishes, you have a fully quenched database running locally. Swap `Demos/SqlServer` for `Demos/PostgreSQL`, `Demos/MySQL`, or `Demos/MariaDB` to target a different engine.

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

Before a schema package ever reaches a database, you can validate that every JSON file is structurally correct. You can add a CI job that validates schema files on every pull request -- catching malformed JSON, missing required properties, and structural errors without spinning up a database at all.

Each schema package includes JSON Schema files in a `.json-schemas/` directory, generated **on the fly** from the live C# domain types. The schemas always match the current engine, for the exact platform the package targets -- so each file carries a platform infix (`.sqlserver.`, `.postgresql.`, `.mysql.`, or `.mariadb.`). The examples below use a SQL Server package; swap the infix for your platform. A typical CI step validates three categories:

```yaml
- name: validate-product
  uses: GrantBirki/json-yaml-validate@v3.3.0
  with:
    json_schema: "./my-product/.json-schemas/products.sqlserver.schema"
    files: "./my-product/Product.json"

- name: validate-templates
  uses: GrantBirki/json-yaml-validate@v3.3.0
  with:
    json_schema: "./my-product/.json-schemas/templates.sqlserver.schema"
    files: "./my-product/Templates/Main/Template.json"

- name: validate-tables
  uses: GrantBirki/json-yaml-validate@v3.3.0
  with:
    json_schema: "./my-product/.json-schemas/tables.sqlserver.schema"
    base_dir: "./my-product/Templates/Main/Tables"
```

What each validator catches:

- **Product definition** -- `Product.json` validated against `products.<platform>.schema`. Catches missing product names, invalid token structures, malformed validation scripts, unknown platform values.
- **Template definitions** -- Each `Template.json` validated against `templates.<platform>.schema`. Catches invalid template order entries, broken database identification scripts, malformed settings, bad custom folder declarations.
- **Table definitions** -- Every table JSON file validated against `tables.<platform>.schema`. Catches invalid column types, malformed index definitions, structural errors in any table. On PostgreSQL packages, there's also `materializedviews.postgresql.schema`; on SQL Server packages, `indexedviews.sqlserver.schema`.

This is the first line of defense. No database, no deployment, no credentials required -- just structural validation that runs in seconds. A typo in a column definition or a missing required field gets caught here, long before it could cause a deployment failure.

**A working example you can copy from.** The SchemaSmith repository ships a complete, runnable workflow at `.github/workflows/validate-demo-schemas.yml` that does exactly this across all four engines -- SQL Server, PostgreSQL, MySQL, and MariaDB -- on every pull request that touches a demo JSON file. No database containers, no credentials, no setup; it passes or fails in seconds.

**It runs as one job, and that is a deliberate correction worth copying.** It began as a matrix with one job per schema/file pair -- 105 of them. Each did about 9 seconds of work, but GitHub bills a job a full minute, so the run cost roughly 105 billed minutes to do about 16 minutes of work: more than SchemaSmith's entire multi-engine integration matrix. The pairs were never the expensive part; the per-job overhead was. It is now a single job that installs `ajv` and runs `scripts/validate-demo-schemas.mjs`, which walks every pair in one process. Coverage is identical. The pair list lives in `scripts/validate-demo-schemas.json` beside it, so adding a package is a data edit rather than a workflow edit.

**Your packages carry their own `$schema` reference**, so an editor validates them with no configuration and most third-party validators can find the schema themselves. The explicit pairing above is still what a CI step wants: it validates the files you name against the schema you name, rather than trusting a path inside the file it is checking.

## Custom Extensions validation

If your team has authored a custom JSON Schema fragment for the `Extensions` bag on tables (for example, to require a `DataClassification` field on every table or to constrain `OwningTeam` to a known list), that fragment rides inside the same `.schema` files and gets validated in the same CI step -- with zero workflow changes. The CI workflow picks it up automatically because it validates the whole `.schema` file, including whatever `Extensions` fragment your team has added. The schema generation process preserves your custom Extensions fragment through regeneration, so your team's governance rules are enforced automatically on every pull request.

See [Custom Properties -- JSON Schema Validation](../reference/custom-properties.md#json-schema-validation) for the fragment format, a real demo that carries table-level and column-level Extensions governance, and step-by-step instructions for constraining the `Extensions` shape in your own `.schema` files.

## WhatIf as a validation gate

Schema validation catches structural problems in your JSON files. But valid JSON can still produce invalid SQL. A column referencing a type that doesn't exist, a foreign key pointing to a table that was renamed, a token that was never defined -- these pass schema validation but fail at deployment time. WhatIf mode catches them.

WhatIf runs the full SchemaQuench deployment logic -- validation scripts, token replacement, DDL generation, dependency resolution -- without executing any changes against the database. It's a complete dry run.

```bash
SmithySettings_WhatIfONLY=true SchemaQuench
```

In WhatIf mode, SchemaQuench:

- **Executes validation scripts normally.** Server validation and baseline validation still run, because they're read-only checks that need real answers.
- **Generates table quench SQL without applying it.** The SQL that would create, alter, or drop tables is generated and logged but never executed.
- **Reports migration script status.** For each migration script, WhatIf reports whether it would be applied or skipped (because it was already tracked in a previous deployment).

The output tells you exactly what SchemaQuench would do -- every table change, every script execution, every migration -- without touching a single row.

The pattern for PR pipelines: spin up a disposable database container, deploy the base branch schema to establish the current state, then run SchemaQuench in WhatIf mode against the PR branch. If WhatIf succeeds, the PR is deployable. If it fails, the PR check fails and the author knows exactly what broke. This catches real SQL execution issues, not just JSON structure problems.

For the full details on WhatIf output, debug SQL files, and configuration options, see the [SchemaQuench Reference -- WhatIf Mode](../reference/schemaquench.md#whatif-mode).

## Validation scripts as deployment gates

The final layer runs at deployment time itself. The `ValidationScript` property in `Product.json` executes before SchemaQuench deploys anything. It's your safety gate: if the script returns 0 or false, deployment stops. This prevents accidentally quenching to the wrong server or an unprepared environment.

**SQL Server -- verify the target database exists:**

```sql
SELECT CAST(CASE WHEN EXISTS(
  SELECT 1 FROM master.sys.databases WHERE [name] = '{{MainDB}}'
) THEN 1 ELSE 0 END AS BIT)
```

**PostgreSQL -- verify the target database exists:**

```sql
SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname = '{{MainDB}}')
```

**MySQL -- verify the target schema exists:**

```sql
SELECT EXISTS(SELECT 1 FROM information_schema.schemata WHERE schema_name = '{{MainDB}}')
```

**Verify a minimum server version (SQL Server):**

```sql
SELECT CAST(CASE WHEN SERVERPROPERTY('ProductMajorVersion') >= 15
    THEN 1 ELSE 0 END AS BIT)
```

**Confirm expected state before a migration (any platform):**

```sql
SELECT CASE WHEN EXISTS(
  SELECT 1 FROM information_schema.tables
    WHERE table_name = 'AppConfig' AND table_schema = '{{AppSchema}}'
) THEN 1 ELSE 0 END
```

Validation scripts support token replacement, so you can use `{{ProductName}}`, `{{MainDB}}`, or any custom token you have defined -- including the advanced `<*Query*>` tag for values you want fetched from the live server. If validation fails, SchemaQuench logs the failure and exits without modifying anything. Nothing touched. Nothing broken. Exactly how a safety gate should work.

Products also support `BaselineValidationScript`, which runs only during initial baseline deployments to verify the target is in the expected starting state.

### Three layers, three classes of problems

Each validation layer catches problems the others can't:

| Layer | What it catches | When it runs | Database required? |
|-------|----------------|--------------|-------------------|
| **JSON Schema validation** | Malformed JSON, missing properties, structural errors, Extensions contract violations | PR time | No |
| **WhatIf mode** | Invalid SQL, missing tokens, dependency failures | PR or pre-deploy | Yes (disposable) |
| **Validation scripts** | Wrong server, wrong state, wrong version | Deploy time | Yes (target) |

Schema validation is fast and cheap -- run it on every PR. WhatIf is thorough but needs a database -- run it on PRs that touch schema files. Validation scripts are your last line of defense -- they run on every deployment, every time, automatically. Three layers. Three stages. Problems caught early, kept far from production.

---

Testing and validation give you confidence that your schema changes will deploy correctly. The next chapter shows how to wire these checks into your CI/CD pipeline so they run automatically on every change. [CI/CD Integration](07-cicd-integration.md)
