# Course 10 bonus recipe — XML data delivery for the legacy tier

Course 10's oldest SQL Server tier (`learn_2008`, compatibility level 100) is where
SchemaSmith's own model ingest quietly switches from JSON to XML — you never see it, because
it's the *engine's* choice (see [Module 4](../course10-module-04/README.md)). This recipe is
about the one knob that puts the same choice in **your** hands: `DataDelivery.ContentEncoding`.

There's only **one product** here, `RefData`, and its `dbo.CountryCode` table. You'll deploy it
twice: first as it's naturally authored — a default JSON `DataDelivery` — and watch that delivery
hit a wall on the legacy tier; then with one property changed, `"ContentEncoding": "Xml"`, and
watch the identical data land everywhere. `sqlserver/json-attempt/` and `sqlserver/package/` are
NOT two products — they're the *same* `RefData` product, before and after that one-line fix, the
same edit-and-redeploy idiom Module 3 used for gating.

A `DataDelivery` block defaults to JSON, shredded with `OPENJSON` — which needs SQL Server
compatibility level 130+. Below that, `OPENJSON` doesn't degrade gracefully; it parse-errors.
Set `"ContentEncoding": "Xml"` on the delivery and SchemaSmith shreds the payload with the XML
data-type methods (`.nodes()`/`.value()`) instead — a path that works at *every* compatibility
level, including the oldest tier in the fleet.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  `learn_2022` (compat 160) and `learn_2008` (compat 100) on the shared SQL Server instance
  (`localhost,11433`), and current-tier PostgreSQL 16 (`localhost:15432`).
- `schemaquench --version` answers **2.5.0 or later** — this recipe runs on the released CLI, no
  from-source override. 2.5.0 is a true minimum, not a pin: earlier releases rejected
  `ContentEncoding: "Xml"` on every engine except SQL Server, which is what Part 4 turns on.

## Part 1 — the problem: JSON delivery hits the compat-130 cliff

`sqlserver/json-attempt` is the `RefData` product as you'd naturally author it: `dbo.CountryCode`
with a plain JSON `DataDelivery` — no `ContentEncoding`, so it defaults to `Json` — pointed at a
JSON `.tabledata` array. Deploy it to `learn_2008`:

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.json-attempt.json --LogPath:"$PWD/logs"
```

The table itself deploys fine — only the *data delivery* hits the cliff. Under the default
`warn` policy, SchemaQuench skips just that delivery with a clear message and leaves the table
empty, rather than failing the whole run:

<!-- TRAINING-RELEASE-PIN #encoding-log -- on 2.7.0 a "model ingest encoding: Xml" line follows the detected-version line
below; add it to the excerpt. -->
```
[localhost,11433].[learn_2008]   detected SQL Server version 16.0.4260.1 (compatibility level 100)
[localhost,11433].[learn_2008]   Delivering table data
[localhost,11433].[learn_2008]     [SKIPPED - requires compatibility level 130 for JSON delivery] JSON data delivery for dbo.CountryCode requires SQL Server compatibility level 130 (target is at 100); re-encode this delivery as XML ("ContentEncoding": "Xml") to deploy it on a legacy-compat target.
[localhost,11433].[learn_2008] Successfully Quenched
```

Exit code `0` — `warn` skips and the run still succeeds. Row count after: `0` — the table was
created, but the delivery that would have populated it never ran.

Now flip the policy. `quench.settings.json-attempt-fail.json` is identical except it sets
`"UnsupportedFeaturePolicy": "fail"` under `Target`. Same package, same target, same JSON
delivery — this time SchemaQuench aborts the run instead of skipping:

```
schemaquench --ConfigFile:quench.settings.json-attempt-fail.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2008] FAILED to quench:
JSON data delivery for dbo.CountryCode requires SQL Server compatibility level 130 (target is at 100); re-encode this delivery as XML ("ContentEncoding": "Xml") to deploy it on a legacy-compat target.
[localhost,11433].[learn_2008] *** FAILED [Template:Main] ***
Template 'Main' had 1 failed work unit(s)
```

Exit code `2`.

Both outcomes are the built-in degrade behavior for a JSON-encoded delivery aimed at a
below-130 target — `warn` (the default) skips the delivery and lets the rest of the deploy
proceed; `fail` aborts before any deployment work begins. Neither one lands the reference data
on the legacy tier. That's the fix this recipe teaches.

## Part 2 — the fix: `ContentEncoding: "Xml"`

`sqlserver/package` is the *same* `RefData` product, redeployed with one change: `dbo.CountryCode`
is unchanged, but its `DataDelivery` is:

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
encoding, and it's the row this recipe uses to prove it.

Deploy the XML package to `learn_2008`:

```
schemaquench --ConfigFile:quench.settings.legacy.json --LogPath:"$PWD/logs"
```

This time the delivery isn't gated on anything — `.nodes()`/`.value()` shredding works at
compatibility level 100, so all five rows land, including `JP` with a `NULL` continent:

```
[localhost,11433].[learn_2008]   Delivering table data
[localhost,11433].[learn_2008]     Delivering dbo.CountryCode
[localhost,11433].[learn_2008] Successfully Quenched
```

Exit code `0`. Read the rows back and confirm the `NULL`:

```
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2008 -Q "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
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

`JP.Continent` is `NULL` — the absent-`<c>` contract, holding on a tier `OPENJSON` can't even
parse. That's the money shot: the same reference data your JSON attempt couldn't get past the
compat-130 cliff now lands cleanly, on the oldest tier in the fleet.

## Part 3 — portable: the same XML package on the modern tier

`ContentEncoding: "Xml"` isn't a legacy-only mode — it's just a different shred path, and it
works everywhere SQL Server does. Deploy the *same* `sqlserver/package` to `learn_2022`
(compat 160):

```
schemaquench --ConfigFile:quench.settings.modern.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2022]   Delivering table data
[localhost,11433].[learn_2022]     Delivering dbo.CountryCode
[localhost,11433].[learn_2022] Successfully Quenched
```

```
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2022 -Q "SELECT Code, Name, Continent FROM dbo.CountryCode ORDER BY Code;"
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

Row-for-row identical to the `learn_2008` readback. One package, one `DataDelivery` block, both
tiers — you don't maintain a JSON copy for modern targets and an XML copy for the old one.

## Part 4 — parity: XML is accepted everywhere, for different reasons

**Requires SchemaSmith 2.5.0 or newer.** Before that, `ContentEncoding: "Xml"` was rejected on
every engine except SQL Server.

The interesting part isn't that all four engines take the encoding — it's *why* you would ask for
it, which is not the same question on each:

| Engine | How the XML is shredded | What declaring `Xml` buys you |
| --- | --- | --- |
| SQL Server | Natively, `.nodes()`/`.value()` | **Version reach.** Below compatibility level 130 there is no `OPENJSON`, so XML is the only wire format the legacy tier can read at all. |
| PostgreSQL | Natively, `xmltable()` | Nothing on its own — PostgreSQL shreds JSON at every supported version. |
| MySQL / MariaDB | Converted to JSON once, up front, then through the unchanged JSON row source | Nothing on its own — neither engine has a cliff either. |

So on three of the four engines the encoding buys **authoring uniformity, not version reach**: one
`DataDelivery` block, one payload file, shared across a package that has to serve SQL Server's
legacy tier as well. Without that, a shared package needed an XML declaration for SQL Server and a
JSON one for its siblings — the exact divergence this course keeps warning about.

`postgres/package` declares `public.countrycode` with the same `ContentEncoding: "Xml"` block,
lowercase-named for PostgreSQL. Deploy it:

```
cd ../postgres
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```
[localhost].[learn]         Create new table public.countrycode
[localhost].[learn]         Add missing Constraint public.countrycode.pk_countrycode
[localhost].[learn]   Delivering table data
[localhost].[learn]     Delivering public.countrycode
[localhost].[learn] Successfully Quenched
```

Exit code `0`. Read it back:

```
PGPASSWORD="Learn!Passw0rd" psql -h localhost -p 15432 -U postgres -d learn -c "SELECT * FROM public.countrycode ORDER BY code;"
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

The same five rows the two SQL Server tiers hold, from the same payload file — including `JP`'s
NULL continent, which survives the round trip through `xmltable()` rather than arriving as the
string `"NULL"`.

**MySQL and MariaDB take the same declaration.** They reject dynamic XPath outright, so SchemaSmith
converts the payload to JSON once before shredding it through the ordinary JSON row source — the
result is identical, and the difference is invisible in the package. There is no separate lab
package for those two here; the declaration is the same block, with the schema dropped (on
MySQL/MariaDB, schema *is* database).

**When should you actually declare `Xml`?** Only when the package has to reach SQL Server below
compatibility level 130. If nothing in your fleet is that old, omit `ContentEncoding` and ship JSON
— it is the default, it is what every engine reads most directly, and the payload is easier to read
in a diff.

## Part 5 — where that payload actually comes from

Look back at Part 2. You were handed an XML content file and told what shape it takes: a `rows`
root, one `row` per record, one `c` per column with an `n` attribute naming it, and a column
whose `c` is **absent** meaning NULL. Five country codes — fine to type by hand.

Now picture a real reference table. Two hundred rows, fifteen columns, three of them nullable.
Nobody hand-types that, and a recipe that stops here has taught you a technique you can't
actually use.

**DataTongs writes the file.** It is the same tool that casts your reference data into a content
file in the first place — it just needs telling which encoding you want:

```
cd ../sqlserver
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

**Find the `JP` row.** It has a `Code` and a `Name` and no `Continent` element at all — exactly the
convention Part 2 told you to follow. You didn't configure that. NULL-as-absent-element isn't a
rule you have to remember when authoring by hand; it's what the extractor emits, because it's how
the shred reads the file at the other end. The hand-written payload in Part 2 was written to match
*this*, not the other way round.

`--DeliveryEncoding` takes `Json` (the default) or `Xml`, and **both work on every source engine.**
SQL Server builds the XML natively; PostgreSQL, MySQL and MariaDB extract their normal JSON and
convert it to this identical dialect, so the file you get does not betray which engine it came
from. Try it against the PostgreSQL tier you deployed in Part 4:

```
cd ../postgres
datatongs --ConfigFile:tongs.settings.json --DeliveryEncoding:Xml
```

```xml
<rows><row><c n="code">CA</c><c n="continent">North America</c><c n="name">Canada</c></row>…
```

Same shape, lowercase names because that's what PostgreSQL calls those columns.

### Close the loop

The file DataTongs just wrote is a drop-in for the one you were given. Copy it over the payload in
`package/Templates/Main/data/`, redeploy to the compat-100 tier, and the rows land unchanged —
`JP` still NULL:

```
cd ../sqlserver
schemaquench --ConfigFile:quench.settings.legacy.json --LogPath:"$PWD/logs"
```

That's the round trip closed: **extract as XML → deploy as XML**, no hand-editing anywhere in it.

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
declared SRID, so the round trip is lossless. Where it bites is an **untyped** spatial column holding
a non-default SRID — that comes back as SRID 0, coordinates intact, no error. Right numbers, wrong
reference system, silently. Extract spatial data from SQL Server, type the destination column, or set
the SRID yourself. Every other column type — including binary, dates, booleans and NULLs — is fully
portable.

## Cleanup

```
cd ../sqlserver
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2022 -Q "DROP TABLE IF EXISTS dbo.CountryCode;"
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2008 -Q "DROP TABLE IF EXISTS dbo.CountryCode;"

cd ../postgres
PGPASSWORD="Learn!Passw0rd" psql -h localhost -p 15432 -U postgres -d learn -c "DROP TABLE IF EXISTS public.countrycode;"
```

## What's in here

| Path | Purpose |
| --- | --- |
| `sqlserver/package/` | Product `RefData`, the fix — `dbo.CountryCode` with `ContentEncoding: "Xml"`, deployed to both `learn_2008` and `learn_2022`. |
| `sqlserver/json-attempt/` | The *same* product `RefData`, the problem — `dbo.CountryCode` with a default-JSON `DataDelivery`, which hits the compat-130 cliff on `learn_2008`. Not a second product; SchemaSmith's ownership guard would reject two products claiming the same table. |
| `sqlserver/quench.settings.legacy.json` / `…modern.json` | The XML-encoded `RefData` deployed to `learn_2008` (compat 100) and `learn_2022` (compat 160). |
| `sqlserver/quench.settings.json-attempt.json` / `…json-attempt-fail.json` | The JSON-encoded `RefData` attempt under the default `warn` policy and under `Target:UnsupportedFeaturePolicy=fail`. |
| `postgres/package/` | The same XML delivery on `Platform: PostgreSQL`, used in Part 4 to show the encoding is accepted there too — natively, via `xmltable()`. |
| `sqlserver/tongs.settings.json` / `postgres/tongs.settings.json` | Part 5 — DataTongs extraction of `CountryCode`, run with `--DeliveryEncoding:Xml` to write the payload instead of hand-typing it. Output lands in `extracted/` and is not tracked. |

Editor JSON-schema files (`.json-schemas/`) for all three packages are generated by the CLI's
`--WriteSchemasOnly` switch and are not hand-authored.

## Up next

Back to [Module 4](../course10-module-04/README.md) if you haven't read it yet — it's the
companion story: the engine's own model ingest makes this same JSON-vs-XML choice
*automatically*, with no flag, no package change. This recipe is the one place SchemaSmith
leaves that choice to you, and only for the payload that's your data, not the engine's model.
