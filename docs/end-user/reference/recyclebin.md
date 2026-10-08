# Recyclebin — Soft-Drop and Restore Hooks

When `DropTablesRemovedFromProduct` is on, a table that disappears from your product definition is gone — a clean drop, no safety net. That's the right call for CI, where you want the database to match the product exactly. In production, though, a plain drop can leave you relying on a full restore to recover data that might still be needed. The recyclebin hooks give you a third path: auto-drops stay on, but a removed table moves into a recyclebin instead of being destroyed. It lives there for a retention window, auto-restores if the table reappears in a later deployment, and gets purged only when the window expires.

> **MariaDB:** Recyclebin on MariaDB works exactly as it does on MySQL — the same schema-less model, the same `CREATE EVENT` cleanup scheduler, and the same hook names and signatures. Throughout this page, wherever a row or note says *MySQL*, it applies to MariaDB as well. MariaDB is its own `Platform: MariaDb` selection in the MySQL family — a separate native package, not the MySQL package retargeted.

---

## Two Rollback-Friendly Postures

Before you wire up the recyclebin, it helps to see where it fits among the options SchemaSmith gives you for protecting tables from unintended removal.

**Posture 1 — never drop.** Set `DropTablesRemovedFromProduct: false` in your production configuration. A table that leaves your product definition is simply left in place; SchemaSmith touches nothing. No data at risk, no cleanup needed. The cost is accumulation — the database collects orphaned tables over time, and you need a manual process to retire them. Full configuration reference: [DropTablesRemovedFromProduct](schemaquench.md#droptablesremovedfromproduct).

**Posture 2 — drop but recoverable.** Keep auto-drops on and install the recyclebin hooks. A removed table moves to the recyclebin rather than being destroyed; it auto-restores if the table returns to the product; it is purged after the retention window. The database stays clean without permanent data loss risk.

The two postures address different concerns. "Never drop" is protection against the deployment tool doing the wrong thing. The recyclebin is protection against the team doing the right thing — removing a table from the product — and still needing the data for a window afterward. A per-object or per-environment drop protection that's meant to be permanent is a different need again, beyond what either posture covers.

> **Warning:** The recyclebin protects tables only. A dropped column is never caught — the engine removes it directly, regardless of whether the recyclebin hooks are installed. Preserve column data yourself (for example, with a migration script that copies the column to a staging table) before any quench that drops it.

---

## The Hooks — Contract and Detection

The recyclebin is built on two optional stored procedures you create in the target database. SchemaSmith probes the target's catalog for each hook by name before every quench; if a hook is present, table removal and pre-creation route through it. If a hook is absent, SchemaSmith emits a plain `DROP TABLE` or `CREATE TABLE` as normal. Installing the hooks in a database opts that database in; removing them opts it back out. No settings file change required.

Reference implementations ship with the Northwind demos at `Demos/<engine>/Northwind/Templates/Northwind/Procedures/`. SchemaTongs does not extract the hooks, since they live in SchemaSmith's own namespace, and it never reports their files as orphans, so re-extracting into the package leaves them where they are.

### Hook names

| Engine | Drop hook | Restore hook |
|---|---|---|
| SQL Server | `[SchemaSmith].[CustomTableDrop]` | `[SchemaSmith].[CustomTableRestore]` |
| PostgreSQL | `"SchemaSmith"."CustomTableDrop"` | `"SchemaSmith"."CustomTableRestore"` |
| MySQL | `` `SchemaSmith_CustomTableDrop` `` | `` `SchemaSmith_CustomTableRestore` `` |

**Drop hook signatures:**

| Engine | Signature |
|---|---|
| SQL Server | `@SchemaName SYSNAME, @TableName SYSNAME, @RetentionDays INT = 90` |
| PostgreSQL | `(p_schema_name TEXT, p_table_name TEXT, p_retention_days INT DEFAULT 90)` |
| MySQL | `(IN p_DatabaseName VARCHAR(64), IN p_TableName VARCHAR(64))` |

The engine always calls the drop hook with the schema (or database, on MySQL) and table name only — it never passes a retention value. SQL Server and PostgreSQL default `RetentionDays` / `p_retention_days` to 90 at the parameter level.

> **MySQL:** MySQL cannot declare default parameter values, so retention is hard-coded to 90 inside the procedure body rather than declared as a default. The call signature has no retention argument.

### WhatIf behavior

The hooks honor `WhatIfONLY`. In WhatIf mode, the `EXEC` / `CALL` line for each hook invocation is emitted to the output log exactly as it would be executed, but the call is not made. The recyclebin tables and registry are unchanged. This lets you verify that SchemaSmith has detected the hooks and would route through them, without committing any soft-drops.

### Invocation points

- **Drop path:** the `ModifiedTableQuench` pass that handles tables removed from the product. When a `CustomTableDrop` hook is detected, SchemaSmith also skips its own temporal-history-table drop pass — the hook owns table removal entirely.
- **Restore path:** the `MissingTableAndColumnQuench` pass, ahead of table creation. When a table that was previously recycled reappears in the product, the restore hook runs before SchemaSmith would create the table fresh.

> **Note:** The engine clears inbound foreign keys before calling the drop hook. The hook implementation also drops inbound foreign keys itself. Both orders are safe; the belt-and-suspenders approach ensures constraint removal succeeds regardless of which side acts first.

---

## What the Drop Hook Does

The drop hook carries out a structured soft-drop sequence that strips constraints from the table, moves it intact to the recyclebin, and records the move in the registry.

1. **Validate existence.** Confirms the table is still present before acting. A table that has already been removed (for example, by a prior migration script) produces a no-op.
2. **Compute a recycled name.** The recycled name encodes the table's original location:
   - SQL Server / PostgreSQL: `<schema>_<table>` — for example, `dbo_Promotion` or `public_Promotion`.
   - MySQL: `recyclebin_<table>`.
   If a recycled table with that name already exists, a numeric suffix is appended (`_2`, `_3`, and so on) until the name is unique.
3. **Strip constraints.** Removes inbound foreign keys (other tables referencing this one), the table's own foreign keys, and check constraints. On SQL Server, also drops indexes, primary key, and unique constraints — these are recreated as part of the index structure and would block the schema transfer.
4. **Move the table.** Transfers the bare table to the recyclebin:
   - SQL Server: `ALTER SCHEMA recyclebin TRANSFER` then `sp_rename` to the recycled name.
   - PostgreSQL: `ALTER TABLE … SET SCHEMA recyclebin` then rename.
   - MySQL: `RENAME TABLE … TO recyclebin_<name>`.
5. **Record in the registry.** Inserts a row into the recyclebin registry capturing the original location, the recycled name, the recycled date, the retention window, and the computed expiration date.

> **MySQL:** MySQL has no schema namespace inside a database, so the recyclebin uses a `recyclebin_` table-name prefix instead of a dedicated schema. SQL Server and PostgreSQL use a separate `recyclebin` schema.

---

## What the Restore Hook Does

When a table returns to the product — added back to a product JSON file after having been removed — SchemaSmith calls the restore hook before attempting to create the table.

The hook queries the registry for the most recently recycled copy of the original schema+table combination (`ORDER BY recycled_date DESC`). If a recycled copy exists, the hook reverses the soft-drop: renames the recyclebin table back to its original name and schema, and restores it in place. The data that was soft-dropped comes back intact. If no recycled copy exists — the table was never soft-dropped, or the retention window has already expired — the hook is a no-op, and SchemaSmith creates the table fresh as normal.

Only the most recent recycled copy is considered. If a table was cycled through the recyclebin more than once, older copies stay in the recyclebin until they expire; only the newest is eligible for auto-restore.

---

## Registry and Retention

The registry tracks every soft-dropped table and drives the cleanup job.

**Registry location:**

| Engine | Registry table |
|---|---|
| SQL Server | `recyclebin.Registry` |
| PostgreSQL | `recyclebin.Registry` |
| MySQL | `recyclebin_Registry` |

**Registry columns:**

| Column | Description |
|---|---|
| Original schema | The schema (or database, on MySQL) the table was removed from. |
| Original name | The table name as it appeared in the product. |
| Recycled name | The name the table was moved to in the recyclebin. |
| Recycled date | Timestamp when the soft-drop occurred. |
| RetentionDays | Retention window in days. Default 90. |
| Expiration date | The date after which the recycled table is eligible for purge. SQL Server computes this as a persisted computed column (`DATEADD(day, RetentionDays, RecycledDate)`); PostgreSQL, MySQL, and MariaDB insert the value explicitly at soft-drop time. |

---

## Scheduling the Cleanup Job

Each engine ships a `CleanupJob` purge procedure that drops recyclebin tables past their expiration date and removes the corresponding registry rows. The procedure is deployed as part of the recyclebin hook package — you schedule it; SchemaSmith does not ship a required job artifact.

### SQL Server

Call `EXEC recyclebin.CleanupJob;` on a schedule. The Northwind demo ships a ready-to-use SQL Server Agent job at `Demos/SqlServer/Northwind/Jobs/RecyclebinCleanupJob.sql` that wires this call to a daily run at 23:00 — this is the canonical starting point for scheduling.

### PostgreSQL

Use `pg_cron` to schedule the cleanup procedure:

```sql
SELECT cron.schedule('recyclebin-cleanup', '0 23 * * *', $$CALL recyclebin.cleanup_job()$$);
```

This schedules a daily run at 23:00 using the standard cron expression for `pg_cron`. The `pg_cron` extension must be installed and enabled on the target database.

### MySQL

Use a MySQL Event:

```sql
CREATE EVENT recyclebin_cleanup
  ON SCHEDULE EVERY 1 DAY
  DO CALL recyclebin_CleanupJob();
```

MySQL Events require the event scheduler to be enabled (`SET GLOBAL event_scheduler = ON;`). Check your server configuration before creating the event.

---

## See Also

The links below cover the drop-protection setting that gates recyclebin activation, the dry-run mode that previews hook invocations, and the full quench sequence showing where the drop and restore paths sit.

- [DropTablesRemovedFromProduct](schemaquench.md#droptablesremovedfromproduct) — the setting that controls whether auto-drops are on at all; the recyclebin hooks only activate on the drop path.
- [WhatIf Mode](schemaquench.md#whatif-mode) — full reference for dry-run behavior, including how hook invocations appear in WhatIf output.
- [Database Quench Sequence](schemaquench.md#database-quench-sequence) — the full ordered sequence showing where `MissingTableAndColumnQuench` (restore path) and `ModifiedTableQuench` (drop path) fit.
