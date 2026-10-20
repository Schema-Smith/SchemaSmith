# Course 4, Recipe 8 — Authoring your own recyclebin hooks (lab)

Goal: Recipe 6 installed the *simplest* soft-drop hook — rename the table aside, rename it back. This recipe
**authors a richer hook body**. Same contract (SchemaQuench calls `CustomTableDrop` when a table is removed
and `CustomTableRestore` before it would recreate one), but the body you write now:

- **captures the row count** before the table moves,
- archives under a **timestamped name** so repeated drops of the same table never collide,
- writes a full **audit row** — schema, table, archived name, rows, retention, *who* and *when* — to a
  `TableDropAudit` table that doubles as the restore registry, and
- **no-ops safely** if the table is already gone.

On top of the audit trail, the drop hook does the two things a *correct* soft-drop must do before it sets a
table aside:

- **strips the table's own constraints** (FK / CHECK / DEFAULT / UNIQUE / PK on SQL Server; constraints +
  indexes on PostgreSQL; foreign keys on MySQL) — their names are schema-scoped, so an archived copy that
  kept them would collide the next time a same-named table is created. The engine re-adds them from the model
  on restore. (SchemaQuench already clears *inbound* FKs before calling the hook; the hook owns the rest.)
- **clears the product-ownership marker** (a `ProductName` extended property on SQL Server; a
  `ProductOwnership` row on PostgreSQL and MySQL) — otherwise the next quench re-detects the archived copy as
  "owned but removed from the product" and routes it right back through the drop hook, every run.

The recyclebin contract is the same one the reference hooks use; what changes is that the *body is yours*.
The full-featured reference implementation (a `recyclebin` schema, retention/expiration, a scheduled
`CleanupJob`) ships with the Northwind demos — this recipe shows how to author your own to fit your shop.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships two packages — `Package` (with `Coupon`) and
`Package-NoCoupon` (without it) — plus `install-audit-hooks.sql`, all targeting `cookbook_r8`.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup) once — it creates `cookbook_r8`).
- The CLI is on your PATH (`schemaquench --version` → `2.7.0.0` or later).

## Step 1: Install the hooks

```bash
# from course4-recipe-08
../lab-sql.sh sqlserver cookbook_r8 --file sqlserver/install-audit-hooks.sql
../lab-sql.sh postgres cookbook_r8 --file postgres/install-audit-hooks.sql
../lab-sql.sh mysql cookbook_r8 --file mysql/install-audit-hooks.sql
../lab-sql.sh mariadb cookbook_r8 --file mariadb/install-audit-hooks.sql
```

Installing the procedures opts this database into the recyclebin — no settings change needed. SchemaQuench
probes for the hooks by name before every quench.

## Step 2: Deploy and seed

```bash
# from course4-recipe-08/<engine>
schemaquench --ConfigFile:deploy.settings.json      # creates Keeper + Coupon
```

Put rows in `Coupon`:

```bash
# from course4-recipe-08
../lab-sql.sh sqlserver cookbook_r8 "INSERT INTO dbo.Coupon (CouponId, Code, DiscountPct) VALUES (1,'SAVE10',10),(2,'SAVE20',20)"
../lab-sql.sh postgres cookbook_r8 "INSERT INTO public.coupon (couponid, code, discountpct) VALUES (1,'SAVE10',10),(2,'SAVE20',20)"
../lab-sql.sh mysql cookbook_r8 "INSERT INTO Coupon (CouponId, Code, DiscountPct) VALUES (1,'SAVE10',10),(2,'SAVE20',20)"
../lab-sql.sh mariadb cookbook_r8 "INSERT INTO Coupon (CouponId, Code, DiscountPct) VALUES (1,'SAVE10',10),(2,'SAVE20',20)"
```

## Step 3: Remove Coupon — the drop hook archives and audits

```bash
# from course4-recipe-08/<engine>
schemaquench --ConfigFile:remove-coupon.settings.json   # Coupon left the product
```

`Coupon` isn't dropped — it's renamed to a timestamped archive, and the move is recorded:

```bash
# from course4-recipe-08
../lab-sql.sh sqlserver cookbook_r8 "SELECT TableName, ArchivedName, RowsArchived, RetentionDays, Action FROM SchemaSmith.TableDropAudit"
# → Coupon  Coupon__dropped_20260704215904493  2  90  DROP
../lab-sql.sh postgres cookbook_r8 "SELECT table_name, archived_name, rows_archived, retention_days, action FROM \"SchemaSmith\".\"TableDropAudit\""
../lab-sql.sh mysql cookbook_r8 "SELECT table_name, archived_name, rows_archived, retention_days, action FROM SchemaSmith_TableDropAudit"
../lab-sql.sh mariadb cookbook_r8 "SELECT table_name, archived_name, rows_archived, retention_days, action FROM SchemaSmith_TableDropAudit"
```

The row count (`2`) was captured before the table moved, and the retention window (`90`) is on the record.

## Step 4: Bring Coupon back — the restore hook completes the trail

```bash
# from course4-recipe-08/<engine>
schemaquench --ConfigFile:deploy.settings.json      # Coupon returns to the product
```

`CustomTableRestore` finds the most recent archived copy in the audit registry and renames it back before the
engine would recreate the table. The rows survive, and the trail is complete:

```bash
# from course4-recipe-08
../lab-sql.sh sqlserver cookbook_r8 "SELECT CouponId, Code FROM dbo.Coupon; SELECT Action, ArchivedName FROM SchemaSmith.TableDropAudit ORDER BY AuditId"
# → 1 SAVE10 / 2 SAVE20 ;  DROP + RESTORE
../lab-sql.sh postgres cookbook_r8 "SELECT couponid, code FROM public.coupon ORDER BY couponid"
../lab-sql.sh postgres cookbook_r8 "SELECT action, archived_name FROM \"SchemaSmith\".\"TableDropAudit\" ORDER BY audit_id"
../lab-sql.sh mysql cookbook_r8 "SELECT CouponId, Code FROM Coupon ORDER BY CouponId; SELECT action, archived_name FROM SchemaSmith_TableDropAudit ORDER BY audit_id"
../lab-sql.sh mariadb cookbook_r8 "SELECT CouponId, Code FROM Coupon ORDER BY CouponId; SELECT action, archived_name FROM SchemaSmith_TableDropAudit ORDER BY audit_id"
```

## Per-engine notes

| | SQL Server | PostgreSQL | MySQL |
| --- | --- | --- | --- |
| Hook names | `SchemaSmith.CustomTableDrop` / `…Restore` | `"SchemaSmith"."CustomTableDrop"` / `…Restore` | `SchemaSmith_CustomTableDrop` / `…Restore` |
| Drop signature | `@SchemaName, @TableName, @RetentionDays INT = 90` | `(p_schema_name, p_table_name, p_retention_days INT DEFAULT 90)` | `(p_DatabaseName, p_TableName)` — retention hard-coded 90 |
| Audit / registry | `SchemaSmith.TableDropAudit` | `"SchemaSmith"."TableDropAudit"` | `SchemaSmith_TableDropAudit` (no schema namespace) |
| Rename | `sp_rename` | `ALTER TABLE … RENAME TO` | `ALTER TABLE … RENAME TO` (dynamic SQL) |
| Clear ownership | drop `ProductName` extended property | delete `"SchemaSmith"."ProductOwnership"` row | delete `SchemaSmith_ProductOwnership` row |
| Strip constraints | FK / CHECK / DEFAULT / UNIQUE / PK | all constraints + indexes | foreign keys (PK/index names are per-table) |
| "Who" | `SUSER_SNAME()` | `current_user` | `CURRENT_USER()` |

> *MariaDB is a fourth platform in the MySQL family — its own `Platform: MariaDb` selection and native package, not the MySQL package retargeted. Its dialect matches MySQL except for a few DDL specifics (invisible indexes, check-constraint drops, column-default reporting) that SchemaSmith handles for you.* Its hook body mirrors the MySQL column exactly.

The engine calls the drop hook with schema (or database, on MySQL/MariaDB) and table name only — it never
passes a retention value. SQL Server and PostgreSQL default it at the parameter level; MySQL and MariaDB
can't, so it's hard-coded in the body.

> **MySQL/MariaDB gotcha:** SchemaSmith creates `SchemaSmith_ProductOwnership` as `utf8mb4_unicode_ci`. Comparing its
> columns against a procedure parameter (server-default collation) raises *"Illegal mix of collations"* — so
> the hook coerces the params with `COLLATE utf8mb4_unicode_ci` in the ownership `DELETE`.

## The principle

The recyclebin contract is fixed, but the hook is a procedure *you* write — so a soft-drop can do whatever
your shop needs on the way through: capture a row count for the audit, stamp who ran the deploy, keep every
archived copy under a unique name, log it all where compliance can see it. But authoring the body means owning
its correctness too — freeing the schema-scoped constraint names and clearing the ownership tag so the archive
doesn't break or boomerang on the next quench. That's exactly why the full reference implementation exists; you
author the body, the engine calls it where a hard `DROP` or `CREATE` would have run, and you own what it does.
