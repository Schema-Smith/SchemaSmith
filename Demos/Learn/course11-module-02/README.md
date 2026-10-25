# Course 11, Module 2 — State your package never described (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (grants come back; #581 undeclared foreign key): delete the "On 2.7.x" callouts -->
<!-- TRAINING-RELEASE-PIN 2.8.0 (#535, #537, #538, #542): bump the pre-flight to 2.8.0 and delete the "merged, not yet released" note -->

Goal: Module 1 refused a change because *delivering* it would rewrite the table. This module refuses
for a different reason — the change itself is fine, the data is fine, and SchemaSmith still stops,
because the database holds something about your table that **your package does not contain**. Drop it
and there is no recipe to build it back from.

Every hard change in this module comes down to one question: **can I rebuild what I drop?** There are
three answers, and every engine fits on them:

1. **The package** has the recipe — a view you scripted, a policy or an index your table JSON declares.
2. **The catalog** has the recipe — an object the engine describes in full, so SchemaSmith can script it
   from the server whether you declared it or not.
3. **Neither** has it — and the deploy stops, by name, before it drops anything for the change.

Everything runs against `vault_m2`.

> **Merged, not yet released.** Parts 2 and 3 teach SchemaSmith 2.8 behaviour, which is merged to `main`
> and not yet in a tagged release. Where a 2.7.x CLI does something different, an **On 2.7.x** note says
> what.

## Before you start

- The [sandbox](../docker) is up and [`../course11-setup`](../course11-setup) has been run once.
- The CLI is on your PATH: `schemaquench --version` answers **2.7.0 or later**.
- You have run [Course 8, Module 2](../course8-module-02) — its Beat 3 is the CDC rebuild refusal, and
  this module builds on it rather than repeating it.

## Part 1 — the guard family is wider than CDC

Course 8 showed a rebuild refused because Change Data Capture was on. CDC is one member of a family.
`mysql/` and `mariadb/` deploy a **partitioned** `AuditTrail`, then ask for a column reorder — which,
as you know from Course 8, only a rebuild can deliver. From the `mysql` or `mariadb` folder:

```bash
# from course11-module-02/mysql
schemaquench --ConfigFile:quench.settings.baseline.json        # exit 0
schemaquench --ConfigFile:quench.settings.rebuild-blocked.json # exit 2
```

```bash
# from course11-module-02/mariadb
schemaquench --ConfigFile:quench.settings.baseline.json        # exit 0
schemaquench --ConfigFile:quench.settings.rebuild-blocked.json # exit 2
```

```text
Table rebuild refused for `vault_m2`.`AuditTrail`: the table is partitioned. A rebuild replaces the
table with a shadow copy, and that state lives outside the schema package -- the copy discards it and
no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the
blocking state first and re-run.
```

Same sentence as the CDC refusal, different blocking state. That is the point: **the guard is not a
list of special cases, it is one rule applied to whatever the engine is holding.** Change Data Capture,
system versioning, replication, Change Tracking, inheritance, partitioning — whichever of them the
engine in front of you supports.

Ask yourself what a partitioned table's shadow copy would actually lose. The partitioning is a property
of the live object, and the copy is a new object. The tool cannot promise to put back what it never
had written down.

## Part 2 — where the recipe comes from

Now the counterweight: the hard changes SchemaSmith *does* force through, because it can answer *yes*.
One subsection per engine.

### The package, when you opt in (SQL Server)

`sqlserver/` deploys `AuditTrail` plus a **schema-bound view** over it (the `baseline/` package's
`SchemaBound Views/dbo.vw_AuditActors.sql`; nothing to run by hand):

```sql
CREATE OR ALTER VIEW dbo.vw_AuditActors WITH SCHEMABINDING AS
  SELECT AuditId, Actor FROM dbo.AuditTrail;
```

Deploy it twice:

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0 -- nothing to do
```

The first run creates the table and quenches the view script. The second changes nothing, and says so
about the view:

```text
  Unchanged, not re-run: .\baseline\Templates\Main\SchemaBound Views\dbo.vw_AuditActors.sql
```

The script text is what it was and the server still holds the view the last run left, so the script is
not run again.

> **On 2.7.x** the second run quenches the view script again (`Quenched …dbo.vw_AuditActors.sql`):
> 2.7 runs every object script on every deploy.

Now widen `Actor` from `NVARCHAR(64)` to `NVARCHAR(128)`. SQL Server refuses to alter a column while a
schema-bound module references it — error **4922**, which names neither the module nor the remedy.
Deploy `widen-blocked` (the setting off, which is the default) and read what SchemaSmith says instead:

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.widen-blocked.json   # exit 2
```

```text
Column change blocked by SCHEMABINDING: dbo.vw_AuditActors (blocks [dbo].[AuditTrail].[Actor]).
SQL Server will not alter a column while a schema-bound module references it, and SchemaSmith will not
drop a scripted object it cannot put back. Move the listed module(s) into a schema-bound object folder
(QuenchSlot AfterTablesObjects) and set DropSchemaBoundDependents so the deploy can drop them around
the table work and the after-tables object pass recreates them, or remove SCHEMABINDING from them.
```

Exit **2** — and notice it names the module, the column it blocks, and *both* remedies. Compare that to
`Msg 4922`.

Before turning the setting on, give the table two things your package does **not** describe, the way
a DBA would: a `GRANT` on the view, and a schema-bound **row-level security policy** that reads `Actor`.
SQL Server will not alter a column such a policy reads either.

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "EXEC('CREATE FUNCTION dbo.fn_ActorVisible(@Actor NVARCHAR(128)) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS Visible WHERE LEN(@Actor) > 0')"
../lab-sql.sh sqlserver vault_m2 "EXEC('CREATE SECURITY POLICY dbo.pol_Actor ADD FILTER PREDICATE dbo.fn_ActorVisible(Actor) ON dbo.AuditTrail WITH (STATE = ON, SCHEMABINDING = ON)'); GRANT SELECT ON dbo.vw_AuditActors TO public"
../lab-sql.sh sqlserver vault_m2 "SELECT p.name, p.is_enabled, p.is_schema_bound, (SELECT COUNT(*) FROM sys.database_permissions WHERE major_id = OBJECT_ID('dbo.vw_AuditActors') AND permission_name = 'SELECT' AND state_desc = 'GRANT') FROM sys.security_policies p"
# → pol_Actor 1 1 1
```

The policy, enabled and schema-bound, and one `GRANT` on the view. Now turn the setting on.
`quench.settings.widen-allowed.json` carries:

```json
{ "DropSchemaBoundDependents": true }
```

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.widen-allowed.json   # exit 0
```

```text
      Drop schema-bound module dbo.vw_AuditActors - blocks a column change; the after-tables object pass recreates it
      Drop security policy [dbo].[pol_Actor] - it reads a changing column; created again after the column pass
    Create Again The Security Policies Dropped For The Column Pass
  Quenched .\widen-allowed\Templates\Main\SchemaBound Views\dbo.vw_AuditActors.sql
      Re-applying the permissions and extended properties on [dbo].[vw_AuditActors]
```

The view is dropped, the column is widened, and the view is recreated — **from your package**, not from
a copy of what was on the server. That is why the module has to live in a `SchemaBound Views/` folder
on the `AfterTablesObjects` slot: your script is the authority on what the view should be, and it has
to run *after* the table work. A module left in the ordinary `Views/` folder is recreated too early.

The security policy took the other road. It is in no package. SchemaSmith scripted it **from the
catalog** — the server describes a security policy in full, predicates, state and all — dropped it
before the column changed and created it again after. And the `GRANT` your package never carried was
captured before the drop and put back once the view existed again. Check all three, from the lab
folder:

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "SELECT c.name, ty.name, c.max_length/2 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.AuditTrail') AND c.name='Actor'"
# → Actor nvarchar 128
../lab-sql.sh sqlserver vault_m2 "SELECT p.name, p.is_enabled, p.is_schema_bound, (SELECT COUNT(*) FROM sys.database_permissions WHERE major_id = OBJECT_ID('dbo.vw_AuditActors') AND permission_name = 'SELECT' AND state_desc = 'GRANT') FROM sys.security_policies p"
# → pol_Actor 1 1 1
```

> **On 2.7.x** this deploy fails on the security policy with SQL Server's own error 4922 (#538). Without
> the policy, the view comes back from your package but its `GRANT` does not: 2.7 does not carry
> permissions across this drop.

### The package, by default — a changed function (SQL Server)

`DropSchemaBoundDependents` is for a **column** change, and it is off unless you opt in. A changed
**function** is different. `function-baseline/` adds a schema-bound function and a schema-bound view
that calls it:

```sql
-- SchemaBound Functions/dbo.fn_ActorKey.sql
CREATE OR ALTER FUNCTION dbo.fn_ActorKey(@Actor NVARCHAR(128)) RETURNS NVARCHAR(128) WITH SCHEMABINDING AS
BEGIN
  RETURN UPPER(@Actor);
END;

-- SchemaBound Views/dbo.vw_ActorKeys.sql
CREATE OR ALTER VIEW dbo.vw_ActorKeys WITH SCHEMABINDING AS
  SELECT AuditId, dbo.fn_ActorKey(Actor) AS ActorKey FROM dbo.AuditTrail;
```

`change-function/` changes the function's body to `UPPER(LTRIM(RTRIM(@Actor)))`. SQL Server will not
alter a function a schema-bound view is bound to. Neither settings file turns anything on:

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.function-baseline.json   # exit 0
schemaquench --ConfigFile:quench.settings.change-function.json     # exit 0
```

```text
  Unchanged, not re-run: .\change-function\Templates\Main\SchemaBound Views\dbo.vw_ActorKeys.sql
  Dropping schema-bound view dbo.vw_ActorKeys: it is bound to dbo.fn_ActorKey, which .\change-function\Templates\Main\SchemaBound Functions\dbo.fn_ActorKey.sql changes; the run puts it back
  Quenched .\change-function\Templates\Main\SchemaBound Functions\dbo.fn_ActorKey.sql
  Quenched .\change-function\Templates\Main\SchemaBound Views\dbo.vw_ActorKeys.sql
```

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "SELECT CASE WHEN OBJECT_ID('dbo.vw_ActorKeys') IS NULL THEN 'view missing' ELSE 'view present' END, CASE WHEN OBJECT_DEFINITION(OBJECT_ID('dbo.fn_ActorKey')) LIKE '%LTRIM%' THEN 'function changed' ELSE 'function unchanged' END"
# → view present function changed
```

The view was dropped, the function changed, and the view put back in the same run — no setting,
because the view is in your package and so is the function. The opt-in exists for column changes; a
changed module whose dependents you declared needs nothing. The first log line is the object pass
noting that the view's own script did not change; the view still comes back, because the function under
it did.

> **On 2.7.x** the change to `fn_ActorKey` fails: SQL Server refuses to alter a function a schema-bound
> view references (error 3729), and 2.7 does not take the view down around it.

### The package by default, the catalog for the rest (PostgreSQL)

PostgreSQL has the same problem without the opt-in: it will not change a column's type while a view or
a row-level security policy reads it. `postgres/` deploys `audit_trail` with a **declared** policy in
its table JSON, and a view in an ordinary `Views/` folder:

```json
"RowLevelSecurity": true,
"Policies": [ { "Name": "p_actor_present", "UsingExpression": "actor <> ''" } ]
```

```sql
-- Views/public.vw_audit_actors.sql
CREATE OR REPLACE VIEW public.vw_audit_actors AS
SELECT audit_id, actor FROM public.audit_trail;
```

```bash
# from course11-module-02/postgres
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0 -- nothing to do
```

```text
  Unchanged, not re-run: .\baseline\Templates\Main\Views\public.vw_audit_actors.sql
```

> **On 2.7.x** the second run quenches the view script again.

Now add what a DBA would add and the package does not mention — an audit trigger that fires only when
`actor` changes:

```bash
# from course11-module-02
../lab-sql.sh postgres vault_m2 "CREATE FUNCTION public.fn_actor_changed() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'; CREATE TRIGGER trg_actor_changed BEFORE UPDATE OF actor ON public.audit_trail FOR EACH ROW WHEN (NEW.actor IS DISTINCT FROM OLD.actor) EXECUTE FUNCTION public.fn_actor_changed()"
```

Its `UPDATE OF actor` list and its `WHEN` clause both read the column. Widen `actor` to `varchar(128)`,
with no setting at all:

```bash
# from course11-module-02/postgres
schemaquench --ConfigFile:quench.settings.widen.json   # exit 0
```

```text
      Dropping Policy p_actor_present on public.audit_trail
      Dropping Trigger public.audit_trail.trg_actor_changed - it reads a type-changing column; put back after the column pass
      Dropping View public.vw_audit_actors
      Creating Trigger public.audit_trail.trg_actor_changed again
  Quenched .\widen\Templates\Main\Views\public.vw_audit_actors.sql
    Add Missing Row Level Security Policies
```

```bash
# from course11-module-02
../lab-sql.sh postgres vault_m2 "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.audit_trail'::regclass AND attname = 'actor'"
# → character varying(128)
../lab-sql.sh postgres vault_m2 "SELECT (SELECT count(*) FROM pg_policy WHERE polname = 'p_actor_present')::text || ' ' || (SELECT tgenabled FROM pg_trigger WHERE tgname = 'trg_actor_changed')::text || ' ' || (SELECT count(*) FROM pg_views WHERE viewname = 'vw_audit_actors')::text"
# → 1 O 1
```

Three dependents, two sources. The view came back from its script and the policy from the table JSON —
**the package** (the policy has no line of its own; it is recreated in the `Add Missing Row Level Security
Policies` phase). The trigger is in no package: PostgreSQL describes a trigger in full, so SchemaSmith
scripted it **from the catalog**, took it down before the change and put it back after, still enabled
(`O`). A publication with a row filter over the column (PostgreSQL 15 and later) is handled the same
way.

> **On 2.7.x** this deploy fails: 2.7 does not take a row-level security policy or a trigger's `WHEN`
> clause down around a type change, so the engine refuses it (#535).

### A column rename (MySQL)

On MySQL the change that needs a recipe is a **rename**: MySQL will not rename a column that a
functional index reads. Deploy a plain `Contact` table — a separate product, `VaultContacts`, so Part 1's
`AuditTrail` stays out of it — and then give it what a DBA would add for case-insensitive lookups:

```bash
# from course11-module-02/mysql
schemaquench --ConfigFile:quench.settings.rename-baseline.json   # exit 0
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "CREATE INDEX ix_dba_email ON Contact ((LOWER(Email)))"
```

```bash
# from course11-module-02/mysql
schemaquench --ConfigFile:quench.settings.rename-baseline.json   # exit 0 -- the index is left alone
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contact' AND INDEX_NAME = 'ix_dba_email'"
# → 1
```

The deploy leaves `ix_dba_email` alone because `DropUnknownIndexes` defaults to `false`: an index the
package does not mention is not the package's business. Hold that thought — Part 3 renames `Email`.

### MariaDB

MariaDB carries functional and generated-column dependents across a rename by itself, so it has
nothing here for SchemaSmith to rebuild. Its refusal is in Part 3, beside MySQL's: a foreign key nobody
declared.

### Three sources

| Source | SQL Server | PostgreSQL | MySQL |
|---|---|---|---|
| **The package** | schema-bound views and functions in the after-tables folders — for a column change with `DropSchemaBoundDependents`, for a changed function by default | declared views, routines, materialized views and policies, by default | declared functional indexes and generated columns, by default |
| **The catalog** | a schema-bound security policy | a trigger's `WHEN` / `UPDATE OF`, a publication's row filter | — |
| **Neither** | refused before the drop (Part 3) | refused before the drop (Part 3) | refused before the rename, and before a foreign key is dropped (Part 3; MariaDB too) |

## Part 3 — neither: refused before the drop

### An undeclared view (SQL Server)

Start again from the baseline's `NVARCHAR(64)`. The reset drops Part 2's policy and functions too — a
schema-bound policy on `AuditTrail` would otherwise block the `DROP TABLE`:

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "DROP SECURITY POLICY IF EXISTS dbo.pol_Actor; DROP VIEW IF EXISTS dbo.vw_ActorKeys, dbo.vw_AuditActors; DROP FUNCTION IF EXISTS dbo.fn_ActorKey, dbo.fn_ActorVisible; DROP TABLE IF EXISTS dbo.AuditTrail"
```

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0 -- back to NVARCHAR(64)
```

Now a second schema-bound view over `Actor`, made by hand and in no package:

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "EXEC('CREATE VIEW dbo.vw_DbaActors WITH SCHEMABINDING AS SELECT AuditId, Actor FROM dbo.AuditTrail')"
```

Deploy the same widening as Part 2, setting on:

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.widen-allowed.json   # exit 2
```

```text
Column change blocked by SCHEMABINDING: dbo.vw_DbaActors, which the package does not declare, so
nothing in this run could put it back and SchemaSmith will not drop it. Add its script to a
schema-bound object folder (QuenchSlot AfterTablesObjects), or remove SCHEMABINDING from it.
Nothing was changed.
```

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "SELECT c.name, ty.name, c.max_length/2 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.AuditTrail') AND c.name='Actor'"
# → Actor nvarchar 64
../lab-sql.sh sqlserver vault_m2 "SELECT COUNT(*) FROM sys.views WHERE name IN ('vw_AuditActors', 'vw_DbaActors')"
# → 2
```

Both views are still there — including `vw_AuditActors`, which the package *could* have rebuilt. The
refusal fires before the deploy drops anything for this change, not after dropping the one it could
put back.

> **On 2.7.x** this deploy drops `vw_DbaActors` along with `vw_AuditActors`, widens the column, and
> exits 0 with `vw_DbaActors` gone for good (#542).

### An encrypted view (SQL Server)

Remove the hand-made view, make `vw_AuditActors` **encrypted**, and try the same widening with the
setting still on:

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "DROP VIEW IF EXISTS dbo.vw_DbaActors"
```

```bash
# from course11-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.encrypt-view.json   # exit 0 -- the view is now WITH ENCRYPTION
schemaquench --ConfigFile:quench.settings.encrypted.json      # exit 2
```

```text
Column change blocked by an ENCRYPTED schema-bound module: dbo.vw_AuditActors. Its definition cannot
be read from the server, so nothing can recreate it once dropped -- DropSchemaBoundDependents
deliberately does not extend to it. Remove SCHEMABINDING or the encryption from the listed module(s).
```

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "SELECT c.name, ty.name, c.max_length/2 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.AuditTrail') AND c.name='Actor'"
# → Actor nvarchar 64
../lab-sql.sh sqlserver vault_m2 "SELECT COUNT(*) FROM sys.views WHERE name = 'vw_AuditActors'"
# → 1
```

The view is still there, `Actor` is still `NVARCHAR(64)`, and **nothing was dropped**.

Read why. `OBJECT_DEFINITION` returns `NULL` for an encrypted module — the server will not give the
text back to SchemaSmith, to SchemaTongs, or to you. So the one guarantee the whole feature rests on,
*"the after-tables pass will recreate it"*, cannot be made. And the moment it cannot be made, the setting stops applying. An opt-in that
quietly became unrecoverable in one case would be worse than no opt-in at all.

### An undeclared view (PostgreSQL)

Start again from the baseline's `varchar(64)`, then add a report view the package knows nothing about:

```bash
# from course11-module-02
../lab-sql.sh postgres vault_m2 "DROP VIEW IF EXISTS public.vw_dba_report, public.vw_audit_actors; DROP TABLE IF EXISTS public.audit_trail; DROP FUNCTION IF EXISTS public.fn_actor_changed()"
```

```bash
# from course11-module-02/postgres
schemaquench --ConfigFile:quench.settings.baseline.json   # exit 0 -- back to varchar(64)
```

```bash
# from course11-module-02
../lab-sql.sh postgres vault_m2 "CREATE VIEW public.vw_dba_report AS SELECT actor FROM public.audit_trail"
```

```bash
# from course11-module-02/postgres
schemaquench --ConfigFile:quench.settings.widen.json   # exit 2
```

```text
A column type change in this package has to drop and re-create what reads that column, and the
package does not declare: view public.vw_dba_report. Declare it in the package so it can be put back,
or drop it in a Before script. Nothing was changed.
```

```bash
# from course11-module-02
../lab-sql.sh postgres vault_m2 "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'public.audit_trail'::regclass AND attname = 'actor'"
# → character varying(64)
../lab-sql.sh postgres vault_m2 "SELECT count(*) FROM pg_views WHERE viewname IN ('vw_audit_actors', 'vw_dba_report')"
# → 2
```

This refusal is new. Before it, the same deploy dropped `vw_dba_report` to make room for the type
change and nothing put it back: a successful deploy, minus a view. A tool that *learned* to refuse is
the clearest case in this course of what the refusals are for.

> **On 2.7.x** nothing checks for an undeclared view: a deploy that reaches the type change drops
> `vw_dba_report` and does not put it back (#535).

### An undeclared functional index (MySQL)

Part 2 left `ix_dba_email` on `Contact`, unmanaged. `rename-blocked/` renames `Email` to `Mail`
(`"OldName": "Email"`) and still says nothing about the index:

```bash
# from course11-module-02/mysql
schemaquench --ConfigFile:quench.settings.rename-blocked.json   # exit 2
```

The log names the index and both remedies:

```text
  A column rename would break index Contact.ix_dba_email, which the package does not declare. Declare it in the table's JSON, or drop it in a Before script.
```

and the run ends on:

```text
A column rename would break undeclared index ix_dba_email on Contact; see the deploy log.
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contact'"
# → Id,Email
../lab-sql.sh mysql vault_m2 "SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contact' AND INDEX_NAME = 'ix_dba_email'"
# → 1
```

Nothing renamed, index intact. The index was left alone in Part 2 *because* it was unmanaged — and for
exactly the same reason, SchemaSmith cannot rebuild it over a new column name. So write it down.
`rename-declared/` is `rename-blocked/` plus the index, over the new name:

```json
{ "Name": "ix_dba_email", "IndexColumns": "(lower(`Mail`))" }
```

```bash
# from course11-module-02/mysql
schemaquench --ConfigFile:quench.settings.rename-declared.json   # exit 0
```

```text
  Drop functional index `ix_dba_email` on `Contact` ahead of a column rename
  Rename column `Email` to `Mail` on ``Contact``
  Create index: Contact.ix_dba_email
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contact'"
# → Id,Mail
../lab-sql.sh mysql vault_m2 "SELECT EXPRESSION FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Contact' AND INDEX_NAME = 'ix_dba_email'"
# → lower(`Mail`)
```

Declaring the index adopted it. Now the package has the recipe, so the deploy drops the index ahead of
the rename and builds it again over `Mail`. That is Module 4's rule arriving early: *unset means
unmanaged* — and unmanaged is something SchemaSmith will not destroy on your behalf.

> **On 2.7.x** both renames fail with MySQL's own refusal ("has a functional index dependency"): 2.7
> does not take a functional index down around a rename (#537).

### An undeclared foreign key (MySQL, MariaDB)

A different product — call it Payments — owns two tables, `Account` and `Payment`. Its package declares both,
and their indexes, but not the foreign key between them: a DBA added that by hand one afternoon.

```bash
# from course11-module-02/<engine>
schemaquench --ConfigFile:quench.settings.fk-baseline.json   # exit 0
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "ALTER TABLE Payment ADD CONSTRAINT fk_dba_payment_account FOREIGN KEY (AccountCode) REFERENCES Account (Code)"
../lab-sql.sh mariadb vault_m2 "ALTER TABLE Payment ADD CONSTRAINT fk_dba_payment_account FOREIGN KEY (AccountCode) REFERENCES Account (Code)"
```

Now the package moves both code columns to a case-sensitive collation, `utf8mb4_bin` (`fk-collate/`). Neither
engine changes a collation under a foreign key, so the key has to come off first, and SchemaSmith can only put
back a key it has a recipe for:

```bash
# from course11-module-02/<engine>
schemaquench --ConfigFile:quench.settings.fk-collate.json   # exit 2
```

```text
  Foreign key Payment.fk_dba_payment_account blocks a column type or collation change and is not in the package, so it would be dropped and not put back. Declare it in the package, or drop it before the deploy.
```

```bash
# from course11-module-02
../lab-sql.sh mysql vault_m2 "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fk_dba_payment_account'"
# → 1
../lab-sql.sh mariadb vault_m2 "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS WHERE TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'fk_dba_payment_account'"
# → 1
```

The key is still there and no column changed. Before 2.8 this deploy dropped the key, changed both columns, and
exited `0` — the key was simply gone, and nothing in the log said it would not be coming back (#581). Write it
down in `Payment.json`:

<!-- walk[mysql]: python -I "$WALK_TOOLS/jedit.py" "mysql/fk-collate/Templates/Main/Tables/Payment.json" set ForeignKeys '[{"Name": "fk_dba_payment_account", "Columns": "AccountCode", "RelatedTable": "Account", "RelatedColumns": "Code"}]' -->
<!-- walk[mariadb]: python -I "$WALK_TOOLS/jedit.py" "mariadb/fk-collate/Templates/Main/Tables/Payment.json" set ForeignKeys '[{"Name": "fk_dba_payment_account", "Columns": "AccountCode", "RelatedTable": "Account", "RelatedColumns": "Code"}]' -->
```json
"ForeignKeys": [
  { "Name": "fk_dba_payment_account", "Columns": "AccountCode", "RelatedTable": "Account", "RelatedColumns": "Code" }
]
```

```bash
# from course11-module-02/<engine>
schemaquench --ConfigFile:quench.settings.fk-collate.json   # exit 0
```

```text
  Drop FK for column collation change: ALTER TABLE `vault_m2`.`Payment` DROP FOREIGN KEY `fk_dba_payment_account`
  Modify column: ALTER TABLE `vault_m2`.`Account` MODIFY COLUMN `Code` VARCHAR(10) COLLATE utf8mb4_bin NOT NULL
  Modify column: ALTER TABLE `vault_m2`.`Payment` MODIFY COLUMN `AccountCode` VARCHAR(10) COLLATE utf8mb4_bin NULL
  Create FK: Payment.fk_dba_payment_account
```

Down, change, back up. The same rule as the index above: once the package has the recipe, SchemaSmith takes the
key down around the change and builds it again.

> **On 2.7.x** the `fk-collate` deploy exits `0` with the key dropped and never put back.

## Cleanup

```bash
# from course11-module-02
../lab-sql.sh sqlserver vault_m2 "DROP SECURITY POLICY IF EXISTS dbo.pol_Actor; DROP VIEW IF EXISTS dbo.vw_DbaActors, dbo.vw_ActorKeys, dbo.vw_AuditActors; DROP FUNCTION IF EXISTS dbo.fn_ActorKey, dbo.fn_ActorVisible; DROP TABLE IF EXISTS dbo.AuditTrail"
../lab-sql.sh postgres vault_m2 "DROP VIEW IF EXISTS public.vw_dba_report, public.vw_audit_actors; DROP TABLE IF EXISTS public.audit_trail; DROP FUNCTION IF EXISTS public.fn_actor_changed()"
../lab-sql.sh mysql vault_m2 "DROP TABLE IF EXISTS Payment, Account, AuditTrail, Contact"
../lab-sql.sh mariadb vault_m2 "DROP TABLE IF EXISTS Payment, Account, AuditTrail"
```

## The principle

Three postures toward the same table in one module. It **converged** the column when nothing was in
the way. It **refused** when a rebuild would have discarded state the package never described. And in
between, it **acted** — dropping and rebuilding dependents it *could* put back.

That middle case is what makes the other two coherent. SchemaSmith is not refusing because it is
cautious; it is answering a specific, checkable question: *can I rebuild what I drop?* The recipe is in
your package — a view you scripted, a policy or an index you declared. Or it is in the catalog — a
security policy, a trigger, which the engine describes in full. Or it is in neither — an encrypted
module, a view someone made by hand, an index nobody declared, a CDC configuration, a partitioning
layout — and the deploy stops, names it, and tells you how to give it a recipe.
