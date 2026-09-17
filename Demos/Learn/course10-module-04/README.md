# Course 10 · Module 4 — The oldest tier

Module 3 gave you two shapes of one query and a gate to pick between them. This module goes
all the way down to the **oldest tier in the fleet** — a SQL Server database so far back that
`OPENJSON` won't even parse — and shows what SchemaSmith does about it automatically, and
where the one real choice lands in **your** hands.

Two moving parts, and it's worth keeping them straight:

1. **The engine's own model ingest switches encoding by itself.** SchemaSmith's built-in
   `SchemaSmith.TableQuench` reads a serialized model payload to deploy your tables. On a modern
   tier it reads that payload as JSON (`OPENJSON`); below SQL Server database compatibility level
   130 it reads the **same** payload as XML, because `OPENJSON` parse-errors down there. Same
   proc name, same package, no flag — the engine swaps the body at kindle time. You never see it.
   The knob that governs it, `Target:CompatEncoding` (`auto｜legacy｜modern`, default `auto`),
   is SQL-Server-only and almost never touched.

2. **When you shred a model-payload token in your *own* SQL, the choice is yours.** The
   `{{TableSchema}}`-family tokens each ship an always-present **XML twin** — `{{TableXml}}`,
   `{{IndexedViewXml}}`, `{{MaterializedViewXml}}`. A script that shreds `{{TableSchema}}` with
   `OPENJSON` works on the modern tier and parse-errors on the old one. So you pair two variants
   behind Module 3's folder gate: `OPENJSON` on `{{TableSchema}}` where `{{CompatibilityLevel}}
   >= 130`, `.nodes()`/`.value()` on `{{TableXml}}` where `< 130`. **That is Course 10's gating
   pattern, turned on the reader's own code.**

The cliff that matters here is **compat 130** — the level `OPENJSON` needs — not 160 (Module 3's
axis) and not the server binary. On this fleet: `learn_2022` (compat 160) and `learn_2016` (compat
130) both clear it and take the JSON shred; `learn_2008` (compat 100) does not and takes the XML
shred.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  the three SQL Server tiers (`learn_2022` / `learn_2016` / `learn_2008` on `localhost,11433`)
  and the floor engines from the `mixed-fleet` profile — PostgreSQL 12 (`15433`), MySQL 5.7
  (`13316`), MariaDB 10.2 (`13317`). This module deploys into those; it does not create them.
- `schemaquench --version` answers **2.4.0** or later (for the encoding switch and the XML twin
  tokens).

---

## Part A — deploy the package (the engine's encoding switch is invisible)

<!-- TRAINING-RELEASE-PIN #encoding-log -- on 2.7.0, SchemaQuench logs "[db] model ingest encoding: Json|Xml" after
the "detected SQL Server version" line. Retitle Part A (the switch is automatic, no longer invisible), replace
"nothing in the log or the result tells you which" with the new log line on both tiers, and adjust the intro's
"You never see it". -->

The `sqlserver/package` declares two tables — `dbo.Widget` (a model worth serializing) and
`dbo.TableCatalog` (an audit table Part B fills). Deploying it to any tier looks identical from
the outside. The `learn_2008` deploy reads its model payload as XML internally; the `learn_2022`
deploy reads it as JSON — and nothing in the log or the result tells you which, because it does
not matter. The tables land the same way on both.

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD/logs"     # macOS / Linux
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD\logs"     # Windows
```

```
[localhost,11433].[learn_2022]   Skipping folder 'Shred/Legacy' — ShouldApplyExpression evaluated false
[localhost,11433].[learn_2022]     Quenching Shred/Modern\PopulateTableCatalog [ALWAYS].sql
[localhost,11433].[learn_2022] Successfully Quenched
```

That the same package deploys clean to `learn_2008` at compat 100 — where the engine quietly
used XML ingest — is the point. You'll see that deploy in Part C.

---

## Part B — the choice that IS yours: shredding the model your own way

The package's `Shred/Modern` and `Shred/Legacy` folders are a matched pair, folder-gated on
`{{CompatibilityLevel}}`. Each runs an `After` script that shreds the deployed model and writes
one row per column into `dbo.TableCatalog`, tagging the `Encoding` it used — so the audit table
itself proves which variant fired.

**Modern** (`ShouldApplyExpression: {{CompatibilityLevel}} >= 130`) shreds the JSON token:

```sql
DECLARE @model NVARCHAR(MAX) = N'{{TableSchema}}';   -- resolves to a JSON array [{...},{...}]
DELETE FROM dbo.TableCatalog;   -- [ALWAYS] script: clear then repopulate, so a re-run stays idempotent
INSERT INTO dbo.TableCatalog (TableName, ColumnName, IsNullable, Encoding)
SELECT tbl.[Name], col.[ColumnName], col.[IsNullable], N'JSON (OPENJSON)'
FROM OPENJSON(@model) WITH ([Name] NVARCHAR(128) '$.Name', [Columns] NVARCHAR(MAX) '$.Columns' AS JSON) AS tbl
CROSS APPLY OPENJSON(tbl.[Columns]) WITH ([ColumnName] NVARCHAR(128) '$.Name', [IsNullable] BIT '$.Nullable') AS col;
```

**Legacy** (`ShouldApplyExpression: {{CompatibilityLevel}} < 130`) shreds the XML twin:

```sql
DECLARE @model xml = N'{{TableXml}}';                -- resolves to <Tables><Table>...</Table></Tables>
DELETE FROM dbo.TableCatalog;   -- [ALWAYS] script: clear then repopulate, so a re-run stays idempotent
INSERT INTO dbo.TableCatalog (TableName, ColumnName, IsNullable, Encoding)
SELECT t.n.value('(Name/text())[1]', 'nvarchar(128)'),
       c.col.value('(Name/text())[1]', 'nvarchar(128)'),
       CONVERT(BIT, CASE LOWER(c.col.value('(Nullable/text())[1]', 'varchar(8)')) WHEN 'true' THEN 1 WHEN 'false' THEN 0 END),
       N'XML (.nodes/.value)'
FROM @model.nodes('/Tables/Table') AS t(n)
CROSS APPLY t.n.nodes('Columns') AS c(col);
```

### `learn_2022` (compat 160) ran the Modern shred

The deploy in Part A already ran the `After` scripts. `Shred/Modern` applied; `Shred/Legacy` was
skipped. Read the catalog back:

```
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2022 -Q "SELECT TableName, ColumnName, IsNullable, Encoding FROM dbo.TableCatalog ORDER BY TableName, CatalogId;"
```

```
TableName     ColumnName   IsNullable  Encoding
------------  -----------  ----------  ---------------
TableCatalog  CatalogId    0           JSON (OPENJSON)
TableCatalog  TableName    0           JSON (OPENJSON)
TableCatalog  ColumnName   0           JSON (OPENJSON)
TableCatalog  IsNullable   1           JSON (OPENJSON)
TableCatalog  Encoding     0           JSON (OPENJSON)
Widget        WidgetId     0           JSON (OPENJSON)
Widget        Name         0           JSON (OPENJSON)
Widget        IsActive     1           JSON (OPENJSON)
Widget        Notes        1           JSON (OPENJSON)
```

### `learn_2008` (compat 100) ran the Legacy shred

Deploy to the oldest tier. `OPENJSON` would parse-error here, so `Shred/Modern` is gated off and
`Shred/Legacy` shreds the XML twin instead:

```
schemaquench --ConfigFile:quench.settings.2008.json --LogPath:"$PWD/logs"
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2008 -Q "SELECT TableName, ColumnName, IsNullable, Encoding FROM dbo.TableCatalog ORDER BY TableName, CatalogId;"
```

```
[localhost,11433].[learn_2008]   Skipping folder 'Shred/Modern' — ShouldApplyExpression evaluated false
[localhost,11433].[learn_2008]     Quenching Shred/Legacy\PopulateTableCatalog [ALWAYS].sql
[localhost,11433].[learn_2008] Successfully Quenched

TableName     ColumnName   IsNullable  Encoding
------------  -----------  ----------  --------------------
TableCatalog  CatalogId    0           XML (.nodes/.value)
TableCatalog  TableName    0           XML (.nodes/.value)
TableCatalog  ColumnName   0           XML (.nodes/.value)
TableCatalog  IsNullable   1           XML (.nodes/.value)
TableCatalog  Encoding     0           XML (.nodes/.value)
Widget        WidgetId     0           XML (.nodes/.value)
Widget        Name         0           XML (.nodes/.value)
Widget        IsActive     1           XML (.nodes/.value)
Widget        Notes        1           XML (.nodes/.value)
```
Row-for-row identical to the compat-160 readback — only the `Encoding` tag differs.

Same rows, same `IsNullable` values — the two encodings carry the identical model. Only the
`Encoding` tag differs, proving which shred your gate fired on which tier.

> `learn_2016` sits at compat 130. Since `130 >= 130`, it takes the **Modern** shred exactly like
> `learn_2022` — the cliff is at 130, not 160. Deploy `quench.settings.2016.json` if you want to
> watch the boundary land on the JSON side.

### The footgun the XML path teaches

Look at the `IsNullable` conversion in the Legacy shred. In JSON, `Nullable` is a real boolean and
`OPENJSON ... WITH ([IsNullable] BIT '$.Nullable')` reads it straight into a `BIT`. In XML **every
scalar is text**, so the same flag arrives as the literal string `'true'`/`'false'`. A direct
`CAST('true' AS BIT)` errors:

```
Msg 245, Level 16, State 1
Conversion failed when converting the varchar value 'true' to data type bit.
```

So the XML shred routes the flag through an explicit `CASE LOWER(...) WHEN 'true' THEN 1 WHEN
'false' THEN 0 END` before `CONVERT(BIT, ...)`. It's the same conversion SchemaSmith's own built-in
XML ingest uses. When you hand-write a legacy shred, this is the tax the text encoding charges.

---

## Part C — one package, every floor

The JSON-vs-XML *choice* is SQL-Server-only, but the promise that **one package deploys to the
floor of every engine** is not. Deploy the light single-table package to each engine's oldest
supported tier and watch them all take it — the encoding, wherever it applies, is automatic.

```
cd ../sqlserver && schemaquench --ConfigFile:quench.settings.2008.json --LogPath:"$PWD/logs"   # SQL Server compat 100
cd ../postgres  && schemaquench --ConfigFile:quench.settings.pg12.json  --LogPath:"$PWD/logs"   # PostgreSQL 12
cd ../mysql     && schemaquench --ConfigFile:quench.settings.json       --LogPath:"$PWD/logs"   # MySQL 5.7
cd ../mariadb   && schemaquench --ConfigFile:quench.settings.json       --LogPath:"$PWD/logs"   # MariaDB 10.2
```

```
[localhost,11433].[learn_2008] Successfully Quenched     # SQL Server 2008-floor (compat 100)
[localhost].[learn]            Successfully Quenched     # PostgreSQL 12 (15433)
[localhost].[learn]            Successfully Quenched     # MySQL 5.7 (13316)
[localhost].[learn]            Successfully Quenched     # MariaDB 10.2 (13317)
```

Why the choice doesn't travel: PostgreSQL has had JSON functions since 9.2, so its `{{TableSchema}}`
shreds at every supported version; MySQL and MariaDB below their JSON cliff fall back to
`JSON_EXTRACT` — still JSON. SQL Server is the only engine whose oldest tier can't parse JSON at
all, so it's the only one where the XML twin is a *needed* alternative rather than a portability
convenience. **This is not a parity gap** — it's an accurate reflection of where the encoding cliff
actually exists.

---

## An honest footnote on the legacy round-trip

The XML twin carries the structured model faithfully — schemas, tables, columns, indexes, all of
it. What it does **not** preserve through a SchemaTongs re-extract on the legacy tier is the
free-form `Extensions` bag (arbitrary author-supplied metadata). So don't over-claim a byte-perfect
round-trip on the oldest tier: the schema comes back whole; loose `Extensions` metadata may not.

## What's in here

| Path | Purpose |
| --- | --- |
| `sqlserver/package/Templates/Main/Tables/dbo.Widget.json` | A table with a model worth serializing — mixed nullability, a `BIT` column. |
| `sqlserver/package/Templates/Main/Tables/dbo.TableCatalog.json` | Audit table the shred fills — one row per shredded column, tagged with the `Encoding` used. |
| `sqlserver/package/Templates/Main/Shred/Modern/PopulateTableCatalog [ALWAYS].sql` | `OPENJSON` on `{{TableSchema}}`, folder-gated `{{CompatibilityLevel}} >= 130`. |
| `sqlserver/package/Templates/Main/Shred/Legacy/PopulateTableCatalog [ALWAYS].sql` | `.nodes()`/`.value()` on `{{TableXml}}`, folder-gated `{{CompatibilityLevel}} < 130`. |
| `sqlserver/quench.settings.2022.json` / `…2016.json` / `…2008.json` | Same package to each SQL Server tier (compat 160 / 130 / 100). |
| `postgres/` · `mysql/` · `mariadb/` | Light single-table packages for the "one package, every floor" beat (PG 12 / MySQL 5.7 / MariaDB 10.2). |

## Up next

Module 5 — **retiring the gates.** The oldest tier finally upgrades. Now the gates you built
across this course are debt with a payoff date: you delete the legacy variants, raise
`MinimumVersion` so pre-flight refuses the tier you stopped writing for, and the whole scheme
retires itself — which is exactly what made it a good pattern in the first place.
