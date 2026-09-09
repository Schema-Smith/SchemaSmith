# Course 11, Module 3 — The refusal you did not ask for (lab)

Goal: every refusal so far has been *"the engine cannot do this"*. This one is different, and it is the
strongest beat in the course.

Here the engine would have done it happily. `DROP TABLE` is a perfectly ordinary statement, your
package asked for it by omission, and you switched drop-by-absence on yourself. SchemaSmith declines
anyway — and **fails the run closed** rather than skipping quietly, because a skipped drop that nobody
notices is how you find out a year later.

Everything runs against `vault_m3`.

## Before you start

- The [sandbox](../docker) is up and [`../course11-setup`](../course11-setup) has been run once.
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.

> SQL Server needs a partition scheme in `vault_m3`. Create it once:
> ```bash
> ../lab-sql.sh sqlserver vault_m3 "IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name='pf_vault_year') CREATE PARTITION FUNCTION pf_vault_year (INT) AS RANGE RIGHT FOR VALUES (2024,2025,2026); IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name='ps_vault_year') CREATE PARTITION SCHEME ps_vault_year AS PARTITION pf_vault_year ALL TO ([PRIMARY]);"
> ```

## Step 1: Deploy two tables, one of them partitioned

`baseline/` declares `LegalHold` (ordinary) and `LedgerEntry` (**partitioned**, by year). Note that
every settings file here already carries the switch this module is about:

```json
{ "DropTablesRemovedFromProduct": true }
```

```bash
cd <engine> && schemaquench --ConfigFile:quench.settings.baseline.json ; cd ..   # exit 0
```

**Both tables were created by SchemaSmith**, including the partitioned one. Hold on to that — it
matters in a moment.

## Step 2: Delete the partitioned table from the package

`removed/` is the same package with `LedgerEntry`'s file gone. That is drop-by-absence: the table is
no longer declared, the switch is on, so the table should be dropped.

```bash
cd <engine> && schemaquench --ConfigFile:quench.settings.removed.json ; echo "exit=$?" ; cd ..
```

**SQL Server** — exit `2`:

```text
Partitioned table(s) removed from the product but NOT dropped: [dbo].[LedgerEntry]. SchemaSmith cannot
verify that data spread across partitions can be safely destroyed by DROP TABLE. Drop the table
manually after confirming the data is no longer needed, or mark it PreventDrop to keep it in the
product permanently.
```

**MySQL and MariaDB** — exit `2`:

```text
      Partitioned table removed from product, not dropped (data-loss guard): LedgerEntry
Partitioned table(s) skipped by drop-by-absence guard; drop manually or mark PreventDrop.
```

Check the database — `LedgerEntry` is still there on all three engines. Nothing was dropped, and the
run did not pretend to succeed.

## Step 3: Notice what just happened

Read the sequence back. You turned the setting on. You deleted the file. The engine had no objection.
And the deploy **stopped**.

That is categorically different from every refusal in Modules 1 and 2. Those were the tool declining to
do something that could not be done safely *by any means*. This is the tool declining to do something
that would have worked — because it decided your package probably did not mean it.

The reason is in the message: **it cannot verify that data spread across partitions is disposable.** A
table is usually partitioned because it got big. Big means years of rows, often across storage a DBA
arranged deliberately. `DROP TABLE` takes all of it, and no re-deploy brings it back — the package
describes the *shape*, never the rows.

So the guard weighs a deleted file against an irreversible loss and decides the file is the more likely
mistake. **And it fails closed rather than skipping.** A skipped drop with a warning would leave you
with a green pipeline and a table you believe is gone; exit 2 puts the decision in front of a human
while they are still looking.

> **Why the guard protects a table SchemaSmith built.** Before 2.6.0 the reference explained this as
> "SchemaSmith has no partitioning support of its own, so a partitioned table is one someone
> partitioned by hand." Declarative partitioning made that premise false — **SchemaSmith created the
> partitioned table in this lab**, from your package — and the docs were
> [corrected](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/schemaquench.md)
> from this lab. They now argue from the rationale that actually holds: a partitioned table holds data
> spread across every partition, and no declaration can tell SchemaSmith that data is disposable. A
> hand-partitioned table and one SchemaSmith deployed are **equally protected**.

## Step 4: Say what you meant

The guard is a safety net, not the mechanism. If you genuinely want the table kept, say so — mark it
`PreventDrop` **while it is still declared**:

```json
{ "Name": "[LedgerEntry]", "PreventDrop": true, "...": "..." }
```

```bash
cd <engine>
schemaquench --ConfigFile:quench.settings.protected.json   # exit 0 -- marks it, still declared
schemaquench --ConfigFile:quench.settings.removed.json     # exit 0 -- now the removal is clean
cd ..
```

```text
    Table LedgerEntry removed from product but PreventDrop is set - skipping drop (protected)
```

Exit **0**. `PreventDrop` takes precedence — a marked table never reaches the guard at all, the skip is
recorded as an ordinary event, and the run succeeds. The marker is **sticky**: it survives the table
leaving the package, which is exactly the point, because a marker that vanished with the declaration
would protect nothing at the only moment it mattered.

Which gives you the rule: `PreventDrop` is how you say *"keep this."* The partition guard is what
catches you when you forgot to.

## Cleanup

```bash
../lab-sql.sh sqlserver vault_m3 "DROP TABLE IF EXISTS dbo.LedgerEntry; DROP TABLE IF EXISTS dbo.LegalHold"
for e in mysql mariadb; do ../lab-sql.sh $e vault_m3 "DROP TABLE IF EXISTS LedgerEntry; DROP TABLE IF EXISTS LegalHold"; done
```

## The principle

Modules 1 and 2 were about the tool's limits — things it cannot do without destroying something it
cannot rebuild. This module is about its **judgement**: an operation it is fully capable of, that you
explicitly enabled, that it declines anyway.

That only works because the judgement is narrow and legible. It does not second-guess every drop — an
ordinary table goes when you stop declaring it, and `LegalHold` would have too. It intervenes on one
specific, checkable signal that the loss would be large and irreversible, it says exactly what it saw,
and it gives you two ways to proceed.

A tool that refuses too broadly gets ignored. A tool that never refuses gets trusted right up until the
day it shouldn't have been.
