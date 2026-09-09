# Course 11, Module 1 — Applied at CREATE, refused on change (lab)

Goal: some things about a table are decided when it is built and cannot be moved afterwards without
rewriting every row in it. SchemaSmith applies those at CREATE and **refuses them by name** on a
deployed table — it does not quietly ignore your declaration, and it does not quietly rewrite your
data. This module is where you read the refusal and learn to want it.

Partitioning is the anchor, because it carries the sharpest answer to *why not just converge
everything?* — given two partition layouts, a state-based diff **cannot tell a SPLIT from a MERGE**.
There is no safe automatic answer, so there is no automatic answer at all.

Three engine families, three different mechanisms, one posture. All against `vault_m1`.

## Before you start

- The [sandbox](../docker) is up, and [`../course11-setup`](../course11-setup) has been run once.
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.

> **What setup created, and why that matters.** `course11-setup` created a SQL Server partition
> function (`pf_vault_year`) and two schemes (`ps_vault_year`, `ps_vault_year_alt`), plus a PostgreSQL
> tablespace (`vault_ts`). **SchemaSmith created none of them and never would.** It *names* a partition
> scheme and a tablespace; it does not create them, exactly as it never creates a filegroup. Those are
> server-side objects a DBA owns, sized and placed for reasons a schema package cannot see. Your
> package says *"put this table there"* — it does not say what *there* is.

## Step 1: Deploy the baseline

`LedgerEntry` is the Vault's append-only financial record. Each engine declares its placement in its
own idiom:

```json
// SQL Server -- a scheme NAME the DBA created, plus the column the function is applied to
{ "PartitionScheme": "ps_vault_year", "PartitionColumn": "[EntryYear]" }

// MySQL / MariaDB -- partitioning lives on the table itself
{ "Partitioning": { "Method": "RANGE", "Expression": "EntryYear",
    "Partitions": [ { "Name": "p2024", "Values": "2025" }, { "Name": "p2025", "Values": "2026" },
                    { "Name": "p2026", "Values": "2027" } ] } }

// PostgreSQL -- declarative partitioning is not supported; this is the tablespace beat instead
{ "Tablespace": "vault_ts" }
```

```bash
cd <engine>
schemaquench --ConfigFile:quench.settings.baseline.json      # exit 0
schemaquench --ConfigFile:quench.settings.baseline.json      # exit 0 again -- idempotent
cd ..
```

## Step 2: Ask to move it

The `changed/` package declares the same table somewhere else — a different partition scheme, a
different tablespace, a different partitioning expression:

```bash
cd <engine> && schemaquench --ConfigFile:quench.settings.changed.json ; echo "exit=$?" ; cd ..
```

**SQL Server** — exit `2`:

```text
Table [dbo].[LedgerEntry] declares partition scheme ps_vault_year_alt, but is currently deployed on
partition scheme ps_vault_year. SchemaSmith does not move an existing table between partition schemes
-- that rewrites every row. Migrate it manually, or correct the declared scheme to match.
```

**PostgreSQL** — exit `2`:

```text
P0001: table public.ledgerentry declares tablespace pg_default, but is currently deployed on vault_ts.
SchemaSmith does not move an existing object to a different tablespace (that is a rewrite) -- migrate
it manually, or correct the declared tablespace to match.
```

**MySQL and MariaDB** — exit `2`:

```text
Declared partitioning does not match the deployed table (refused -- repartitioning rewrites every row):
LedgerEntry declares RANGE(EntryId), deployed RANGE(`EntryYear`)
```

Three engines, three mechanisms, one sentence in three dialects: *moving this rewrites the table, so
name both sides and stop.* Nothing was attempted — check the table and it is exactly where it was.

## Step 3: The distinction that trips everyone

Now remove the placement property from `baseline/` entirely — delete `PartitionScheme` and
`PartitionColumn`, or `Tablespace` — and deploy again.

**Exit 0.** No refusal, and the table has *not* moved.

That is not a bug and it is not a loophole. **Unset means SchemaSmith does not manage placement here.**
It does not mean "the default", and it does not mean "move it back". A table a DBA placed by hand,
in a package that never mentions placement, is left exactly where they put it — which is what makes
this tool safe to point at a database you did not create. Module 4 comes back to this as the doctrine
that makes the whole course coherent; here, just notice that *declaring nothing* and *declaring
something different* are two completely different requests.

## Step 4: Put it back

Restore the baseline declaration and re-quench. Exit 0, nothing changes — you were always in the
state the model described. The refusal never left you halfway.

## What else lives in this family

The same posture, the same reason, across the roster — all applied at CREATE, all refused by name on
a deployed table because the change rewrites the table:

| Property | Engine |
| --- | --- |
| `PartitionScheme` + `PartitionColumn` | SQL Server |
| `Partitioning` (RANGE / LIST / HASH / KEY) | MySQL, MariaDB |
| `FileGroup`, `TextImageFileGroup`, `FileStreamFileGroup` | SQL Server |
| `Tablespace` (table + index) | PostgreSQL |
| `Tablespace` (general) | MySQL |
| `DataDirectory` | MySQL, MariaDB |
| `XmlCompression` | SQL Server |
| InnoDB page compression | MySQL, MariaDB |

And the irreversible table *types*, which behave identically because the engine has no `ALTER` for
them at all: memory-optimized durability and inline-index shape, `Ledger`, `GraphType`.

> **Known gap (SchemaSmith 2.6.0):** on MySQL and MariaDB, adding a **boundary partition** to the
> `Partitions` list — next year's `p2027`, say — is currently neither applied nor refused; the run
> reports success and the partition never appears. That matters because RANGE without a `MAXVALUE`
> catch-all rejects the insert once the calendar reaches it (`ERROR 1526: Table has no partition for
> value 2027`). The comparison covers `Method` and `Expression`, which is what Step 2 exercises. This
> is a reported defect; the lab will be updated when it ships fixed.

## Cleanup

```bash
for e in sqlserver postgres mysql mariadb; do
  ../lab-sql.sh $e vault_m1 "DROP TABLE IF EXISTS LedgerEntry"
done
```

(SQL Server: `DROP TABLE IF EXISTS dbo.LedgerEntry`. PostgreSQL: `public.ledgerentry`.)

## The principle

A declarative tool earns its keep by converging what it can. It earns your *trust* by being explicit
about what it will not. Placement is the cleanest case in the whole product: the declaration is
honoured exactly once, at the moment the table is built, and from then on a disagreement between file
and database is reported rather than resolved — because resolving it means moving your data, and no
file edit should be able to authorise that on its own.

Read the refusal as a design decision, not an obstacle. The next three modules are the same decision
in harder places.
