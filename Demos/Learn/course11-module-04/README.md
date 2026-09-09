# Course 11, Module 4 — Unset means unmanaged (lab)

Goal: the doctrine underneath the whole course, in one A/B test.

**Omitting a property means SchemaSmith does not manage it. It never means "set it to the default."**

That one sentence is what lets you point a declarative deploy tool at a database somebody else has been
tuning for years without it undoing their work on the first run. This module proves it on a single
table, twice, changing exactly one thing.

Runs against `vault_m4`.

## Before you start

- The [sandbox](../docker) is up and [`../course11-setup`](../course11-setup) has been run once — it
  created the `vault_ts` tablespace this module uses.
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.

## Step 1: A DBA places a table by hand

Before your package ever runs, someone put this table on a dedicated tablespace, for reasons of their
own — storage, IO separation, a retention policy, an audit requirement. You were not consulted, and
that is normal.

```bash
../lab-sql.sh postgres vault_m4 "CREATE TABLE public.retaineddocument (docid BIGINT NOT NULL PRIMARY KEY, body TEXT) TABLESPACE vault_ts"
../lab-sql.sh postgres vault_m4 "SELECT COALESCE(t.spcname,'pg_default') FROM pg_class c LEFT JOIN pg_tablespace t ON t.oid=c.reltablespace WHERE c.relname='retaineddocument'"
# → vault_ts
```

## Step 2: Deploy a package that never mentions placement

`unmanaged/` declares the table — columns, primary key — and has **no `Tablespace` key at all**:

```json
{
  "Schema": "public", "Name": "retaineddocument",
  "Columns": [ { "Name": "docid", "DataType": "int8", "Nullable": false },
               { "Name": "body",  "DataType": "text", "Nullable": true } ],
  "Indexes": [ { "Name": "pk_retaineddocument", "IndexColumns": "docid", "PrimaryKey": true, "Unique": true } ]
}
```

```bash
cd postgres && schemaquench --ConfigFile:quench.settings.unmanaged.json ; cd ..    # exit 0
../lab-sql.sh postgres vault_m4 "SELECT COALESCE(t.spcname,'pg_default') FROM pg_class c LEFT JOIN pg_tablespace t ON t.oid=c.reltablespace WHERE c.relname='retaineddocument'"
# → vault_ts
```

Exit 0, and **the table is still on `vault_ts`**.

Sit with what did *not* happen. The obvious alternative reading of an absent property is "the default" —
and under that reading, this deploy would have moved the table to `pg_default`, because the package
implicitly said so by saying nothing. On a real archive, that is a rewrite of everything, on a Tuesday,
triggered by a package that never mentioned tablespaces at all.

## Step 3: Change exactly one thing

`declared/` is the same file with **one key added**:

```json
{ "Tablespace": "pg_default" }
```

```bash
cd postgres && schemaquench --ConfigFile:quench.settings.declared.json ; echo "exit=$?" ; cd ..
```

```text
P0001: table public.retaineddocument declares tablespace pg_default, but is currently deployed on
vault_ts. SchemaSmith does not move an existing object to a different tablespace (that is a rewrite)
-- migrate it manually, or correct the declared tablespace to match.
```

Exit **2**. Same table, same database, same deployed state — and the opposite outcome, because the
package now *claims* placement and disagrees about it.

That is the whole module. **Silence is not a value.** Declaring nothing and declaring the default are
different statements, and SchemaSmith treats them differently:

| Package says | Meaning | Result here |
| --- | --- | --- |
| *nothing* | "placement is not mine to manage" | exit 0, left alone |
| `"Tablespace": "pg_default"` | "placement is mine, and it should be `pg_default`" | exit 2, refused as a move |

## Step 4: Where else the doctrine holds

Not a special case for tablespaces — the same reading applies across the product:

| Property | Unset means |
| --- | --- |
| `Tablespace` (PostgreSQL table + index, MySQL general) | placement unmanaged; a hand-placed object stays put |
| `DataDirectory` (MySQL, MariaDB) | placement unmanaged, **not** "has none" |
| `PartitionScheme` / `Partitioning` | partitioning unmanaged; a hand-partitioned table is left exactly as it is |
| `Encryption` / `Encrypted` | encryption unmanaged; a tablespace someone encrypted stays encrypted |
| `ReplicaIdentity` (PostgreSQL) | leave the server's current setting alone |
| `Statistics` | only named statistics your product defines; **auto-created statistics are never touched** |

Read that list as one idea rather than six: your package's authority extends exactly as far as what it
declares, and no further.

## Cleanup

```bash
../lab-sql.sh postgres vault_m4 "DROP TABLE IF EXISTS public.retaineddocument"
```

## The principle — and the course

Four modules, four reasons a declarative tool stops, and one rule underneath all of them:

> **SchemaSmith will rebuild what it declares, and will not destroy what it does not.**

Module 1: placement is applied at CREATE and refused on change, because a diff of two layouts cannot
tell a SPLIT from a MERGE. Module 2: a rebuild is refused when the copy would discard state the package
never described — and `DropSchemaBoundDependents` shows the tool acting freely where it *can* rebuild
what it drops. Module 3: a partitioned table is never dropped by absence, even though you asked, because
a deleted file is a likelier mistake than the loss is a decision.

And this module is why those three are coherent rather than arbitrary. Every one of them turns on the
same question — *is the thing I am about to disturb written down in the model?* — and "unset means
unmanaged" is what keeps the answer honest. A property you never declared was never yours to change, so
it never enters the comparison, so it can never be lost to a deploy you did not intend.

That is what makes a tool safe to point at a database you did not build: not that it does less, but
that it knows exactly what it has been told to own.
