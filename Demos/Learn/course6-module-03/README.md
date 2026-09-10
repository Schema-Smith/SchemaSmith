# Course 6, Module 3 — CI schema validation & Extensions governance (lab)

**Goal:** validate a schema package structurally on every pull request with no database, then turn the generated `.json-schemas` into a governance contract — requiring and constraining custom `Extensions` metadata at the table and column level — and wire it all into a CI gate. SQL Server, PostgreSQL, MySQL, and MariaDB.

## The scenario

Your schema lives in Git. Before anything reaches a database, you want two guarantees on every pull request: that each `Product.json`, `Template.json`, and table file is *structurally* valid, and that it obeys your team's *governance* rules — every table names an owning team, every column declares a data classification, and nobody can invent a value outside the approved list. All of this is enforceable with the schema files SchemaSmith already generates, a single reusable GitHub Action, and zero database access. This lab is entirely database-free.

## Before you start

> **Engine floor:** this lab deliberately declares a floor *higher* than it needs (SQL Server `2019`, PostgreSQL `15`, MySQL `8.0`, MariaDB `10.6`) so that raising it can be shown failing. On your own server, either meet those or edit `Product.json` down — they are the lab's teaching values, not SchemaSmith limits.

- This lab needs no sandbox and no running database — it validates JSON files against JSON Schema.
- Each engine package under `sqlserver/`, `postgres/`, `mysql/`, and `mariadb/` already ships its generated `.json-schemas/` with a governance fragment applied. You can validate them as-is, or regenerate and re-apply to practice the workflow.
- To validate locally you need Node. This lab uses `ajv-cli`: `npx ajv-cli@5 ...`. In CI, the `GrantBirki/json-yaml-validate` action does the same thing with no setup.
- To regenerate the schemas you need the SchemaSmith CLI on your PATH (`schematongs --version`). Regeneration is database-free.

> **Local `ajv-cli` note:** `ajv-cli` reads schemas by file extension and does not recognize the `.schema` extension. Copy the schema to a `.json` file first, then validate. The `GrantBirki/json-yaml-validate` CI action reads `.schema` files directly — this is a local-tool convenience only.

## Scenario 1 — structural validation (the base gate)

Every package ships generated `.json-schemas/*.schema` files — one per content type, per platform. Validate a table file against its schema (SQL Server shown; swap the infix for `postgresql` / `mysql` / `mariadb`):

```
cp sqlserver/Package/.json-schemas/tables.sqlserver.schema /tmp/tables.json
npx ajv-cli@5 validate -s /tmp/tables.json -d "sqlserver/Package/Templates/Main/Tables/*.json" --strict=false
```

Both tables report `valid`. Now break one — remove the `DataType` from a column in `dbo.Customer.json` — and re-run. Validation fails with `must have required property 'DataType'`. No database was touched; a structural typo is caught in seconds.

## Scenario 2 — table-level governance

`Extensions` is an open metadata bag; the generated schema imposes no structure on it. To make it a contract, this lab's `tables.<platform>.schema` adds a fragment under `properties.Extensions` requiring an `OwningTeam` from an approved list, and adds `Extensions` to the schema's top-level `required`:

```json
"Extensions": {
  "type": "object",
  "required": ["OwningTeam"],
  "properties": {
    "OwningTeam": { "type": "string", "enum": ["Identity", "Billing", "Compliance", "Platform"] }
  }
}
```

A table with no `Extensions` block fails (`must have required property 'Extensions'`). A table whose `OwningTeam` is not in the list — say `"Marketing"` — fails with `must be equal to one of the allowed values`.

## Scenario 3 — column-level governance

The same pattern under `properties.Columns.items.properties.Extensions` requires every column to declare a `DataClassification`:

```json
"Extensions": {
  "type": "object",
  "required": ["DataClassification"],
  "properties": {
    "DataClassification": { "type": "string", "enum": ["Public", "Internal", "Confidential", "PII", "Financial"] }
  }
}
```

Now a PII column shipped without a classification fails the pull request (`/Columns/1 must have required property 'Extensions'`), and a classification outside the approved list fails the same way. Your reviewers never have to catch an unclassified column by eye again.

## Scenario 4 — regeneration and the governance you keep

Regenerate the schemas (database-free):

```
SmithySettings_Product__Path="$(pwd)/sqlserver/Package" schematongs --WriteSchemasOnly
```

(Run the equivalent for the `postgres/Package`, `mysql/Package`, and `mariadb/Package` directories to regenerate those engines.)

Both the **table-level** and **column-level** `Extensions` fragments survive the round-trip — your `OwningTeam` and `DataClassification` rules are still enforced after regeneration. Governance is regeneration-safe at both levels.

> **One thing to know about the file your governance lives in.** These rules are *inside*
> `.json-schemas/tables.<platform>.schema`. If that file is ever committed **malformed** — a bad merge, a
> truncated write — it cannot be parsed, so the rules cannot be recovered and validation falls back to a
> freshly generated schema that has never heard of them. The failure is directional: a table violating
> your own enum rule then validates **clean**, because the rule was never evaluated. `--Validate` reports
> it as `SS-STALE-002`, and [Module 6, Scenario 5b](../course6-module-06) walks the whole thing. Worth
> knowing now, while you are the one authoring the fragment.

## Scenario 5 — wire it into CI

`ci/validate-schemas.yml` is a copy-ready workflow. Copy it into your repository's `.github/workflows/` and adjust the paths to your package layout. Each matrix entry names a schema file and a glob of JSON files; `GrantBirki/json-yaml-validate` validates them on every pull request and comments on failures — no database, no credentials. The SchemaSmith repository's own `.github/workflows/validate-demo-schemas.yml` is the same pattern at production scale across all four engines.

## Cross-platform

The workflow is identical on all four engines. Only the identifier quoting and native type spellings differ (`dbo` schema and `NVARCHAR` on SQL Server; lowercase `public` and `varchar` on PostgreSQL; backtick-quoted identifiers on MySQL and MariaDB). The switch names, the schema filenames, the CI action, and the pass/fail behavior are the same everywhere.
