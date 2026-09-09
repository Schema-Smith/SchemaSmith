# --Validate — Static Schema Package Linting

What if you could catch a broken schema package before it ever touched a database -- no connection string, no target server, no waiting for a deployment window? `--Validate` is SchemaQuench's static linter: it loads your schema package through the same domain model the real quench uses, runs a battery of structural checks against it, and tells you exactly what's wrong -- in seconds, from a laptop or a CI runner with no database anywhere in sight.

It's the fastest, cheapest gate in the whole pre-flight family, and the only one that needs nothing but the files on disk.

---

## Invocation

`--Validate` is a switch on the same `SchemaQuench` binary you already have installed -- there's nothing extra to download and nothing extra to configure beyond pointing it at a package:

```bash
SchemaQuench --Validate
```

Run it from the directory containing `SchemaQuench.settings.json`, or point `SchemaPackagePath` (config file, `SmithySettings_SchemaPackagePath` environment variable, or `--SchemaPackagePath` switch) at the package you want to check:

```bash
SchemaQuench --Validate --SchemaPackagePath=./MyProduct
```

No `Target`, no `--ConnectionString`, no credentials of any kind -- `--Validate` never opens a connection. It reads `Product.json` to determine the declared `Platform` (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`), loads the package through that platform's domain types, runs every check, prints the findings, and exits.

---

## The pre-flight gate family

SchemaQuench gives you four read-only gates that run before (or instead of) a real deployment, each validating a different layer:

| Gate | Needs a connection? | Needs a target? | What it checks |
|------|:---:|:---:|---|
| `--Validate` | No | No | Package structure -- static analysis of the files on disk |
| `--TestConnection` | Yes | No | Server reachability + `MinimumVersion` |
| `--PreviewTargets` | Yes | Yes | The database/schema roster a real quench would touch |
| `WhatIfONLY` | Yes | Yes | The actual SQL a real quench would generate |

`--Validate` is the only target-less, connection-less member of the family -- run it on every commit, from any machine, against any package, with nothing installed but SchemaQuench itself. The other three need a live server to answer their question; `--Validate` answers its question from the files alone. For the full behavior of the connection-based gates, see [SchemaQuench -- Pre-Flight Diagnostics](schemaquench.md#pre-flight-diagnostics); for a full dry-run of the deployment itself, see [WhatIf Mode](schemaquench.md#whatif-mode).

---

## What it checks

`--Validate` loads the package once, then runs every registered check against the loaded model. A load failure that takes down the whole package (`Product.json` itself is missing/unparseable) short-circuits everything else -- there's no point running structural checks against a package that couldn't be parsed at all. A narrower load failure -- one component file, or one whole template -- excludes just that piece and lets every other check still run against the rest of the package.

### Package load

A schema package has to load before it can be checked at all -- malformed JSON, a missing required file, anything that would otherwise crash `Product.Load()` mid-deployment. `--Validate` catches the failure and reports it as a clean finding instead of an unhandled exception:

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-LOAD-001` | Error | Either the whole package failed to load (`Product.json` itself is missing/unparseable, or the configured path doesn't exist -- every other check is skipped when this happens), or one component file (a table/materialized-view/indexed-view JSON) inside an otherwise-loadable template was unparseable and got excluded -- the rest of the package still loads and every other check still runs. |
| `SS-LOAD-002` | Error | A whole template's own `Template.json` couldn't be loaded (unparseable JSON, or a self-contradictory template configuration such as `CreateSchemaIfMissing` without a `SchemaIdentificationScript`). That template is excluded and every check for it is skipped, but every OTHER template in the package still loads and still gets full check coverage. |

When `SS-LOAD-001` means the whole package failed to load, it's the only finding you'll see on that run. When it (or `SS-LOAD-002`) means one component/template was excluded from an otherwise-loadable package, every other check still runs and you may see it alongside other findings.

### Duplication

Two entries sharing the same name at the same level -- two columns both called `Status`, two indexes both called `IX_Orders_CustomerId` -- is almost always a copy-paste accident, not intent. But SchemaSmith also supports **conditional variants**: multiple same-named entries gated by different `ShouldApplyExpression` predicates, each one applying to a different target. `--Validate` tells these two cases apart:

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-DUP-001` | Error | Same-name entries exist and at least one isn't gated by `ShouldApplyExpression` -- an accidental duplicate. |
| `SS-DUP-VAR-002` | Warning | Every entry in the group IS gated (a legitimate variant set), but not every entry declares `VariantName` -- label them for clarity. |

The check runs at every level a name collision could hide: columns, indexes, foreign keys, check constraints, tables (within a template), the product's `TemplateOrder`, and the platform-specific collections (SQL Server XML indexes and statistics; PostgreSQL statistics and exclude constraints; MySQL full-text indexes).

> **Note:** `--Validate` can only confirm that every member of a same-name group is gated -- it can't prove the gates are mutually *exclusive* on any given target. That requires evaluating the predicates against a live database, which is exactly what a real quench does at deploy time. A gated variant set that passes `--Validate` can still fail at deployment if two variants' expressions both resolve true against the same target.

### Cross-object coherence

The JSON schema can enforce shape, but it can't confirm that a foreign key actually points at something real. `CoherenceCheck` walks every foreign key and index in the package and confirms the columns it references exist:

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-FK-001` | Error | A foreign key's `Columns` entry names a column that doesn't exist on the local table. |
| `SS-FK-002` | Error | A foreign key's `RelatedTable` doesn't resolve to any known table in the package. |
| `SS-FK-004` | Error | A foreign key's `RelatedColumns` entry names a column that doesn't exist on the related table. |
| `SS-FK-005` | Error | `Columns` and `RelatedColumns` have different entry counts -- the column lists must be the same length. |
| `SS-IDX-001` | Error | An index's `IndexColumns` entry names a column that doesn't exist on the table. |
| `SS-COL-001` | Warning | A column sets `BackfillExistingRows` but has no `Default`, so there is no value to apply to rows that already exist and the setting does nothing. |
| `SS-TBL-001` | Error | A table's `RebuildPolicy` uses `Mode: "THRESHOLD"` without a `Threshold` of 1 or more, so the policy cannot be evaluated. |
| `SS-RLS-001` | Warning | PostgreSQL — a table sets `RowLevelSecurity` but declares no `Policies`. PostgreSQL returns no rows to anyone except the table owner until a permissive policy exists, so this locks the table rather than restricting it. |
| `SS-RLS-002` | Warning | PostgreSQL — a table declares `Policies` but does not set `RowLevelSecurity`, so the policies are created and enforced against nothing. |
| `SS-RI-001` | Error | PostgreSQL — a table sets `ReplicaIdentity` to `INDEX` but declares no `ReplicaIdentityIndex`, so there is no index to carry the identity. |
| `SS-RI-002` | Error | PostgreSQL — a table's `ReplicaIdentityIndex` names an index the table does not declare, so the deploy would point the replica identity at an index that is never created. |
| `SS-RI-003` | Error | PostgreSQL — a table's `ReplicaIdentityIndex` names an index that is not unique. PostgreSQL requires a unique, non-partial index over `NOT NULL` columns. |
| `SS-RI-004` | Warning | PostgreSQL — a table names a `ReplicaIdentityIndex` while its `ReplicaIdentity` is not `INDEX`, so the index name is ignored. |
| `SS-SV-001` | Warning | MariaDB — a column sets `WithoutSystemVersioning` on a table that does not set `IsSystemVersioned`. MariaDB accepts the clause there and silently discards it, so the exclusion does nothing and nothing at deploy time reports it. |
| `SS-CO-001` | Error | MySQL/MariaDB — a table sets `Compression` (MySQL) or `PageCompressed` (MariaDB) together with `RowFormat: "COMPRESSED"`. Both engines refuse that combination (MySQL error 1031, MariaDB errno 140) and neither error names the option. |
| `SS-CO-002` | Warning | MariaDB — a table sets `PageCompressionLevel` without `PageCompressed`, so the level is ignored. |
| `SS-EVT-001` | Error | MySQL/MariaDB — a scheduled event is declared as JSON *and* scripted as a `.sql` file in the same `Events` folder. The scripted form drops and recreates the event on every deploy, undoing what the declared form converged. |
| `SS-PART-001` | Error | SQL Server — a table or index declares `PartitionScheme` without `PartitionColumn`, or the reverse. The `ON` clause needs both: the scheme, and the column its partition function is applied to. |
| `SS-XTP-001` | Error | SQL Server — a table sets `MemoryOptimized` and also declares a placement property (`FileGroup`, `TextImageFileGroup`, `FileStreamFileGroup`, or `PartitionScheme`). A memory-optimized table lives in the `MEMORY_OPTIMIZED_DATA` filegroup and cannot be placed on a regular or FILESTREAM filegroup, nor partitioned. One finding per offending property. |
| `SS-PART-002` | Error | SQL Server — a table or index declares both `FileGroup` and `PartitionScheme`. It lives on one data space; declare one or the other. |

Related-table resolution honors the same schema defaulting a real deployment uses -- an explicit `RelatedTableSchema`, a `schema.table` prefix on `RelatedTable`, or (falling back) the owning table's own schema. Because every foreign key target resolves to a concrete schema one way or another, there's no such thing as an "ambiguous target" here -- a `RelatedTable` either resolves to a real table or it doesn't.

This check is deliberately structural only -- it confirms columns exist, not that their types agree, and it doesn't validate `DeleteAction`/`UpdateAction` values. See [Data types](#data-types) below for why type agreement is left to deployment.

### Token validation

`{{Token}}` references are resolved at deploy time, which means a typo or a forgotten definition normally surfaces only when SchemaQuench actually runs -- deep into a deployment, possibly against a production target. `--Validate` scans every script and JSON file in the package as raw text (the same way `Template.Load` itself resolves tokens) and catches the problem before that ever happens:

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-TOK-001` | Error | A `{{Token}}` reference has no matching definition anywhere in the package. |
| `SS-TOK-002` | Error | A file contains an unmatched `{{` with no closing `}}`. |
| `SS-TOK-003` | Warning | A `ScriptTokens` entry is defined but never referenced anywhere in the package. |
| `SS-TOK-004` | Error | A `<*File*>`/`<*BinaryFile*>`/`<*QueryFile*>` ScriptTokens entry points at a file that doesn't exist (or can't be read). |

Built-in tokens (`ProductName`, `TemplateName`, `SchemaName`, `TableSchema`, `MaterializedViewSchema`, `IndexedViewSchema`, `repo_path`, `BranchName`) always count as defined, along with every `ScriptTokens` entry and every name derived from an `Extensions` block, in every prefixed form (`Table.`, `MaterializedView.`, `IndexedView.`) a token could legitimately take. When it's unclear whether a name is a genuine custom token, `--Validate` always resolves the uncertainty toward "assume defined" -- a linter that flags a token you actually defined is worse than one that occasionally misses a stale one.

`SS-TOK-001`/`002`/`003` scan the package as raw text and only confirm a token's *key* is defined somewhere -- they can't tell whether a `<*File*>` reference actually resolves. `SS-TOK-004` closes that gap: it comes from the loaded package (the same file-token resolution `Template.Load`/`Product.Load` perform at deploy time), so it reports the token key **and** the full resolved path it looked for -- product-level tokens resolve against the package root, template-level tokens against that template's own directory, so the same key can mean two different paths depending on where it's declared.

### Schema lint & staleness

Every package ships committed `.json-schemas/*.schema` files -- generated by SchemaTongs' `--WriteSchemasOnly` switch from the live C# domain model -- that describe the exact shape `Product.json`, `Template.json`, table JSON, and view JSON are supposed to have. `--Validate` checks your package against them in two passes:

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-STALE-001` | Error | The committed schema no longer matches what the current domain model would generate -- regenerate it. |
| `SS-JSON-001` | Error | A package JSON file violates its schema -- a misnamed property, a missing required field, a value outside a declared enum, or a violation of a hand-authored `Extensions` governance fragment. |

**Pass 1 (staleness) runs first.** `--Validate` regenerates each committed schema type in memory from the current domain model, merges the committed file's hand-authored `Extensions` fragment back in, and compares the result to what's actually committed. A mismatch means the domain model moved on since someone last ran `--WriteSchemasOnly` -- `SS-STALE-001` -- and that type is skipped in Pass 2: structural results checked against a schema that no longer matches the model would be misleading rather than helpful.

**Pass 2 (structural + governance) runs on every type that passed Pass 1 clean.** Every JSON file in the package is validated against its committed schema. Because Pass 2 validates against the *committed* schema file -- not a freshly generated one -- any custom governance you've hand-authored onto an `Extensions` property (a `required` list, an `enum` constraint) is enforced right alongside the standard structural rules, with zero extra configuration. See [Custom Properties -- JSON Schema Validation](custom-properties.md#json-schema-validation) for how to author that governance fragment.

If a package has no `.json-schemas/` directory at all, Pass 1 has nothing to compare against and raises no staleness finding -- it doesn't fail a package for omitting schemas it was never asked to carry. **Pass 2 still runs.** The domain model is the authority here, not the committed artifact, so structural validation falls back to a schema generated in memory, and a misnamed or misplaced property is still reported as `SS-JSON-001`. What the fallback costs you is governance you hand-authored: a freshly generated schema carries no `Extensions` fragment, so a `required` list or `enum` constraint you authored onto a custom property is not enforced on that run. The same fallback applies per type when a committed schema is missing or fails to parse.

### File naming

`--Validate` leans table files toward a canonical, sortable name derived from their content.

| Code | Severity | Meaning |
| --- | --- | --- |
| `SS-FILE-NAME-003` | Warning | A table file's on-disk name differs from the canonical `<schema>.<table>[.<VariantName>].json` derived from its `Schema`, `Name`, and `VariantName`. |

A table's identity lives in its file *content*, never its filename, so a misnamed file still deploys correctly -- this is a **lean, not a gate** (Warning only, never an Error, never changes the exit code). The canonical name keeps a table's conditional variants sorted together in source control and makes a file's name a reliable pointer to the table it holds. The canonical name is derived from content alone, so the schema segment is omitted whenever the table carries no `Schema`: MySQL and MariaDB (no per-table schema), schema-template packages (the schema is the iteration variable), and any table that omits `Schema` to inherit the platform default -- which is how SchemaTongs writes a PostgreSQL `public` table. A table that *declares* its schema keeps the prefix whether that schema is the default or not (`public.order_lines.json`, `sales.order_lines.json`). Either way the rule is the same: the filename must mirror the content. SchemaTongs writes canonical names on extraction; this check catches hand-authored files that have drifted.

### Data types

`--Validate` deliberately does not check that a column's `DataType` string is a real, spellable type for the target engine. That's not an oversight -- `DataType` is an open-ended field by design. It carries not just built-in types (`NVARCHAR(50)`, `INTEGER`, `VARCHAR(50)`) but engine user-defined types, PostgreSQL domains, and platform-specific aliases, all of which are only resolvable against a real, connected engine that knows what's been declared in `DataTypes/` (SQL Server) or `Domain Types/`/`Enum Types/`/`Composite Types/` (PostgreSQL). A static linter has no reliable way to distinguish a genuine typo from a legitimate custom type it's never heard of -- so rather than guess, `--Validate` leaves type correctness exactly where it belongs: deployment time, where the engine itself is the authority. This is the same trade-off Foreign Keys make: structure is checked statically, type agreement isn't.

---

## Reading the output

Findings print as one line each, errors first, then warnings, followed by a summary count:

```
ERROR [SS-FK-002] Template 'Main' / Table 'Orders' / FK 'FK_Orders_Customer': RelatedTable 'Customer' does not resolve to any known table (resolved schema 'dbo').
WARN [SS-TOK-003] ScriptTokens entry 'LegacyFlag' (defined in Product.json) is never referenced anywhere in the package.
1 error(s), 1 warning(s)
```

A package with no findings at all prints a single clean line:

```
PASS — no issues found
```

---

## Exit codes

The exit code is what makes `--Validate` a real CI gate rather than just a log you have to read: a pipeline step can trust the process exit code to decide pass or fail, with no output-parsing required.

| Code | Condition |
|------|-----------|
| `0` | No findings, or warnings only. |
| `2` | At least one Error-severity finding (including a load failure). |

Warnings never fail the run on their own -- they're advisory (a missing `VariantName` label, an unused token). Only Error-severity findings, or a package that fails to load at all, trip the exit code CI cares about.

---

## Use it in CI

`--Validate` needs nothing but the package and the SchemaQuench binary -- no database container, no secrets, no target environment. That makes it the cheapest possible gate to run on every pull request that touches schema files:

```yaml
# GitHub Actions -- static schema lint, no database required
name: Validate Schema Package
on:
  pull_request:
    paths:
      - 'MyProduct/**'

jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Install SchemaQuench
        run: curl -fsSL https://schemasmith.com/dl/install.sh | sh
      - name: Validate schema package
        working-directory: MyProduct
        run: schemaquench --Validate
```

Because `--Validate` exits `2` on any Error, the job fails naturally on a broken package -- no extra scripting needed to interpret the result. Cross-platform: the same command works whether `MyProduct/Product.json` declares `SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb` as its `Platform` -- `--Validate` reads the declared platform and loads the package through the matching domain model automatically.

Pair it with the connection-based gates for a layered pipeline: `--Validate` on every PR (fast, no database), `--TestConnection` and `--PreviewTargets` immediately before a real deployment window (see [Pre-Flight Diagnostics](schemaquench.md#pre-flight-diagnostics)), and `WhatIfONLY` when you want to see the actual generated SQL for a tricky change (see [WhatIf Mode](schemaquench.md#whatif-mode)).

---

## Related Documentation

- [SchemaQuench Reference](schemaquench.md) -- The deployment tool that `--Validate` is a switch on
- [SchemaQuench -- Pre-Flight Diagnostics](schemaquench.md#pre-flight-diagnostics) -- `--TestConnection` and `--PreviewTargets`, the connection-based gates in the same family
- [SchemaQuench -- WhatIf Mode](schemaquench.md#whatif-mode) -- The full dry-run gate, for seeing the actual SQL a deployment would generate
- [Custom Properties -- JSON Schema Validation](custom-properties.md#json-schema-validation) -- Authoring governance fragments that `SS-JSON-001` enforces
- [Schema Packages Reference](schema-packages.md#conditional-application) -- `ShouldApplyExpression` and `VariantName`, the mechanism `SS-DUP-001`/`SS-DUP-VAR-002` reason about
- [Script Tokens Reference](script-tokens.md) -- Token syntax and resolution, the mechanism `SS-TOK-001`/`SS-TOK-002`/`SS-TOK-003`/`SS-TOK-004` check
- [Testing and Validation Guide](../guide/06-testing-and-validation.md) -- Where `--Validate` fits alongside Docker-based testing and WhatIf
