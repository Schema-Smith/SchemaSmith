# SQL Script Conventions

This guide covers how the stored-procedure and function scripts under `Schema/Scripts/{SqlServer,PostgreSQL,MySQL}/` -- and the per-file MariaDB overrides under `Schema/Scripts/MariaDb/` -- should be written. It's the convention contributors and reviewers check a PR against — read it before touching a quench/generate procedure, and expect review comments to point back here.

## MariaDb Overrides

MariaDB is not a fourth script tree. `Platform.MariaDb` maps to `Platform.MySQL` through `GetBasePlatform()` (`Schema/Domain/Platform.cs`), and `ForgeKindler.GetKindlingScripts` builds the kindling list from that base platform, so a MariaDB target kindles the MySQL list, in the MySQL order, under the MySQL names. What differs is how each name is resolved: `ResourceLoader.GetPlatformResourceStream` (`Schema/Utility/ResourceLoader.cs`) looks for the file in the variant folder first (`Scripts/MariaDb/<name>`) and falls back to the base folder (`Scripts/MySQL/<name>`). So:

- **An override replaces the same-named MySQL script wholesale** on MariaDB. It must keep the file name and the routine's name and signature, because the shared MySQL procedures `CALL` it by those.
- **A file that exists only under `MariaDb/` is never kindled** unless the MySQL kindling list names it. Add the name to the list (with a MySQL body, even a stub that returns `NULL`) and put the MariaDB behaviour in the override -- `SchemaSmith_TableTablespace.sql` is the shape: the MySQL body reads `INNODB_TABLESPACES`, the MariaDb override sets its `OUT` parameter `NULL`.
- **Keep a MariaDB-only catalog object out of the MySQL body.** On both engines a procedure resolves the catalog columns, system variables and views it names when it is `CREATE`d, not when it runs, so a version guard around the reference does not protect it -- the mention itself fails kindling on the engine or version that lacks it. That is the usual reason an override exists at all (`SchemaSmith_ColumnLevelCheck.sql`, `SchemaSmith_IndexPeriodJson.sql`); the other is a feature MySQL does not have at any version (`SchemaSmith_SequenceQuench.sql`). The one exception runs the other way: `ForgeKindler.ResolveKindleScript` kindles the MySQL body of `SchemaSmith_ColumnLevelCheck.sql` on a MariaDB below 10.2.22, which has no `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` for the override to read.

Everything below applies to an override exactly as it applies to the MySQL script it replaces, including the crash-safety rule.

## The Convention

**Prefer aggregate-to-string, single execute over row-by-row dynamic SQL**, wherever the target engine supports it without a size limit that defeats the approach.

Concretely: build the full set of DDL/DML statements for a step as one string (via `STRING_AGG` on SQL Server/PostgreSQL, or `GROUP_CONCAT` on MySQL), then execute that string once. This is preferred over opening a cursor and running one small dynamic statement per row — one round trip and one parse instead of N.

Row-by-row cursor/loop processing is still legitimate, but only where the loop is *intrinsically required* by what the code is doing (see the [allow-list](#cursor--loop-allow-list) below), not as the default way to apply N similar changes.

## Per-Engine Reference Shapes

### SQL Server: `STRING_AGG` + `EXEC`

Build one multi-statement batch with `STRING_AGG`, then run it through the codebase's `WhatIf`-aware execute switch (`EXEC(@v_SQL)` for real runs, `EXEC SchemaSmith.PrintWithNoWait @v_SQL` for a dry-run preview) — one round trip, one parse:

```sql
SELECT @v_SQL = STRING_AGG(CAST('RAISERROR(''  Turn OFF Temporal Tracking for ' + T.[Schema] + '.' + T.[Name] + ''', 10, 100) WITH NOWAIT;' + CHAR(13) + CHAR(10) +
                                'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' SET (SYSTEM_VERSIONING = OFF);' + CHAR(13) + CHAR(10) +
                                'ALTER TABLE ' + T.[Schema] + '.' + T.[Name] + ' DROP PERIOD FOR SYSTEM_TIME;' AS NVARCHAR(MAX)), CHAR(13) + CHAR(10))
  FROM #Tables T WITH (NOLOCK)
  WHERE t.IsTemporal = 0
    AND OBJECTPROPERTY(OBJECT_ID([Schema] + '.' + [Name]), 'TableTemporalType') = 2
IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)
```

Reference: `Schema/Scripts/SqlServer/SchemaSmith.ModifiedTableQuench.sql:35-41`. The same `STRING_AGG` → `IF @WhatIf = 1 EXEC SchemaSmith.PrintWithNoWait @v_SQL ELSE EXEC(@v_SQL)` shape repeats throughout that file for every step that touches an arbitrary number of tables/columns/indexes.

### PostgreSQL: `STRING_AGG` + `EXECUTE`

PostgreSQL can run a `;`-joined multi-statement string directly, so the same fold pattern applies. The codebase routes it through `SchemaSmith.ExecuteOrDebug`, which either `RAISE NOTICE`s the script (WhatIf) or wraps it in an anonymous `DO` block and `EXECUTE`s it:

```sql
-- SchemaSmith.ModifiedTableQuench.sql:25-33
SELECT STRING_AGG('ALTER TABLE "' || pn.nspname || '"."' || pc.relname || '" DROP CONSTRAINT IF EXISTS "' || con.conname || '";', CHR(10))
  INTO sql_script
  FROM temp_product_ownership tp
  JOIN pg_class fc       ON fc.relname = tp."TableName"
  JOIN pg_namespace fn   ON fn.oid = fc.relnamespace AND fn.nspname = tp."Schema"
  JOIN pg_constraint con ON con.contype = 'f' AND con.confrelid = fc.oid
  JOIN pg_class pc       ON pc.oid = con.conrelid
  JOIN pg_namespace pn   ON pn.oid = pc.relnamespace
  WHERE ...;
CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf);
```

Reference: `Schema/Scripts/PostgreSQL/SchemaSmith.ModifiedTableQuench.sql:25-42`. `ExecuteOrDebug` itself (`Schema/Scripts/PostgreSQL/SchemaSmith.ExecuteOrDebug.sql:11-22`) does the single `EXECUTE code_block;` (line 21) that runs the whole aggregated script in one shot, or `RAISE NOTICE '%', p_Script;` for WhatIf.

This `STRING_AGG` → `CALL "SchemaSmith"."ExecuteOrDebug"(sql_script, p_WhatIf)` pair repeats throughout the same file for foreign keys, check constraints, statistics, exclude constraints, index renames, index drops, column drops, and fillfactor fixups.

### MySQL — Different by Necessity

MySQL's `PREPARE` executes exactly **one** statement. You cannot `PREPARE` a `;`-joined batch the way SQL Server's `EXEC` or PostgreSQL's `EXECUTE` will run one. Aggregate-to-string on MySQL therefore doesn't mean "join everything into one script" — it means **fold everything that targets the same operation into one multi-clause statement**, then `PREPARE`/`EXECUTE` that single statement:

- Multi-clause `ALTER TABLE t ADD COLUMN ..., ADD COLUMN ..., DROP COLUMN ...` instead of one `ALTER TABLE` per column change.
- Multi-target `RENAME TABLE a TO b, c TO d` instead of one `RENAME TABLE` per table.
- Multi-target `DROP TABLE a, b, c` instead of one `DROP TABLE` per table.
- `CASE`-aggregated `UPDATE` instead of one `UPDATE` per row.

Build the folded clause list with `GROUP_CONCAT`, and **chunk it to fit `group_concat_max_len`** — raise the session ceiling (the codebase already does this defensively) but still chunk for realistic schema sizes, since a single `GROUP_CONCAT` result is still bounded by that setting:

```sql
SET SESSION group_concat_max_len = 1000000;
```

References: `Schema/Scripts/MySQL/SchemaSmith_MissingTableAndColumnQuench.sql:75`, `Schema/Scripts/MySQL/SchemaSmith_MissingIndexesAndConstraintsQuench.sql:32`, `Schema/Scripts/MySQL/SchemaSmith_GenerateTableJson.sql:25`.

The fold-via-`GROUP_CONCAT`-into-one-statement mechanics are already in use for building a table's full column list into a single `CREATE TABLE`:

```sql
-- SchemaSmith_MissingTableAndColumnQuench.sql:33-53
DECLARE cur_NewTables CURSOR FOR
    SELECT
        t.TableName,
        t.VariantName,
        CONCAT(
            'CREATE TABLE `', p_DatabaseName COLLATE utf8mb4_unicode_ci, '`.', t.TableName, ' (',
            GROUP_CONCAT(c.ColumnScript ORDER BY c.OrdinalPosition SEPARATOR ', '),
            ...
        ) AS CreateTableStatement
    FROM _SchemaSmith_Tables t
    INNER JOIN _SchemaSmith_Columns c ON c.TableName = t.TableName
    WHERE t.NewTable = 1 ...
    GROUP BY t.TableName, t.VariantName, t.Engine, t.RowFormat, t.AutoIncrementValue;
```

`GROUP_CONCAT` at `SchemaSmith_MissingTableAndColumnQuench.sql:38` folds every column definition for a table into one `CREATE TABLE`. Apply the same idea to `ALTER TABLE`/`RENAME TABLE`/`DROP TABLE`/`UPDATE` when a change touches more than one column, table, or row: build the folded clause with `GROUP_CONCAT`, then a single `PREPARE`/`EXECUTE` — not one `PREPARE`/`EXECUTE` per item.

**Note for reviewers:** several existing MySQL procedures (e.g. the table-rename and table-drop steps in `Schema/Scripts/MySQL/SchemaSmith_ModifiedTableQuench.sql`, around lines 88-131 and 1117-1161) still open a cursor and run one `PREPARE`/`EXECUTE` per table for operations (`RENAME TABLE`, `DROP TABLE`) that MySQL *can* fold into a single multi-target statement. These predate this convention and are not a template to copy in new code — new code touching this shape should fold, per the categories above.

**Legitimately stays per-row/per-target:** a distinct `CREATE TABLE` per table (each table's shape is unique — there's nothing to fold across tables), and per-table `ALTER ... ENGINE=`/collation/`ROW_FORMAT`/`AUTO_INCREMENT` changes (MySQL requires each of these as a standalone `ALTER TABLE` per target). These don't violate the convention — there's no single-statement form to fold into.

## MySQL Crash-Safety Rule

**Never read `INFORMATION_SCHEMA` inside set-based DML that runs on every quench.** MySQL's optimizer has been observed to behave incorrectly with correlated subqueries against `INFORMATION_SCHEMA` — caching or materializing results in a way that produces wrong answers (or worse) when the same query shape runs repeatedly inside a procedure. Keeping `INFORMATION_SCHEMA` out of repeated set-based DML is a robustness requirement, not a style preference.

The pattern: **materialize the catalog rows you need into a snapshot temp table once, near the start of the procedure**, then run detection and fold-building queries against the snapshot instead of `INFORMATION_SCHEMA` directly.

```sql
-- SchemaSmith_ParseTableJson.sql:93-116
-- Snapshot existing tables into a temp table to avoid MySQL optimizer issues
-- with correlated NOT EXISTS subqueries against INFORMATION_SCHEMA.
-- The optimizer can cache/materialize INFORMATION_SCHEMA results incorrectly
-- when used in correlated subqueries (both in JSON_TABLE and UPDATE contexts).
DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;
CREATE TEMPORARY TABLE _SchemaSmith_ExistingTables (
    TableName VARCHAR(128) NOT NULL PRIMARY KEY
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

INSERT INTO _SchemaSmith_ExistingTables (TableName)
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES
WHERE BINARY TABLE_SCHEMA = BINARY p_DatabaseName
AND TABLE_TYPE = 'BASE TABLE';

-- Now set NewTable = 1 for tables not found in snapshot
UPDATE _SchemaSmith_Tables t
SET t.NewTable = 1
WHERE NOT EXISTS (
    SELECT 1 FROM _SchemaSmith_ExistingTables et
    WHERE BINARY et.TableName = BINARY SchemaSmith_StripBacktickWrapping(t.TableName)
    ...
);

DROP TEMPORARY TABLE IF EXISTS _SchemaSmith_ExistingTables;
```

The same snapshot-then-query shape repeats for columns at `Schema/Scripts/MySQL/SchemaSmith_ParseTableJson.sql:196-213` (`_SchemaSmith_ExistingColumns`). One `INFORMATION_SCHEMA` read populates the snapshot; every subsequent detection query in the procedure reads the snapshot, not the catalog.

## Cursor / Loop Allow-List

Cursors and `FOR`/`WHILE`/`LOOP` constructs are acceptable **only** where the loop is intrinsically required by the algorithm — not as a way to run one small dynamic statement per row when a fold would work.

### Recursive JSON traversal

`SchemaSmith.FormatJson` (PostgreSQL) walks an arbitrary-depth JSON document, recursing into itself for each object key and array element — recursion (or an equivalent iterative-with-explicit-stack traversal) is the only way to walk a tree of unknown depth:

Reference: `Schema/Scripts/PostgreSQL/SchemaSmith.FormatJson.sql:21-46`. The function calls `"SchemaSmith"."FormatJson"(...)` on itself at line 28 (inside the object-key `FOR` loop) and line 39 (inside the array-element `FOR` loop).

### Char-level string tokenizer / normalizer

Splitting an index column list on top-level commas (while ignoring commas inside parentheses or quoted literals) or peeling matched outer parens from a normalized expression requires walking the string character by character — there's no set-based way to track quote/paren nesting state across characters.

References:
- PostgreSQL: `Schema/Scripts/PostgreSQL/SchemaSmith.QuoteIndexColumnList.sql:39-87` — a `WHILE i <= len LOOP` that walks the input one character at a time, tracking `paren_level`, `in_double`, and `in_single` state to find top-level comma boundaries.
- MySQL: `Schema/Scripts/MySQL/SchemaSmith_NormalizeCheckExpression.sql:49-79` — `peel_loop` / nested `depth_loop` `WHILE` loops that walk a CHECK expression character by character to verify a leading `(` matches the trailing `)` before peeling an enclosing paren pair.

### Fixpoint dependency discovery

Assigning a dependency level to generated columns (so columns that reference other generated columns are created after their dependencies) requires iterating until the unresolved set stops shrinking — a fixpoint computation, not a single set-based pass:

Reference: `Schema/Scripts/MySQL/SchemaSmith_ParseTableJson.sql:298-336` — `dep_loop: WHILE @_ssc_curr_level < 10 DO`, which repeatedly marks columns resolved once none of their still-unresolved sibling columns appear in their expression, stopping when the unresolved count stops changing (or detecting a circular dependency).

### Dependent multi-batch data-preserving migration

Changing a column in a way that can't be done with a single `ALTER COLUMN` (e.g. a data-preserving type/encryption change) requires several DDL/DML statements to run as separate batches in sequence — SQL Server must resolve each `ALTER TABLE`/`UPDATE` before the next statement can reference the result, so these can't be folded into one aggregated batch:

Reference: `Schema/Scripts/SqlServer/SchemaSmith.ModifiedTableQuench.sql:699-745` — the `swap_cursor` loop (add temp column → copy data → enforce `NOT NULL` → drop original → `sp_rename` temp to original), one column swap at a time, each step its own `EXEC`.

### Interleaved catalog reads that depend on same-iteration DDL

Recreating an indexed view requires reading `sys.indexes`/`sys.extended_properties` for the view **after** that same iteration's `CREATE VIEW`/`DROP VIEW` has run, since the object doesn't exist (or existed under a stale definition) until the DDL for that specific view executes — the catalog read and the DDL are interdependent within one loop iteration, so the iteration can't be flattened into a single upfront aggregate query:

Reference: `Schema/Scripts/SqlServer/SchemaSmith.IndexedViewQuench.sql:198-203` (`view_cursor`, iterating one view at a time) and `:260-320` (within the same iteration: `CREATE VIEW` at line 262, then `OBJECT_ID(...)` and a `sys.indexes`/`sys.extended_properties` read against that just-created object at lines 270-320 to diff/create its indexes).

## PR-Time Check

Reviewers should reject:

- A **new** per-row `PREPARE`/cursor loop on MySQL where a fold (multi-clause `ALTER`, multi-target `RENAME`/`DROP`, `CASE`-aggregated `UPDATE`) would work.
- A **new** `INFORMATION_SCHEMA` read inside set-based DML in a MySQL procedure that runs on every quench — require the snapshot-temp-table pattern instead.
- A **new** row-by-row cursor/loop on any engine that isn't one of the intrinsic categories above (recursive traversal, char-level tokenization, fixpoint iteration, dependent multi-batch migration, same-iteration catalog/DDL interleaving). If it doesn't match one of those shapes, ask whether a `STRING_AGG`/`GROUP_CONCAT` fold would work instead.
