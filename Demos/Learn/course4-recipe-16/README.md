# Course 4, Recipe 16 — Trust the round trip: extraction that gives you back what you have (lab)

Goal: extract a database you inherited, rebuild it from the package, and prove nothing was lost, widened or
reshuffled on the way. "Extract what you have and trust the result" is how most people adopt SchemaSmith — it is
the whole premise of Course 5 — so the round trip has to be exact. This lab checks the three places it was not
before 2.7.0:

| Before 2.7.0 | What it cost |
| --- | --- |
| PostgreSQL `bit(8)` extracted as bare `bit` | the package rebuilt the column as `bit(1)` — every value truncated to one bit, with no diff to review |
| PostgreSQL `bit varying(16)` extracted as unlimited `varbit` | a silent widening |
| MySQL/MariaDB indexes, foreign keys and checks extracted in query-plan order | the same table came out differently on two runs |
| Every engine: the first re-extraction into an existing package | rewrote every table file with empty keys it never had |

Everything runs against `cookbook_r16`, on PostgreSQL, MySQL and MariaDB.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r16`).
- The CLI is on your PATH: `schematongs --version` and `schemaquench --version` answer **2.7.0 or later**.

## Step 1: Build the database you inherited

Nobody wrote a package for this one — it was built by hand. Run each engine's script once:

```bash
for e in postgres mysql mariadb; do ../lab-sql.sh $e cookbook_r16 --file $e/inherited.sql; done
```

PostgreSQL gets a `device` table with a `bit(8)` flags column and a `bit varying(16)` mask, and two rows of data.
MySQL and MariaDB get `customer` and `order_line`, whose indexes, foreign keys and checks were deliberately
created **out of name order** — `ix_zeta` before `ix_alpha`, `fk_zeta` before `fk_alpha`, `ck_zeta` before
`ck_alpha`.

## Step 2: Extract it

```bash
for e in postgres mysql mariadb; do (cd $e && schematongs --ConfigFile:SchemaTongs.settings.json); done
```

Each engine now has a package in `extracted/`. Open PostgreSQL's `extracted/Templates/Main/Tables/device.json`
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
../lab-sql.sh postgres cookbook_r16 "DROP TABLE public.device"
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r16 "DROP TABLE order_line; DROP TABLE customer"; done
for e in postgres mysql mariadb; do (cd $e && schemaquench --ConfigFile:deploy.settings.json); done
```

Check the PostgreSQL column types the package produced:

```bash
../lab-sql.sh postgres cookbook_r16 "SELECT attname, format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.device'::regclass AND attnum > 0 AND NOT attisdropped ORDER BY attnum"
# → device_id|integer
# → flags|bit(8)
# → label|text
# → mask|bit varying(16)
```

`bit(8)`, not `bit(1)`. On 2.6.0 this is the step where eight bits of every flag value became one, and nothing
anywhere said so. Deploy once more with `--report:./rerun` and `rerun.md` reports zero changes on all three
engines — the package describes the database exactly.

## Step 4: Re-extract, and expect nothing

The everyday loop is to extract again into the same package after someone changes the database, and commit
the diff. That only works if an **unchanged** database produces **no** diff:

```bash
for e in postgres mysql mariadb; do (cd $e && cp -r extracted before && schematongs --ConfigFile:SchemaTongs.settings.json && diff -r before extracted && echo "$e: identical"); done
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
../lab-sql.sh postgres cookbook_r16 "DROP TABLE IF EXISTS public.device"
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r16 "DROP TABLE IF EXISTS order_line; DROP TABLE IF EXISTS customer"; done
for e in postgres mysql mariadb; do rm -rf $e/extracted $e/before $e/rerun.*; done
```

## The principle

An extraction is a claim: *this package is your database*. A claim you cannot check by rebuilding is a claim you
are taking on faith — and a round trip that quietly truncates a type, or a diff that is noise every time, trains
people to stop reading. Extract, rebuild, compare, re-extract: when all four are exact, the package really is the
source of truth, and a diff means something changed.
