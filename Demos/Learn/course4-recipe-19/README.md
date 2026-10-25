# Course 4, Recipe 19 — History you can't rewrite (lab)

<!-- TRAINING-RELEASE-PIN 2.8.0 (ledger columns and view by role, #523; rebuild refusal, #591; #600 note once that fix ships): delete the "merged, not yet released" note, and the "Pending #600" note in Step 9 -->

Goal: [Recipe 18](../course4-recipe-18) kept old versions of a row so you could ask what it used to say. A **ledger
table** answers a harder question: *has anyone rewritten it?* SQL Server 2022 chains every change to a ledger table
into cryptographic blocks, so tampering, even by a sysadmin, shows up when you verify the database.

It's also close to permanent. There's no `ALTER` to or from a ledger table, and `DROP` doesn't remove one: SQL Server
keeps it under a generated name. So this is the recipe where declaring it right the first time matters most. You'll
see what a deploy changes in place, what it refuses, and what a ledger table keeps no matter what you do.

SQL Server only: `sqlserver/`, everything against `cookbook_r19`.

> **Merged, not yet released.** This recipe teaches SchemaSmith 2.8 behaviour, which is merged to `main` and not yet
> in a tagged release.

## Before you start

- The [sandbox](../docker) is up and the Course 4 databases exist (run [`../course4-setup`](../course4-setup)
  once — it creates `cookbook_r19`).
- The CLI is on your PATH: `schemaquench --version` answers **2.8.0 or later**.
- SQL Server **2022 or later** (the sandbox is). Below 2022 a ledger table deploys as an ordinary one and the run
  records a downgrade. Creating an ordinary table by mistake is easy to undo; creating a ledger table by mistake isn't.

## Step 1: Declare two ledger tables

[`dbo.Account.json`](sqlserver/Package/Templates/Main/Tables/dbo.Account.json) is **updatable**: rows change, and
every old version is kept. It names its ledger view and declares one of its four ledger columns by role:

```json
"Ledger": "Updatable",
"LedgerView": { "Name": "[AccountHistory]" },
"Columns": [
  ...
  { "Name": "[TxStart]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "TransactionIdStart", "Hidden": true }
]
```

[`dbo.AuditEntry.json`](sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json) is **append-only**: rows go in
and never change. It declares just `"Ledger": "AppendOnly"`.

```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        Adding new table [dbo].[Account]
        Adding new table [dbo].[AuditEntry]
        Creating constraint [dbo].[Account].[PK_Account]
        Creating constraint [dbo].[AuditEntry].[PK_AuditEntry]
```

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "SELECT name, ledger_type_desc, OBJECT_NAME(ledger_view_id) FROM sys.tables WHERE ledger_type IN (2, 3) ORDER BY name"
# → Account UPDATABLE_LEDGER_TABLE AccountHistory
# → AuditEntry APPEND_ONLY_LEDGER_TABLE AuditEntry_Ledger
../lab-sql.sh sqlserver cookbook_r19 "SELECT name, is_hidden FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Account') AND generated_always_type <> 0 ORDER BY column_id"
# → TxStart 1
# → ledger_end_transaction_id 1
# → ledger_start_sequence_number 1
# → ledger_end_sequence_number 1
```

The column you declared has your name. The three you left out took the engine's, hidden. `AuditEntry` has no
`LedgerView` in its package, so its view is the default, `AuditEntry_Ledger`. Run the deploy again: exit 0, nothing to
do.

## Step 2: Write to them

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "INSERT dbo.Account (AccountId, Owner, Balance) VALUES (1, N'Ada', 100.00); UPDATE dbo.Account SET Balance = 75.00 WHERE AccountId = 1"
../lab-sql.sh sqlserver cookbook_r19 "SELECT AccountId, Balance, ledger_operation_type_desc FROM dbo.AccountHistory ORDER BY ledger_transaction_id, ledger_sequence_number"
# → 1 100.00 INSERT
# → 1 75.00 INSERT
# → 1 100.00 DELETE
```

The update shows up as a pair in one transaction: the new row in, the old row out. Now the append-only table:

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "INSERT dbo.AuditEntry (EntryId, Message) VALUES (1, N'opened')"
../lab-sql.sh sqlserver cookbook_r19 "UPDATE dbo.AuditEntry SET Message = N'edited' WHERE EntryId = 1"     # refused
#   Updates are not allowed for the append only Ledger table 'dbo.AuditEntry'.
```

The update fails with a non-zero exit. That's the table doing its job, not a broken step.

## Step 3: Prove nobody rewrote it

A digest is a hash of the ledger up to now. Verifying the database against a digest recomputes every block and
compares. In production you store digests somewhere the database's own admins can't write to; here, generate one and
verify against it straight away. Verification needs snapshot isolation on:

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "ALTER DATABASE cookbook_r19 SET ALLOW_SNAPSHOT_ISOLATION ON"
../lab-sql.sh sqlserver cookbook_r19 "SET NOCOUNT ON; DECLARE @d TABLE (j NVARCHAR(MAX)); INSERT @d EXEC sys.sp_generate_database_ledger_digest; DECLARE @j NVARCHAR(MAX) = (SELECT j FROM @d); EXEC sys.sp_verify_database_ledger @j; SELECT 'verified'"
# → verified
```

Above `verified`, SQL Server prints `Ledger verification successfully verified up to block` and a block number. If a
row had been changed behind the ledger's back, the procedure would raise an error naming the table instead.

## Step 4: What a deploy changes in place

Rename `TxStart` to `TxBegin` and make it visible, and rename the ledger view:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "Columns[Name=[TxStart]].OldName" '"[TxStart]"' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "Columns[Name=[TxStart]].Name" '"[TxBegin]"' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "Columns[Name=[TxBegin]].Hidden" false -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "LedgerView.Name" '"[AccountLedger]"' -->
```json
"LedgerView": { "Name": "[AccountLedger]" },
...
{ "Name": "[TxBegin]", "OldName": "[TxStart]", "DataType": "BIGINT", "Nullable": false, "GeneratedAlways": "TransactionIdStart", "Hidden": false }
```

```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        Renaming ledger column dbo.Account.TxStart to TxBegin
        Renaming ledger view dbo.AccountHistory to AccountLedger
```

Both are `sp_rename`, and the `Hidden` change is `ALTER COLUMN … DROP HIDDEN`. The log doesn't name that one, but
the catalog shows it:

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "SELECT name, is_hidden FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Account') AND name = 'TxBegin'"
# → TxBegin 0
```

Now two things SQL Server can't do to a ledger view: move it to another schema, or rename its columns. Ask for both:

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "IF SCHEMA_ID('audit') IS NULL EXEC('CREATE SCHEMA audit')"
```

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "LedgerView.Schema" '"[audit]"' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "LedgerView.TransactionIdColumn" '"[TxId]"' -->
```json
"LedgerView": { "Name": "[AccountLedger]", "Schema": "[audit]", "TransactionIdColumn": "[TxId]" }
```

```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
```

```
        WARNING: dbo.Account: the ledger view is in schema dbo, the package declares audit. SQL Server cannot move a ledger view; it is left as it is.
        WARNING: dbo.Account: the ledger view's columns are TransactionIdColumn ledger_transaction_id (declared TxId). SQL Server cannot rename them; they are left as they are.
```

Each warning names what's there, what you declared, and why it stays. Take both back out:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" del "LedgerView.Schema" -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" del "LedgerView.TransactionIdColumn" -->

## Step 5: What a deploy refuses

Change `Account` to append-only:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set Ledger '"AppendOnly"' -->
```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 2
```

```
Table [dbo].[Account] declares Ledger AppendOnly, but is currently deployed as Updatable. SQL Server has no ALTER that converts a table to or from a ledger table, and DROP does not remove one (it is retained as MSSQL_DroppedLedgerTable_<name>_<guid>), so SchemaSmith will not attempt it -- correct the declared Ledger to match, or migrate the data to a new table.
```

Put it back to `Updatable`. Now a column change, but with a policy that asks for a rebuild:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set Ledger '"Updatable"' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set RebuildPolicy '{"Mode": "ALWAYS"}' -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" set "Columns[Name=[Owner]].DataType" '"NVARCHAR(200)"' -->
```json
"RebuildPolicy": { "Mode": "ALWAYS" },
...
{ "Name": "[Owner]", "DataType": "NVARCHAR(200)", "Nullable": false }
```

```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 2
```

```
Table rebuild refused for [dbo].[Account]: the table is a ledger table. A rebuild replaces the table with a shadow copy, and that state lives outside the schema package -- the copy discards it and no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the blocking state first and re-run.
```

A rebuild builds a shadow copy and swaps it in. The copy would be an ordinary table, and the original would join the
dropped ledger tables. Remove the policy and the same column change goes through in place:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" del RebuildPolicy -->
```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
#         Altering Column [dbo].[Account].[Owner]
```

## Step 6: Deliver data to an append-only table

An append-only table refuses `UPDATE` and `DELETE`, so a data delivery to it can only insert.
[`data/dbo.AuditEntry.tabledata`](sqlserver/Package/Templates/Main/data/dbo.AuditEntry.tabledata) changes row 1 and
adds row 2:

```
[
{"EntryId":1,"Message":"opened, then edited"},
{"EntryId":2,"Message":"closed"}
]
```

Give `AuditEntry` a delivery that uses it:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json" set DataDelivery '{"ContentFile": "data/dbo.AuditEntry.tabledata", "MergeType": "Insert/Update/Delete", "MatchColumns": "[EntryId]"}' -->
```json
"DataDelivery": { "ContentFile": "data/dbo.AuditEntry.tabledata", "MergeType": "Insert/Update/Delete", "MatchColumns": "[EntryId]" }
```

```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
#     Delivering dbo.AuditEntry
```

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "SELECT EntryId, Message FROM dbo.AuditEntry ORDER BY EntryId"
# → 1 opened
# → 2 closed
```

Row 2 went in. Row 1 stayed as it was, even though the delivery says otherwise and asked for `Update`: on an
append-only table the history wins. Take the delivery back out:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json" del DataDelivery -->

## Step 7: Catch a bad declaration before it deploys

An append-only table has only the two *Start* ledger columns. Declare an *End* one and lint:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json" append Columns '{"Name": "[TxEnd]", "DataType": "BIGINT", "Nullable": true, "GeneratedAlways": "TransactionIdEnd", "Hidden": true}' -->
```bash
# from course4-recipe-19
schemaquench --Validate --SchemaPackagePath:./sqlserver/Package        # exit 2
```

```
ERROR [SS-LDG-002] Template 'Main' / Table '[AuditEntry]': Column '[TxEnd]' declares GeneratedAlways TransactionIdEnd on an append-only ledger table, which has no End columns: rows are never updated or deleted.
```

No server needed. Take the column back out:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json" remove-where Columns "Name=[TxEnd]" -->

## Step 8: A ledger table never really leaves

Take `AuditEntry` out of the package, and turn on `DropTablesRemovedFromProduct` (a top-level key in
`deploy.settings.json`):

<!-- walk: mv sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json sqlserver/dbo.AuditEntry.json -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/deploy.settings.json" set DropTablesRemovedFromProduct true -->
```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
#         Dropping table [dbo].AuditEntry
```

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "SELECT LEFT(name, 35), is_dropped_ledger_table FROM sys.tables WHERE ledger_type = 3 ORDER BY name"
# → MSSQL_DroppedLedgerTable_AuditEntry 1
```

The `DROP` worked, and the table is still there under a generated name, with its rows and its place in the ledger. Run
the deploy again: exit 0, and nothing tries to drop it a second time. The engine owns it now. Put `AuditEntry` back
and the next deploy creates a brand-new, empty `AuditEntry` beside the retained one:

<!-- walk: mv sqlserver/dbo.AuditEntry.json sqlserver/Package/Templates/Main/Tables/dbo.AuditEntry.json -->
<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/deploy.settings.json" del DropTablesRemovedFromProduct -->

## Step 9: A dropped column stays too, and extraction knows

Remove `Owner` from `Account` and deploy:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "sqlserver/Package/Templates/Main/Tables/dbo.Account.json" remove-where Columns "Name=[Owner]" -->
```bash
# from course4-recipe-19/sqlserver
schemaquench --ConfigFile:deploy.settings.json        # exit 0
#         Dropping columns from [dbo].[Account] ([Owner])
```

```bash
# from course4-recipe-19
../lab-sql.sh sqlserver cookbook_r19 "SELECT LEFT(name, 31), is_hidden, is_dropped_ledger_column FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Account') AND is_dropped_ledger_column = 1"
# → MSSQL_DroppedLedgerColumn_Owner 1 1
```

The column is gone from your queries and kept in the table, hidden, so the ledger still verifies. Extract the
database:

```bash
# from course4-recipe-19/sqlserver
schematongs --ConfigFile:SchemaTongs.settings.json        # exit 0
```

`extracted/Templates/Main/Tables/dbo.Account.json` has `AccountId`, `Balance` and `TxBegin`, plus `"Ledger":
"Updatable"` and `"LedgerView": { "Name": "[AccountLedger]" }`. The retained column isn't in it, so the package
doesn't try to build the engine's leftovers on the next database it deploys to.

> **Pending #600.** Right now the *next* deploy after a ledger column drop tries to drop the retained column too, and
> fails with `cannot be dropped because it is already a dropped ledger column`. The fix leaves it out of the
> comparison, as extraction already does. Until it ships, start this table over (below) rather than deploying again.

## What you proved

- Ledger tables, their kind, their view and their columns are declarations.
- Names and `Hidden` change in place. The view's schema and column names can't, and the deploy says so.
- The ledger kind and a rebuild are refused by name. Neither can be done without breaking the ledger.
- An append-only table takes inserts only, from your application and from a data delivery alike.
- `DROP` doesn't remove a ledger table or a ledger column. SQL Server keeps both, and SchemaSmith leaves them be.

## Clean up

A ledger table can't be dropped for real, so start over by dropping the database:

```bash
# from course4-recipe-19
rm -rf sqlserver/extracted
bash -c '. ../lab-sql.sh && lab_remove_db sqlserver cookbook_r19 && lab_confirm_db sqlserver cookbook_r19'
```

Put the package back with `git checkout -- sqlserver` if you want to run the recipe again.
