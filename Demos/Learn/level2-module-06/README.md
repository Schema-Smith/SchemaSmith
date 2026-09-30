# Course 2, Module 6 — Custom properties: teach the tools your metadata (lab)

Goal: attach **your own metadata** to schema objects and have the tools act on it. You'll tag a
`Customer` table and its columns with governance metadata (an `OwningTeam` on the table, a
`Classification` on columns), then deploy a script that reads the whole model — your metadata
included — through the `{{TableSchema}}` system token and **builds a self-maintaining `DataCatalog`
from it**. Change a tag, re-deploy, and the catalog re-derives itself.

Custom properties live in an open **`Extensions`** bag on any component (table, column, index, FK,
check constraint). They're consumed two ways:

- **As tokens** in that component's expression fields — `{{PropName}}` from the component's own
  `Extensions`, `{{Table.PropName}}` from the parent table — usable in `ShouldApplyExpression`,
  `Default`, `CheckExpression`, `FilterExpression`.
- **Via `{{TableSchema}}`** (Module 4) — the system token serializes the entire model *including*
  `Extensions`, so a deploy-time script can read your metadata and act on it. This lab uses this path.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` and a
`deploy.settings.json`.

## Before you start

- The [sandbox](../docker) is up (`docker compose up -d`) and verified (all four engines `PASS`).
- The CLI is on your PATH (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later).

## Step 1: Look at the metadata

Open `<engine>/Package/Templates/Main/Tables/…Customer.json` (SQL Server shown):

```json
"Extensions": { "OwningTeam": "Identity" },
"Columns": [
  { "Name": "CustomerId",  "DataType": "INT" },
  { "Name": "Email",       "DataType": "NVARCHAR(256)", "Extensions": { "Classification": "PII" } },
  { "Name": "Ssn",         "DataType": "CHAR(11)",      "Extensions": { "Classification": "PII" } },
  { "Name": "DisplayName", "DataType": "NVARCHAR(128)", "Extensions": { "Classification": "Internal" } }
]
```

`OwningTeam` is table-level metadata; `Classification` is column-level. The `DataCatalog` table has
*no* metadata — it's the catalog the metadata flows into.

## Step 2: See the script that reads it

Open `Templates/Main/After Scripts/Build Data Catalog [ALWAYS].sql`. It reads `{{TableSchema}}`
(the whole model, Extensions included), walks tables → columns, and rebuilds `DataCatalog`: one row
per column of any table carrying an `OwningTeam` tag, recording that column's `Classification` and the
table's `OwningTeam`. Note the JSON paths — `'$.Extensions.OwningTeam'`, `'$.Extensions.Classification'`
— reading the exact keys you put in your table JSON. The `[ALWAYS]` tag re-runs it every deploy, and
it rebuilds from scratch so the catalog can't drift.

## Step 3: Deploy and read the catalog

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

```bash
# SQL Server (from a SQL client):
#   SELECT TableName, ColumnName, ISNULL(Classification,'(none)') AS Classification, OwningTeam
#   FROM dbo.DataCatalog ORDER BY ColumnName;
../lab-sql.sh postgres learn "SELECT tablename, columnname, COALESCE(classification,'(none)') AS classification, owningteam FROM public.datacatalog ORDER BY columnname"
```

```
TableName | ColumnName  | Classification | OwningTeam
Customer  | CustomerId  | (none)         | Identity
Customer  | DisplayName | Internal       | Identity
Customer  | Email       | PII            | Identity
Customer  | Ssn         | PII            | Identity
```

The metadata you declared in JSON is now a queryable data dictionary. `DataCatalog` isn't listed —
it has no `OwningTeam` tag, so the walk skips it.

## Step 4: The aha — change a tag, re-deploy

Edit `DisplayName`'s `Classification` from `Internal` to `Public`, then re-deploy:

```bash
schemaquench --ConfigFile:deploy.settings.json
```

```
Customer | DisplayName | Public | Identity
```

You didn't touch `DataCatalog` — you changed the metadata on the column, and the catalog re-derived
itself from the model on the next quench. Add a `Classification` to `CustomerId`, or tag a second
table with an `OwningTeam`, and it appears on the next deploy. The dictionary maintains itself because
it's computed from your declared model.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| JSON walk | `OPENJSON` + `CROSS APPLY` | `jsonb_array_elements` | `JSON_TABLE` + `NESTED PATH` |
| Object names in `{{TableSchema}}` | bare (`Customer`) | folded lowercase (`customer`) | backticked (`` `Customer` ``) — stripped with `REPLACE` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The metadata you author and the JSON keys you read (`$.Name`, `$.Extensions.Classification`) are
identical across engines. Only the dialect's JSON-shredding syntax differs.

## The principle

Your schema knows the shape of your data; custom properties let it carry what the data *means* to
your team. Attach metadata in an `Extensions` bag on any object, versioned with the schema and
preserved through extraction. Consume it as a token inside an expression, or read the whole model —
Extensions and all — through `{{TableSchema}}` in a deploy-time script. A data catalog, a masking
pass, an audit-trigger generator: all driven by metadata that lives on the object and can't drift,
because the tools re-read it from the one source of truth on every deploy.
