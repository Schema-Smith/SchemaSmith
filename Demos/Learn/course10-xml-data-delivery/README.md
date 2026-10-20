# Course 10 bonus recipe — XML data delivery, on purpose

<!-- TRAINING-RELEASE-PIN 2.8.0 (JSON delivery below compatibility level 130): delete the "On 2.7.x" callouts, bump the pre-flight to 2.8.0 -->
<!-- TRAINING-RELEASE-PIN #573 (JSON extract at compatibility level 100): delete the Part 4 known-issue note and show the no-switch extract from learn_2008 -->

Course 10's oldest SQL Server tier (`learn_2008`, compatibility level 100) is where
SchemaSmith's own model ingest switches from JSON to XML, because `OPENJSON` needs level 130
(see [Module 4](../course10-module-04/README.md)). From 2.8 your seed data crosses the same cliff
the same way: a plain JSON `DataDelivery` aimed at a below-130 database is converted to the XML row
shape as it is read, and all its rows land. So this recipe is about the one knob that is still yours,
`DataDelivery.ContentEncoding`, and when declaring `"Xml"` is the right call now that it isn't needed
for reach.

There's only **one product** here, `RefData`, and its `dbo.CountryCode` table. `sqlserver/json/`
and `sqlserver/package/` are NOT two products — they're the *same* `RefData` product with its seed
data in two encodings: a default JSON `DataDelivery`, and the same delivery declared
`"ContentEncoding": "Xml"` with an XML payload file.

> **Engine floor:** this lab deploys into the `learn_2022` tier, which is a database at **compatibility level 160** — a level only **SQL Server 2022+** offers. On an older engine [`course10-setup`](../course10-setup/README.md) cannot provision that tier and the lab stops with *"No database targets discovered"*. The sandbox default is 2022, so this only applies if you have pointed `MSSQL_IMAGE` at an older release. The other engines in this lab are unaffected.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  `learn_2022` (compat 160) and `learn_2008` (compat 100) on the shared SQL Server instance
  (`localhost,11433`), and current-tier PostgreSQL 16 (`localhost:15432`). MySQL (`localhost:13306`)
  and MariaDB (`localhost:13307`) use the sandbox's `learn` database.
- `schemaquench --version` answers **2.7.0 or later**. Part 1 describes 2.8; on 2.7.x read its callout.

## Part 1 — a JSON delivery lands on the floor

`sqlserver/json` is the `RefData` product as you'd naturally author it: `dbo.CountryCode` with a
plain JSON `DataDelivery` — no `ContentEncoding`, so it defaults to `Json` — pointed at a JSON
`.tabledata` array whose `JP` row carries `"Continent": null`. Deploy it to `learn_2008`:

```bash
# from course10-xml-data-delivery/sqlserver
schemaquench --ConfigFile:quench.settings.json.legacy.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2008]   [learn_2008] detected SQL Server version 16.0.4260.1 (compatibility level 100)
[localhost,11433].[learn_2008]   [learn_2008] model ingest encoding: Xml (auto)
[localhost,11433].[learn_2008]   Delivering table data
[localhost,11433].[learn_2008]     Delivering dbo.CountryCode (converted to Xml for compatibility level 100)
[localhost,11433].[learn_2008] Successfully Quenched
```

Exit code `0`. Two lines tell the story. The model ingest switched to XML for this database, as in
Module 4, and the delivery line says the same happened to your data: the JSON file was converted to
the XML row shape as it was read and went through the XML merge. Read the rows back:

```bash
# from course10-xml-data-delivery
../lab-sql.sh sqlserver learn_2008 "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
```

```
Code  Name            Continent
----  --------------  -------------
CA    Canada          North America
DE    Germany         Europe
GB    United Kingdom  Europe
JP    Japan           NULL
US    United States   North America
```

Five rows, on a tier where `OPENJSON` can't even parse. `JP`'s JSON `null` arrived as `NULL`.

> **On 2.7.x** this delivery doesn't land. Under the default `warn` policy it is skipped —
> `[SKIPPED - requires compatibility level 130 for JSON delivery] JSON data delivery for dbo.CountryCode requires SQL Server compatibility level 130 (target is at 100); re-encode this delivery as XML ("ContentEncoding": "Xml") to deploy it on a legacy-compat target.`
> — the run still exits `0`, and the table is empty. Under `fail` (next step) the run stops with
> that message and exits `2`. Upgrade, or deploy the XML package from Part 3.

Now set the policy to `fail`. `quench.settings.json.legacy-fail.json` is identical except it sets
`"UnsupportedFeaturePolicy": "fail"` under `Target`:

```bash
# from course10-xml-data-delivery/sqlserver
schemaquench --ConfigFile:quench.settings.json.legacy-fail.json --LogPath:"$PWD/logs"
grep -E '"outcome"|"unsupportedDowngrade"' "logs/SchemaQuench - Summary.json"
```

```
    "outcome": "Success",
      "outcome": "Success",
  "unsupportedDowngrade": null
```

The same lines as before, exit `0`, and the summary records no downgrade. The policy has nothing to
say because nothing was degraded: a conversion loses nothing, so there is nothing to warn about or
refuse.

## Part 2 — one JSON package, every tier

Deploy the same `sqlserver/json` package to `learn_2022` (compat 160):

```bash
# from course10-xml-data-delivery/sqlserver
schemaquench --ConfigFile:quench.settings.json.modern.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2022]   Delivering table data
[localhost,11433].[learn_2022]     Delivering dbo.CountryCode
[localhost,11433].[learn_2022] Successfully Quenched
```

No conversion here: the target reads JSON with `OPENJSON` directly. The readback is identical:

```bash
# from course10-xml-data-delivery
../lab-sql.sh sqlserver learn_2022 "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
```

One JSON package covers the whole SQL Server fleet. You don't keep an XML copy for the laggard.

## Part 3 — when you'd still declare `Xml`, and what the file looks like

`ContentEncoding` describes the file. It is never inferred and never rewritten: a `Json` file
below level 130 is converted on read, and an `Xml` file is shipped as written on every tier. So
declare `Xml` when the file **is** XML:

- **DataTongs wrote it from a SQL Server source older than 2017.** There, an unset
  `DeliveryEncoding` extracts XML (Part 4).
- **Someone handed you a file in that shape**, or a consumer of the file wants XML.

`sqlserver/package` is the same `RefData` product with that declaration:

```json
"DataDelivery": {
  "ContentFile": "data/dbo.CountryCode.xml.tabledata",
  "ContentEncoding": "Xml",
  "MergeType": "Insert/Update",
  "MatchColumns": "Code"
}
```

pointed at `data/dbo.CountryCode.xml.tabledata`, which carries the same five rows in the
documented XML shape — one `<c>` element per column, named by an `n` attribute:

```xml
<rows>
<row><c n="Code">US</c><c n="Name">United States</c><c n="Continent">North America</c></row>
<row><c n="Code">CA</c><c n="Name">Canada</c><c n="Continent">North America</c></row>
<row><c n="Code">GB</c><c n="Name">United Kingdom</c><c n="Continent">Europe</c></row>
<row><c n="Code">DE</c><c n="Name">Germany</c><c n="Continent">Europe</c></row>
<row><c n="Code">JP</c><c n="Name">Japan</c></row>
</rows>
```

Notice the last row: `JP` has no `<c n="Continent">` element at all. **An absent `<c>` is
`NULL`** — not an empty string, not a parse error. That's the documented contract for the XML
encoding (it's also what Part 1's conversion did with `JP`'s JSON `null`).

Deploy the XML package to both tiers:

```bash
# from course10-xml-data-delivery/sqlserver
schemaquench --ConfigFile:quench.settings.legacy.json --LogPath:"$PWD/logs"
schemaquench --ConfigFile:quench.settings.modern.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2008]   Delivering table data
[localhost,11433].[learn_2008]     Delivering dbo.CountryCode
[localhost,11433].[learn_2008] Successfully Quenched
```

Exit code `0` on each, with no conversion note: the file is already XML. Read both tiers back and
you get the same five rows as Parts 1 and 2, `JP` still `NULL`:

```bash
# from course10-xml-data-delivery
../lab-sql.sh sqlserver learn_2008 "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
../lab-sql.sh sqlserver learn_2022 "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
```

## Part 4 — where an XML file comes from

Five country codes are fine to type. A real reference table, two hundred rows and fifteen columns,
is not. **DataTongs writes the file**, told which encoding you want:

```bash
# from course10-xml-data-delivery/sqlserver
datatongs --ConfigFile:tongs.settings.json --DeliveryEncoding:Xml
```

```
    Casting data for: dbo.CountryCode
    Extracted 5 row(s) from dbo.CountryCode.
    Writing contents to : ./extracted\dbo.CountryCode.tabledata
```

Open `extracted/dbo.CountryCode.tabledata`:

```xml
<rows><row><c n="Code">CA</c><c n="Continent">North America</c><c n="Name">Canada</c></row><row><c n="Code">DE</c><c n="Continent">Europe</c><c n="Name">Germany</c></row><row><c n="Code">GB</c><c n="Continent">Europe</c><c n="Name">United Kingdom</c></row><row><c n="Code">JP</c><c n="Name">Japan</c></row><row><c n="Code">US</c><c n="Continent">North America</c><c n="Name">United States</c></row></rows>
```

**Find the `JP` row.** It has a `Code` and a `Name` and no `Continent` element — the convention
Part 3 told you to follow, emitted without being asked. The hand-written payload was written to
match the extractor, not the other way round.

**Without the switch, DataTongs chooses by the source server's version.** A SQL Server source older
than 2017 extracts XML, because the JSON extraction needs `FOR JSON` and `STRING_AGG`, and it says so:

```
  Delivery data is extracted as Xml: the source is SQL Server <version>, and the Json extraction needs SQL Server 2017. Set DeliveryEncoding to choose.
```

Ask for `Json` against such a source and the run is refused with exit `2`:

```
FAILED: DeliveryEncoding is Json, but the source is SQL Server <version>, and the Json extraction needs SQL Server 2017 or later. Set DeliveryEncoding to Xml, or leave it unset to choose automatically.
```

The sandbox can't show those two — every tier in it is one SQL Server 2022 instance. It does show
the other half of the rule: the choice follows the **server**, not the database's compatibility
level. `tongs.settings.legacy.json` extracts from `learn_2008`, and with no switch it would pick JSON,
because the server is 2022. That's the case to pass the switch:

```bash
# from course10-xml-data-delivery/sqlserver
datatongs --ConfigFile:tongs.settings.legacy.json --DeliveryEncoding:Xml
```

```
    Casting data for: dbo.CountryCode
    Extracted 5 row(s) from dbo.CountryCode.
    Writing contents to : ./extracted\dbo.CountryCode.tabledata
```

> **Known issue ([#573](https://github.com/Schema-Smith/SchemaSmith/issues/573)):** a JSON extract
> from a database at compatibility level 100 fails — `Error processing table dbo.CountryCode:
> Incorrect syntax near '('.`, exit `2` — so leaving the switch off here fails. Until it's fixed,
> pass `--DeliveryEncoding:Xml` when you extract from a database below level 110.

`--DeliveryEncoding` is the switch that decides what DataTongs writes. Don't reach for
`Source:CompatEncoding` here — it sounds related, but it only chooses how DataTongs reads a SQL
Server source's catalog while building its output; the files and scripts come out the same either way.

### Close the loop

Extract the XML again, copy it over the payload in `package/Templates/Main/data/`, and redeploy to
the compat-100 tier. The rows land unchanged, `JP` still NULL:

```bash
# from course10-xml-data-delivery/sqlserver
datatongs --ConfigFile:tongs.settings.json --DeliveryEncoding:Xml
cp extracted/dbo.CountryCode.tabledata package/Templates/Main/data/dbo.CountryCode.xml.tabledata
schemaquench --ConfigFile:quench.settings.legacy.json --LogPath:"$PWD/logs"
```

That's the round trip closed: **extract as XML → deploy as XML**, no hand-editing anywhere in it.

## Part 5 — the other engines

PostgreSQL, MySQL and MariaDB have no compatibility cliff: each shreds JSON at every version
SchemaSmith supports. They accept an XML file too, so a file DataTongs wrote from an old SQL Server
deploys there unchanged — but declaring `Xml` buys nothing on them, and on MySQL and MariaDB it costs
something.

| Engine | How an `Xml` file is shredded |
| --- | --- |
| SQL Server | Natively, `.nodes()`/`.value()` |
| PostgreSQL | Natively, `xmltable()` |
| MySQL / MariaDB | Converted to JSON once, up front, then through the ordinary JSON row source |

`postgres/package`, `mysql/package` and `mariadb/package` declare `countrycode` with the same
`ContentEncoding: "Xml"` block and lowercase names. Deploy each from its engine folder:

```bash
# from course10-xml-data-delivery/postgres
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```bash
# from course10-xml-data-delivery/mysql
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```bash
# from course10-xml-data-delivery/mariadb
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```
[localhost].[learn]         Create new table public.countrycode
[localhost].[learn]         Add missing Constraint public.countrycode.pk_countrycode
[localhost].[learn]       Add missing Product ownership to tables
[localhost].[learn]   Delivering table data
[localhost].[learn]     Delivering public.countrycode
[localhost].[learn] Successfully Quenched
```

Exit code `0` on each (PostgreSQL shown). Read them back from the lab folder:

```bash
# from course10-xml-data-delivery
../lab-sql.sh postgres learn "SELECT * FROM public.countrycode ORDER BY code;"
../lab-sql.sh mysql learn "SELECT * FROM countrycode ORDER BY code;"
../lab-sql.sh mariadb learn "SELECT * FROM countrycode ORDER BY code;"
```

```
 code |      name      |   continent
------+----------------+---------------
 CA   | Canada         | North America
 DE   | Germany        | Europe
 GB   | United Kingdom | Europe
 JP   | Japan          |
 US   | United States  | North America
```

The same five rows on every engine, `JP`'s continent NULL rather than the string `"NULL"`.

DataTongs writes the same dialect from these engines: it extracts their normal JSON and converts it,
so the file doesn't betray which engine it came from.

```bash
# from course10-xml-data-delivery/postgres
datatongs --ConfigFile:tongs.settings.json --DeliveryEncoding:Xml
```

```xml
<rows><row><c n="code">CA</c><c n="continent">North America</c><c n="name">Canada</c></row>…
```

Same shape, lowercase names because that's what PostgreSQL calls those columns.

**The cost on MySQL and MariaDB.** DataTongs can also write merge *scripts* alongside the content
file, and by default those scripts are tokenized: they read the content file with `JSON_TABLE` when
they're deployed. Nothing converts an XML file on that path, so DataTongs refuses the combination.
`mysql/tongs.settings.scripts.json` asks for scripts:

```bash
# from course10-xml-data-delivery/mysql
datatongs --ConfigFile:tongs.settings.scripts.json --DeliveryEncoding:Xml
```

```
    Writing contents to : ./extracted\countrycode.tabledata
    Cannot wire the '{{countrycode.tabledata}}' token for countrycode: ContentPath './extracted' is not within a template. The generated script will not deploy without manual ScriptTokens configuration.
  Error processing table countrycode: DeliveryEncoding is Xml, but MySQL and MariaDB merge scripts read their content file with JSON_TABLE, so tokenized scripts cannot deploy an Xml file. Use DeliveryEncoding Json, or set ShouldCast:TokenizeScripts to false to embed the data in each script.
=== DataTongs Summary ===
  Tables processed: 0
  Errors: 1
```

Exit code `2`: the content file is written, the script is refused. (The `Cannot wire` warning is
about where this lab extracts to — `extracted/` sits outside any template, so even a JSON script
would need its token wired by hand. Extract into a template's folder and DataTongs wires it.) A
`DataDelivery` is unaffected, as the deploys above showed.

> **On 2.7.x** there's no refusal: DataTongs writes the XML file and the scripts, and each script
> fails with `Invalid JSON text` when it's deployed.

`mysql/tongs.settings.json` writes content files only. Leave the switch off and you get JSON:

```bash
# from course10-xml-data-delivery/mysql
datatongs --ConfigFile:tongs.settings.json
head -c 80 extracted/countrycode.tabledata; echo
```

```
[{"code": "CA", "name": "Canada", "continent": "North America"}, {"code": "DE", 
```

**So when should you declare `Xml`?** When the file is XML. If you're producing the file yourself
from a 2017-or-later SQL Server, PostgreSQL, MySQL or MariaDB source, omit `ContentEncoding` and
ship JSON: it reaches every tier now, and it's easier to read in a diff.

### Two things to know before you rely on it

**Column order in the file is not stable, and doesn't need to be.** SQL Server and PostgreSQL emit
columns alphabetically; MySQL and MariaDB emit them in table order. Diff two extracts from
different engines and the columns move around. It doesn't matter: every value is addressed by its
`n` attribute, never by position, so the shred reads `n="Continent"` wherever it sits. Don't build
anything that depends on the order.

**One real gap — spatial columns from PostgreSQL and MySQL.** A `geometry`/`geography` column
normally extracts as WKT plus a companion `<c n="Column.STSrid">` carrying its spatial reference
system, and the SQL Server shred needs that companion to reconstruct the value exactly. PostgreSQL's
and MySQL's extraction doesn't capture the SRID at all, so a payload from either carries the WKT
alone. **This is not an XML thing** — a JSON payload from those engines loses it the same way; XML
inherited the gap rather than caused it.

It usually self-heals: a *typed* column (`geometry(Point,4326)`) coerces the incoming value to its
declared SRID, so the round trip is lossless, and a MySQL column restricted with an `SRID` attribute
does the same. Where it bites is an **untyped** spatial column holding a non-default SRID — that
comes back as SRID 0, coordinates intact, no error. Right numbers, wrong reference system, silently.
Extract spatial data from SQL Server, type the destination column, or set the SRID yourself. Every
other column type — including binary, dates, booleans and NULLs — is fully portable.

## Cleanup

```bash
# from course10-xml-data-delivery
../lab-sql.sh sqlserver learn_2022 "DROP TABLE IF EXISTS dbo.CountryCode;"
../lab-sql.sh sqlserver learn_2008 "DROP TABLE IF EXISTS dbo.CountryCode;"
../lab-sql.sh postgres learn "DROP TABLE IF EXISTS public.countrycode;"
../lab-sql.sh mysql learn "DROP TABLE IF EXISTS countrycode;"
../lab-sql.sh mariadb learn "DROP TABLE IF EXISTS countrycode;"
```

## What's in here

| Path | Purpose |
| --- | --- |
| `sqlserver/json/` | Product `RefData` with a default-JSON `DataDelivery`. Converted to XML on read below level 130 (Part 1), read with `OPENJSON` above it (Part 2). |
| `sqlserver/package/` | The *same* product `RefData` with `ContentEncoding: "Xml"` and an XML payload (Part 3). Not a second product; SchemaSmith's ownership guard would reject two products claiming the same table. |
| `sqlserver/quench.settings.json.legacy.json` / `…json.legacy-fail.json` / `…json.modern.json` | The JSON package to `learn_2008` under `warn` and `fail`, and to `learn_2022`. |
| `sqlserver/quench.settings.legacy.json` / `…modern.json` | The XML package to `learn_2008` (compat 100) and `learn_2022` (compat 160). |
| `sqlserver/tongs.settings.json` / `…legacy.json` | Part 4 — DataTongs extraction of `CountryCode` from `learn_2022` and from `learn_2008`. Output lands in `extracted/` and is not tracked. |
| `postgres/package/`, `mysql/package/`, `mariadb/package/` | The same XML delivery on each of the other engines (Part 5). |
| `postgres/tongs.settings.json` | DataTongs extraction from PostgreSQL. |
| `mysql/tongs.settings.json` / `mariadb/tongs.settings.json` / `mysql/tongs.settings.scripts.json` | DataTongs extraction, content files only; the `scripts` file also writes merge scripts, for the tokenized-script refusal in Part 5. |

Editor JSON-schema files (`.json-schemas/`) for every package are generated by the CLI's
`--WriteSchemasOnly` switch and are not hand-authored.

## Up next

Back to [Module 4](../course10-module-04/README.md) if you haven't read it yet — it's the
companion story: the engine's own model ingest makes the same JSON-vs-XML choice for its model,
with no flag and no package change.
