# CI/CD Integration

SchemaSmith tools are self-contained executables. No SDK to install. No runtime to configure. No package manager plugins to maintain. Drop them into any pipeline and your database deployments become as automated as your application builds. One binary, a handful of environment variables, and your schema changes flow from pull request to production without anyone writing a deployment script.

This works identically whether you're deploying to **SQL Server**, **PostgreSQL**, **MySQL**, or **MariaDB** -- the `Platform` value on `Product.json` tells SchemaQuench which adapter to use, and your pipeline YAML stays the same.

## The build and deploy model

SchemaSmith separates schema management into two clean stages that map directly to how CI/CD pipelines already work.

**Build: package your schema into a versioned artifact.** Your schema package -- `Product.json`, templates, table definitions, scripts -- is already a directory structure in source control. Zip it, tag a release, publish it to an artifact store. The package is the artifact. No compilation step, no transformation, no intermediate format.

**Deploy: point SchemaQuench at the artifact and run it.** SchemaQuench reads everything it needs from the schema package and the environment. Set the target server, credentials, and package path via environment variables, then execute. One command. Done.

SchemaQuench reads directly from zip files -- no extraction step needed. Build the artifact once, store it in your artifact repository, and deploy the same artifact to dev, staging, and production. The only thing that changes between environments is the configuration injected through environment variables. Same artifact, every environment, every time.

## Configuration via environment variables

Every SchemaSmith setting can be injected through environment variables, making the tools pipeline-native from the start. The convention is straightforward: prefix with `SmithySettings_` and use double underscores (`__`) to represent nesting in the configuration hierarchy.

| Setting path | Environment variable |
|---|---|
| `Target:Server` | `SmithySettings_Target__Server` |
| `Target:User` | `SmithySettings_Target__User` |
| `Target:Password` | `SmithySettings_Target__Password` |
| `Target:SecondaryServers` | `SmithySettings_Target__SecondaryServers` |
| `SchemaPackagePath` | `SmithySettings_SchemaPackagePath` |
| `WhatIfONLY` | `SmithySettings_WhatIfONLY` |
| `ScriptTokens:ReportingDB` | `SmithySettings_ScriptTokens__ReportingDB` |

No secrets in config files. Every sensitive value -- server addresses, credentials, tokens that vary by environment -- injected at runtime by your pipeline's secret management system. The settings file in your repository holds only development defaults; production configuration lives where it belongs, in your CI/CD platform's secret store.

For the full mapping rules, precedence chain, and every available setting, see the [Configuration Reference](../reference/configuration.md#environment-variables).

## Pipeline examples

The examples below show complete, working pipelines for four major CI/CD platforms. Each one checks out the schema package, injects credentials from the platform's secret store, and runs SchemaQuench. That's the entire deployment.

### GitHub Actions

```yaml
name: Deploy Database Schema

on:
  push:
    branches: [main]
  workflow_dispatch:

jobs:
  deploy:
    runs-on: self-hosted
    steps:
      - name: Checkout schema package
        uses: actions/checkout@v4

      - name: Deploy Schema
        env:
          SmithySettings_SchemaPackagePath: ${{ github.workspace }}
          SmithySettings_Target__Server: ${{ secrets.DB_SERVER }}
          SmithySettings_Target__User: ${{ secrets.DB_USER }}
          SmithySettings_Target__Password: ${{ secrets.DB_PASSWORD }}
        run: schemaquench
```

This uses a self-hosted runner with SchemaQuench pre-installed. Credentials flow from GitHub Repository Secrets -- never stored in the workflow file, never printed in logs. The `workflow_dispatch` trigger lets you run deployments manually when needed. Nothing in this YAML is platform-specific -- the same workflow deploys a SQL Server, PostgreSQL, MySQL, or MariaDB package.

### First-party composite action: SchemaSmith Deploy

SchemaSmith ships a composite action, listed on the
[GitHub Marketplace](https://github.com/marketplace/actions/schemasmith-deploy) --
`uses: Schema-Smith/schemasmith-deploy@vX.Y.Z`
(replace with a release tag; pinning the tag pins both the action and the CLI
version it runs, or set the `version` input explicitly). It fetches the matching
self-contained binary for the runner OS at run time -- no runtime install, and no
self-hosted runner with SchemaQuench pre-installed required. Modes: `deploy`,
`whatif`, `validate`, `test-connection`, `preview-targets`; connection inputs
pass the password via env, never on the command line; outputs include
`exit-code`, `log-dir`, and `summary-path`.

### Jenkins

```groovy
pipeline {
    agent any

    environment {
        SmithySettings_SchemaPackagePath = '/opt/artifacts/product-definition.zip'
        SmithySettings_Target__Server    = credentials('db-server')
        SmithySettings_Target__User      = credentials('db-user')
        SmithySettings_Target__Password  = credentials('db-password')
    }

    stages {
        stage('Deploy Schema') {
            steps {
                sh 'schemaquench'
            }
        }
    }
}
```

Jenkins injects credentials through the Credentials Plugin. Notice the package path points to a zip file -- SchemaQuench reads directly from zip archives, so there's no extraction step. Build a zip artifact upstream, pass the path, and deploy.

### GitLab CI

```yaml
stages:
  - deploy

deploy-schema:
  stage: deploy
  tags:
    - schemasmith
  variables:
    SmithySettings_SchemaPackagePath: $CI_PROJECT_DIR
    SmithySettings_Target__Server: $DB_SERVER
    SmithySettings_Target__User: $DB_USER
    SmithySettings_Target__Password: $DB_PASSWORD
  script:
    - schemaquench
  only:
    - main
  environment:
    name: production
```

Credentials are stored as CI/CD Variables in the project settings with the masked flag enabled -- GitLab redacts them from job logs automatically. The `tags` field routes the job to a runner where SchemaQuench is installed.

### Azure DevOps

```yaml
trigger:
  - main

pool:
  name: 'SchemaSmith'

steps:
  - checkout: self

  - script: schemaquench
    displayName: 'Deploy Schema'
    env:
      SmithySettings_SchemaPackagePath: $(Build.SourcesDirectory)
      SmithySettings_Target__Server: $(DB_SERVER)
      SmithySettings_Target__User: $(DB_USER)
      SmithySettings_Target__Password: $(DB_PASSWORD)
```

Credentials are stored in Variable Groups or Azure Key Vault and linked to the pipeline. The named agent pool `SchemaSmith` ensures the job runs on an agent with SchemaQuench installed.

### Runner and agent requirements

All four examples assume SchemaQuench is pre-installed on the runner or agent. SchemaSmith tools are self-contained executables with no dependencies -- copy the binary to the runner, add it to the PATH, and every pipeline on that runner can use it. No package restore, no SDK installation, no version management in the pipeline itself.

The same pattern works for any CLI-capable CI system — TeamCity, CircleCI, Bamboo, Buildkite, Concourse, Octopus Deploy, Harness, and others. Install SchemaQuench on the agent, set the credential environment variables from your CI's secret store, and invoke `schemaquench` from a script step. Nothing in the integration is platform-specific to the four examples above.

## Pre-flight readiness checks

Before a deployment run opens a single connection in anger, you can confirm the package and the environment are actually ready for it. A family of read-only switches run targeted diagnostics and exit without deploying anything -- so a pipeline can fail fast on a broken package, a bad connection string, an unpropagated firewall rule, a below-floor server, or a target roster that resolved to the wrong set, long before the deploy window opens.

**`--Validate`** is the only member of the family that needs no connection and no target at all -- it loads the schema package and statically lints it: malformed load, accidental duplicates, dangling foreign keys, undefined tokens, and JSON-schema violations, all caught from the files on disk. Because it needs nothing but the package itself, it's cheap enough to run on every pull request. It exits `0` clean (or warnings-only), `2` on any error. See [`--Validate` Reference](../reference/validate.md) for every check it runs.

**`--TestConnection`** opens a connection to every configured server (primary plus any secondary servers), runs a liveness query, and validates that each server meets the product's declared `MinimumVersion` floor. Nothing is read, generated, or deployed. It exits `0` when every server connects and clears the floor, `2` on any connection failure or version violation.

**`--PreviewTargets`** does everything `--TestConnection` does, then prints a read-only per-template report of the databases and schemas the deployment would target -- the exact set of work units a full quench would process, without processing any of them. A template marked `RequireAtLeastOneTarget` that resolves nothing fails the preview, so a misconfigured environment is caught here rather than at run time. Same exit codes: `0` on pass, `2` on any connection failure, version violation, or required-template match miss.

Because every switch in the family returns `0` for go and `2` for stop, they drop straight into a pipeline as a layered readiness gate ahead of the deploy step.

**Readiness gate (PowerShell / bash):**

```powershell
# Abort the deploy if any pre-flight check fails
foreach ($gate in '--Validate', '--TestConnection', '--PreviewTargets') {
    schemaquench $gate
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Pre-flight $gate failed - aborting deploy"
        exit 1
    }
}

# Only reached when every gate passes:
schemaquench
```

```bash
# Abort the deploy if any pre-flight check fails
schemaquench --Validate         || { echo "Package validation failed — aborting deploy"; exit 1; }
schemaquench --TestConnection   || { echo "Pre-flight failed — aborting deploy"; exit 1; }
schemaquench --PreviewTargets   || { echo "Target preview failed — aborting deploy"; exit 1; }

# Only reached when every gate passes:
schemaquench
```

> **Why `$LASTEXITCODE` and not `$?`.** `schemaquench` is a native executable, not a PowerShell cmdlet, so PowerShell reports its result through `$LASTEXITCODE` rather than through the terminating-error pipeline. Checking `$LASTEXITCODE` explicitly after each call is what makes the gate actually gate — a bare `schemaquench --Validate` on its own line will not stop the script on exit `2`.

This complements the WhatIf-in-PR pattern below: `--Validate` catches structural problems in the package itself with no database at all, WhatIf validates *the change* against a disposable database during review, and `--TestConnection`/`--PreviewTargets` validate *the live target environment* immediately before a real deployment. For the full behavior of the connection-based switches -- secondary-server handling, the version-floor rules, and the target-report format -- see [SchemaQuench -- Pre-flight diagnostics](../reference/schemaquench.md#pre-flight-diagnostics).

## The WhatIf-in-PR pattern

This is the most powerful CI pattern you can build with SchemaSmith. It catches deployment failures before code reaches your main branch -- not after.

**The idea:** run SchemaQuench in WhatIf mode on every pull request that touches schema files. WhatIf performs the full deployment logic -- validation scripts, token replacement, DDL generation, dependency resolution -- without executing any changes. If WhatIf succeeds, the PR is deployable. If it fails, the author knows exactly what broke before anyone reviews the code.

### PR pipeline: validate the change

The PR pipeline spins up a disposable database container matching the target platform, deploys the base branch schema to establish the current state, then runs WhatIf against the PR branch:

```yaml
# GitHub Actions — WhatIf validation on PRs (SQL Server example)
name: Validate Schema Change

on:
  pull_request:
    paths:
      - 'Schema/**'

jobs:
  whatif:
    runs-on: self-hosted
    services:
      sqlserver:
        image: mcr.microsoft.com/mssql/server:2022-latest
        env:
          ACCEPT_EULA: Y
          SA_PASSWORD: YourStr0ngPassword!
        ports:
          - 1433:1433

    steps:
      - name: Checkout PR branch
        uses: actions/checkout@v4

      - name: Deploy base schema
        env:
          SmithySettings_SchemaPackagePath: ${{ github.workspace }}
          SmithySettings_Target__Server: localhost
          SmithySettings_Target__User: sa
          SmithySettings_Target__Password: YourStr0ngPassword!
        run: |
          git checkout ${{ github.event.pull_request.base.sha }}
          schemaquench

      - name: WhatIf PR changes
        env:
          SmithySettings_SchemaPackagePath: ${{ github.workspace }}
          SmithySettings_Target__Server: localhost
          SmithySettings_Target__User: sa
          SmithySettings_Target__Password: YourStr0ngPassword!
          SmithySettings_WhatIfONLY: "true"
        run: |
          git checkout ${{ github.sha }}
          schemaquench
```

The exact same shape works for PostgreSQL (swap the service image to `postgres:16` and use `Host=localhost;...` credentials), MySQL (swap to `mysql:8.4`), and MariaDB (swap to `mariadb:11.4`). The SchemaQuench invocation is identical -- the platform adapter comes from the package.

If WhatIf fails, the PR check fails. The author sees exactly which SQL statement would have broken, which token was missing, which dependency couldn't be resolved. Fix it in the PR, not in production.

### Merge pipeline: deploy through environments

Once the PR merges, the deployment pipeline takes over. A typical flow:

1. **Deploy to staging** -- full SchemaQuench run against the staging database
2. **Run integration tests** -- your application's test suite validates the schema change
3. **Approval gate** -- manual approval before production (most CI platforms support this natively)
4. **Deploy to production** -- same artifact, same SchemaQuench command, different target via environment variables

The combination catches problems at every stage. WhatIf catches SQL errors, missing tokens, and broken references in the PR. Staging deployment catches environment-specific issues. Integration tests catch application-level regressions. The approval gate gives humans the final word.

## Operational Profiles

SchemaQuench has one config surface — a handful of top-level boolean settings — but your pipeline has more than one job. A full release pipeline, a patch-only datafix pipeline, a PR validation check, and an idempotency gate each call for a different posture. These six settings control which posture you're in, and all six work identically on SQL Server, PostgreSQL, MySQL, and MariaDB.

### Full release pipeline

The standard deployment profile. Structural changes land, helper procedures stay current, and tables removed from the product are caught early.

```json
{
  "KindleTheForge": true,
  "UpdateTables": true,
  "WhatIfONLY": false,
  "DropTablesRemovedFromProduct": true
}
```

`DropTablesRemovedFromProduct` is environment-dependent. Set `true` in CI and staging to catch removals early. In production many teams set it `false` for rollback safety — the next release can always clean it up once the window passes. See [DropTablesRemovedFromProduct](../reference/schemaquench.md#droptablesremovedfromproduct) and the rollback guidance in [Chapter 08](08-rollback-and-recovery.md) for the full reasoning.

### Datafix patch pipeline

Migration scripts only. No DDL, no table quenching, no tracking inserts for run-once scripts — *SchemaSmith itself* performs no structural changes under this profile. Your migration scripts, though, often still need targeted rights: a fix that backs up the rows it changes needs `CREATE TABLE`. You can grant that without any power over your product tables by giving the deploy account its own schema to create backups in. See the [datafix-role grants reference](../reference/datafix-role-grants.md) for least-privilege grant sets on SQL Server, PostgreSQL, MySQL, and MariaDB.

```json
{
  "KindleTheForge": false,
  "UpdateTables": false,
  "DropTablesRemovedFromProduct": false,
  "TrackRunOnceMigrations": false
}
```

Use this when shipping a data patch, hotfix, or bulk data load between structural releases. For the full per-flag reasoning, the comparison table, and patterns that pair well, see [Partial-Package Deployments (Data Fixes)](../reference/schemaquench.md#partial-package-deployments-data-fixes).

### WhatIf PR check

WhatIf runs the full deployment logic — validation, token replacement, DDL generation, dependency resolution — without touching a real database. Wire it up as a PR gate against a disposable database container and catch SQL errors, missing tokens, and broken references before code reaches your main branch.

```json
{
  "WhatIfONLY": true
}
```

The [WhatIf-in-PR pattern](#the-whatif-in-pr-pattern) above shows a complete pipeline for this. Once you trust the package and the pipeline, direct deploys are the normal mode — WhatIf earns its keep during complex migrations and when you're working with an unfamiliar target, not as a standing gate on every production run.

### Idempotency CI check

Runs every object script and `[ALWAYS]` script twice in sequence and requires both passes to succeed. A strong guarantee that your scripts are truly stateless and can be re-applied after a partial failure.

```json
{
  "WhatIfONLY": false,
  "RunScriptsTwice": true,
  "DropTablesRemovedFromProduct": true
}
```

Run this against a disposable database in CI — it is not meant for production targets. `RunScriptsTwice` is an idempotency check, not a dependency-resolution mechanism: the retry loop already handles inter-object dependencies. Run-once migration scripts are excluded from the double-run.

## Secret management

Every CI/CD platform has a built-in secret store. SchemaSmith's environment variable model was designed to work with all of them -- every sensitive setting injected at runtime, nothing stored in files committed to source control.

| Platform | Secret storage | How it works |
|---|---|---|
| GitHub Actions | Repository Secrets | Referenced as `${{ secrets.NAME }}` in workflow env blocks |
| Jenkins | Credentials Plugin | Bound to environment variables via `credentials('id')` |
| GitLab CI | CI/CD Variables (masked) | Referenced as `$NAME` in job variables, masked in logs |
| Azure DevOps | Variable Groups / Key Vault | Referenced as `$(NAME)` in pipeline env blocks |

The pattern is the same regardless of platform: store the credential in the platform's secret store, reference it in the pipeline definition, and SchemaSmith picks it up as an environment variable. No custom integration, no plugins, no secret management SDKs. The tools read environment variables -- your platform manages the secrets.

## Best practices

**Test in dev first.** Deploy to a development environment restored from a production backup before promoting anywhere. This catches edge cases that only appear with real data volumes and real object counts.

**Separate config per environment with env vars.** The same schema package deploys everywhere. Environment variables differentiate targets -- server, credentials, script tokens. No environment-specific config files to maintain, no risk of deploying the wrong config to the wrong server.

**Validate before deploying.** Wire up JSON Schema validation on every PR to catch structural problems without a database. The SchemaSmith repository ships a complete, working example at `.github/workflows/validate-demo-schemas.yml` -- a per-PR, no-database validation workflow covering SQL Server, PostgreSQL, MySQL, and MariaDB using a content-type matrix. Copy it and adapt the paths for your own packages. See [Testing and Validation](06-testing-and-validation.md#schema-validation-in-ci) for the full pattern.

**WhatIf for tricky changes.** Reach for WhatIf when you're validating a complex migration, deploying an unfamiliar package, or working with an unfamiliar target. It costs minutes in CI and surfaces exactly which SQL statement or token would fail before it hits a real database. Once you trust the package and the pipeline, direct deploys are the normal mode.

**Build once, deploy the same artifact.** Zip your schema package, version it, store it. Deploy that same zip to dev, staging, and production. If you rebuild per environment, you're not testing what you're deploying.

**Keep SchemaQuench on the runner, not in the pipeline.** Pre-install the binary on your self-hosted runners or agents. This keeps pipeline definitions clean and avoids downloading tools on every run.

---

Your pipeline's set -- schema changes deploy automatically, validated at every stage. One artifact. Every environment. No manual steps. But what happens when you need to go backwards? The next chapter covers rollback and recovery. [Rollback and Recovery](08-rollback-and-recovery.md)
