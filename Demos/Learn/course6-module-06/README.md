# Course 6, Module 6 — Lint before you deploy: the `--Validate` semantic linter (lab)

**Goal:** catch the cross-object errors JSON Schema can't express — a dangling foreign key, an ungated duplicate column, an undefined token, a stale editor schema, a drifted file name — with a single static command that needs no database, no credentials, and no running engine. Read a broken package's findings board, fix each error to green, prove the linter is *semantic* (not dumb), surface a misnamed-file warning that never gates the exit code, clear an induced staleness finding, then wire it in as a CI gate. SQL Server, PostgreSQL, MySQL, and MariaDB.

## The scenario

Structural validation (Module 3) catches the typo — a missing `DataType`, a misspelled property — on every pull request with no database. But it validates each file in isolation. It can't see that a foreign key points at a table you forgot to include, that two columns collide on a name, or that a `ShouldApplyExpression` references a token nobody defined. Those are errors of *meaning across objects*, and they only surface at deploy time — the most expensive place to find them.

`--Validate` closes that gap. It's a static, target-less semantic linter: it loads the whole package, checks cross-object meaning, prints a findings board, and exits `2` if anything is wrong. No database. No connection string. Seconds to run. It belongs first in your pre-flight family, ahead of every gate that needs a live engine.

## Before you start

- **No sandbox, no database, no credentials.** `--Validate` (and `schematongs --WriteSchemasOnly`, used later) never connect to an engine. This lab is entirely database-free.
- **The CLI is on your PATH** — `schemaquench --version` answers **2.6.0** or later. This lab quotes `--Validate` output verbatim so you can diff your own against it, and 2.6.0 tidied the output: a finding's location is printed once, as the line's prefix, instead of twice. On 2.5.0 every check still fires and every fix still works, but `SS-FK-002`, `SS-TOK-001` and `SS-STALE-001` will each read slightly differently from the boards below.
- Each engine package under `sqlserver/`, `postgres/`, `mysql/`, and `mariadb/` ships **deliberately broken** — that's the starting point. You'll fix it.

> **Path binding note:** if `--SchemaPackagePath:./sqlserver/Package` doesn't bind in your shell, pass the absolute path via the environment instead: `SmithySettings_SchemaPackagePath="$(pwd)/sqlserver/Package" schemaquench --Validate`. Run `schematongs --WriteSchemasOnly` from *inside* a `Package` directory (it defaults to `.`).

## Scenario 1 — read the board

Run the linter against the broken SQL Server package (swap `sqlserver` for `postgres` / `mysql` / `mariadb` — the same three errors reproduce on every engine):

```
schemaquench --Validate --SchemaPackagePath:./sqlserver/Package
```

It exits `2` and prints exactly three errors, one from each check engine:

```
ERROR [SS-DUP-001] Template 'Main' / Table '[OrderItem]': Duplicate column name '[Quantity]' at Template 'Main' / Table '[OrderItem]' - 2 entries share this name and at least one is not gated by ShouldApplyExpression.
ERROR [SS-FK-002] Template 'Main' / Table '[OrderItem]' / FK '[FK_OrderItem_Supplier]': RelatedTable '[Supplier]' does not resolve to any known table (resolved schema 'dbo').
ERROR [SS-TOK-001] .../dbo.Customer.json: References undefined token '{{IncludePiiColumns}}'.
3 error(s), 0 warning(s)
```

> **Your `SS-TOK-001` will show the real path where you cloned the repo** in place of the `...` above. Every
> finding is prefixed with where it lives — a file path for file-scoped checks, the object's position in the
> package for the rest. `SS-DUP-001` then names that position a second time inside its sentence ("…`at`
> Template 'Main' / Table '[OrderItem]'"), because *which* table holds the duplicate is the finding, not just
> where it was found.


Three real errors, no database touched:

- **`SS-DUP-001`** — `OrderItem` has two `[Quantity]` columns and neither is gated. A duplicate that would blow up at `CREATE TABLE`.
- **`SS-FK-002`** — `OrderItem` declares `[FK_OrderItem_Supplier]` pointing at a `[Supplier]` table that isn't in the package. You forgot to include the table.
- **`SS-TOK-001`** — `Customer`'s `[Email]` column is gated on `{{IncludePiiColumns}}`, a token nobody defined. It would silently evaluate to nothing at deploy.

## Scenario 2 — clear the board

Fix each error and re-run. Three edits:

1. **`SS-DUP-001`** — in `sqlserver/Package/Templates/Main/Tables/dbo.OrderItem.json`, remove the duplicate ungated `[Quantity]` column (keep the original).
2. **`SS-FK-002`** — in the same file, remove the `[SupplierId]` column *and* the `[FK_OrderItem_Supplier]` foreign key (the `[Supplier]` table was never part of this package).
3. **`SS-TOK-001`** — in `sqlserver/Package/Product.json`, add `"IncludePiiColumns": "1"` to `ScriptTokens` to declare the token the `[Email]` column references.

Re-run:

```
schemaquench --Validate --SchemaPackagePath:./sqlserver/Package
```

```
PASS - no issues found
```

Exit `0`. Green board — zero errors, zero warnings. (A warning-only board would still say `PASS` and exit `0` — you'll see that in Scenario 4.)

## Scenario 3 — smart, not dumb

Before you fixed anything, `Product` already had *two* columns named `[Discontinued]` — and the linter never flagged them. That's the point.

```json
{ "Name": "[Discontinued]", "DataType": "BIT",     "ShouldApplyExpression": "'{{Edition}}' = 'Legacy'", "VariantName": "Legacy" },
{ "Name": "[Discontinued]", "DataType": "TINYINT", "ShouldApplyExpression": "'{{Edition}}' = 'Modern'", "VariantName": "Modern" }
```

Both are gated on the **defined** `{{Edition}}` token, and each carries a distinct `VariantName`. That's a legitimate variant pair — the same logical column, materialized differently per edition, only ever one at a time. `--Validate` understands the difference between a variant pair and a collision. The two ungated `[Quantity]` columns were a bug; the two gated `[Discontinued]` columns are a feature. A dumb name-uniqueness check would flag both. A semantic linter flags only the first.

## Scenario 4 — a lean, not a gate

Every finding so far has been an error. `--Validate` also has a warning tier, and a warning never gates the exit code. Induce it:

1. Rename `sqlserver/Package/Templates/Main/Tables/dbo.OrderItem.json` to `sqlserver/Package/Templates/Main/Tables/orderitem-legacy.json`.
2. Re-run:
   ```
   schemaquench --Validate --SchemaPackagePath:./sqlserver/Package
   ```
   ```
   WARN [SS-FILE-NAME-003] .../orderitem-legacy.json: Table file 'orderitem-legacy.json' does not match its canonical name 'dbo.OrderItem.json' (from Schema/Name/VariantName). Identity is content, so this is a naming lean, not an error - rename to keep the file a reliable pointer to its table.
   ```
   Exit `0`. The count line reads `0 error(s), 1 warning(s)` — a warning-only board still passes.
3. Rename the file back to `dbo.OrderItem.json`. This is an induced scenario — the committed package files stay canonical.

Identity lives in the table's *content* (`Schema` / `Name` / `VariantName`), never its filename — a misnamed file still deploys correctly. The canonical name is there for you: it keeps a table's variants sorted together in source control and keeps the filename a reliable pointer to the table inside it. Canonical shape is `<schema>.<table>[.<VariantName>].json`. This scenario is database-free like every other one in this lab, and it works the same on all four engines — on PostgreSQL, MySQL, and MariaDB the canonical name is schema-less (`<table>.json`); on SQL Server it keeps the schema segment (`dbo.<table>.json`).

## Scenario 5 — when your schemas drift

The editor `.json-schemas` that give you red-squiggle validation in your IDE are generated from the domain model. If the model changes and nobody regenerates them, they lie. `--Validate` catches that too. Induce it:

1. Hand-edit `sqlserver/Package/.json-schemas/tables.sqlserver.schema` — narrow any `"maxLength": 128` to `"maxLength": 1`, so it no longer matches fresh generation.
2. Re-run `--Validate`:
   ```
   ERROR [SS-STALE-001] .../tables.sqlserver.schema: Committed .json-schemas are stale - regenerate via --WriteSchemasOnly.
   ```
   Exit `2`.
3. Regenerate (database-free), from inside the `Package` directory:
   ```
   cd sqlserver/Package && schematongs --WriteSchemasOnly && cd ../..
   ```
4. Re-run `--Validate` → `PASS - no issues found`, exit `0`. The staleness finding is gone.

<!-- TRAINING-RELEASE-PIN #416 -- on 2.7.0, fold the second sentence into the board above and delete
     this note. The message gained it in #416; 2.6.0 prints only the first. Certified on main 2026-09-10. -->
> **From SchemaSmith 2.7.0 the finding says more, and the extra clause matters.** It reads
> `… regenerate via --WriteSchemasOnly. Structural validation used the current model merged with this
> file's authored custom-property governance.` Through 2.6.0 a stale type **short-circuited** structural
> validation, so any `Extensions` governance you had authored into that file — the kind
> [Module 3](../course6-module-03) has you write — silently stopped being enforced while the only finding
> pointed at the schema file rather than at the violation. From 2.7.0 the stale type is validated against
> the current model *merged with* your recovered fragment, so your rules keep applying while the file is
> stale. Staleness is still an error worth fixing; it is no longer a hole in your governance.

## Scenario 5b — when your schemas are *malformed*, not merely stale

Staleness has a sibling, and it is the more dangerous of the two. A stale file still parses — SchemaSmith
can read the governance you authored into it. A **malformed** one cannot be read at all, so there is
nothing to recover, and validation falls back to a freshly generated schema that has never heard of your
rules.

Induce it. Replace `sqlserver/Package/.json-schemas/tables.sqlserver.schema` with something that is not
JSON at all:

```bash
printf '{ "not valid json
' > sqlserver/Package/.json-schemas/tables.sqlserver.schema
```

Re-run `--Validate`:

```
ERROR [SS-STALE-002] .../tables.sqlserver.schema: Committed .json-schemas file is malformed - validated
against a freshly generated schema instead, so any custom-property governance authored in this file
(Extensions required/enum rules) was NOT applied this run. Regenerate via --WriteSchemasOnly.
```

Exit `2`. Now clear it — and this is where the malformed case stops behaving like the stale one.

**The finding's own advice does not work here.** Do exactly what it says and you get a stack trace:

```bash
cd sqlserver/Package && schematongs --WriteSchemasOnly
# EXCEPTION - Newtonsoft.Json.JsonReaderException: Unterminated string. Expected delimiter: "
# exit 3
```

`--WriteSchemasOnly` **reads** the committed schema before rewriting it, so it can preserve the
`Extensions` fragment you authored — which is the behaviour you want in every other situation, and
exactly the behaviour that cannot cope with a file it is unable to parse. **Delete the unreadable file
first, then regenerate:**

```bash
cd sqlserver/Package && rm .json-schemas/tables.sqlserver.schema && schematongs --WriteSchemasOnly && cd ../..
```

Exit `0`, the schema is rebuilt, and `--Validate` is clean again. **Note what deleting cost you:** the
authored governance that lived in that file is gone with it, and you re-apply the fragment by hand —
which is the whole reason the finding warns you it was not enforced. (The circular advice is a reported
rough edge; the remedy above is what works today.)

### Why the wording earns its length

Read what the finding is telling you, because "your file is malformed" is not the part you act on.

The `Extensions` governance from [Module 3](../course6-module-03) — `OwningTeam` restricted to an approved
list, every column carrying a `DataClassification` — lives **inside** that schema file. When the file
cannot be parsed, those rules are not enforced. And the failure is **directional**: a table violating your
own enum rule now validates *clean*, because the rule was never evaluated. Your governance reads as
satisfied precisely when it has stopped working.

Prove it to yourself with two runs. Set a table's `OwningTeam` to something outside the approved list:

```
schema intact     → ERROR [SS-JSON-001] ... value for 'OwningTeam' not in allowed enumeration
schema malformed  → that finding is GONE. Only SS-STALE-002 remains.
```

Same package, same violation, and the one finding that mattered has vanished. That is why the message
names the consequence rather than the cause — and why it says so **unconditionally**, even for a package
that authored no governance at all: an unparseable file cannot be inspected to find out whether it
carried any, and a false reassurance would be worse than a redundant warning.

<!-- TRAINING-RELEASE-PIN #415 -- the governance sentence arrived in #415. On 2.6.0 the finding reads
     "...validated against a freshly generated schema instead. Regenerate via --WriteSchemasOnly." with
     no governance clause. Delete this note at 2.7.0. Certified on main 2026-09-10. -->
> **On SchemaSmith 2.6.0 this finding is shorter** — it stops after "validated against a freshly generated
> schema instead" and never mentions governance. Same exit code, same fallback, same silent
> non-enforcement; you simply were not told about the part that matters. From 2.7.0 it says so.

## Scenario 6 — make it a gate

`ci/validate.yml` is a copy-ready GitHub Actions workflow. Copy it into your repository's `.github/workflows/` and adjust the package path. It runs `--Validate` on every pull request; the exit-2-on-error behavior fails the PR automatically — no database, no credentials, no matrix of engine containers. Because it needs no live engine, it's the cheapest gate you have: run it first, ahead of anything that connects.

## Cross-platform

The same three-error board reproduces on all four engines. Only the identifier quoting and native type spellings differ (`[dbo]` schema and bracket quoting on SQL Server; lowercase `public` and unquoted lowercase identifiers on PostgreSQL; backtick-quoted, schema-less names on MySQL and MariaDB). On MySQL and MariaDB, foreign-key resolution is **name-only** — there are no schemas within a database — so `SS-FK-002` resolves `Supplier` by bare name.

`SS-FILE-NAME-003`'s canonical name carries the same schema rule: SQL Server keeps it (`dbo.<table>.json`, from the table's `"Schema": "[dbo]"`), while PostgreSQL, MySQL, and MariaDB are schema-less (`<table>.json`) — PostgreSQL because these packages leave `Schema` empty and rely on the default `public`, MySQL and MariaDB because they have no schemas within a database at all.

The finding codes, the switch names, the exit behavior, and the pass/fail semantics are identical everywhere — a naming warning never flips a pass to a fail, on any engine.
