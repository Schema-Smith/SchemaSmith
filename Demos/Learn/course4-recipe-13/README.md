# Course 4, Recipe 13 — Index-Only Templates: tuning a table you do not own (lab)

Goal: you need an index on a table you are not allowed to change. It belongs to a vendor product, or
it is a replicated copy whose shape is dictated upstream — either way, the columns are not yours and a
deploy that "corrects" them would be a bug, not a feature. `IndexOnlyTableQuenches` narrows a whole
template to exactly that: it manages indexes, statistics, and XML/full-text indexes, and touches
nothing else.

The table in this lab is created **outside the package**, by a script standing in for the vendor. The
package never declares its columns — only the indexes you want on it.

> **PostgreSQL is not in this lab, and not for a design reason.** `IndexOnlyTableQuenches` is currently
> broken on PostgreSQL: the generated `CALL` omits a required argument and every deploy fails with
> `42883 … procedure SchemaSmith.IndexOnlyQuench(…) does not exist`. That is a reported defect, and the
> PostgreSQL folder will be added here when it ships fixed. SQL Server, MySQL and MariaDB are unaffected.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r13`).
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.

## Step 1: Let the "vendor" create its table

This is the part you do not control. Run it once per engine:

```bash
for e in sqlserver mysql mariadb; do
  ../lab-sql.sh $e cookbook_r13 "$(cat vendor-schema/create-vendor-table.$e.sql)"
done
```

`vendor_order` now exists with four columns — `order_id`, `customer_ref`, `placed_at`, `status` — and a
primary key. Your package had nothing to do with any of it.

## Step 2: Declare indexes, and only indexes

Look at the table file. It has **no `Columns` block at all**:

```json
{
  "Schema": "[dbo]",
  "Name": "[vendor_order]",
  "Indexes": [
    { "Name": "[IX_vendor_order_customer_placed]", "IndexColumns": "[customer_ref],[placed_at]" },
    { "Name": "[IX_vendor_order_status]", "IndexColumns": "[status]" }
  ]
}
```

That is only legal because the template says so:

```json
{ "Name": "Main", "IndexOnlyTableQuenches": true, "DatabaseIdentificationScript": "..." }
```

It is a **template-level** switch, not a per-table one — the whole template runs in index-only mode.
Deploy:

```bash
cd <engine> && schemaquench --ConfigFile:deploy.settings.json ; cd ..
# exit 0 on SQL Server, MySQL and MariaDB
```

Watch the log line, because it changes shape to tell you which mode you are in:

```text
  Quenching indexes                    <- index-only mode
  Quenching indexes and constraints    <- a normal template
```

## Step 3: Check what did *not* happen

This is the assertion that matters. The indexes landed:

```bash
../lab-sql.sh sqlserver cookbook_r13 "SELECT i.name FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id WHERE t.name='vendor_order' AND i.name IS NOT NULL ORDER BY i.name"
# → IX_vendor_order_customer_placed, IX_vendor_order_status, PK__vendor_o__...
```

And the vendor's columns are exactly as the vendor left them:

```bash
../lab-sql.sh sqlserver cookbook_r13 "SELECT STRING_AGG(name,',') FROM sys.columns WHERE object_id=OBJECT_ID('dbo.vendor_order')"
# → order_id,customer_ref,placed_at,status
```

Four columns, unchanged, in the vendor's order. **Do not take the clean exit code as proof of that** —
a green run tells you nothing about what was left alone, which is the entire value of this mode. Query
the columns. Re-run the deploy and query them again; it is idempotent, and the second run changes
nothing either.

## Step 4: A table that is not there fails the deploy

Worth knowing before you design a package around this. Declare an index on a table the database does
not have, and the run does **not** skip it:

```text
        Creating index [dbo].[ghost_table].[IX_ghost_never_lands]
Cannot find the object "dbo.ghost_table" because it does not exist or you do not have permissions.
```

Exit **2**. Same on MySQL and MariaDB (`Table 'cookbook_r13.ghost_table' doesn't exist`). Index-only
mode skips *table creation* — it does not skip *tables*. So one package covering a vendor's optional
modules, aimed at a deployment that installed only some of them, fails rather than quietly doing the
subset. If you need that, gate the table with `ShouldApplyExpression` and let the package decide
explicitly.

> **Known documentation error (SchemaSmith 2.6.0):** the reference currently says "Tables that don't
> exist are silently skipped." They are not, on any engine tested. Reported; the behaviour above is
> what the tool actually does, and is the better default.

## Cleanup

```bash
for e in sqlserver mysql mariadb; do ../lab-sql.sh $e cookbook_r13 "DROP TABLE IF EXISTS vendor_order"; done
```

## The principle

Every other template in this course owns its tables outright: declare the shape, and SchemaSmith
converges the database to it. That is the right default and the wrong behaviour for a table someone
else defines — there, "converge to the model" would mean reverting the vendor's next release.
`IndexOnlyTableQuenches` narrows the contract to the part that *is* yours. The tuning is yours; the
shape stays theirs; and your supplementary indexes survive in source control like everything else you
deploy.
