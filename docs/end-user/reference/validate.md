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
| `SS-DUP-001` | Error | Same-name entries exist and at least one isn't gated by `ShouldApplyExpression` -- an accidental duplicate. Checked at every table level, and across each template's declared enum types, domain types, sequences, materialized views, indexed views and events (schema-qualified, so the same name in two schemas is not a duplicate). |
| `SS-DUP-VAR-002` | Warning | Every entry in the group IS gated (a legitimate variant set), but not every entry declares `VariantName` -- label them for clarity. |

The check runs at every level a name collision could hide: columns, indexes, foreign keys, check constraints, tables (within a template), the product's `TemplateOrder`, the platform-specific collections (SQL Server XML indexes and statistics; PostgreSQL statistics and exclude constraints; MySQL full-text indexes), and each template's declared objects (the enum types, domain types, sequences, materialized views, indexed views and events listed under `SS-DUP-001`).

> **Note:** `--Validate` can only confirm that every member of a same-name group is gated -- it can't prove the gates are mutually *exclusive* on any given target. That requires evaluating the predicates against a live database, which is exactly what a real quench does at deploy time. A gated variant set that passes `--Validate` can still fail at deployment if two variants' expressions both resolve true against the same target.

### Cross-object coherence

The JSON schema can enforce shape, but it can't confirm that a foreign key actually points at something real. `CoherenceCheck` walks every foreign key and index in the package and confirms the columns it references exist. Names match without regard to case, except on PostgreSQL: SchemaSmith deploys every PostgreSQL name quoted, so `Customer` and `customer` are two different tables there, and a reference to the wrong spelling is reported.

| Code | Severity | Meaning |
|------|----------|---------|
| `SS-FK-001` | Error | A foreign key's `Columns` entry names a column that doesn't exist on the local table. |
| `SS-FK-002` | Warning | A foreign key's `RelatedTable` doesn't resolve to any table in the package. A warning rather than an error because the deploy creates a foreign key to any table that already exists on the target, declared or not. A partial deployment such as a SchemaShears patch or a bootstrap references tables it does not declare by design, so expect the warning there; it still exits 0. |
| `SS-FK-004` | Error | A foreign key's `RelatedColumns` entry names a column that doesn't exist on the related table. |
| `SS-FK-005` | Error | `Columns` and `RelatedColumns` have different entry counts -- the column lists must be the same length. |
| `SS-FK-006` | Error or Warning | Two tables in one template declare a foreign key with the same name. MySQL names foreign keys per database and SQL Server per schema, so the second one fails the deploy (Error). MariaDB names them per table from 12.1, so there it is a Warning, and none at all when the product's `MinimumVersion` is 12.1 or later. PostgreSQL names them per table on every version and is not checked. |
| `SS-FK-007` | Error | A PostgreSQL foreign key sets `SetNullColumns` without `DeleteAction: "SET NULL"`, or names a column that is not one of its `Columns`. |
| `SS-FK-008` | Warning | A MySQL foreign key whose `RelatedColumns` are not the whole primary key or a whole unique key of a related table in the package (a non-unique index, a prefix key, part of a key). MySQL 8.4 refuses it while `restrict_fk_on_non_standard_key` is on, its default; MariaDB has no such restriction. SchemaTongs logs the same warning for each one it extracts from MySQL. |
| `SS-FK-009` | Warning | A MySQL or MariaDB foreign key declares `SET DEFAULT` as its `DeleteAction` or `UpdateAction`. Both engines accept it and then act as `RESTRICT`: the change to the parent row is refused. |
| `SS-FK-010` | Warning | A foreign key's `RelatedTable` is declared in another template whose `DatabaseIdentificationScript` differs from this one's, so the two deploy to different databases. A foreign key resolves within its own template; the deploy succeeds only if the table already exists on this template's database. Templates with the same identification script resolve against each other. |
| `SS-IDX-001` | Error | An index's `IndexColumns` entry names a column that doesn't exist on the table. |
| `SS-IDX-002` | Error | An index sets `WithoutOverlaps` but is not a primary key or unique constraint (PostgreSQL) or unique key (MariaDB), so the clause would not be emitted. |
| `SS-IDX-003` | Error | A MariaDB index sets `WithoutOverlaps` but its last `IndexColumns` part is not one of the table's `Periods`. |
| `SS-IDX-004` | Warning | A PostgreSQL primary key sets `NullsNotDistinct`. Its columns cannot be NULL, so the clause decides nothing and the engine never records it on a primary key; the deploy leaves it off. Declare it on a unique index or unique constraint instead. |
| `SS-TMP-001` | Error | A SQL Server column declares `GeneratedAlways` `RowStart` or `RowEnd` and no column declares the other: `PERIOD FOR SYSTEM_TIME` needs both. |
| `SS-TMP-002` | Error | More than one column on a SQL Server table declares the same `GeneratedAlways` role. |
| `SS-TMP-003` | Error | A SQL Server table declares period columns but not `IsTemporal`; they are created only with system versioning. |
| `SS-TMP-004` | Error | A SQL Server period column is not `DATETIME2` or `DATETIME2(n)`. |
| `SS-LDG-001` | Error | A SQL Server column declares a ledger `GeneratedAlways` role (`TransactionIdStart`, `TransactionIdEnd`, `SequenceNumberStart`, `SequenceNumberEnd`) on a table that declares no `Ledger`. |
| `SS-LDG-002` | Error | An append-only ledger table declares an End column; it has only the two Start columns. |
| `SS-LDG-003` | Error | More than one column on a ledger table declares the same ledger role. |
| `SS-LDG-004` | Error | A table declares a `LedgerView` but no `Ledger`. |
| `SS-LDG-005` | Error | A ledger column is not `BIGINT`. |
| `SS-SPX-001` | Error | A SQL Server spatial index names a column the table does not declare, or one that is not `geometry` or `geography`. |
| `SS-SPX-002` | Error | A spatial index's `Tessellation` is for the other family: a `GEOGRAPHY_` scheme on a geometry column, or a `GEOMETRY_` scheme on a geography column. |
| `SS-SPX-003` | Error | A spatial index on a geometry column has no `BoundingBox`, one on a geography column has one, or the box has no area (`XMax` not above `XMin`, or `YMax` not above `YMin`). |
| `SS-SPX-004` | Error | A spatial index declares `Grids` with an `AUTO_GRID` tessellation, which chooses its own. |
| `SS-COL-001` | Warning | A column sets `BackfillExistingRows` but has no `Default`, so there is no value to apply to rows that already exist and the setting does nothing. |
| `SS-COL-002` | Warning | A MySQL or MariaDB column declares a `CharacterSet` other than the table's and no `Collation`, so it gets that character set's default collation, which varies by engine version. A column in the table's own character set gets the table's collation; `utf8` and `utf8mb3` count as one set, and a MariaDB short collation such as `uca1400_ai_ci` belongs to the table's `CharacterSet`. |
| `SS-COL-003` | Warning | A PostgreSQL check constraint or foreign key is named `<table>_<column>_not_null`, the name PostgreSQL 18 gives the NOT NULL constraint it makes for that column: a collision on 18, and a misleading name everywhere. |
| `SS-COL-004` | Warning | A MySQL column's generation expression or default, a check constraint, an index key part or the partitioning expression calls `MD5()`, `SHA1()` or `SHA()`, which MySQL 9.6 removed. `SHA2()` is the replacement. |
| `SS-COL-005` | Warning | A MySQL or MariaDB column is declared `NVARCHAR`, `NCHAR` or `NATIONAL ...`, which creates a `utf8mb3` column. Declare `VARCHAR` or `CHAR` with `CharacterSet: utf8mb4`. |
| `SS-COL-006` | Warning | A MySQL column uses a form MySQL 8.0.17 deprecated: `FLOAT(M,D)` or `DOUBLE(M,D)`, `UNSIGNED` on `FLOAT`, `DOUBLE` or `DECIMAL`, or `ZEROFILL`. |
| `SS-TBL-001` | Error | A `RebuildPolicy` — on the product, a template, or a table — uses `Mode: "THRESHOLD"` without a `Threshold` of 1 or more, so the policy cannot be evaluated. Because a policy declared at any level replaces the one it would have inherited, an unusable one also blocks rebuilds an outer level asked for. |
| `SS-TBL-002` | Warning | A `RebuildPolicy` sets a `Threshold` while its `Mode` is not `"THRESHOLD"`, so the threshold is ignored. An omitted `Mode` defaults to `"NEVER"`, so `{ "Threshold": 50 }` on its own means *never rebuild* — and, replacing the inherited policy, can block rebuilds an outer level asked for. |
| `SS-TBL-003` | Error | A SQL Server table declares `EdgeConstraints` but its `GraphType` is not `"Edge"`; only an edge table can carry one. |
| `SS-TBL-004` | Warning | An edge constraint clause names a table the package does not declare as a node table, so the deploy succeeds only if one already exists on the target. |
| `SS-RLS-001` | Warning | PostgreSQL — a table sets `RowLevelSecurity` but declares no `Policies`. PostgreSQL returns no rows to anyone except the table owner until a permissive policy exists, so this locks the table rather than restricting it. |
| `SS-RLS-002` | Warning | PostgreSQL — a table declares `Policies` but does not set `RowLevelSecurity`, so the policies are created and enforced against nothing. |
| `SS-RI-001` | Error | PostgreSQL — a table sets `ReplicaIdentity` to `INDEX` but declares no `ReplicaIdentityIndex`, so there is no index to carry the identity. |
| `SS-RI-002` | Error | PostgreSQL — a table's `ReplicaIdentityIndex` names an index the table does not declare, so the deploy would point the replica identity at an index that is never created. |
| `SS-RI-003` | Error | PostgreSQL — a table's `ReplicaIdentityIndex` names an index that is not unique. PostgreSQL requires a unique, non-partial index over `NOT NULL` columns. |
| `SS-RI-004` | Warning | PostgreSQL — a table names a `ReplicaIdentityIndex` while its `ReplicaIdentity` is not `INDEX`, so the index name is ignored. |
| `SS-VER-001` | Error | A product's `MinimumVersion` is not a version the engine can resolve, so the deploy refuses the package before touching any object. On **SQL Server** a value of 2000 or more is read as a *release year* and must be one of 2008, 2012, 2014, 2016, 2017, 2019, 2022 or 2025 — an in-between year such as `2018` or `2020` looks plausible and does not resolve; a major version number (`13`, `16`) works too. PostgreSQL and the MySQL family read the value arithmetically, so only a non-numeric one fails. Reported here because `MinimumVersion` has no pattern in the generated JSON schema, so an editor cannot flag it. |
| `SS-VER-002` | Warning | SQL Server — the package's scripts use `CREATE OR ALTER` while its `MinimumVersion` admits a build before 2016 SP1. `2016` covers the RTM build, `13.0.1601`, which rejects the statement as a syntax error, so the deploy fails on the first module. One finding per package, naming the scripts. Declare `MinimumVersion` 2017 or later, or extract the package with `ShouldCast:ModuleSyntax` set to `Legacy`, which writes each module as a guarded `CREATE` stub followed by `ALTER` and runs on every supported version. A package with no `MinimumVersion` makes no claim and is not reported. |
| `SS-DESC-001` | Error | A table, column, index, full-text index or event says two different things about its description: `Description` and the older spelling (`Comment` on MySQL and MariaDB, `Extensions.ExtendedProperties.MS_Description` on SQL Server) disagree. The deploy applies `Description` and ignores the other, so keep one. |
| `SS-CHK-001` | Error | PostgreSQL — a check constraint declares `Deferrable` or `InitiallyDeferred`. PostgreSQL rejects `DEFERRABLE` on a CHECK constraint (only a foreign key, primary key, unique or exclusion constraint can be deferred), and the deploy no longer emits it, so the package is stopped rather than deployed without what it asked for. Remove the key. |
| `SS-CHK-002` | Error | MariaDB — a `CheckConstraints` entry is named like a column of its table. MariaDB gives that name to the column's own column-level check, which SchemaSmith reads as the column's `CheckExpression`, and from 10.11.19 / 11.4.13 / 13.0.2 the engine refuses a table-level check that shares it. Rename the constraint, or declare it as the column's `CheckExpression`. |
| `SS-DESC-002` | Warning | A table, column, index, full-text index or event still uses the older spelling of `Description` (`Comment` on MySQL and MariaDB, `Extensions.ExtendedProperties.MS_Description` on SQL Server). It still deploys; move the value to `Description`, which is what SchemaTongs writes on re-extraction. One finding per table, naming the components. |
| `SS-SV-001` | Warning | MariaDB — a column sets `WithoutSystemVersioning` on a table that does not set `IsSystemVersioned`. MariaDB accepts the clause there and silently discards it, so the exclusion does nothing and nothing at deploy time reports it. |
| `SS-CDC-001` | Warning | SQL Server — a table sets `CdcFilegroup`, `CdcSupportsNetChanges` or `CdcIndexName` without `"EnableCDC": true`, so there is no capture instance for the setting to apply to and it does nothing. Set `EnableCDC`, or drop the setting. |
| `SS-CDC-002` | Error | SQL Server — a table's `CdcIndexName` names no declared index, a non-unique one, or one with a nullable key column. CDC identifies rows by a unique index over `NOT NULL` columns, and the deploy refuses anything else. |
| `SS-FT-001` | Error | SQL Server — a full-text index's `KeyIndex` names no declared index, or one that is not unique, has more than one key column, has a filter, or has a nullable key column. SQL Server builds a full-text index only over an unfiltered unique index on a single `NOT NULL` column (error 7653), so the deploy fails creating it. |
| `SS-CO-001` | Error | MySQL/MariaDB — a table sets `Compression` (MySQL) or `PageCompressed` (MariaDB) together with `RowFormat: "COMPRESSED"`. Both engines refuse that combination (MySQL error 1031, MariaDB errno 140) and neither error names the option. |
| `SS-CO-002` | Warning | MariaDB — a table sets `PageCompressionLevel` without `PageCompressed`, so the level is ignored. |
| `SS-EVT-001` | Error | MySQL/MariaDB — a scheduled event is declared as JSON *and* scripted as a `.sql` file in the same `Events` folder. The scripted form drops and recreates the event on every deploy, undoing what the declared form converged. |
| `SS-ENUM-001` | Error | PostgreSQL — an enum type is declared as JSON *and* scripted as a `.sql` file in the same `Enum Types` folder. The scripted form is a guarded `CREATE TYPE`, so once the type exists the script silently does nothing while the declared form is what converges. |
| `SS-SEQ-001` | Error | PostgreSQL — a sequence is declared as JSON *and* scripted as a `.sql` file in the same `Sequences` folder. Two authoring paths for one object leave it ambiguous which one is in charge. |
| `SS-SEQ-002` | Warning | MariaDB — a script outside `MigrationScripts` re-creates a sequence with `CREATE OR REPLACE SEQUENCE`. Object scripts run on every deploy, and replacing a sequence resets it to its `START` value, so `NEXTVAL` returns numbers it has already given. Use `CREATE SEQUENCE IF NOT EXISTS`, which is how SchemaTongs extracts sequences; packages extracted by earlier versions carry the replacing form. |
| `SS-GEN-001` | Warning | A script SchemaTongs generated, such as one in `Prerequisites/`, has been edited since. The next extraction overwrites it, so change the source database instead, or delete the script's first line to make it your own. |
| `SS-IDENT-002` | Error | PostgreSQL — an object name is longer than 63 bytes. PostgreSQL truncates it, so the object is stored under a shorter name and the next deploy, not finding the declared one, fails creating it again. The limit is in bytes, so names in multi-byte characters reach it sooner. Covers a table, its schema, columns, indexes, keys, constraints, statistics, replica identity index and `OldName`s, and materialized views, enum types, sequences and domain types. |
| `SS-DOM-001` | Error | PostgreSQL — a domain type is declared as JSON *and* scripted as a `.sql` file in the same `Domain Types` folder. There is no `CREATE OR REPLACE DOMAIN`, so the scripted form is a guarded `CREATE DOMAIN` and silently does nothing once the domain exists. |
| `SS-PART-001` | Error | SQL Server — a table or index declares `PartitionScheme` without `PartitionColumn`, or the reverse. The `ON` clause needs both: the scheme, and the column its partition function is applied to. |
| `SS-XTP-001` | Error | SQL Server — a table sets `MemoryOptimized` and also declares a placement property (`FileGroup`, `TextImageFileGroup`, `FileStreamFileGroup`, or `PartitionScheme`). A memory-optimized table lives in the `MEMORY_OPTIMIZED_DATA` filegroup and cannot be placed on a regular or FILESTREAM filegroup, nor partitioned. One finding per offending property. |
| `SS-PART-002` | Error | SQL Server — a table or index declares both `FileGroup` and `PartitionScheme`. It lives on one data space; declare one or the other. |
| `SS-PART-003` | Error | MySQL, MariaDB — a table partitions by `RANGE` or `LIST` (or their `COLUMNS` forms) and declares no `Partitions`. Each partition is named with its boundary; use `HASH` or `KEY` with a `PartitionCount` to spread rows without boundaries. |
| `SS-PART-004` | Error | MySQL, MariaDB — a `HASH` or `KEY` partition declares a `Values` boundary. Those methods assign rows by hashing and have none. |
| `SS-PART-005` | Error | MySQL, MariaDB — a primary or unique key does not hold a partitioning column whole: the column is missing from the key, or the key holds only a prefix of it (`` `code`(10) ``). The engine refuses the table (`1503`). Checked for `KEY` (an empty list means the primary key's columns) and the `COLUMNS` forms; an expression method's columns are not read. |

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
| `SS-STALE-002` | Error | A committed schema file could not be parsed. It is unusable, so any `Extensions` governance authored in it was **not** applied this run. |
| `SS-JSON-001` | Error | A package JSON file violates its schema -- a misnamed property, a missing required field, a value outside a declared enum, or a violation of a hand-authored `Extensions` governance fragment. |

**Pass 1 (staleness) runs first.** `--Validate` regenerates each committed schema type in memory from the current domain model, merges the committed file's hand-authored `Extensions` fragment back in, and compares the result to what's actually committed. A mismatch means the domain model moved on since someone last ran `--WriteSchemasOnly` -- `SS-STALE-001`.

**A stale type is still validated in Pass 2**, against that merged schema. Out of date is not the same as meaningless: the governance authored into the file is still what you asked for, so skipping the type would quietly stop enforcing it while reporting only the staleness. Expect Pass 2 findings a stale schema was previously hiding -- those files really are invalid against what will be deployed.

**A schema that cannot be parsed is a different case** -- `SS-STALE-002`. There is no fragment to recover from an unreadable file, so that type falls back to a freshly generated schema and the governance the file carried is **not** enforced on that run. The finding says so rather than only naming the file. Regenerate it with `--WriteSchemasOnly`: the malformed file is replaced and you are warned, by name, that its authored `Extensions` fragment could not be preserved and must be re-applied.

**Pass 2 (structural + governance) runs on every type that passed Pass 1 clean.** Every JSON file in the package is validated against its committed schema. Because Pass 2 validates against the *committed* schema file -- not a freshly generated one -- any custom governance you've hand-authored onto an `Extensions` property (a `required` list, an `enum` constraint) is enforced right alongside the standard structural rules, with zero extra configuration. See [Custom Properties -- JSON Schema Validation](custom-properties.md#json-schema-validation) for how to author that governance fragment.

If a package has no `.json-schemas/` directory at all, Pass 1 has nothing to compare against and raises no staleness finding -- it doesn't fail a package for omitting schemas it was never asked to carry. **Pass 2 still runs.** The domain model is the authority here, not the committed artifact, so structural validation falls back to a schema generated in memory, and a misnamed or misplaced property is still reported as `SS-JSON-001`. What the fallback costs you is governance you hand-authored: a freshly generated schema carries no `Extensions` fragment, so a `required` list or `enum` constraint you authored onto a custom property is not enforced on that run. The same fallback applies per type when a committed schema is missing or fails to parse -- but **not** when one is merely stale, which is still enforced (above).

### File naming

`--Validate` leans table and declared-object files toward a canonical, sortable name derived from their content.

| Code | Severity | Meaning |
| --- | --- | --- |
| `SS-FILE-NAME-003` | Warning | A table or declared-object file's on-disk name differs from the canonical `<schema>.<name>[.<VariantName>].json` derived from its `Schema`, `Name`, and `VariantName` (no schema segment when the file declares none, as for events; in a schema template the variant follows a `~`, as in `Customers~eu.json`). Covers `Tables/` and the declared-object folders the package's engine loads: `Enum Types/`, `Domain Types/`, `Sequences/` and `Materialized Views/` (PostgreSQL), `Indexed Views/` (SQL Server), `Events/` (MySQL, MariaDB). |

A table's identity lives in its file *content*, never its filename, so a misnamed file still deploys correctly -- this is a **lean, not a gate** (Warning only, never an Error, never changes the exit code). The canonical name keeps a table's conditional variants sorted together in source control and makes a file's name a reliable pointer to the table it holds. The canonical name is derived from content alone, so the schema segment is omitted whenever the table carries no `Schema`: MySQL and MariaDB (no per-table schema), schema-template packages (the schema is the iteration variable), and any table that omits `Schema` to inherit the platform default -- which is how SchemaTongs writes a PostgreSQL `public` table. A table that *declares* its schema keeps the prefix whether that schema is the default or not (`public.order_lines.json`, `sales.order_lines.json`). Either way the rule is the same: the filename must mirror the content. SchemaTongs writes canonical names on extraction; this check catches hand-authored files that have drifted.

### Deprecated aliases

A deprecated alias keeps an older package deploying: load migrates it to the current form and the deploy log says so. `--Validate` reports every one it migrated, so a package does not validate clean while it depends on something scheduled for retirement.

| Code | Severity | Meaning |
| --- | --- | --- |
| `SS-DEP-001` | Warning | A MySQL or MariaDB template uses `SchemaIdentificationScript`, the deprecated alias for `DatabaseIdentificationScript`. Rename the key; the value is unchanged. (On SQL Server and PostgreSQL `SchemaIdentificationScript` is a real schema-template setting and is never reported.) |

Warnings only: neither changes the exit code.

### Data types

`--Validate` deliberately does not check that a column's `DataType` string is a real, spellable type for the target engine. That's not an oversight -- `DataType` is an open-ended field by design. It carries not just built-in types (`NVARCHAR(50)`, `INTEGER`, `VARCHAR(50)`) but engine user-defined types, PostgreSQL domains, and platform-specific aliases, all of which are only resolvable against a real, connected engine that knows what's been declared in `DataTypes/` (SQL Server) or `Domain Types/`/`Enum Types/`/`Composite Types/` (PostgreSQL). A static linter has no reliable way to distinguish a genuine typo from a legitimate custom type it's never heard of -- so rather than guess, `--Validate` leaves type correctness exactly where it belongs: deployment time, where the engine itself is the authority. This is the same trade-off Foreign Keys make: structure is checked statically, type agreement isn't.

---

## Reading the output

Findings print as one line each, errors first, then warnings, followed by a summary count:

```
ERROR [SS-FK-001] Template 'Main' / Table 'Orders' / FK 'FK_Orders_Customer': Local column 'CustomerID' referenced in Columns does not exist on table 'Orders'.
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
