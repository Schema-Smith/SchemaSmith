# Course 6, Module 6 — Lint before you deploy: the `--Validate` semantic linter (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (SS-STALE-001 on 2.7.x boards; MariaDB CheckExpression): delete the "On 2.7.x" callouts -->

**Goal:** catch the cross-object errors JSON Schema can't express — a dangling foreign key, an ungated duplicate column, an undefined token, a stale editor schema, a drifted file name — with a single static command that needs no database, no credentials, and no running engine. Read a broken package's findings board, fix each error to green, prove the linter is *semantic* (not dumb), surface a misnamed-file warning that never gates the exit code, clear an induced staleness finding, see where the engines' own rules give different verdicts, then wire it in as a CI gate. SQL Server, PostgreSQL, MySQL, and MariaDB.

## The scenario

Structural validation (Module 3) catches the typo — a missing `DataType`, a misspelled property — on every pull request with no database. But it validates each file in isolation. It can't see that a foreign key points at a table you forgot to include, that two columns collide on a name, or that a `ShouldApplyExpression` references a token nobody defined. Those are errors of *meaning across objects*, and they only surface at deploy time — the most expensive place to find them.

`--Validate` closes that gap. It's a static, target-less semantic linter: it loads the whole package, checks cross-object meaning, prints a findings board, and exits `2` if anything is wrong. No database. No connection string. Seconds to run. It belongs first in your pre-flight family, ahead of every gate that needs a live engine.

## Before you start

- **No sandbox, no database, no credentials.** `--Validate` (and `schematongs --WriteSchemasOnly`, used later) never connect to an engine. This lab is entirely database-free.
- **The CLI is on your PATH** — `schemaquench --version` answers **2.7.0 or later**. This lab quotes `--Validate` output verbatim so you can diff your own against it.
- Each engine package under `sqlserver/`, `postgres/`, `mysql/`, and `mariadb/` ships **deliberately broken** — that's the starting point. You'll fix it.

> **Path binding note:** if `--SchemaPackagePath:./sqlserver/Package` doesn't bind in your shell, pass the absolute path via the environment instead: `SmithySettings_SchemaPackagePath="$(pwd)/sqlserver/Package" schemaquench --Validate`. Run `schematongs --WriteSchemasOnly` from *inside* a `Package` directory (it defaults to `.`).

## Scenario 1 — read the board

Run the linter against your engine's broken package — the same board reproduces on every engine; SQL Server's is shown:

```bash
# from course6-module-06
schemaquench --Validate --SchemaPackagePath:./<engine>/Package
```

It exits `2` and prints two errors and a warning:

```
ERROR [SS-DUP-001] Template 'Main' / Table '[OrderItem]': Duplicate column name '[Quantity]' at Template 'Main' / Table '[OrderItem]' - 2 entries share this name and at least one is not gated by ShouldApplyExpression.
ERROR [SS-TOK-001] .../dbo.Customer.json: References undefined token '{{IncludePiiColumns}}'.
WARN [SS-FK-002] Template 'Main' / Table '[OrderItem]' / FK '[FK_OrderItem_Supplier]': RelatedTable '[Supplier]' does not resolve to any table in the package (resolved schema 'dbo'). The deploy succeeds only if it already exists on the target.
2 error(s), 1 warning(s)
```

> **On 2.7.x** the board carries one more error, `ERROR [SS-STALE-001] .../tables.sqlserver.schema: Committed .json-schemas are stale - regenerate via --WriteSchemasOnly. Structural validation used the current model merged with this file's authored custom-property governance.`, and counts `3 error(s), 1 warning(s)` (PostgreSQL flags `tables`, `domaintypes` and `sequences`: `5 error(s), 1 warning(s)`); it stays on every board until Scenario 5 regenerates the schemas, so Scenario 2 reads `1 error(s), 0 warning(s)` (`3 error(s)` on PostgreSQL) and exits `2` instead of `PASS`. Scenario 5 is where it clears.

> **Your `SS-TOK-001` will show the real path where you cloned the repo** in place of the `...` above. Every
> finding is prefixed with where it lives — a file path for file-scoped checks, the object's position in the
> package for the rest. `SS-DUP-001` then names that position a second time inside its sentence ("…`at`
> Template 'Main' / Table '[OrderItem]'"), because *which* table holds the duplicate is the finding, not just
> where it was found.

Two real errors and a lean, no database touched:

- **`SS-DUP-001`** — `OrderItem` has two `[Quantity]` columns and neither is gated. A duplicate that would blow up at `CREATE TABLE`.
- **`SS-TOK-001`** — `Customer`'s `[Email]` column is gated on `{{IncludePiiColumns}}`, a token nobody defined. It would silently evaluate to nothing at deploy.
- **`SS-FK-002`** (warning) — `OrderItem` declares `[FK_OrderItem_Supplier]` pointing at a `[Supplier]` table that isn't in the package. That is a warning, not an error, because the deploy creates a foreign key to any table that already exists on the target, declared or not. Here it's a mistake — nothing creates `Supplier` — but the linter can't know that without a database. In a deliberately partial package -- a SchemaShears patch, a bootstrap -- the same warning is expected and you read past it.

## Scenario 2 — clear the board

Fix both errors, and the mistake behind the warning, then re-run. Three edits:

1. **`SS-DUP-001`** — in `sqlserver/Package/Templates/Main/Tables/dbo.OrderItem.json`, remove the duplicate ungated `[Quantity]` column (keep the original).
2. **`SS-FK-002`** — in the same file, remove the `[SupplierId]` column *and* the `[FK_OrderItem_Supplier]` foreign key (the `[Supplier]` table was never part of this package).
3. **`SS-TOK-001`** — in `sqlserver/Package/Product.json`, add `"IncludePiiColumns": "1"` to `ScriptTokens` to declare the token the `[Email]` column references.

On the other engines the table file is `postgres/Package/Templates/Main/Tables/orderitem.json` or `<engine>/Package/Templates/Main/Tables/OrderItem.json` (MySQL, MariaDB), and the names carry that engine's quoting (`quantity`, `supplierid`, `fk_orderitem_supplier` on PostgreSQL; `` `Quantity` ``, `` `SupplierId` ``, `FK_OrderItem_Supplier` on MySQL and MariaDB). MySQL and MariaDB also declare the index that backs the foreign key, `FK_OrderItem_Supplier` under `Indexes`: remove it too, or `SS-IDX-001` reports its column missing. The token edit is the same in every engine's `Product.json`.

Re-run:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/dbo.OrderItem.json" remove Columns '{"Name": "[Quantity]", "DataType": "INT", "ShouldApplyExpression": "", "OldName": "", "VariantName": "", "Collation": "", "DataMaskFunction": "", "EncryptionType": "NONE", "EncryptionKey": "", "EncryptionAlgorithm": ""}' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/dbo.OrderItem.json" remove-where Columns 'Name=[SupplierId]' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/dbo.OrderItem.json" remove-where ForeignKeys 'Name=[FK_OrderItem_Supplier]' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set ScriptTokens.IncludePiiColumns '"1"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/orderitem.json" remove Columns '{"Name": "quantity", "DataType": "int4", "ShouldApplyExpression": "", "OldName": "", "VariantName": ""}' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/orderitem.json" remove-where Columns 'Name=supplierid' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/orderitem.json" remove-where ForeignKeys 'Name=fk_orderitem_supplier' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set ScriptTokens.IncludePiiColumns '"1"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove Columns '{"Name": "`Quantity`", "DataType": "int", "ShouldApplyExpression": "", "OldName": "", "VariantName": ""}' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where Columns 'Name=`SupplierId`' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where ForeignKeys 'Name=FK_OrderItem_Supplier' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where Indexes 'Name=FK_OrderItem_Supplier' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set ScriptTokens.IncludePiiColumns '"1"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove Columns '{"Name": "`Quantity`", "DataType": "int", "ShouldApplyExpression": "", "OldName": "", "VariantName": ""}' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where Columns 'Name=`SupplierId`' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where ForeignKeys 'Name=FK_OrderItem_Supplier' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/OrderItem.json" remove-where Indexes 'Name=FK_OrderItem_Supplier' && python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set ScriptTokens.IncludePiiColumns '"1"' -->
```bash
# from course6-module-06
schemaquench --Validate --SchemaPackagePath:./<engine>/Package
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

You met the warning tier in Scenario 1: a warning never gates the exit code. Here it is on its own, on a package that is otherwise clean. Induce it:

1. Rename `sqlserver/Package/Templates/Main/Tables/dbo.OrderItem.json` to `sqlserver/Package/Templates/Main/Tables/orderitem-legacy.json` (on PostgreSQL rename `orderitem.json`, on MySQL and MariaDB `OrderItem.json`, to the same `orderitem-legacy.json`).
2. Re-run:
   <!-- walk[sqlserver]: mv "<engine>/Package/Templates/Main/Tables/dbo.OrderItem.json" "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" -->
   <!-- walk[postgres]: mv "<engine>/Package/Templates/Main/Tables/orderitem.json" "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" -->
   <!-- walk[mysql]: mv "<engine>/Package/Templates/Main/Tables/OrderItem.json" "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" -->
   <!-- walk[mariadb]: mv "<engine>/Package/Templates/Main/Tables/OrderItem.json" "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" -->
   ```bash
   # from course6-module-06
   schemaquench --Validate --SchemaPackagePath:./<engine>/Package
   ```
   ```
   WARN [SS-FILE-NAME-003] .../orderitem-legacy.json: Table file 'orderitem-legacy.json' does not match its canonical name 'dbo.OrderItem.json' (from Schema/Name/VariantName). Identity is content, so this is a naming lean, not an error - rename to keep the file a reliable pointer to its table.
   ```
   Exit `0`. The count line reads `0 error(s), 1 warning(s)` — a warning-only board still passes.
3. Rename the file back to `dbo.OrderItem.json` (`orderitem.json` on PostgreSQL, `OrderItem.json` on MySQL and MariaDB). This is an induced scenario — the committed package files stay canonical.

<!-- walk[sqlserver]: mv "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" "<engine>/Package/Templates/Main/Tables/dbo.OrderItem.json" -->
<!-- walk[postgres]: mv "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" "<engine>/Package/Templates/Main/Tables/orderitem.json" -->
<!-- walk[mysql]: mv "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" "<engine>/Package/Templates/Main/Tables/OrderItem.json" -->
<!-- walk[mariadb]: mv "<engine>/Package/Templates/Main/Tables/orderitem-legacy.json" "<engine>/Package/Templates/Main/Tables/OrderItem.json" -->

Identity lives in the table's *content* (`Schema` / `Name` / `VariantName`), never its filename — a misnamed file still deploys correctly. The canonical name is there for you: it keeps a table's variants sorted together in source control and keeps the filename a reliable pointer to the table inside it. Canonical shape is `<schema>.<table>[.<VariantName>].json`. This scenario is database-free like every other one in this lab, and it works the same on all four engines — on PostgreSQL, MySQL, and MariaDB the canonical name is schema-less (`<table>.json`); on SQL Server it keeps the schema segment (`dbo.<table>.json`).

## Scenario 5 — when your schemas drift

The editor `.json-schemas` that give you red-squiggle validation in your IDE are generated from the domain model. If the model changes and nobody regenerates them, they lie. `--Validate` catches that too. Induce it:

1. Hand-edit `sqlserver/Package/.json-schemas/tables.sqlserver.schema` (on the other engines, `tables.postgresql.schema`, `tables.mysql.schema` or `tables.mariadb.schema` in that engine's `Package/.json-schemas`) — narrow any `"maxLength": 128` to `"maxLength": 1`, so it no longer matches fresh generation.
2. Re-run `--Validate`:
   <!-- walk: sed -i '0,/"maxLength": 128/s//"maxLength": 1/' <engine>/Package/.json-schemas/tables.*.schema -->
   ```bash
   # from course6-module-06
   schemaquench --Validate --SchemaPackagePath:./<engine>/Package
   ```
   ```
   ERROR [SS-STALE-001] .../tables.sqlserver.schema: Committed .json-schemas are stale - regenerate via --WriteSchemasOnly. Structural validation used the current model merged with this file's authored custom-property governance.
   ```
   Exit `2`.
3. Regenerate (database-free), from inside the `Package` directory:
   ```bash
   # from course6-module-06
   cd <engine>/Package && schematongs --WriteSchemasOnly && cd ../..
   ```
   ```
   Regenerating .json-schemas for Shop (SqlServer)...
   Done. 0 created, 1 updated, 3 already current.
   All package files already reference their schema.
   ```
4. Re-run `--Validate` → `PASS - no issues found`, exit `0`. The staleness finding is gone.
   ```bash
   # from course6-module-06
   schemaquench --Validate --SchemaPackagePath:./<engine>/Package
   ```

The second sentence matters. Any `Extensions` governance you authored into that file — the kind
[Module 3](../course6-module-03) has you write — is recovered from it and merged with the current model,
so your rules keep applying while the file is stale. Staleness is an error worth fixing; it is not a hole
in your governance.

## Scenario 5b — when your schemas are *malformed*, not merely stale

Staleness has a sibling, and it is the more dangerous of the two. A stale file still parses — SchemaSmith
can read the governance you authored into it. A **malformed** one cannot be read at all, so there is
nothing to recover, and validation falls back to a freshly generated schema that has never heard of your
rules.

Induce it. Replace `sqlserver/Package/.json-schemas/tables.sqlserver.schema` with something that is not
JSON at all (`tables.*.schema` matches the one table schema in your engine's package):

<!-- walk: printf '{ "not valid json\n' > <engine>/Package/.json-schemas/tables.*.schema -->
```bash
# from course6-module-06
printf '{ "not valid json\n' > <engine>/Package/.json-schemas/tables.*.schema
```

Re-run `--Validate`:

```bash
# from course6-module-06
schemaquench --Validate --SchemaPackagePath:./<engine>/Package
```

```
ERROR [SS-STALE-002] .../tables.sqlserver.schema: Committed .json-schemas file is malformed - validated
against a freshly generated schema instead, so any custom-property governance authored in this file
(Extensions required/enum rules) was NOT applied this run. Regenerate via --WriteSchemasOnly.
```

Exit `2`. Clear it the way the finding says — regenerate:

```bash
# from course6-module-06
cd <engine>/Package && schematongs --WriteSchemasOnly && cd ../..
```

```
Regenerating .json-schemas for Shop (SqlServer)...
WARNING: 'tables.sqlserver.schema' could not be parsed (Unterminated string. Expected delimiter: ". Path '', line 2, position 0.) and was regenerated from the current model. Any hand-authored "Extensions" governance it carried (required properties, enum rules) was NOT preserved and must be re-applied.
Done. 0 created, 1 updated, 3 already current.
All package files already reference their schema.
```

Exit `0`, the schema is rebuilt, and `--Validate` is clean again. **Read the warning before you move on.**
A stale file hands its governance forward; a malformed one cannot, so the regenerated file carries none of
the rules you authored into it. Re-apply the fragment by hand — the tool can rebuild the structure, but
only you know what your rules were.

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

## Scenario 6 — the engine decides what is an error

Every finding so far reproduced on all four engines. Some can't, because the engines disagree about what a name
has to be unique within. Copy a table file as the start of another one, forget to rename its foreign key, and
`SalesOrder`'s key to `Customer` carries `OrderItem`'s key name. Make that edit in your engine's
`SalesOrder` file: rename the foreign key `FK_SalesOrder_Customer` (`[FK_SalesOrder_Customer]` on SQL Server,
`fk_salesorder_customer` on PostgreSQL) to `FK_OrderItem_Product` (`[FK_OrderItem_Product]`,
`fk_orderitem_product`). Re-run:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/dbo.SalesOrder.json" set 'ForeignKeys[Name=[FK_SalesOrder_Customer]].Name' '"[FK_OrderItem_Product]"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/salesorder.json" set 'ForeignKeys[Name=fk_salesorder_customer].Name' '"fk_orderitem_product"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/SalesOrder.json" set 'ForeignKeys[Name=FK_SalesOrder_Customer].Name' '"FK_OrderItem_Product"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/SalesOrder.json" set 'ForeignKeys[Name=FK_SalesOrder_Customer].Name' '"FK_OrderItem_Product"' -->
```bash
# from course6-module-06
schemaquench --Validate --SchemaPackagePath:./<engine>/Package
```

Four engines, three verdicts:

```
SQL Server  ERROR [SS-FK-006] Template 'Main': Foreign key 'FK_OrderItem_Product' is declared on tables '[OrderItem]', '[SalesOrder]'. SqlServer names foreign keys per schema, so the second one fails the deploy. Rename one.
MySQL       ERROR [SS-FK-006] Template 'Main': Foreign key 'FK_OrderItem_Product' is declared on tables '`OrderItem`', '`SalesOrder`'. MySQL names foreign keys per database, so the second one fails the deploy. Rename one.
MariaDB     WARN [SS-FK-006] Template 'Main': Foreign key 'FK_OrderItem_Product' is declared on tables '`OrderItem`', '`SalesOrder`'. MariaDB names foreign keys per database before 12.1, so the deploy fails on an older server. Rename one, or set the product's MinimumVersion to 12.1 or later.
PostgreSQL  PASS - no issues found
```

SQL Server and MySQL exit `2`: a foreign-key name is unique per schema on one and per database on the other, so
the second `CREATE` fails. PostgreSQL exits `0` with nothing to say, because it names foreign keys per table and
both keys deploy. MariaDB names them per table from 12.1 and per database before it, so the finding is a
warning and the exit is `0`. Tell the linter which servers you deploy to and it stops warning. On MariaDB, set
the product's `MinimumVersion` and re-run:

<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" set MinimumVersion '"12.1"' -->
```bash
# from course6-module-06/mariadb
schemaquench --Validate --SchemaPackagePath:./Package
```

`PASS - no issues found`, exit `0`. That is the rule behind every engine-specific finding: the linter judges a
package against the oldest server it says it supports.

PostgreSQL has a rule of its own here. It keeps the first 63 bytes of an identifier and drops the rest, so a
longer name is stored under a name the package never declared, and the next deploy fails creating it again. On
PostgreSQL, rename the same foreign key to `fk_salesorder_customer_who_placed_the_order_and_is_billed_for_it` (64 bytes) and re-run:

<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/salesorder.json" set 'ForeignKeys[Name=fk_orderitem_product].Name' '"fk_salesorder_customer_who_placed_the_order_and_is_billed_for_it"' -->
```bash
# from course6-module-06/postgres
schemaquench --Validate --SchemaPackagePath:./Package
```

```
ERROR [SS-IDENT-002] Template 'Main' / Table 'salesorder': Foreign key 'fk_salesorder_customer_who_placed_the_order_and_is_billed_for_it' is 64 bytes long. PostgreSQL truncates identifiers to 63 bytes, so it would be stored under a shorter name and the next deploy, not finding the declared one, would fail creating it again. Shorten it to 63 bytes or fewer.
```

Exit `2`. Put the key's own name back (`FK_SalesOrder_Customer`, `[FK_SalesOrder_Customer]`,
`fk_salesorder_customer`), and on MariaDB remove `MinimumVersion` again:

<!-- walk[sqlserver]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/dbo.SalesOrder.json" set 'ForeignKeys[Name=[FK_OrderItem_Product]].Name' '"[FK_SalesOrder_Customer]"' -->
<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/SalesOrder.json" set 'ForeignKeys[Name=FK_OrderItem_Product].Name' '"FK_SalesOrder_Customer"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/SalesOrder.json" set 'ForeignKeys[Name=FK_OrderItem_Product].Name' '"FK_SalesOrder_Customer"' -->
<!-- walk[postgres]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/salesorder.json" set 'ForeignKeys[Name=fk_salesorder_customer_who_placed_the_order_and_is_billed_for_it].Name' '"fk_salesorder_customer"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Product.json" del MinimumVersion -->
```bash
# from course6-module-06
schemaquench --Validate --SchemaPackagePath:./<engine>/Package
```

`PASS - no issues found` on every engine.

## Scenario 7 — deprecate, warn, retire (MySQL and MariaDB)

A property doesn't disappear in one release. It is deprecated and still works, the linter warns about it, and
later it stops loading. MySQL and MariaDB have one property at each stage. On MySQL or MariaDB, add the old
alias for `DatabaseIdentificationScript` to `<engine>/Package/Templates/Main/Template.json`:

```json
"SchemaIdentificationScript": "SELECT DATABASE()"
```

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Template.json" set SchemaIdentificationScript '"SELECT DATABASE()"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Template.json" set SchemaIdentificationScript '"SELECT DATABASE()"' -->
```bash
# from course6-module-06/mysql
schemaquench --Validate --SchemaPackagePath:./Package
```

```
WARN [SS-DEP-001] .../Template.json: Template 'Main' uses 'SchemaIdentificationScript', a deprecated alias on MySQL and MariaDB. Rename it to 'DatabaseIdentificationScript' -- the value is identical, and the alias only still works because load migrates it.
0 error(s), 1 warning(s)
```

Exit `0`, and the same board from `mariadb/`:

```bash
# from course6-module-06/mariadb
schemaquench --Validate --SchemaPackagePath:./Package
```

The package deploys, which is exactly why the warning matters: nothing else will tell you before the alias goes.
Remove the line.

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Template.json" del SchemaIdentificationScript -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Template.json" del SchemaIdentificationScript -->
Now the retired stage. A column-level `CheckExpression` was deprecated on MySQL and MariaDB when it was
introduced, because `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` has no link from a constraint back to a column, so
those engines could never extract it the way it was written. Add one to `Product`'s `UnitPrice` column in
`<engine>/Package/Templates/Main/Tables/Product.json`:

```json
"CheckExpression": "`UnitPrice` >= 0"
```

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/Product.json" set 'Columns[Name=`UnitPrice`].CheckExpression' '"`UnitPrice` >= 0"' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/Product.json" set 'Columns[Name=`UnitPrice`].CheckExpression' '"`UnitPrice` >= 0"' -->
```bash
# from course6-module-06/mysql
schemaquench --Validate --SchemaPackagePath:./Package
```

```
ERROR [SS-JSON-001] .../Product.json: unexpected property 'CheckExpression' at #/Columns[3].CheckExpression
WARN [SS-FK-002] Template 'Main' / Table '`OrderItem`' / FK 'FK_OrderItem_Product': RelatedTable '`Product`' does not resolve to any table in the package (resolved schema ''). The deploy succeeds only if it already exists on the target.
1 error(s), 1 warning(s)
```

Exit `2`. On MySQL the property is retired: the file no longer loads, and the error names the property and the
file. Read the warning under it as a consequence, not a second problem. `Product.json` didn't load, so for the
rest of the checks there is no `Product` table, and the foreign key into it looks dangling. Fix the error and
the warning goes with it. The way to say this on MySQL is a table-level `CheckConstraints` entry.

Run the same edit from `mariadb/`:

```bash
# from course6-module-06/mariadb
schemaquench --Validate --SchemaPackagePath:./Package
```

`PASS - no issues found`, exit `0`. MariaDB gives a column-level check the column's own name, so it can tell
which column a check belongs to, and 2.8 brought the property back there: it stays column-level through
extraction and deploys. Same property, two engines, opposite verdicts, and each for a reason in its catalog.

> **On 2.7.x** the property is retired on MariaDB too, and the file does not load there either.

Remove the `CheckExpression` line again.

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/Product.json" del 'Columns[Name=`UnitPrice`].CheckExpression' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "<engine>/Package/Templates/Main/Tables/Product.json" del 'Columns[Name=`UnitPrice`].CheckExpression' -->

## Scenario 8 — make it a gate

`ci/validate.yml` is a copy-ready GitHub Actions workflow. Copy it into your repository's `.github/workflows/` and adjust the package path. It runs `--Validate` on every pull request; the exit-2-on-error behavior fails the PR automatically — no database, no credentials, no matrix of engine containers. Because it needs no live engine, it's the cheapest gate you have: run it first, ahead of anything that connects.

## Cross-platform

The same board — two errors and a warning — reproduces on all four engines. Only the identifier quoting and native type spellings differ (`[dbo]` schema and bracket quoting on SQL Server; lowercase `public` and unquoted lowercase identifiers on PostgreSQL; backtick-quoted, schema-less names on MySQL and MariaDB). On MySQL and MariaDB, foreign-key resolution is **name-only** — there are no schemas within a database — so `SS-FK-002` resolves `Supplier` by bare name.

`SS-FILE-NAME-003`'s canonical name carries the same schema rule: SQL Server keeps it (`dbo.<table>.json`, from the table's `"Schema": "[dbo]"`), while PostgreSQL, MySQL, and MariaDB are schema-less (`<table>.json`) — PostgreSQL because these packages leave `Schema` empty and rely on the default `public`, MySQL and MariaDB because they have no schemas within a database at all.

The switch names, the exit behavior, and the pass/fail semantics are identical everywhere — a naming warning never flips a pass to a fail, on any engine. What differs is where an engine's own rules differ, and Scenarios 6 and 7 are those findings.
