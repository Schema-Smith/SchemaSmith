# Course 4, Recipe 16 — Trust the round trip: extraction that gives you back what you have (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (ObjectOrder defaults to Physical; SQL Server alias type #489): delete the "On 2.7.x" callouts -->

Goal: extract a database you inherited, rebuild it from the package, and prove nothing was lost, widened or
reshuffled on the way. "Extract what you have and trust the result" is how most people adopt SchemaSmith — it is
the whole premise of Course 5 — so the round trip has to be exact. This lab checks the places it was not:

| Before | What it cost |
| --- | --- |
| PostgreSQL `bit(8)` extracted as bare `bit` (fixed in 2.7.0) | the package rebuilt the column as `bit(1)` — every value truncated to one bit, with no diff to review |
| PostgreSQL `bit varying(16)` extracted as unlimited `varbit` (2.7.0) | a silent widening |
| MySQL/MariaDB indexes, foreign keys and checks extracted in query-plan order (2.7.0) | the same table came out differently on two runs |
| Every engine: the first re-extraction into an existing package (2.7.0) | rewrote every table file with empty keys it never had |
| SQL Server column collations extracted as `""` (2.7.2, #451) | a redeploy moved every column with its own collation to the database default, silently |
| SQL Server `XML(DOCUMENT …)` extracted as `CONTENT` (2.7.2) | the column stopped rejecting a fragment that is not a whole document |
| SQL Server alias type `ShortChar` extracted as `SHORTCHAR(10)` (2.8, #489) | the package could not deploy at all |

Everything runs against `cookbook_r16`, on all four engines.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r16`).
- The CLI is on your PATH: `schematongs --version` and `schemaquench --version` answer **2.7.0 or later**.

## Step 1: Build the database you inherited

Nobody wrote a package for this one — it was built by hand. Run each engine's script once:

```bash
# from course4-recipe-16
for e in sqlserver postgres mysql mariadb; do ../lab-sql.sh $e cookbook_r16 --file $e/inherited.sql; done
```

SQL Server gets an `account` table that leans on three things a database can carry beside the table itself: a
`login_name` column with its own case-sensitive collation (`Latin1_General_CS_AS`, while the database is
case-insensitive), a `region_code` column of the alias type `dbo.ShortChar` (`varchar(10)` under another name), and
an `invoice` column bound to an XML schema collection as `XML(DOCUMENT dbo.InvoiceSchema)`.
PostgreSQL gets a `device` table with a `bit(8)` flags column and a `bit varying(16)` mask, and two rows of data.
MySQL and MariaDB get `customer` and `order_line`, whose indexes, foreign keys and checks were deliberately
created **out of name order** — `ix_zeta` before `ix_alpha`, `fk_zeta` before `fk_alpha`, `ck_zeta` before
`ck_alpha`.

## Step 2: Extract it

```bash
# from course4-recipe-16
for e in sqlserver postgres mysql mariadb; do (cd $e && schematongs --ConfigFile:SchemaTongs.settings.json); done
```

Each engine now has a package in `extracted/`. Open SQL Server's `extracted/Templates/Main/Tables/dbo.account.json`
and find the three columns:

```json
{ "Name": "[login_name]",  "DataType": "VARCHAR(40)", "Collation": "Latin1_General_CS_AS", ... },
{ "Name": "[region_code]", "DataType": "ShortChar", "Nullable": true, ... },
{ "Name": "[invoice]",     "DataType": "XML(DOCUMENT [dbo].[InvoiceSchema])", "Nullable": true, ... }
```

The type and the schema collection travel too, as scripts: `extracted/Templates/Main/DataTypes/dbo.ShortChar.sql`
and `extracted/Templates/Main/XMLSchemaCollections/dbo.InvoiceSchema.sql`.

> **On 2.7.x** `region_code` extracts as `"DataType": "SHORTCHAR(10)"`, and Step 3's SQL Server deploy fails with
> `Cannot specify a column width on data type SHORTCHAR.` (exit `2`). The collation and the `DOCUMENT` binding come
> through on 2.7.2 and later.

Open PostgreSQL's `extracted/Templates/Main/Tables/device.json`
and find the two bit columns:

```json
{ "Name": "flags", "DataType": "bit(8)", ... },
{ "Name": "mask",  "DataType": "varbit(16)", "Nullable": true, ... }
```

Both lengths survived. Now open MySQL's `extracted/Templates/Main/Tables/order_line.json` and read the three
lists:

```
Indexes:           fk_alpha, ix_alpha, ix_mid, ix_zeta, PRIMARY
ForeignKeys:       fk_alpha, fk_zeta
CheckConstraints:  ck_alpha, ck_zeta
```

Name order, whatever order they were created in — and MariaDB's file matches MySQL's line for line. (`fk_alpha`
in the index list is real: MySQL creates an index for a foreign key whose column has none.) These lists are
sets; nothing about the table depends on their sequence, so the only sequence worth having is one that never
changes. `Product:ObjectOrder` still decides the **column** sequence, which is a different question.

## Step 3: Rebuild from the package

Throw the hand-built tables away and let the package build them:

```bash
# from course4-recipe-16
../lab-sql.sh sqlserver cookbook_r16 "DROP TABLE dbo.account; DROP TYPE dbo.ShortChar; DROP XML SCHEMA COLLECTION dbo.InvoiceSchema"
../lab-sql.sh postgres cookbook_r16 "DROP TABLE public.device"
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r16 "DROP TABLE order_line; DROP TABLE customer"; done
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
```

Ask SQL Server what it built:

```bash
# from course4-recipe-16
../lab-sql.sh sqlserver cookbook_r16 "SELECT c.name, TYPE_NAME(c.user_type_id), c.collation_name, c.is_xml_document FROM sys.columns c WHERE c.object_id = OBJECT_ID('dbo.account') ORDER BY c.column_id"
# → account_id int NULL 0
# → login_name varchar Latin1_General_CS_AS 0
# → region_code ShortChar SQL_Latin1_General_CP1_CI_AS 0
# → invoice xml NULL 1
```

`login_name` kept its own collation, so `'Alice'` and `'alice'` are still two different logins. Before 2.7.2 the
rebuilt column took the database default and they became the same one, with no error anywhere. `region_code` is the
alias type again, and `invoice` still accepts only a whole document.

Check the PostgreSQL column types the package produced:

```bash
# from course4-recipe-16
../lab-sql.sh postgres cookbook_r16 "SELECT attname, format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.device'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum"
# → device_id|integer
# → flags|bit(8)
# → mask|bit varying(16)
# → label|text
```

`bit(8)`, not `bit(1)`. On 2.6.0 this is the step where eight bits of every flag value became one, and nothing
anywhere said so. The columns also come back in the order the inherited table had them, because extraction
wrote them in the table's physical order. Deploy once more with `--report:./rerun` and `rerun.md` reports zero
changes on all four engines — the package describes the database exactly.

> **On 2.7.x** extraction writes columns in name order (`ObjectOrder` defaults to `Name`), so the rebuilt table
> reads `device_id`, `flags`, `label`, `mask`.

```bash
# from course4-recipe-16
for e in sqlserver postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json --report:./rerun); done
```

## Step 4: Re-extract, and expect nothing

The everyday loop is to extract again into the same package after someone changes the database, and commit
the diff. That only works if an **unchanged** database produces **no** diff:

```bash
# from course4-recipe-16
for e in sqlserver postgres mysql mariadb; do (cd $e && cp -r extracted before && schematongs --ConfigFile:SchemaTongs.settings.json && diff -r before extracted && echo "$e: identical"); done
# → sqlserver: identical
# → postgres: identical
# → mysql: identical
# → mariadb: identical
```

Re-extraction carries forward what only the package knows — a `ShouldApplyExpression`, a `VariantName`, a data
delivery — from the file it replaces. Before 2.7.0 it carried forward keys the file never had, as empty strings,
so the first re-extraction of every package rewrote every table file with no schema change behind it. Now a
real change is the only thing that shows up in the diff.

## Cleanup

```bash
# from course4-recipe-16
../lab-sql.sh sqlserver cookbook_r16 "DROP TABLE IF EXISTS dbo.account; DROP TYPE IF EXISTS dbo.ShortChar; IF EXISTS (SELECT 1 FROM sys.xml_schema_collections WHERE name = 'InvoiceSchema') DROP XML SCHEMA COLLECTION dbo.InvoiceSchema"
../lab-sql.sh postgres cookbook_r16 "DROP TABLE IF EXISTS public.device"
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r16 "DROP TABLE IF EXISTS order_line; DROP TABLE IF EXISTS customer"; done
for e in sqlserver postgres mysql mariadb; do rm -rf $e/extracted $e/before $e/rerun.*; done
```

## The principle

An extraction is a claim: *this package is your database*. A claim you cannot check by rebuilding is a claim you
are taking on faith — and a round trip that quietly truncates a type, or a diff that is noise every time, trains
people to stop reading. Extract, rebuild, compare, re-extract: when all four are exact, the package really is the
source of truth, and a diff means something changed.
