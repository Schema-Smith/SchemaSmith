# Course 4, Recipe 2 — Policy that enforces itself (lab)

Goal: declare your retention policy **once**, as nested custom-property metadata on the table, and have it
drive two things at deploy time — a column's `Default` and a check constraint that enforces a ceiling.
Change the policy number, re-quench, and the column **default follows** the new value. The numbers live in
one place; the schema reads them.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the full `Package/` plus `deploy.settings.json`,
all targeting `cookbook_r2`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup) once — it creates `cookbook_r2`).
- The CLI is on your PATH (`schemaquench --version` → `2.7.0.0` or later).

## Step 1: Look at the policy and what it drives

Open `Tables/…Document.json`. The table carries a nested custom property with two numbers:

```json
"Extensions": { "Retention": { "ArchiveDays": "90", "MaxRetentionDays": "365" } }
```

Each is read back through a `{{Table.Retention.*}}` token (nested values flatten with dots):

- the `ArchiveAfterDays` column default reads `{{Table.Retention.ArchiveDays}}` → a default of `90`;
- the check constraint reads `{{Table.Retention.MaxRetentionDays}}` → enforces `RetentionDays <= 365`.

You never typed `90` or `365` into the default or the check. Both read from the one place you declared them.

## Step 2: Deploy and watch the policy take hold

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json
```

The default and the check are both built from the metadata:

```bash
# SQL Server
cd ..            # back to the lab folder
../lab-sql.sh sqlserver cookbook_r2 "SELECT definition FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.Document')"
# → ([RetentionDays]<=(365))
```

Prove the default applies and the check enforces:

```sql
-- omit ArchiveAfterDays → it defaults to 90
INSERT INTO Document (DocumentId, Title, RetentionDays) VALUES (1, 'Contract', 50);    -- OK
-- exceed the ceiling → the check rejects it
INSERT INTO Document (DocumentId, Title, ArchiveAfterDays, RetentionDays) VALUES (2, 'Forever', 90, 500);
-- → CHECK constraint violation (Msg 547 / "violates check constraint" / Error 3819)
```

## Step 3: Change the archive policy once — the default follows

Edit the metadata — `ArchiveDays` from `90` to `30` — and re-quench:

```bash
cd <engine>       # back into the engine folder
schemaquench --ConfigFile:deploy.settings.json
```

The column default re-derives from the new number, identically on all four engines:

```bash
# PostgreSQL
cd ..            # back to the lab folder
../lab-sql.sh postgres cookbook_r2 "SELECT column_default FROM information_schema.columns WHERE table_name='document' AND column_name='archiveafterdays'"
# → 30
```

You didn't touch the column. You changed the *policy on the table*, and the next ordinary quench rebuilt the
default from it. (The check still enforces `MaxRetentionDays`, which you didn't change — each number drives its
own object, and each lives in exactly one place.)

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Nested tokens | `{{Table.Retention.ArchiveDays}}` / `…MaxRetentionDays` | same | same |
| Default from metadata | `DEFAULT ((90))` | `DEFAULT 90` | `DEFAULT 90` |
| Check from metadata | `([RetentionDays]<=(365))` | `CHECK ((retentiondays <= 365))` | `` (`RetentionDays` <= 365) `` |
| Default follows a policy change | ✅ | ✅ | ✅ |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.*

The `Default` and the check are both authored once on the table and resolve identically across all four
engines; only the dialect's constraint syntax differs.

## The principle

The retention numbers used to live in three places each — the column default, the constraint, and a wiki page
nobody updated. Here each lives in exactly one: a custom property on the table. The default reads its number,
the check reads its number, and when a policy changes you change the *policy*, not the columns. Declare the rule
once on the object; let the schema carry it.
