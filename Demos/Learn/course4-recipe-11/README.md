# Course 4, Recipe 11 — Declared, not scripted: the silent no-op (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (MariaDB declared sequences): delete the "On 2.7.x" callouts -->

Goal: PostgreSQL domain types, enum types and standalone sequences can be **declared** — compared,
converged, and removable by absence — instead of scripted. The reason that matters is sharper than
"it's tidier." The *scripted* form is worse than doing it by hand: a guarded `CREATE …` silently
does nothing once the object exists, so editing the value list or the CHECK in your `.sql` changes
nothing, forever, while the run reports success. You do not get an error. You get a lie.

This lab makes you watch that happen, then does the same work declaratively.

Everything runs against `cookbook_r11` on PostgreSQL; Step 5 takes the sequence to MariaDB, the other engine
with a native `SEQUENCE`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r11`).
- The CLI is on your PATH: `schemaquench --version` answers **2.7.0 or later**.

## Step 1: Watch the scripted form lie to you

[`postgres/scripted-before/email_address.sql`](postgres/scripted-before/email_address.sql) is the shape
almost everyone reaches for first — a guarded `CREATE DOMAIN` in an ordinary migration script:

```sql
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_type WHERE typname = 'email_address') THEN
    CREATE DOMAIN public.email_address AS VARCHAR(256)
      CONSTRAINT email_address_has_at CHECK (VALUE LIKE '%@%');
  END IF;
END $$;
```

Run it, then look at what landed:

```bash
# from course4-recipe-11
../lab-sql.sh postgres cookbook_r11 "$(cat postgres/scripted-before/email_address.sql)"
../lab-sql.sh postgres cookbook_r11 "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname LIKE '%email%'"
# → email_address_has_at|CHECK (((VALUE)::text ~~ '%@%'::text))
```

Now **edit the CHECK** in that file — change `VALUE LIKE '%@%'` to `VALUE LIKE '%@%.%'`, requiring a dot
after the `@` — and run the exact same file again:

```bash
# from course4-recipe-11
../lab-sql.sh postgres cookbook_r11 "$(cat postgres/scripted-before/email_address.sql)"
# → DO
../lab-sql.sh postgres cookbook_r11 "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname LIKE '%email%'"
# → email_address_has_at|CHECK (((VALUE)::text ~~ '%@%'::text))
```

The script reported `DO`. The constraint is **unchanged**. It will stay unchanged for as long as that
domain exists, no matter how many times the script runs or how carefully you edit it. Nothing failed,
so nothing told you. This is the failure mode the declarative form exists to remove.

Put the file back to `'%@%'` before moving on, and clear the hand-made domain so the package starts clean:

```bash
# from course4-recipe-11
../lab-sql.sh postgres cookbook_r11 "DROP DOMAIN IF EXISTS public.email_address CASCADE"
```

## Step 2: Declare the same objects instead

`postgres/Package/Templates/Main/` carries three folders the engine reconciles for you — no scripts:

```
Domain Types/public.email_address.json  # VARCHAR(256), NOT NULL, CHECK (VALUE LIKE '%@%')
Enum Types/public.order_status.json     # placed, picked, shipped, delivered
Sequences/public.order_number.json      # bigint, starts at 1000
Tables/public.customer_order.json       # a table typed BY the domain and the enum
```

They are ordinary declarations:

```json
{ "Schema": "public", "Name": "order_status",
  "Values": ["placed", "picked", "shipped", "delivered"] }
```

Deploy:

```bash
# from course4-recipe-11/postgres
schemaquench --ConfigFile:deploy.settings.json      # exit 0
schemaquench --ConfigFile:deploy.settings.json      # exit 0, and nothing changes -- it is idempotent
```

## Step 3: Change the model — this time it takes

Add a value to the enum. Append `"returned"` to `Values` in
`postgres/Package/Templates/Main/Enum Types/public.order_status.json` and re-quench:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Enum Types/public.order_status.json" append Values '"returned"' -->
```bash
# from course4-recipe-11
cd postgres && schemaquench --ConfigFile:deploy.settings.json ; cd ..
#     Add enum value returned to public.order_status
../lab-sql.sh postgres cookbook_r11 "SELECT e.enumsortorder, e.enumlabel FROM pg_enum e JOIN pg_type t ON t.oid=e.enumtypid WHERE t.typname='order_status' ORDER BY 1"
# → 1|placed  2|picked  3|shipped  4|delivered  5|returned
```

It landed, **in the order you declared it** — not appended wherever the engine felt like putting it.
Compare that to Step 1: same kind of edit, same kind of object, opposite outcome.

Now make the edit that Step 1 could not. In
`postgres/Package/Templates/Main/Domain Types/public.email_address.json`, change the CHECK's
`Expression` to `VALUE LIKE '%@%.%'` — same name, new rule — and re-quench:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set "CheckConstraints[Name=email_address_has_at].Expression" "\"VALUE LIKE '%@%.%'\"" -->
```bash
# from course4-recipe-11
cd postgres && schemaquench --ConfigFile:deploy.settings.json ; cd ..
#         Altering constraint email_address_has_at on domain type public.email_address
../lab-sql.sh postgres cookbook_r11 "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname LIKE '%email%'"
# → email_address_has_at|CHECK (((VALUE)::text ~~ '%@%.%'::text))
```

The exact edit the guarded script swallowed in Step 1 lands here, because nothing guards a declaration
from being compared. The rest of the domain converges the same way. Set `"NotNull": false` and add
`"Default": "'unknown@example.com'"` (both top-level keys in the same `public.email_address.json`),
re-quench, and check:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set NotNull false && python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set Default "\"'unknown@example.com'\"" -->
```bash
# from course4-recipe-11
cd postgres && schemaquench --ConfigFile:deploy.settings.json ; cd ..
../lab-sql.sh postgres cookbook_r11 "SELECT typname, typnotnull, typdefault FROM pg_type WHERE typname='email_address'"
# → email_address|f|'unknown@example.com'::character varying
```

Both moved. Put them back and re-quench, and they move back — the declaration is the truth, and the
database follows it in either direction.

## Step 4: Watch it refuse the things it cannot do

A declarative tool that converges everything would be dangerous. Two changes are refused by name.

**A base-type change.** Set the domain's `"DataType"` to `"text"` in `public.email_address.json` and re-quench:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set NotNull true && python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" del Default && python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set DataType '"text"' -->
```bash
# from course4-recipe-11/postgres
schemaquench --ConfigFile:deploy.settings.json
```

```
P0001: Domain type public.email_address declares base type "text", but is currently deployed as
"character varying(256)". PostgreSQL has no ALTER DOMAIN ... TYPE -- changing it means dropping the
domain and every column that uses it. Migrate it with a script, or correct the declared type to match.
```

Exit **2**. PostgreSQL has no `ALTER DOMAIN … TYPE` — it is a syntax error, not an unsupported
operation — so the only way to deliver it is to drop the domain, which drops every column typed by it.
SchemaSmith names both types and stops. Put it back to `VARCHAR(256)`.

**Removing an enum value.** Take `"returned"` back out of `Values` in `public.order_status.json` and re-quench:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Domain Types/public.email_address.json" set DataType '"VARCHAR(256)"' && python -I "$WALK_TOOLS/jedit.py" "postgres/Package/Templates/Main/Enum Types/public.order_status.json" remove Values '"returned"' -->
```bash
# from course4-recipe-11/postgres
schemaquench --ConfigFile:deploy.settings.json
```

```
Enum type public.order_status has value 'returned' which the package no longer declares. PostgreSQL
cannot remove an enum value without recreating the type (and dropping every column that uses it), so
it is left in place - remove it by hand, or restore it to the package.
```

Exit **0** — this one is a report, not a failure. The value stays. You are told, in the deploy log,
exactly what the model and the database disagree about, and left to decide.

## Step 5 (MariaDB): a sequence you can change without resetting it

MariaDB has a native `SEQUENCE` too, and the scripted form fails it in the opposite direction. A guarded
`CREATE SEQUENCE IF NOT EXISTS` swallows your edits, as Step 1's domain did. The other common form,
`CREATE OR REPLACE SEQUENCE`, takes every edit — by recreating the sequence on every deploy, so `NEXTVAL` starts
again from `START` and hands out order numbers it has already given (before 2.8, that is what extraction
wrote). From 2.8 a MariaDB sequence is declared like PostgreSQL's:

```json
{ "Name": "order_number", "DataType": "bigint", "Start": 1000, "Increment": 1, "Cache": 1, "Cycle": false }
```

That is `mariadb/Package/Templates/Main/Sequences/order_number.json`. Deploy it and take two numbers:

```bash
# from course4-recipe-11/mariadb
schemaquench --ConfigFile:deploy.settings.json      # exit 0
#       Create sequence order_number
```

```bash
# from course4-recipe-11
../lab-sql.sh mariadb cookbook_r11 "SELECT NEXTVAL(order_number); SELECT NEXTVAL(order_number)"
# → 1000
# → 1001
```

Now change the increment to `10` in `order_number.json` and deploy again:

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/Package/Templates/Main/Sequences/order_number.json" set Increment 10 -->
```bash
# from course4-recipe-11/mariadb
schemaquench --ConfigFile:deploy.settings.json      # exit 0
#       Alter sequence order_number
```

```bash
# from course4-recipe-11
../lab-sql.sh mariadb cookbook_r11 "SELECT NEXTVAL(order_number); SELECT NEXTVAL(order_number)"
# → 1002
# → 1012
```

The edit landed, and the sequence carried on from where it was: `1002`, then counting by ten. SchemaSmith
altered the sequence in place and never touched its current value. Deploy once more and the log shows
`Quenching sequences` with nothing under it.

> **On 2.7.x** a `.json` file in a MariaDB `Sequences/` folder is not read: the deploy exits `0` and creates no
> sequence. Only `.sql` sequence scripts deploy there.

## Cleanup

```bash
# from course4-recipe-11
../lab-sql.sh postgres cookbook_r11 "DROP TABLE IF EXISTS public.customer_order CASCADE; DROP DOMAIN IF EXISTS public.email_address CASCADE; DROP TYPE IF EXISTS public.order_status CASCADE; DROP SEQUENCE IF EXISTS public.order_number CASCADE"
../lab-sql.sh mariadb cookbook_r11 "DROP SEQUENCE IF EXISTS order_number"
```

## The principle

A scripted object is delivered **once** and then drifts in silence: the guard that makes the script
safe to re-run is the same guard that makes every later edit a no-op. A declared object is compared on
every deploy, so the file stays the truth — it converges what it can, refuses by name what would
destroy data, and reports what it will not do. The difference is not tidiness. It is whether editing
the file means anything at all.
