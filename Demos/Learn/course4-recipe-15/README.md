# Course 4, Recipe 15 — The re-run that wasn't a no-op: expression change detection (lab)

Goal: prove that a second deploy of an unchanged package changes **nothing** — on the objects where that used to
be a lie. Every engine rewrites an expression when it stores it. SQL Server keeps `RetentionDays <= 365` as
`([RetentionDays]<=(365))`; PostgreSQL keeps `starts_with(plan_code, 'P')` as
`starts_with((plan_code)::text, 'P'::text)`; MySQL adds backticks and charset introducers to a generated column.
Compare what you wrote against what the catalog hands back and they never match — so before 2.7.0 a check
constraint, a computed column or a generated column could be dropped and rebuilt on **every** deploy, at exit 0,
with nothing in the log saying why. On a `PERSISTED` or `STORED` column that is a table rewrite every time.

SchemaSmith 2.7.0 decides from what it **applied** rather than from the text. This lab makes you watch both the
old behaviour and the new one, on the same table, on all four engines.

Everything runs against `cookbook_r15`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r15`).
- The CLI is on your PATH: `schemaquench --version` answers **2.7.0 or later**.

## The table

Each engine's package declares one table, `Subscription`, written the way people actually write it — no
brackets, no casts, natural spacing:

| Engine | Expression-bearing objects |
| --- | --- |
| SQL Server | `CHECK (RetentionDays <= 365)`, a `PERSISTED` computed column `RetentionDays / 7`, `DEFAULT SYSUTCDATETIME()`, a filtered index `WHERE IsActive = 1` |
| PostgreSQL | `CHECK (starts_with(plan_code, 'P'))`, a generated column `retention_days / 7`, a partial index `WHERE is_active` |
| MySQL / MariaDB | a `STORED` generated column `RetentionDays DIV 7` and a `VIRTUAL` one `concat(PlanCode, '-', RetentionDays)` |

The PostgreSQL generated column declares only its `GenerationExpression` — `Generated` can be left out, since
an expression can only mean `ALWAYS`. None of the generated columns declares `Nullable`; see the note at the end.

## Step 1: Deploy it twice

```bash
cd <engine>
schemaquench --ConfigFile:deploy.settings.json                     # exit 0 -- builds the table
schemaquench --ConfigFile:deploy.settings.json --report:./rerun    # exit 0
cd ..
```

`--report:` writes a deployment summary (`rerun.json` and `rerun.md`). Open `rerun.md` and read **Object Changes**:

```
## Object Changes
- Created: tables=0, columns=0, indexes=0, constraints=0, foreignKeys=0, procedures=0, views=0, functions=0
- Modified: tables=0, columns=0
- Dropped: tables=0, indexes=0, constraints=0, foreignKeys=0
```

Zeros, on every engine. That is the claim this whole recipe is about, and the report is the evidence — not the
absence of scary lines in a log you skimmed.

## Step 2: See what SchemaSmith recorded

The answer lives in a table SchemaSmith kindles into every target: `SchemaSmith.ExpressionMap`
(`SchemaSmith_ExpressionMap` on MySQL and MariaDB). One row per expression — what your package **authored**,
what the engine **stored** after applying it, and the engine version (and on SQL Server the compatibility level)
in force at the time:

```bash
../lab-sql.sh sqlserver cookbook_r15 "SELECT ObjectName, Slot, AuthoredText, CanonicalText, CompatLevel FROM SchemaSmith.ExpressionMap"
# → CK_Subscription_Retention   expression  RetentionDays <= 365   ([RetentionDays]<=(365))   160
# → CreatedAt                   default     SYSUTCDATETIME()       (sysutcdatetime())         160
# → IsActive                    default     1                      ((1))                      160
# → RetentionWeeks              computed    RetentionDays / 7      ([RetentionDays]/(7))      160
# → IX_Subscription_ActivePlan  filter      IsActive = 1           [IsActive]=(1)             160

../lab-sql.sh postgres cookbook_r15 'SELECT "ObjectName", "AuthoredText", "CanonicalText" FROM "SchemaSmith"."ExpressionMap"'
# → ck_subscription_plan         starts_with(plan_code, 'P')   starts_with((plan_code)::text, 'P'::text)
# → retention_weeks              retention_days / 7                 (retention_days / 7)
# → ix_subscription_active_plan  is_active                          is_active

../lab-sql.sh mysql cookbook_r15 "SELECT ObjectName, AuthoredText, CanonicalText FROM SchemaSmith_ExpressionMap"
# → PlanLabel       concat(PlanCode, '-', RetentionDays)   concat(`PlanCode`,_utf8mb4\\'-\\',`RetentionDays`)
# → RetentionWeeks  RetentionDays DIV 7                    (`RetentionDays` DIV 7)
```

Look at the two text columns side by side. No amount of whitespace trimming or paren stripping reconciles
them — MySQL added a charset introducer, PostgreSQL added casts. That gap is exactly what used to churn.

On the next deploy SchemaSmith asks one question per object: *does the live text still match what I recorded
for this declaration?* If yes, nothing moved and nothing is touched, however differently the two texts read.

## Step 3: Watch what 2.6.0 did

The table is safe to empty — the worst it costs is one more comparison. Emptying it also shows you precisely how
a deploy behaved before 2.7.0, because **no record means no opinion**: the comparison falls back to the text.

```bash
../lab-sql.sh sqlserver cookbook_r15 "DELETE FROM SchemaSmith.ExpressionMap"
cd sqlserver && schemaquench --ConfigFile:deploy.settings.json --report:./norecord ; cd ..
```

```
Dropping index [dbo].[Subscription].IX_Subscription_ActivePlan
Dropping columns from [dbo].[Subscription] ([RetentionWeeks])
Dropping check constraint [dbo].[Subscription].CK_Subscription_Retention
Adding 1 new column(s) to [dbo].[Subscription]
Creating index [dbo].[Subscription].[IX_Subscription_ActivePlan]
Adding check constraint [dbo].[Subscription].CK_Subscription_Retention
```

Nothing in the package changed. The computed column was dropped and re-added — a `PERSISTED` column, so every
row was rewritten — and the index and check went with it. Before 2.7.0 that happened on **every** deploy. The
other engines do the same thing in their own dialect:

```
PostgreSQL:  Check Constraint public.subscription.ck_subscription_plan modified or no longer in product
             Recreating generated column public.subscription.retention_weeks (expression changed)
             Add missing check constraint public.subscription.ck_subscription_plan
MySQL:       Modify column: ALTER TABLE `cookbook_r15`.`Subscription` MODIFY COLUMN `PlanLabel` ... VIRTUAL
             Modify column: ALTER TABLE `cookbook_r15`.`Subscription` MODIFY COLUMN `RetentionWeeks` ... STORED
```

That deploy re-recorded everything as it went. Deploy once more and the report is back to zeros — the churn
happens at most once, on the first deploy that has nothing recorded.

## Step 4: Edit it behind SchemaSmith's back

Going quiet on churn would be dangerous if it also went blind to drift. Change a live object by hand — the way
someone fixes a production issue at 2am — and leave the package alone:

```bash
../lab-sql.sh sqlserver cookbook_r15 "ALTER TABLE dbo.Subscription DROP CONSTRAINT CK_Subscription_Retention; ALTER TABLE dbo.Subscription ADD CONSTRAINT CK_Subscription_Retention CHECK (RetentionDays <= 3650)"
cd sqlserver && schemaquench --ConfigFile:deploy.settings.json ; cd ..
#         Dropping check constraint [dbo].[Subscription].CK_Subscription_Retention
#         Adding check constraint [dbo].[Subscription].CK_Subscription_Retention
```

The live text no longer matches what SchemaSmith recorded, so the declaration is put back. The comparison is
not one-sided: it trusts neither the package text nor the catalog text, it checks both against the record.

## Step 5: Change the declaration

Edit the `Expression` in `dbo.Subscription.json` to `RetentionDays <= 730` and re-quench:

```
Dropping check constraint [dbo].[Subscription].CK_Subscription_Retention
Adding check constraint [dbo].[Subscription].CK_Subscription_Retention
```

Applied, recorded, and the next deploy is quiet again. An edit you make is always an edit.

## Step 6 (SQL Server): change the compatibility level

SQL Server freezes an expression's stored text at the compatibility level it was created under, so a database
raised from 150 to 160 would hold both forms for years. Re-applying every expression-bearing object the first
time you deploy after that change would be a far bigger event than the churn it prevents. Try it:

```bash
../lab-sql.sh sqlserver master "ALTER DATABASE cookbook_r15 SET COMPATIBILITY_LEVEL = 150"
cd sqlserver && schemaquench --ConfigFile:deploy.settings.json --report:./compat ; cd ..
```

```
Re-baselined 5 recorded expression(s): they were recorded under a different SQL Server version or compatibility
level, and their stored canonical text is now refreshed for version 16.0.4260.1 at compatibility level 150. No
object was changed.
```

Zeros in the report. SchemaSmith **re-baselines** — it re-reads the live text and updates its record — and it
says so, with a count and the version, so an engine upgrade shows up in the first deploy after it rather than
as a mystery. Set it back to `160` and you get the same line for 160.

## Step 7 (optional): the PostgreSQL floor

The PostgreSQL generated-column churn is worst at the supported floor — it reproduces on PostgreSQL 12 and not
on 17, so a modern server alone would never show you the bug. If you run Course 10's mixed fleet
(`docker compose --profile mixed-fleet up -d`), point a copy of `postgres/deploy.settings.json` at port `15433`,
create `cookbook_r15` there, and repeat Steps 1 and 3: the same zeros on the rerun, the same single
re-application with the record emptied.

## A note on nullability

None of the generated or computed columns here declares `Nullable`, and that is deliberate. On a computed or
generated column an **omitted** `Nullable` leaves nullability to the engine — and the engine makes it nullable —
so SchemaSmith never narrows it. Declare `"Nullable": false` when you genuinely want `NOT NULL` (SQL Server on a
`PERSISTED` column, PostgreSQL and MySQL on any generated column; MariaDB cannot declare it at all). Asking for
`NOT NULL` on a table whose existing rows make the expression NULL fails the deploy, which is the right answer.

## Cleanup

```bash
../lab-sql.sh sqlserver cookbook_r15 "ALTER DATABASE cookbook_r15 SET COMPATIBILITY_LEVEL = 160; DROP TABLE IF EXISTS dbo.Subscription"
../lab-sql.sh postgres  cookbook_r15 "DROP TABLE IF EXISTS public.subscription"
for e in mysql mariadb; do ../lab-sql.sh $e cookbook_r15 "DROP TABLE IF EXISTS Subscription"; done
```

## The principle

"Re-run and nothing changes" is only worth something if it is true for the objects that are hard to get right.
A comparison built on text cannot win against an engine that rewrites what you give it — the rewrite is a fixed
point, and no normaliser catches every one. So SchemaSmith stopped asking the text and started asking what it
did: an unchanged declaration is left alone, a changed one is applied, a hand-edited object is put back, and an
engine upgrade is recorded rather than replayed.
