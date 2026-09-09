# Course 4, Recipe 11 — Declared, not scripted: the silent no-op (lab)

Goal: PostgreSQL domain types, enum types and standalone sequences can be **declared** — compared,
converged, and removable by absence — instead of scripted. The reason that matters is sharper than
"it's tidier." The *scripted* form is worse than doing it by hand: a guarded `CREATE …` silently
does nothing once the object exists, so editing the value list or the CHECK in your `.sql` changes
nothing, forever, while the run reports success. You do not get an error. You get a lie.

This lab makes you watch that happen, then does the same work declaratively.

Everything runs against `cookbook_r11` on PostgreSQL.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r11`).
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.

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
../lab-sql.sh postgres cookbook_r11 "$(cat postgres/scripted-before/email_address.sql)"
../lab-sql.sh postgres cookbook_r11 "SELECT conname, pg_get_constraintdef(oid) FROM pg_constraint WHERE conname LIKE '%email%'"
# → email_address_has_at|CHECK (((VALUE)::text ~~ '%@%'::text))
```

Now **edit the CHECK** in that file — change `VALUE LIKE '%@%'` to `VALUE LIKE '%@%.%'`, requiring a dot
after the `@` — and run the exact same file again:

```bash
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
../lab-sql.sh postgres cookbook_r11 "DROP DOMAIN IF EXISTS public.email_address CASCADE"
```

## Step 2: Declare the same objects instead

`postgres/Package/Templates/Main/` carries three folders the engine reconciles for you — no scripts:

```
Domain Types/email_address.json     # character varying(256), NOT NULL, CHECK (VALUE LIKE '%@%')
Enum Types/order_status.json        # placed, picked, shipped, delivered
Sequences/order_number.json         # bigint, starts at 1000
Tables/public.customer_order.json   # a table typed BY the domain and the enum
```

They are ordinary declarations:

```json
{ "Schema": "public", "Name": "order_status",
  "Values": ["placed", "picked", "shipped", "delivered"] }
```

Deploy:

```bash
cd postgres
schemaquench --ConfigFile:deploy.settings.json      # exit 0
schemaquench --ConfigFile:deploy.settings.json      # exit 0, and nothing changes -- it is idempotent
cd ..
```

> **Spell the base type the way PostgreSQL reports it.** The domain declares
> `"DataType": "character varying(256)"`, not `VARCHAR(256)`. PostgreSQL canonicalises aliases when it
> stores them, and SchemaSmith compares your declared spelling against what the catalog reports — so an
> alias (`VARCHAR`, `INT`, `BOOL`, `DECIMAL`) never matches and the deploy stops. Use the canonical name.

## Step 3: Change the model — this time it takes

Add a value to the enum. Append `"returned"` to `Values` in `order_status.json` and re-quench:

```bash
cd postgres && schemaquench --ConfigFile:deploy.settings.json ; cd ..
#     Add enum value returned to public.order_status
../lab-sql.sh postgres cookbook_r11 "SELECT e.enumsortorder, e.enumlabel FROM pg_enum e JOIN pg_type t ON t.oid=e.enumtypid WHERE t.typname='order_status' ORDER BY 1"
# → 1|placed  2|picked  3|shipped  4|delivered  5|returned
```

It landed, **in the order you declared it** — not appended wherever the engine felt like putting it.
Compare that to Step 1: same kind of edit, same kind of object, opposite outcome.

The domain converges the same way. Set `"NotNull": false` and add
`"Default": "'unknown@example.com'"`, re-quench, and check:

```bash
../lab-sql.sh postgres cookbook_r11 "SELECT typname, typnotnull, typdefault FROM pg_type WHERE typname='email_address'"
# → email_address|f|'unknown@example.com'::character varying
```

Both moved. Put them back and re-quench, and they move back — the declaration is the truth, and the
database follows it in either direction.

> **Known limitation (SchemaSmith 2.6.0):** editing a domain check constraint's `Expression` while
> leaving its `Name` alone is currently ignored — the constraint is reconciled by name only, and the
> run reports success. Rename the constraint when you change its expression and it applies correctly.
> This is a reported defect, not the designed behaviour; this lab will be updated when it ships fixed.

## Step 4: Watch it refuse the things it cannot do

A declarative tool that converges everything would be dangerous. Two changes are refused by name.

**A base-type change.** Set the domain's `"DataType"` to `"text"` and re-quench:

```
P0001: Domain type public.email_address declares base type "text", but is currently deployed as
"character varying(256)". PostgreSQL has no ALTER DOMAIN ... TYPE -- changing it means dropping the
domain and every column that uses it. Migrate it with a script, or correct the declared type to match.
```

Exit **2**. PostgreSQL has no `ALTER DOMAIN … TYPE` — it is a syntax error, not an unsupported
operation — so the only way to deliver it is to drop the domain, which drops every column typed by it.
SchemaSmith names both types and stops. Put it back to `character varying(256)`.

**Removing an enum value.** Take `"returned"` back out of `Values` and re-quench:

```
Enum type public.order_status has value 'returned' which the package no longer declares. PostgreSQL
cannot remove an enum value without recreating the type (and dropping every column that uses it), so
it is left in place - remove it by hand, or restore it to the package.
```

Exit **0** — this one is a report, not a failure. The value stays. You are told, in the deploy log,
exactly what the model and the database disagree about, and left to decide.

## Cleanup

```bash
../lab-sql.sh postgres cookbook_r11 "DROP TABLE IF EXISTS public.customer_order CASCADE; DROP DOMAIN IF EXISTS public.email_address CASCADE; DROP TYPE IF EXISTS public.order_status CASCADE; DROP SEQUENCE IF EXISTS public.order_number CASCADE"
```

## The principle

A scripted object is delivered **once** and then drifts in silence: the guard that makes the script
safe to re-run is the same guard that makes every later edit a no-op. A declared object is compared on
every deploy, so the file stays the truth — it converges what it can, refuses by name what would
destroy data, and reports what it will not do. The difference is not tidiness. It is whether editing
the file means anything at all.
