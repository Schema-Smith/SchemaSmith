# Course 4, Recipe 9 — Extensions as a source of truth: the data dictionary (lab)

Goal: `Extensions` isn't just an input to gates and defaults — it's an **authoritative metadata store** your
own scripts can turn into real work. Here every table carries business metadata in `Extensions` (table-level
`BusinessDomain` / `DataOwner` / `Description` / `RetentionPolicy`; column-level `BusinessName` /
`SensitivityLevel` / `DataSteward`), and an `[ALWAYS]` script reads the **whole template's model** as JSON —
every table, with all its Extensions — shreds it, and `MERGE`s a queryable **`DataDictionary`** table. It runs
every quench, so the dictionary is always in sync with what the schema files declare. The schema files are the
single source of truth; the dictionary is derived from them and can't drift.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` plus `deploy.settings.json`,
all targeting `cookbook_r9`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup) once — it creates `cookbook_r9`).
- The CLI is on your PATH (`schemaquench --version` → `2.7.0.0` or later).

## Step 1: See the metadata on the tables

`Templates/Main/Tables/dbo.Customer.json` carries business metadata at both levels:

```json
{
  "Schema": "dbo", "Name": "Customer",
  "Extensions": { "BusinessDomain": "Identity", "DataOwner": "identity-team", "RetentionPolicy": "7y" },
  "Columns": [
    { "Name": "Email", "DataType": "NVARCHAR(256)", "Nullable": false,
      "Extensions": { "BusinessName": "Email Address", "SensitivityLevel": "PII", "DataSteward": "privacy-office" } }
  ]
}
```

## Step 2: Deploy — the dictionary builds itself

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

The `[ALWAYS]` script reads the whole-template model token, shreds it (`OPENJSON` on SQL Server,
`jsonb_array_elements` on PostgreSQL, `JSON_TABLE` on MySQL and MariaDB), and populates `DataDictionary` — one row per
column:

```bash
# SQL Server
cd ..            # back to the lab folder
../lab-sql.sh sqlserver cookbook_r9 "SELECT TableName, ColumnName, SensitivityLevel, DataSteward FROM dbo.DataDictionary ORDER BY TableName, ColumnName"
# → Customer/Email PII privacy-office ; SalesOrder/TotalAmount Confidential finance ; …  (6 rows)
```

Now it's queryable metadata: *"show me every PII or Confidential column and who stewards it"* is one `SELECT`.

## Step 3: Change the model — the dictionary follows

Change `Email`'s `SensitivityLevel` from `PII` to `Restricted`, drop the `DisplayName` column from
`Customer.json`, and re-quench. The dictionary updates the changed row and **removes the row for the dropped
column** — the `MERGE` keeps it exactly in step with the model. Put them back and re-quench, and it's whole
again. You never edit `DataDictionary`; it's computed from the declared metadata on every deploy.

## Enforcing the metadata in CI (optional)

The dictionary *derives* from your `Extensions`; to *enforce* their shape — require every column to declare a
`SensitivityLevel` from an approved list, fail the PR when one's missing — is a second pass with a JSON Schema
that locks down the `Extensions` blocks. That's covered end-to-end in
**[Course 6 · Module 3 — CI schema validation & Extensions governance](https://learn.schemasmith.com)** and its
lab (`Demos/Learn/course6-module-03`). Author here; enforce there.

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Read the whole model | `OPENJSON` + `CROSS APPLY` | `jsonb_array_elements` + `CROSS JOIN LATERAL` | `JSON_TABLE` + `NESTED PATH` |
| Reach into Extensions | `'$.Extensions.SensitivityLevel'` | `->'Extensions'->>'SensitivityLevel'` | `PATH '$.Extensions.SensitivityLevel'` |
| Upsert + prune | `MERGE … WHEN NOT MATCHED BY SOURCE THEN DELETE` | `INSERT … ON CONFLICT` + `DELETE … NOT EXISTS` | `INSERT … ON DUPLICATE KEY` + `DELETE … NOT EXISTS` |
| Schema key | `$.Schema` (`dbo`) | `$.Schema` (`public`) | `DATABASE()` — MySQL has no schema namespace |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.* Same `DATABASE()` schema key as MySQL.

Same shape everywhere: read the declared model as JSON, walk tables then columns, reach into `Extensions`, and
keep a derived table in sync. Only the JSON-shredding dialect differs.

> **Authoring note:** token substitution is plain text — it expands even inside SQL comments. Don't write the
> whole-model token's `{{…}}` braces in a comment or it inlines the entire JSON there and breaks the script;
> name it in prose without the braces (as these scripts do).

## The principle

Your schema files already know more than the columns and types — they know the *business*: who owns this data,
how sensitive it is, how long you keep it. Put that knowledge in `Extensions` and it rides with the definition,
version-controlled, reviewed, never in a stale spreadsheet. Then one script turns it into whatever you need —
here a data dictionary, but the same move drives replication topology, obfuscation rules, BI exposure, code
generation. The schema is the single source of truth; everything else is derived from it and can't drift.
