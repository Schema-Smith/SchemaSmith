# Course 11, Module 2 — State your package never described (lab)

Goal: Module 1 refused a change because *delivering* it would rewrite the table. This module refuses
for a different reason — the change itself is fine, the data is fine, and SchemaSmith still stops,
because the database knows something about your table that **your package does not contain**. Drop it
and there is no recipe to build it back from.

Then the half that keeps this from being a course about a timid tool: the case where SchemaSmith
**does** force a hard change through, and the case where it still will not.

Everything runs against `vault_m2`.

## Before you start

- The [sandbox](../docker) is up and [`../course11-setup`](../course11-setup) has been run once.
- The CLI is on your PATH: `schemaquench --version` answers **2.6.0 or later**.
- You have run [Course 8, Module 2](../course8-module-02) — its Beat 3 is the CDC rebuild refusal, and
  this module builds on it rather than repeating it.

## Part 1 — the guard family is wider than CDC

Course 8 showed a rebuild refused because Change Data Capture was on. CDC is one member of a family.
`mysql/` and `mariadb/` deploy a **partitioned** `AuditTrail`, then ask for a column reorder — which,
as you know from Course 8, only a rebuild can deliver:

```bash
cd <engine>
schemaquench --ConfigFile:quench.settings.baseline.json        # exit 0
schemaquench --ConfigFile:quench.settings.rebuild-blocked.json # exit 2
cd ..
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

Ask yourself what a partitioned table's shadow copy would actually lose. The partitioning is not in
your package on the *shadow* — it is a property of the live object, and the copy is a new object. The
tool cannot promise to put back what it never had written down.

## Part 2 — where SchemaSmith *does* force it through

Now the counterweight. `sqlserver/` deploys `AuditTrail` plus a **schema-bound view** over it:

```sql
CREATE OR ALTER VIEW dbo.vw_AuditActors WITH SCHEMABINDING AS
  SELECT AuditId, Actor FROM dbo.AuditTrail;
```

```bash
cd sqlserver && schemaquench --ConfigFile:quench.settings.baseline.json ; cd ..   # exit 0
```

Now widen `Actor` from `NVARCHAR(64)` to `NVARCHAR(128)`. SQL Server refuses to alter a column while a
schema-bound module references it — error **4922**, which names neither the module nor the remedy.
Deploy `widen-blocked` (the setting off, which is the default) and read what SchemaSmith says instead:

```text
Column change blocked by SCHEMABINDING: dbo.vw_AuditActors (blocks [dbo].[AuditTrail].[Actor]).
SQL Server will not alter a column while a schema-bound module references it, and SchemaSmith will not
drop a scripted object it cannot put back. Move the listed module(s) into a schema-bound object folder
(QuenchSlot AfterTablesObjects) and set DropSchemaBoundDependents so the deploy can drop them around
the table work and the after-tables object pass recreates them, or remove SCHEMABINDING from them.
```

Exit **2** — and notice it names the module, the column it blocks, and *both* remedies. Compare that to
`Msg 4922`.

Now turn the setting on. `quench.settings.widen-allowed.json` carries:

```json
{ "DropSchemaBoundDependents": true }
```

```bash
cd sqlserver && schemaquench --ConfigFile:quench.settings.widen-allowed.json ; cd ..   # exit 0
```

```text
  Drop schema-bound module dbo.vw_AuditActors - blocks a column change; the after-tables object pass recreates it
  Quenched .\widen-allowed\Templates\Main\SchemaBound Views\dbo.vw_AuditActors.sql
```

The view is dropped, the column is widened, and the view is recreated — **from your package**, not from
a copy of what was on the server. That is why the module has to live in a `SchemaBound Views/` folder
on the `AfterTablesObjects` slot: your script is the authority on what the view should be, and it has
to run *after* the table work. A module left in the ordinary `Views/` folder is recreated too early.

Check it:

```bash
../lab-sql.sh sqlserver vault_m2 "SELECT c.name, ty.name, c.max_length/2 FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id WHERE c.object_id=OBJECT_ID('dbo.AuditTrail') AND c.name='Actor'"
# → Actor nvarchar 128
```

> **Dropping discards permissions.** A dropped view or function loses every `GRANT` on it, and
> SchemaSmith does not put them back — it does not manage permissions on any object, so it has nothing
> to restore them from. Re-grant in the recreating script itself.

## Part 3 — and where it still will not

Make the view **encrypted** and try the same widening with the setting still on.

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.encrypt-view.json   # exit 0 -- the view is now WITH ENCRYPTION
schemaquench --ConfigFile:quench.settings.encrypted.json      # exit 2
cd ..
```

```text
Column change blocked by an ENCRYPTED schema-bound module: dbo.vw_AuditActors. Its definition cannot
be read from the server, so nothing can recreate it once dropped -- DropSchemaBoundDependents
deliberately does not extend to it. Remove SCHEMABINDING or the encryption from the listed module(s).
```

Check the database: the view is still there, `Actor` is still `NVARCHAR(64)`, and **nothing was
dropped**. The refusal fires *before* any drop, not partway through one.

Read why. `OBJECT_DEFINITION` returns `NULL` for an encrypted module — the server will not give the
text back to SchemaSmith, to SchemaTongs, or to you. So the one guarantee the whole feature rests on,
*"the after-tables pass will recreate it"*, cannot be made. And the moment it cannot be made, the
setting stops applying. An opt-in that quietly became unrecoverable in one case would be worse than no
opt-in at all.

## Cleanup

```bash
../lab-sql.sh sqlserver vault_m2 "DROP VIEW IF EXISTS dbo.vw_AuditActors; DROP TABLE IF EXISTS dbo.AuditTrail"
for e in mysql mariadb; do ../lab-sql.sh $e vault_m2 "DROP TABLE IF EXISTS AuditTrail"; done
```

## The principle

Three postures toward the same table in one module. It **converged** the column when nothing was in
the way. It **refused** when a rebuild would have discarded state the package never described. And in
between, it **acted** — dropping and rebuilding a dependency it *could* put back, because your package
contained the recipe.

That middle case is what makes the other two coherent. SchemaSmith is not refusing because it is
cautious; it is refusing because of a specific, checkable fact: *can I reconstruct what I am about to
destroy?* Where the answer is yes — a view you scripted — it does the work without being asked twice.
Where the answer is no — an encrypted module, a CDC configuration, a partitioning layout — it stops,
and it tells you which of those two situations you are in.
