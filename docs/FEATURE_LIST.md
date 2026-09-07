# SchemaSmith Feature List

Complete checklist of what SchemaSmith does, by platform. The
[end-user documentation](end-user/README.md) explains *how* each feature
works; this page is the *what* — a flat inventory you can scan when
evaluating, comparing, or remembering what's in the box.

**Legend:** `✓` supported in v2.0 · `✗` applicable to this engine but not yet supported by SchemaSmith · `n/a` not applicable to this engine

## Platform & Tool Coverage

SchemaSmith ships three CLI tools. All three work against all four supported database engines from a single, shared schema-package format.

| Tool         | Description                                       | SQL Server | PostgreSQL | MySQL | MariaDB |
|--------------|---------------------------------------------------|:----------:|:----------:|:-----:|:-----:|
| SchemaQuench | Deploy a schema package to a target database      | ✓          | ✓          | ✓     | ✓     |
| SchemaTongs  | Extract a live database into a schema package     | ✓          | ✓          | ✓     | ✓     |
| DataTongs    | Generate platform-aware data MERGE/upsert scripts | ✓          | ✓          | ✓     | ✓     |

**Database engines:** SQL Server 2008+ (database compatibility level 100+); PostgreSQL 12+; MySQL 5.7+; MariaDB 10.2+. CI runs SQL Server 2019, PostgreSQL 12 through latest, MySQL 5.7 and 8.0, and MariaDB 10.2, 10.6 and 11.4.
**Operating systems:** Windows, Linux, macOS — on both x64 and ARM64.
**Runtime:** Self-contained .NET 10 executables. No .NET runtime install required on target machines.

## Object Type Support

Every database object SchemaSmith reads from a live database, models as part of a schema package, and writes back during deployment. Objects with structured JSON definitions support diff-based state deployment; objects living in script folders deploy via their `CREATE`/`ALTER` scripts.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Tables | ✓ | ✓ | ✓ | ✓ | `Tables/*.json`; structured JSON |
| Columns | ✓ | ✓ | ✓ | ✓ | Shared `Columns` array |
| Primary keys | ✓ | ✓ | ✓ | ✓ | Index entry with `PrimaryKey: true` |
| Foreign keys | ✓ | ✓ | ✓ | ✓ | `ForeignKeys` array; full cascade actions |
| Check constraints | ✓ | ✓ | ✓ | ✓ | Column-level or `CheckConstraints` array |
| Unique constraints | ✓ | ✓ | ✓ | ✓ | `UniqueConstraint: true` on index entry |
| Default values | ✓ | ✓ | ✓ | ✓ | `Default` column property |
| Clustered indexes | ✓ | n/a | n/a | n/a | `Clustered: true` |
| Non-clustered indexes | ✓ | ✓ | ✓ | ✓ | Default index shape |
| Filtered / partial indexes | ✓ | ✓ | n/a | n/a | `FilterExpression` |
| Covering / INCLUDE indexes | ✓ | ✓ | n/a | n/a | PG 11+; `IncludeColumns` |
| Columnstore indexes | ✓ | n/a | n/a | n/a | `ColumnStore` flag |
| Full-text indexes | ✓ | n/a | ✓ | ✓ | SQL Server: 1 per table; MySQL: multiple |
| XML indexes | ✓ | n/a | n/a | n/a | `XmlIndexes` array; primary + secondary |
| Spatial indexes | n/a | ✓ | ✓ | ✓ | PG via `gist`/`spgist` AccessMethod; MySQL via index on spatial column |
| Statistics | ✓ | ✓ | n/a | n/a | SQL Server traditional; PG extended (`ndistinct`, `dependencies`, `mcv`) |
| Views | ✓ | ✓ | ✓ | ✓ | Script folder |
| Indexed views | ✓ | n/a | n/a | n/a | `Indexed Views/*.json` |
| Materialized views | n/a | ✓ | n/a | n/a | `Materialized Views/*.json` |
| Stored procedures | ✓ | ✓ | ✓ | ✓ | `Procedures/` folder |
| Scalar / table-valued functions | ✓ | ✓ | ✓ | ✓ | `Functions/` folder |
| DML triggers | ✓ | ✓ | ✓ | ✓ | `Triggers/` folder; AfterTablesObjects slot |
| DDL triggers | ✓ | n/a | n/a | n/a | `DDLTriggers/` folder |
| Schemas | ✓ | ✓ | n/a | n/a | `Schemas/` folder; MySQL has no separate schema concept |
| User-defined types (scalar) | ✓ | ✓ | n/a | n/a | SQL Server `DataTypes/`; PG `Domain Types/` |
| User-defined enum types | n/a | ✓ | n/a | n/a | `Enum Types/` |
| User-defined composite types | n/a | ✓ | n/a | n/a | `Composite Types/` |
| User-defined table types | ✓ | n/a | n/a | n/a | Bundled with `UserDefinedTypes` ShouldCast flag |
| Full-text catalogs | ✓ | n/a | n/a | n/a | `FullTextCatalogs/` |
| Full-text stop lists | ✓ | n/a | n/a | n/a | `FullTextStopLists/` |
| XML schema collections | ✓ | n/a | n/a | n/a | `XMLSchemaCollections/` |
| Exclude constraints | n/a | ✓ | n/a | n/a | `ExcludeConstraints` array |
| Sequences | n/a | ✓ | n/a | n/a | `Sequences/` folder |
| Aggregates | n/a | ✓ | n/a | n/a | `Aggregates/` folder |
| Rules | n/a | ✓ | n/a | n/a | `Rules/` folder |
| Trigger functions | n/a | ✓ | n/a | n/a | `Trigger Functions/` |
| Window functions | n/a | ✓ | n/a | n/a | `Window Functions/` |
| Events | n/a | n/a | ✓ | ✓ | `Events/` folder |

## Table Features

Per-table options, column attributes, and table-scoped behavior beyond the basics. Most platform-specific extras live on the platform table type (`SqlServerTable`, `PostgreSqlTable`, `MySqlTable`) and serialize alongside the shared properties.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Identity / auto-increment / serial | ✓ | ✓ | ✓ | ✓ | Encoded in `DataType` string per platform |
| Computed / generated columns | ✓ | ✓ | ✓ | ✓ | `ComputedExpression` (SQL Server) / `GenerationExpression` (PG, MySQL) |
| Persisted computed columns | ✓ | n/a | n/a | n/a | `Persisted: true` |
| Sparse columns | ✓ | n/a | n/a | n/a | `Sparse` column property |
| Dynamic data masking | ✓ | n/a | n/a | n/a | `DataMaskFunction` column property |
| Column collation override | ✓ | ✓ | ✓ | ✓ | `Collation` column property |
| Per-column character set | n/a | n/a | ✓ | ✓ | `CharacterSet` column property |
| Column comment | n/a | n/a | ✓ | ✓ | `Comment` column property |
| Table comment | n/a | n/a | ✓ | ✓ | `Comment` table property |
| Table character set / collation | n/a | n/a | ✓ | ✓ | `CharacterSet`, `Collation` table props |
| Storage engine selection | n/a | n/a | ✓ | ✓ | `Engine` (default `InnoDB`) |
| Row format | n/a | n/a | ✓ | ✓ | `RowFormat`: DYNAMIC / COMPACT / COMPRESSED / REDUNDANT |
| Initial auto-increment value | n/a | n/a | ✓ | ✓ | `AutoIncrementValue` |
| Data compression | ✓ | n/a | n/a | n/a | `CompressionType`: NONE / ROW / PAGE |
| XML compression | ✓ | n/a | n/a | n/a | `XmlCompression` on table and index; deploy 2022+, extract 2025+ |
| Memory-optimized (Hekaton) tables | ✓ | n/a | n/a | n/a | `MemoryOptimized` + `Durability` (SCHEMA_AND_DATA / SCHEMA_ONLY); indexes declared inline; the memory-optimized nature, durability, and inline index shape are refused on change; ownership tracked in `SchemaSmith.ProductOwnership` (extended properties are rejected on such tables); requires In-Memory OLTP support + a `MEMORY_OPTIMIZED_DATA` filegroup |
| Table-level access method | n/a | ✓ | n/a | n/a | `AccessMethod` |
| Persistence type (UNLOGGED / TEMPORARY) | n/a | ✓ | n/a | n/a | `PersistenceType` |
| Row-level security | n/a | ✓ | n/a | n/a | `RowLevelSecurity`, `ForceRowLevelSecurity` |
| Replica identity (logical replication) | n/a | ✓ | n/a | n/a | `ReplicaIdentity`, `ReplicaIdentityIndex` |
| Tablespace placement (table + index) | n/a | ✓ | n/a | n/a | `Tablespace`; create-time only, a move is refused |
| Partition placement (table + index) | ✓ | n/a | n/a | n/a | `PartitionScheme` + `PartitionColumn`; a scheme NAME, never created — applied at create, a change is refused |
| Partitioning (RANGE / LIST / HASH / KEY, incl. COLUMNS) | n/a | n/a | ✓ | ✓ | `Partitioning`; applied at create, a change is refused — repartitioning rewrites every row |
| System-versioned table | n/a | n/a | n/a | ✓ | `IsSystemVersioned`; created WITH SYSTEM VERSIONING, existing table converges via ALTER ADD; removing it is refused (MariaDB purges history on DROP); MariaDB 10.3+ |
| Per-column history exclusion | n/a | n/a | n/a | ✓ | `WithoutSystemVersioning` on a system-versioned table |
| InnoDB page compression | n/a | n/a | ✓ | ✓ | MySQL `Compression`; MariaDB `PageCompressed` + `PageCompressionLevel` |
| Compressed-page size | n/a | n/a | ✓ | ✓ | `KeyBlockSize`, with `RowFormat: COMPRESSED` |
| At-rest table encryption | n/a | n/a | ✓ | ✓ | MySQL `Encryption`; MariaDB `Encrypted` + `EncryptionKeyId`; converges by rebuild; needs a server keyring |
| General tablespace placement | n/a | n/a | ✓ | n/a | `Tablespace` (InnoDB general tablespace); create-time only, a move is refused |
| Data-directory placement | n/a | n/a | ✓ | ✓ | `DataDirectory` (InnoDB `DATA DIRECTORY`); create-time only, a move is refused; MySQL needs `innodb_directories` |
| Scheduled events (declarative) | n/a | n/a | ✓ | ✓ | `Events/*.json`; compared, converges, drop-by-absence via `DropEventsRemovedFromProduct` |
| Domain types (declarative) | n/a | ✓ | n/a | n/a | `Domain Types/*.json`; constraints, default and NOT NULL converge in place — a base-type change is refused |
| Enum types (declarative) | n/a | ✓ | n/a | n/a | `Enum Types/*.json`; values compared and added in declared order |
| Sequences (declarative) | n/a | ✓ | n/a | n/a | `Sequences/*.json`; all attributes converge, current value never touched |
| Table fill factor | n/a | ✓ | n/a | n/a | `FillFactor` 0–100 on table |
| Per-table `UpdateFillFactor` | ✓ | ✓ | n/a | n/a | OR'd with template + index level |
| Temporal tables (system-versioning marker) | ✓ | n/a | n/a | n/a | `IsTemporal` flag |
| Change Data Capture marker | ✓ | n/a | n/a | n/a | `EnableCDC` flag |
| Table rename via `OldName` | ✓ | ✓ | ✓ | ✓ | |
| Column rename via `OldName` | ✓ | ✓ | ✓ | ✓ | |
| Conditional table application | ✓ | ✓ | ✓ | ✓ | `ShouldApplyExpression` |
| Custom metadata bag (`Extensions`) | ✓ | ✓ | ✓ | ✓ | On every component |

## Index Features

Index shape, predicates, included columns, fill factor, and platform-specific index types. Filter expressions and conditional `ShouldApplyExpression` make individual indexes adaptable to environment or version.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Primary key | ✓ | ✓ | ✓ | ✓ | |
| Unique index | ✓ | ✓ | ✓ | ✓ | `Unique: true` |
| Unique constraint | ✓ | ✓ | ✓ | ✓ | `UniqueConstraint: true` |
| Clustered | ✓ | n/a | n/a | n/a | `Clustered` |
| Filtered / partial (`WHERE` predicate) | ✓ | ✓ | n/a | n/a | `FilterExpression` |
| Included / covering columns | ✓ | ✓ | n/a | n/a | PG 11+; `IncludeColumns` |
| Per-index fill factor | ✓ | n/a | n/a | n/a | `FillFactor`, `UpdateFillFactor` |
| Index storage parameters (`WITH`) | n/a | ✓ | n/a | n/a | `StorageParameters` map (e.g. gin `fastupdate`, brin `pages_per_range`, pgvector `hnsw` tuning); `fillfactor` handled separately |
| Hash index bucket count | ✓ | n/a | n/a | n/a | `BucketCount` on a memory-optimized hash index |
| Per-index compression | ✓ | n/a | n/a | n/a | `CompressionType` |
| Columnstore | ✓ | n/a | n/a | n/a | `ColumnStore` |
| Index access method (btree / gin / gist / brin / spgist / hash) | n/a | ✓ | n/a | n/a | `AccessMethod` |
| Index type `BTREE` / `HASH` | n/a | n/a | ✓ | ✓ | `IndexType` |
| Visible / invisible index | n/a | n/a | ✓ | ✓ | `Visible` flag |
| Index tablespace | n/a | ✓ | n/a | n/a | `Tablespace` |
| Index partition alignment | ✓ | n/a | n/a | n/a | `PartitionScheme` + `PartitionColumn`, independent of the table's own placement |
| Sort direction per column | ✓ | ✓ | ✓ | ✓ | In `IndexColumns` string |
| Conditional index application | ✓ | ✓ | ✓ | ✓ | `ShouldApplyExpression` |
| XML index (primary / secondary VALUE / PATH / PROPERTY) | ✓ | n/a | n/a | n/a | `XmlIndexes` |
| Full-text index — full props (catalog, stop list, change tracking, key index) | ✓ | n/a | n/a | n/a | `FullTextIndex` (1 per table) |
| Full-text index with parser (e.g. `ngram`) | n/a | n/a | ✓ | ✓ | `Parser`; multiple per table |

## Constraint Features

Primary keys, foreign keys with full cascade-action support, check / unique / default constraints, plus PostgreSQL-only exclude constraints. Foreign keys participate in FK-aware data delivery, so seed data lands in dependency order without hand-authored sequencing.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Primary key | ✓ | ✓ | ✓ | ✓ | |
| Foreign key | ✓ | ✓ | ✓ | ✓ | `ForeignKeys` array |
| Composite foreign keys | ✓ | ✓ | ✓ | ✓ | Comma-list in `Columns` / `RelatedColumns` |
| FK action `NO ACTION` | ✓ | ✓ | ✓ | ✓ | |
| FK action `CASCADE` | ✓ | ✓ | ✓ | ✓ | |
| FK action `SET NULL` | ✓ | ✓ | ✓ | ✓ | |
| FK action `SET DEFAULT` | ✓ | ✓ | n/a | n/a | MySQL InnoDB does not support |
| FK action `RESTRICT` | n/a | ✓ | ✓ | ✓ | |
| Column-level check constraint | ✓ | ✓ | ✓ | ✓ | `CheckExpression` on column |
| Table-level / multi-column check | ✓ | ✓ | ✓ | ✓ | `CheckConstraints` array |
| Unique constraint | ✓ | ✓ | ✓ | ✓ | |
| Default constraint | ✓ | ✓ | ✓ | ✓ | `Default` on column |
| Exclude constraint | n/a | ✓ | n/a | n/a | `ExcludeConstraints` |
| Deferrable / initially deferred (exclude) | n/a | ✓ | n/a | n/a | `Deferrable`, `InitiallyDeferred` |
| FK-aware data delivery ordering | ✓ | ✓ | ✓ | ✓ | Two-pass; deferred nullable FKs |
| Conditional constraint application | ✓ | ✓ | ✓ | ✓ | `ShouldApplyExpression` |

## Programmable Object Features

Programmable objects — procedures, functions, triggers, views — deploy via `CREATE OR ALTER`-style scripts (where supported) with a dependency-aware retry loop that resolves ordering automatically. Indexed views and materialized views additionally support diff-based state deployment.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Stored procedures | ✓ | ✓ | ✓ | ✓ | |
| Scalar functions | ✓ | ✓ | ✓ | ✓ | |
| Table-valued / set-returning functions | ✓ | ✓ | n/a | n/a | |
| Aggregate functions | n/a | ✓ | n/a | n/a | `Aggregates/` |
| Window functions | n/a | ✓ | n/a | n/a | `Window Functions/` |
| Trigger functions (separate from triggers) | n/a | ✓ | n/a | n/a | `Trigger Functions/` |
| Views | ✓ | ✓ | ✓ | ✓ | |
| Indexed views (diff-based) | ✓ | n/a | n/a | n/a | Index-only changes skip view rebuild |
| Materialized views (diff-based) | n/a | ✓ | n/a | n/a | Index-only changes skip MV rebuild |
| DML triggers (BEFORE / AFTER / INSTEAD OF) | ✓ | ✓ | ✓ | ✓ | AfterTablesObjects slot |
| DDL triggers | ✓ | n/a | n/a | n/a | |
| Rules | n/a | ✓ | n/a | n/a | `Rules/` |
| Events (scheduled) | n/a | n/a | ✓ | ✓ | `Events/` |
| Dependency-aware retry on Objects slot | ✓ | ✓ | ✓ | ✓ | Up to 4 passes |
| Auto dependency drop / recreate for functions | ✓ | ✓ | ✓ | ✓ | `ScriptDynamicDependencyRemovalForFunctions` |
| `CREATE OR ALTER` formatting on extraction | ✓ | ✓ | ✓ | ✓ | Where engine supports it |
| Idempotency-test second pass | ✓ | ✓ | ✓ | ✓ | `RunScriptsTwice` |

## SchemaQuench — Deployment Engine

The deployment engine. Templates run in 9 ordered execution slots, with state-based table reconciliation handled by a set of modular procedures. Conditional application, secondary-server fan-out, parallel execution, checkpoint/resume, and WhatIf are all first-class capabilities.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| 9 execution slots (7 template + 2 product) | ✓ | ✓ | ✓ | ✓ | Before, Objects, BetweenTablesAndKeys, AfterTablesScripts, AfterTablesObjects, TableData, After + Product Before/After |
| MissingTableAndColumnQuench | ✓ | ✓ | ✓ | ✓ | |
| ModifiedTableQuench | ✓ | ✓ | ✓ | ✓ | |
| MissingIndexesAndConstraintsQuench | ✓ | ✓ | ✓ | ✓ | |
| ForeignKeyQuench | ✓ | ✓ | ✓ | ✓ | |
| ParseTableJsonIntoTempTables | ✓ | ✓ | ✓ | ✓ | |
| IndexOnlyQuench mode | ✓ | ✓ | ✓ | ✓ | Template `IndexOnlyTableQuenches` |
| `RebuildPolicy` (rebuild instead of per-column ALTER) | ✓ | ✓ | ✓ | ✓ | `Mode` NEVER / ALWAYS / THRESHOLD, plus `Threshold` and `OnOrderMismatch`; declarable on a table, template, product or the environment (`RebuildPolicyMode`, `RebuildPolicyThreshold`, `RebuildPolicyOnOrderMismatch`) and the nearest level that declares one wins WHOLE. Refused when the live state cannot be reconstructed from the declared definition — system versioning, CDC, replication, Change Tracking, partitioning |
| IndexedViewQuench (diff-based) | ✓ | n/a | n/a | n/a | |
| MaterializedViewQuench + MissingMaterializedViewIndexesQuench | n/a | ✓ | n/a | n/a | |
| ShouldApplyExpression evaluation | ✓ | ✓ | ✓ | ✓ | |
| Conditional folder deployment | ✓ | ✓ | ✓ | ✓ | `ShouldApplyExpression` on a script folder; per-target, fail-closed |
| Secondary servers (parallel deploy) | ✓ | n/a | n/a | n/a | Availability Group; `Target:SecondaryServers` |
| `ServerToQuench` per product folder | ✓ | n/a | n/a | n/a | Primary / Secondary / Both |
| Parallel template / database execution | ✓ | ✓ | ✓ | ✓ | `MaxThreads` 1–20 |
| Parallel file token resolution | ✓ | ✓ | ✓ | ✓ | |
| Checkpoint / `--ResumeQuench` / `--CheckpointDirectory` | ✓ | ✓ | ✓ | ✓ | Auto-cleanup on success |
| `WhatIfONLY` dry run | ✓ | ✓ | ✓ | ✓ | Per-script "Would APPLY / SKIP" logging |
| Debug SQL output files | ✓ | ✓ | ✓ | ✓ | One per quench operation |
| `RunScriptsTwice` (idempotency CI) | ✓ | ✓ | ✓ | ✓ | |
| `KindleTheForge` toggle | ✓ | ✓ | ✓ | ✓ | |
| `UpdateTables` toggle | ✓ | ✓ | ✓ | ✓ | |
| `DropTablesRemovedFromProduct` toggle | ✓ | ✓ | ✓ | ✓ | |
| `TrackRunOnceMigrations` toggle | ✓ | ✓ | ✓ | ✓ | |
| `PruneObsoleteMigrationTracking` toggle | ✓ | ✓ | ✓ | ✓ | |
| `[ALWAYS]` migration suffix | ✓ | ✓ | ✓ | ✓ | |
| Migration tracking table | ✓ | ✓ | ✓ | ✓ | `SchemaSmith.CompletedMigrationScripts` |
| Custom script folders / slots | ✓ | ✓ | ✓ | ✓ | `ScriptFolders` on Template.json |
| Per-table / per-index `UpdateFillFactor` | ✓ | ✓ | n/a | n/a | OR'd with template setting |
| `Template.Required` | ✓ | ✓ | ✓ | ✓ | Fail if 0 databases returned |
| `Template.SkipIfReadOnly` | ✓ | ✓ | ✓ | ✓ | AG secondary / replica handling |
| `VerboseLogging` | ✓ | n/a | n/a | n/a | SQL Server `InfoMessage`; PG/MySQL surface notices by default |
| Engine notices in log | ✓ | ✓ | ✓ | ✓ | PG via Notice event; MySQL via `SchemaSmith_StatusMessages` poller |
| ZIP package consumption | ✓ | ✓ | ✓ | ✓ | `SchemaPackagePath` to `.zip` |
| Direct procedure calls from migrations | ✓ | ✓ | ✓ | ✓ | TableQuench / IndexedViewQuench / MaterializedViewQuench |
| FK-aware two-pass data delivery | ✓ | ✓ | ✓ | ✓ | |
| Datafix profile (4-flag partial-package mode) | ✓ | ✓ | ✓ | ✓ | |

## SchemaTongs — Extraction Engine

The extraction engine. Reads a live database into a versioned schema package using only direct SQL queries — no SMO, no platform SDKs. Handles orphan detection, script validation, subfolder preservation, and schema-only regeneration without a database connection.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Pure SQL extraction (no SMO / external SDK) | ✓ | ✓ | ✓ | ✓ | SMO removed in v2 |
| Package initialization (Product.json, Template.json, folders) | ✓ | ✓ | ✓ | ✓ | |
| Runtime SchemaGenerator for `.json-schemas/` | ✓ | ✓ | ✓ | ✓ | Generated on the fly from C# types |
| `--WriteSchemasOnly` (no DB connection) | ✓ | ✓ | ✓ | ✓ | |
| Orphan detection: `Detect` mode | ✓ | ✓ | ✓ | ✓ | |
| Orphan detection: `DetectWithCleanupScripts` | ✓ | ✓ | ✓ | ✓ | |
| Orphan detection: `DetectDeleteAndCleanup` | ✓ | ✓ | ✓ | ✓ | |
| Post-extraction `ValidateScripts` + `.sqlerror` files | ✓ | ✓ | ✓ | ✓ | `SaveInvalidScripts` toggle |
| Subfolder preservation (`ExtractionFileIndex`) | ✓ | ✓ | ✓ | ✓ | |
| `ObjectList` filter | ✓ | ✓ | ✓ | ✓ | Disables orphan detection |
| `CheckConstraintStyle: ColumnLevel` | ✓ | ✓ | ✓ | ✓ | Default |
| `CheckConstraintStyle: TableLevel` | ✓ | ✓ | ✓ | ✓ | |
| Per-object `ShouldCast` toggles | ✓ | ✓ | ✓ | ✓ | Platform-specific flags ignored when N/A |
| `FolderMapping` rename (per object type) | ✓ | ✓ | ✓ | ✓ | |
| `CREATE OR ALTER` formatting | ✓ | ✓ | ✓ | ✓ | |
| Extensions / custom-property preservation across re-extraction | ✓ | ✓ | ✓ | ✓ | |
| Auto-exclusion of system / SchemaSmith objects | ✓ | ✓ | ✓ | ✓ | Objects under `sys` / `INFORMATION_SCHEMA` (SQL Server), `pg_*` / `information_schema` (PG), `mysql` / `information_schema` / `performance_schema` / `sys` (MySQL), and the `SchemaSmith` infrastructure schema. `dbo` (SQL Server) and `public` (PG) are extracted normally — they're user schemas, not system. |
| Encrypted object skip + warn | ✓ | n/a | n/a | n/a | NULL `sys.sql_modules.definition` |
| Replication-artifact exclusion | ✓ | n/a | n/a | n/a | `MSPeer_`, `MSPub_` |
| Dynamic function-dependency drop / recreate scripting | ✓ | ✓ | ✓ | ✓ | `ScriptDynamicDependencyRemovalForFunctions` |
| Filesystem-illegal character percent-encoding | ✓ | ✓ | ✓ | ✓ | |

## DataTongs — Data MERGE Generation

The data tool. Extracts table data and emits platform-aware deployment scripts — `MERGE` on SQL Server and PostgreSQL 15+, `INSERT ... ON DUPLICATE KEY UPDATE` on MySQL. Auto-detects primary keys, handles complex types, and integrates with FK-aware data delivery.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| Auto primary-key detection (`KeyColumns` optional) | ✓ | ✓ | ✓ | ✓ | Falls back to best unique index |
| NULL-safe match via `*` prefix | ✓ | ✓ | ✓ | ✓ | Auto-applied to nullable detected keys |
| Per-table WHERE filter | ✓ | ✓ | ✓ | ✓ | Scopes extraction and delete clause |
| Column subset (`SelectColumns`) | ✓ | ✓ | ✓ | ✓ | |
| `MERGE` script generation | ✓ | ✓ | n/a | n/a | PG 15+ |
| `INSERT ... ON DUPLICATE KEY UPDATE` | n/a | n/a | ✓ | ✓ | |
| `INSERT IGNORE` (seed-only) | n/a | n/a | ✓ | ✓ | |
| Insert + Update + Delete (full sync) | ✓ | ✓ | ✓ | ✓ | MySQL: ODKU + conditional `DELETE WHERE NOT EXISTS` |
| Trigger disable wrapping | ✓ | ✓ | ✓ | ✓ | `DisableTriggers` |
| Rule disable wrapping | n/a | ✓ | n/a | n/a | `DisableRules` |
| Partition-descendant control (`MERGE ... ONLY`) | n/a | ✓ | n/a | n/a | `UpdateDescendents` |
| Identity insert handling | ✓ | n/a | n/a | n/a | `SET IDENTITY_INSERT` wrapping |
| `OVERRIDING SYSTEM VALUE` on identity | n/a | ✓ | n/a | n/a | |
| Geometry / geography (WKT round-trip) | ✓ | n/a | ✓ | ✓ | MySQL: basic spatial via WKT |
| HierarchyID round-trip | ✓ | n/a | n/a | n/a | |
| XML / NTEXT / TEXT / IMAGE handling | ✓ | n/a | n/a | n/a | |
| JSON / JSONB round-trip | n/a | ✓ | n/a | n/a | |
| Computed / generated column auto-exclusion | ✓ | ✓ | ✓ | ✓ | |
| `--ConfigureDataDelivery` writes block into table JSON | ✓ | ✓ | ✓ | ✓ | |
| Empty-table skip | ✓ | ✓ | ✓ | ✓ | |
| `.tabledata` content file output | ✓ | ✓ | ✓ | ✓ | Consumed by declarative DataDelivery |
| Token resolution / `TokenizeScripts` | ✓ | n/a | n/a | n/a | DataTongs tokenization flag is SQL Server only |
| Full token resolution in script body | ✓ | ✓ | ✓ | ✓ | |
| Type-aware NULL-safe change detection | ✓ | ✓ | ✓ | ✓ | |
| Deterministic key-order extraction | ✓ | ✓ | ✓ | ✓ | Diff-friendly output |

## Token System

A consistent token grammar runs through every script SchemaSmith sees — migrations, object scripts, validation, table data, everything. Simple `{{Tokens}}` resolve from product/template/env-var sources; advanced `<*Tags*>` pull live values from queries, files, or whole-object JSON.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| `{{TokenName}}` resolution (case-insensitive) | ✓ | ✓ | ✓ | ✓ | Every script folder, every JSON expression field |
| Product-level `ScriptTokens` | ✓ | ✓ | ✓ | ✓ | |
| Template-level `ScriptTokens` (override product) | ✓ | ✓ | ✓ | ✓ | |
| Settings-file token override | ✓ | ✓ | ✓ | ✓ | Existing keys only |
| Env-var token override (`SmithySettings_ScriptTokens__*`) | ✓ | ✓ | ✓ | ✓ | Existing keys only |
| `<*File*>` tag | ✓ | ✓ | ✓ | ✓ | |
| `<*BinaryFile*>` tag | ✓ | ✓ | ✓ | ✓ | Platform-correct literal: `0x...` SQL Server / MySQL, `E'\\x...'::bytea` PG |
| `<*Query*>` tag (run against target) | ✓ | ✓ | ✓ | ✓ | |
| `<*QueryFile*>` tag | ✓ | ✓ | ✓ | ✓ | |
| `<*SpecificTable*>` tag | ✓ | ✓ | ✓ | ✓ | |
| `<*SpecificIndexedView*>` tag | ✓ | n/a | n/a | n/a | |
| `<*SpecificMaterializedView*>` tag | n/a | ✓ | n/a | n/a | |
| Auto `{{ProductName}}` / `{{TemplateName}}` | ✓ | ✓ | ✓ | ✓ | |
| Auto `{{TableSchema}}` | ✓ | ✓ | ✓ | ✓ | Template-wide table JSON |
| Auto `{{IndexedViewSchema}}` | ✓ | n/a | n/a | n/a | |
| Auto `{{MaterializedViewSchema}}` | n/a | ✓ | n/a | n/a | |
| Cross-template `{{TableSchema_<TemplateName>}}` and friends | ✓ | ✓ | ✓ | ✓ | |
| Custom-property tokens from `Extensions` | ✓ | ✓ | ✓ | ✓ | Bare and `Table.`-prefixed |
| Parallel file token resolution | ✓ | ✓ | ✓ | ✓ | |
| Single-quote escaping for `<*File*>` values | ✓ | ✓ | ✓ | ✓ | |
| Hard fail on missing file / failed query / unknown specific object | ✓ | ✓ | ✓ | ✓ | |
| Sensitive value masking in logs (settings echo + script tokens) | ✓ | ✓ | ✓ | ✓ | Default name patterns + connection-string subfield strip; tunable via `LogHygiene` |

## Configuration & CLI

Every tool reads its own `*.settings.json`, supports environment-variable overrides for any setting, and accepts CLI switches for the runtime knobs that vary per invocation. Connection details, log paths, config files, and execution mode are all overridable from the command line.

| Feature | SQL Server | PostgreSQL | MySQL | MariaDB | Notes |
|---|---|---|---|---|---|
| `SchemaQuench.settings.json` | ✓ | ✓ | ✓ | ✓ | |
| `SchemaTongs.settings.json` | ✓ | ✓ | ✓ | ✓ | |
| `DataTongs.settings.json` | ✓ | ✓ | ✓ | ✓ | |
| Env-var overrides (`SmithySettings_` prefix, `__` separator) | ✓ | ✓ | ✓ | ✓ | |
| `--ConfigFile:<path>` | ✓ | ✓ | ✓ | ✓ | |
| `--LogPath:<path>` | ✓ | ✓ | ✓ | ✓ | |
| `--ConnectionString:<connstr>` | ✓ | ✓ | ✓ | ✓ | Bypasses individual settings |
| `--version` / `-v` / `--ver` | ✓ | ✓ | ✓ | ✓ | |
| `--help` / `-h` / `-?` | ✓ | ✓ | ✓ | ✓ | |
| `--ResumeQuench` (SchemaQuench) | ✓ | ✓ | ✓ | ✓ | |
| `--CheckpointDirectory:<path>` (SchemaQuench) | ✓ | ✓ | ✓ | ✓ | |
| `--WriteSchemasOnly` (SchemaTongs) | ✓ | ✓ | ✓ | ✓ | |
| `--ConfigureDataDelivery` (DataTongs) | ✓ | ✓ | ✓ | ✓ | |
| `--TemplatePath` (DataTongs) | ✓ | ✓ | ✓ | ✓ | |
| `Port` field in connection settings | ✓ | ✓ | ✓ | ✓ | Defaults 1433 / 5432 / 3306 |
| `ConnectionProperties` arbitrary key/value | ✓ | ✓ | ✓ | ✓ | |
| Windows authentication (blank user/pass) | ✓ | n/a | n/a | n/a | |
| Switch styles `--`, `-`, `/`, `:` and `=` separators | ✓ | ✓ | ✓ | ✓ | Case-insensitive names |
| log4net progress + error logs | ✓ | ✓ | ✓ | ✓ | |
| Console mirror with level-based color | ✓ | ✓ | ✓ | ✓ | |
| Numbered backup of log files per run | ✓ | ✓ | ✓ | ✓ | `<Tool>.NNNN/` |
| Debug SQL files for generated procedures | ✓ | ✓ | ✓ | ✓ | Operations not applicable to platform produce no file |
| `VerboseLogging` setting | ✓ | n/a | n/a | n/a | SQL Server `InfoMessage` only; PG/MySQL surface notices by default |
| Copyright-header CI validation | ✓ | ✓ | ✓ | ✓ | All `.cs` and `.sql` |
| Exit codes 0 / 2 / 3 / 4 | ✓ | ✓ | ✓ | ✓ | |

## Distribution & Install

How SchemaSmith reaches your machine. Multiple install channels per OS — pick whatever fits your platform conventions. All Windows binaries are Authenticode-signed; all release archives are covered by a single `SHA256SUMS` manifest.

| Channel | Windows | Linux | macOS | Notes |
|---|---|---|---|---|
| Chocolatey package (`choco install schemasmith`) | ✓ | n/a | n/a | Combined package; embedded signed binaries |
| `.deb` (amd64) | n/a | ✓ | n/a | Debian / Ubuntu via `dpkg -i`; nfpm-built |
| `.deb` (arm64) | n/a | ✓ | n/a | |
| `.rpm` (amd64) | n/a | ✓ | n/a | RHEL / Fedora / Amazon Linux via `rpm -i` |
| `.rpm` (arm64) | n/a | ✓ | n/a | |
| Cross-platform `install.sh` | n/a | ✓ | ✓ | POSIX-sh; OS+arch detect; SHA-256 verify |
| GitHub Release `.zip` archive | ✓ | n/a | n/a | |
| GitHub Release `.tar.gz` archive | n/a | ✓ | ✓ | |
| `SHA256SUMS` manifest per release | ✓ | ✓ | ✓ | One-shot `sha256sum -c` / `shasum -a 256 -c` |
| Authenticode (Azure Trusted Signing) on binaries | ✓ | n/a | n/a | Eliminates SmartScreen warnings |
| Self-contained single-file executables | ✓ | ✓ | ✓ | 6 RIDs: win / linux / osx × x64 / arm64; no .NET runtime install needed |
| Libicu-independent runtime (private ICU) | n/a | ✓ | n/a | Bundles ICU 72.1.0.3; runs on slim / hardened images |
| Docker images | n/a | ✓ | n/a | Multi-platform, non-root user |
| `INSTALL_VERSION` / `INSTALL_DIR` env-var overrides | n/a | ✓ | ✓ | `install.sh` |
| `/usr/bin/` symlinks for `schemaquench` / `schematongs` / `datatongs` | n/a | ✓ | n/a | `.deb` / `.rpm`; binaries under `/usr/lib/schemasmith/` |
