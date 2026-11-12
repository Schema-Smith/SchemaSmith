# Custom Properties Reference

Applies to: SchemaQuench, SchemaTongs — SQL Server, PostgreSQL, MySQL, and MariaDB.

---

Custom properties are how you attach your own metadata to the things in a schema package -- data classification tags, ownership, retention policies, environment markers, anything you want your team to see, track, and act on. Define a property once and it flows through the pipeline: it rides alongside the standard table definition in source control, survives SchemaTongs re-extraction, and becomes a `{{TokenName}}` substitution inside any expression field SchemaQuench evaluates.

Think of them as sidecar data. SchemaSmith doesn't care what they mean. *You* do -- and that's exactly the point. Custom properties turn the schema package into a governance surface your team owns.

---

## The Extensions Carrier

Every custom value lives inside a single property on its containing object called `Extensions`. It's an open JSON bag: objects, arrays, strings, numbers, booleans -- anything the schema accepts. The core properties stay exactly where they've always been; your data rides in the `Extensions` object alongside them.

**Table-level custom properties:**

```json
{
  "Schema": "[dbo]",
  "Name": "[Orders]",
  "Columns": [],
  "Extensions": {
    "Environment": "Production",
    "DataClassification": "PII",
    "OwningTeam": "Identity"
  }
}
```

**Column-level custom properties:**

```json
{
  "Name": "[Amount]",
  "DataType": "DECIMAL(18,2)",
  "Nullable": false,
  "Extensions": {
    "DataClassification": "Financial",
    "MaskInNonProd": "true"
  }
}
```

**Nested objects and arrays** work naturally:

```json
{
  "Name": "[Orders]",
  "Extensions": {
    "Retention": {
      "Policy": "7years",
      "Tier": "Hot"
    },
    "ResponsibleTeams": ["Billing", "Compliance"]
  }
}
```

That's all there is to storage. One property name, one JSON bag, anywhere you want to attach metadata.

### Why a single carrier?

In earlier versions of SchemaSmith, custom properties were sprinkled directly into the object alongside the standard fields. Community consolidates them into `Extensions` for two concrete reasons:

1. **Zero collision risk.** You will never accidentally shadow a reserved property name. If a future SchemaSmith release adds a new top-level field called `Environment`, and you happened to be using `Environment` as a custom property, the flat form would silently overwrite your value. Under `Extensions`, your data is partitioned from the engine's data forever.
2. **Predictable schema generation.** Community generates the `.json-schemas/*.schema` validation files on the fly from the live C# type definitions. Every time those files are regenerated, they reflect exactly the current standard properties. Your `Extensions` content is never touched, never clobbered, and never silently validated against properties it doesn't belong to.

---

## Supported Objects

`Extensions` is available on every declared object and component in a package -- every type below accepts the key, carries it through load and round-trips it through extraction. The specific set varies by platform because the underlying schema elements vary. The **Tokens** column says whether the values also become `{{Token}}` substitutions (see [Token Integration](#token-integration)); where it says *metadata only*, the bag is stored and preserved but nothing reads it at deploy time.

### All Platforms

| Object | Lives In | Tokens |
|---|---|---|
| Table | Top-level table definition file | Yes |
| Column | `Columns` array | Yes |
| Index | `Indexes` array | Yes |
| ForeignKey | `ForeignKeys` array | Yes |
| CheckConstraint | `CheckConstraints` array | Yes |

### SQL Server Additional

| Object | Lives In | Tokens |
|---|---|---|
| XmlIndex | `XmlIndexes` array | Yes |
| SpatialIndex | `SpatialIndexes` array | Yes |
| JsonIndex | `JsonIndexes` array | Yes |
| Statistic | `Statistics` array | Yes |
| FullTextIndex | `FullTextIndex` object, or each variant in its array | Yes |
| EdgeConstraint | `EdgeConstraints` array | Metadata only |
| EdgeConstraint clause | `Clauses` array inside an edge constraint | Metadata only |
| IndexedView | Top-level indexed view definition file | Yes |
| IndexedView Index | `Indexes` array inside an indexed view definition | Yes |

### PostgreSQL Additional

| Object | Lives In | Tokens |
|---|---|---|
| MaterializedView | Top-level materialized view definition file | Yes |
| MaterializedView Index | `Indexes` array inside a materialized view definition | Yes |
| ExcludeConstraint | `ExcludeConstraints` array | Yes |
| Statistic | `Statistics` array | Yes |
| Policy | `Policies` array | Yes |
| DomainType | Top-level file in `Domain Types/` | Metadata only |
| Domain constraint | `CheckConstraints` array inside a domain type | Metadata only |
| EnumType | Top-level file in `Enum Types/` | Metadata only |
| Sequence | Top-level file in `Sequences/` | Metadata only |

### MySQL Additional

MariaDB tables carry every MySQL object below, plus the two marked MariaDB.

| Object | Lives In | Tokens |
|---|---|---|
| FullTextIndex | `FullTextIndexes` array | Metadata only |
| Event | Top-level file in `Events/` | Metadata only |
| Partitioning | `Partitioning` object | Metadata only |
| Subpartitioning | `Subpartitioning` object inside `Partitioning` | Metadata only |
| Partition | `Partitions` array inside `Partitioning` | Metadata only |
| SystemTime (MariaDB) | `SystemTime` object inside `Partitioning` | Metadata only |
| Sequence (MariaDB) | Top-level file in `Sequences/` | Metadata only |

> **Note:** `Product.json` and `Template.json` do not support `Extensions`, and neither do the entries inside `ScriptFolders`. Custom properties belong to schema components, not to the product or template configuration; a package that puts the key there fails to load, naming the property and the file.

Custom properties at any level are completely independent -- a `DataClassification` on the table and a `DataClassification` on a column are two different values.

---

## Token Integration

Here's where custom properties stop being passive metadata and start driving behavior. When SchemaQuench processes a table, it walks `Extensions` on every component and produces `{{TokenName}}` substitutions you can reference in any expression field.

### Scope rules

- **Table-level `Extensions`** are available inside the table's own `ShouldApplyExpression` using **bare names** (`{{Workload}}`).
- **Table-level `Extensions`** are also available in every *child* component's expression fields using the **`Table.` prefix** (`{{Table.Workload}}`).
- **Component-level `Extensions`** are available in that component's own expression fields using bare names (`{{MaskInNonProd}}`).
- Within a component's expression, the script tokens, the parent table's `Table.`-prefixed tokens and the component's own bare-name tokens are all available. The prefix keeps table and component names apart, so those two never collide.
- **First definition wins.** The token list is built in the order *script tokens* (`Product.json` and `Template.json` `ScriptTokens`, plus the automatic `ProductName` and `TemplateName`), then the table's `Table.`-prefixed values, then the component's bare values -- and a name is replaced by the first entry that matches it. So a bare custom property that happens to share a name with a `ScriptTokens` entry resolves to the **script token**, not the custom property; the same holds for a table's own bare names in its own `ShouldApplyExpression`. Pick names that do not shadow your script tokens.

### Flattening rules

- **Scalar values** (`string`, `number`, `bool`) become direct token values.
- **Nested objects** flatten with dot notation: `Extensions.Retention.Policy` becomes `{{Retention.Policy}}` at table scope or `{{Table.Retention.Policy}}` from child components.
- **Arrays** become comma-joined strings: `["Billing", "Compliance"]` becomes `Billing,Compliance`.
- Token names are matched case-insensitively.

### Where tokens are substituted

Custom property tokens are narrower than script tokens. They are substituted while a **table, indexed-view or materialized-view file** is loaded, in the expression fields of that file and nowhere else:

| Field | Where |
|---|---|
| `ShouldApplyExpression` | The table itself; every column, index, foreign key and check constraint; SQL Server XML, spatial and JSON indexes, statistics and full-text index variants; PostgreSQL statistics, exclude constraints and policies; an indexed or materialized view and its indexes |
| `Default` | Every column |
| `Expression` | Table-level check constraints |
| `CheckExpression`, `ComputedExpression` | SQL Server columns |
| `GenerationExpression` | PostgreSQL, MySQL and MariaDB columns |
| `FilterExpression` | SQL Server indexes and statistics; PostgreSQL indexes, exclude constraints and materialized-view indexes |
| `UsingExpression`, `WithCheckExpression` | PostgreSQL policies |

They do **not** reach `.sql` script files, the `Product.json` and `Template.json` script properties, a `DataDelivery` gate, a MySQL or MariaDB `FullTextIndexes` entry, `Partitioning`, or the declared-object files (events, enum types, domain types, sequences) -- in those places `Extensions` is metadata only. To hand a custom property to a script, read it out of the [`{{TableSchema}}`](script-tokens.md#automatic-tokens) payload, which carries every component's `Extensions` as written.

### Example — a table property combined with the environment

A custom property is part of the package, so it reads the same in every environment: on its own it cannot tell production from development. Pair it with a script token each environment sets -- here `DeployEnvironment`, declared in `Product.json` `ScriptTokens` and overridden per environment with `SmithySettings_ScriptTokens__DeployEnvironment`:

```json
{
  "Schema": "[dbo]",
  "Name": "[Orders]",
  "Extensions": {
    "Workload": "Reporting"
  },
  "Indexes": [
    {
      "Name": "[IX_Orders_CreatedDate]",
      "IndexColumns": "[CreatedDate]",
      "ShouldApplyExpression": "'{{Table.Workload}}' = 'Reporting' AND '{{DeployEnvironment}}' = 'Production'"
    }
  ]
}
```

At quench time, the index is declared only where the deploy's `DeployEnvironment` is `Production`. Everywhere else it is not declared, so it is not created there -- and an existing copy the product owns is dropped (see [A gate that turns off an existing object](schemaquench.md#a-gate-that-turns-off-an-existing-object)). The table says what it is; the environment says where the deploy runs. No per-environment file copies, no branching.

### Example — nested retention value driving a default

```json
{
  "Schema": "[dbo]",
  "Name": "[Documents]",
  "Extensions": {
    "Retention": {
      "ArchiveDays": "90"
    }
  },
  "Columns": [
    {
      "Name": "[ArchiveAfterDays]",
      "DataType": "INT",
      "Nullable": false,
      "Default": "{{Table.Retention.ArchiveDays}}"
    }
  ]
}
```

### Example — access from the component's own Extensions

```json
{
  "Name": "[SSN]",
  "DataType": "VARCHAR(11)",
  "Nullable": true,
  "Extensions": {
    "PII": "true"
  },
  "ShouldApplyExpression": "NOT ('{{PII}}' = 'true' AND '{{DeployEnvironment}}' = 'NonProd')"
}
```

Bare names pull from the column's own `Extensions`; `Table.`-prefixed names climb to the parent table, and script tokens such as `DeployEnvironment` resolve here too. Where the gate is false -- a PII column in a non-production deploy -- the column is not declared, so an existing copy is dropped with its data. That is the point of this gate: the column never lives outside production.

---

## Preservation During Re-extraction

SchemaTongs is a read-first tool. When it writes a table file back to a package that already contains a table file for the same table, it preserves whatever `Extensions` you had on the previous file. Your custom metadata survives the round-trip.

Matching is done by the component's `Name` property (with brackets/quotes stripped and case-insensitive comparison). Columns and the table itself also fall back to `OldName`, so renamed components keep their custom metadata as long as `OldName` is set correctly before the refresh.

| Component | Matched By | Applies To |
|---|---|---|
| Table | root object | All platforms |
| Column | `Name`, then `OldName` | All platforms |
| Index | `Name` | All platforms |
| ForeignKey | `Name` | All platforms |
| CheckConstraint | `Name` | All platforms |
| XmlIndex | `Name` | SQL Server |
| FullTextIndex | `Name` | SQL Server, MySQL |
| ExcludeConstraint | `Name` | PostgreSQL |
| Statistic | `Name` | PostgreSQL |
| MaterializedView | root object | PostgreSQL |
| IndexedView | root object | SQL Server |

If you drop a component from the database between extractions, its `Extensions` disappears with it -- there's nothing to match against in the new file.

---

## JSON Schema Validation

Community generates validation schemas (`.json-schemas/*.schema`) for `Product.json`, `Template.json`, table JSON, materialized view JSON, and indexed view JSON **on the fly** from the current C# type definitions. Every time SchemaTongs extracts a package, those files are written fresh based on the current engine.

`Extensions` is an open JToken -- the generated schema intentionally imposes no structure on it. That keeps the engine out of the business of validating your data.

If you want editor validation for *your* `Extensions` shape, you can hand-edit the relevant `.schema` file and add a JSON Schema fragment under the `Extensions` property. When SchemaTongs regenerates that schema file, it will **preserve your custom `Extensions` definition** and merge it back into the newly generated schema. Your validation rules outlive the regeneration cycle.

### Fragment governance

The `Extensions` governance pattern turns the schema package into a contract surface your team owns. You define what keys are required, what values are acceptable, and the CI pipeline enforces it automatically on every pull request -- no database, no deployment, no extra tooling beyond what the schema validation workflow already does.

**A real demo.** The `Demos/Learn/level2-module-06` packages carry governance-style Extensions on both tables and columns -- table-level `OwningTeam` and column-level `Classification` markers across SQL Server, PostgreSQL, MySQL, and MariaDB:

```json
{
  "Schema": "dbo",
  "Name": "Customer",
  "Extensions": { "OwningTeam": "Identity" },
  "Columns": [
    { "Name": "Email", "DataType": "NVARCHAR(256)", "Extensions": { "Classification": "PII" } },
    { "Name": "Ssn",   "DataType": "CHAR(11)",       "Extensions": { "Classification": "PII" } },
    { "Name": "DisplayName", "DataType": "NVARCHAR(128)", "Extensions": { "Classification": "Internal" } }
  ]
}
```

**Starter fragment** -- tightening `Extensions` on `tables.<platform>.schema` to require a `DataClassification` value and constrain `OwningTeam` to a known list:

```json
{
  "properties": {
    "Extensions": {
      "type": "object",
      "required": ["DataClassification"],
      "properties": {
        "DataClassification": {
          "type": "string",
          "enum": ["Public", "Internal", "Confidential", "PII", "Financial"]
        },
        "OwningTeam": {
          "type": "string",
          "enum": ["Identity", "Billing", "Compliance", "Platform"]
        }
      }
    }
  }
}
```

Drop that into `.json-schemas/tables.<platform>.schema` (e.g. `tables.sqlserver.schema`) under `properties.Extensions`, merging with whatever the schema already contains. Your editor enforces it for every table file in the package immediately. The schema validation workflow picks it up on the next PR with no changes required -- it validates against the whole `.schema` file, which now includes your governance rules.

**What this enforces at PR time:**

- **Require keys** -- `"required": ["DataClassification"]` fails any table that omits the field.
- **Constrain values** -- `"enum": [...]` fails any table that uses a classification or team name not in the list.
- **No database needed** -- the same `GrantBirki/json-yaml-validate` action that checks standard structure now checks your governance rules in the same step.

**What SchemaTongs does on the next extraction** -- it regenerates the standard properties and merges your `Extensions` fragment back in. Your governance contract survives the round-trip.

To add column-level governance (constraining `Classification` on individual columns), add the same pattern under `properties.Columns.items.properties.Extensions` in the same schema file.

> **Note:** The fragment constrains what editors and CI see at authoring time. SchemaQuench reads the `Extensions` values at quench time and resolves them as script tokens -- it does not re-validate the fragment itself at deployment time. CI validation is the enforcement point.

> **`Extensions` is data, authored as JSON.** You edit the JSON directly, hand-author the optional `Extensions` schema fragment, and consume the values through script tokens. That keeps the tooling focused on what every team needs: schema authorship, not form design.

---

## Tool Interactions

**SchemaTongs** -- Re-extraction preserves `Extensions` on every supported component as described above. Anything you add manually survives subsequent schema refreshes so long as the component name is unchanged (or `OldName` is set for renames).

**SchemaQuench** -- Reads `Extensions` at quench time and resolves the contents as script tokens in expression fields. `Extensions` has no direct effect on DDL generation; its influence is entirely through the token substitution mechanism. The full table metadata -- including `Extensions` -- is available at runtime through the `{{TableSchema}}` automatic token if you need to emit it into a migration script or audit row.

**DataTongs** -- Table files are round-tripped transparently; `Extensions` is neither inspected nor modified.

---

## Naming Guidance

Because everything lives inside the `Extensions` bag, there are no reserved names to worry about -- you cannot collide with a standard property. That said, keep your names stable and descriptive. The names become script tokens across your entire deployment, and renaming a property is a search-and-replace across every expression field that uses it.

A few practices that hold up well:

- **Pick one vocabulary per team** and commit it to writing. `DataClassification` and `dataClassification` and `data_classification` will all work, but consistency keeps the tokens predictable.
- **Group related values under a nested object** rather than flattening everything. `Extensions.Retention.Policy` reads better across a big schema than `Extensions.RetentionPolicy`, and it gives you a natural place to add related fields later.
- **Reserve namespace-style prefixes for shared metadata.** If multiple products in the same repo have common metadata needs, a `Company.` or `Compliance.` prefix prevents accidental reuse across unrelated domains.

---

## Related Documentation

- [Script Tokens Reference](script-tokens.md) -- Full token syntax, resolution order, and automatic tokens including `{{TableSchema}}`
- [Schema Packages Reference](schema-packages.md) -- Where `Extensions` fits in the overall package structure
- [SchemaTongs Reference](schematongs.md) -- Extraction behavior and the preservation pass
- [SchemaQuench Reference](schemaquench.md) -- How expression fields are evaluated and where tokens resolve
