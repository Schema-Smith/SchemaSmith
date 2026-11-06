# Course 8 · Module 2 — Structure-change failures

Two induced incidents on one database (`diag_structure`), read end to end, then the rebuilds a policy elects. This is the **structure
half** of the mechanical engine: how column *adds* (`MissingTableAndColumnQuench`) and column
*alters* (`ModifiedTableQuench`) fail, and how to read them. Same method as Module 1 — locate the
phase, read the error, recover.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`) — see [`../README.md`](../README.md).
- `schemaquench --version` answers **2.8.0 or later** on your PATH. New to the CLI? [Course 1, Module 1](https://learn.schemasmith.com/01-install-connect/).

## Step 1 — create the sandbox database

**macOS / Linux**

```bash
# from course8-module-02
bash setup-databases.sh --reset
```

**Windows (PowerShell)**

```powershell
# from course8-module-02
.\setup-databases.ps1 -Reset
```

`--reset` (`-Reset`) drops `diag_structure` and recreates it empty, so every attempt — the first or a retake — starts from the same clean state. Prints `PASS (reset)` per engine. Only a database the labs created is ever dropped.

## Step 2 — deploy the baseline (green)

Change into your engine's folder (`sqlserver`, `postgres`, `mysql`, or `mariadb`) for Beats 1 and 2:

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Exits `0` and forges the `Shop` schema into `diag_structure`, then a run-once seed populates
`Customer` with four rows — one of them `FullName = 'Ana Fielding-Reyes'` (18 chars). A **populated**
table is the precondition both incidents need.

## Beat 1 — adding a required column over existing data (`4901` / `23502`)

Deploy the change that adds a `NOT NULL` column with no default:

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat1-broken.json --LogPath:"$PWD/logs"
```

**Read the trail — and notice the engines disagree.** Same package, three outcomes:

| Engine | Result |
| --- | --- |
| **SQL Server** | **Fails, exit `2`** at `Quenching missing tables and columns`: *"ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT … Column 'LoyaltyTier' cannot be added to non-empty table 'Customer'…"* (error `4901`). |
| **PostgreSQL** | **Fails, exit `2`** at the same phase: `23502: column "loyaltytier" of relation "customer" contains null values`. |
| **MySQL** | **Exits `0`.** No failure — MySQL adds the column and **silently backfills `''`** into every existing row. Even in `STRICT_TRANS_TABLES` mode. |
| **MariaDB** | **Exits `0`.** Identical to MySQL — it adds the column and **silently backfills `''`** into every existing row, even in `STRICT_TRANS_TABLES` mode. |

That MySQL/MariaDB row is the lesson: **the engine that fails loud is protecting you.** SQL Server and
PostgreSQL refuse a required column with no value for the rows already there. MySQL and MariaDB just fill
blanks — you get a `LoyaltyTier` column full of empty strings and no warning. (This is engine
behavior, not SchemaSmith — SchemaSmith issues the same correct `ALTER` everywhere; the two MySQL-family
engines choose to fill rather than refuse.)

**The fix** is what SQL Server / PostgreSQL were asking for: give the new column a `Default`, so the
rows already in the table get a real value. `beat1-fixed/` does exactly that (`Default 'Standard'`):

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat1-fixed.json --LogPath:"$PWD/logs"
```

Green on all four. On SQL Server / PostgreSQL the existing rows now read `Standard`. **On MySQL and
MariaDB they stay `''`** — the column was already added back in `beat1-broken`, and a default only
applies to *new* rows. If MySQL and MariaDB had failed loud like the others, you'd have caught it
before the blanks landed.

## Beat 2 — narrowing a column that still holds long data (`2628` / `22001` / `1406`)

Now a column *alter*. `beat2-broken/` narrows `FullName` from `NVARCHAR(200)` to `NVARCHAR(10)`, but
`'Ana Fielding-Reyes'` is 18 characters:

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

This one fails the **same way on all four** — exit `2` at `Quenching modified tables`:

| Engine | Error |
| --- | --- |
| **SQL Server** | `String or binary data would be truncated in table 'diag_structure.dbo.Customer', column 'FullName'. Truncated value: ''.` (error `2628`; `8152`, without the table and value, below compatibility level 150) |
| **PostgreSQL** | `22001: value too long for type character varying(10)` |
| **MySQL** | `Data too long for column 'FullName' at row 1` (error `1406`) |
| **MariaDB** | `Data too long for column 'FullName' at row 1` (error `1406`) |

**The fix is data, not schema** — the existing values are too long for the shape you asked for.
Shorten them (from the lab folder), then redeploy the same change from your engine's folder:

```bash
# from course8-module-02
../lab-sql.sh sqlserver diag_structure "UPDATE dbo.Customer SET FullName = LEFT(FullName, 10);"
../lab-sql.sh postgres diag_structure "UPDATE public.customer SET fullname = LEFT(fullname, 10);"
../lab-sql.sh mysql diag_structure "UPDATE Customer SET FullName = LEFT(FullName, 10);"
../lab-sql.sh mariadb diag_structure "UPDATE Customer SET FullName = LEFT(FullName, 10);"
```

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat2-broken.json --LogPath:"$PWD/logs"
```

Green on all four — the narrow now applies. (The failed run's checkpoint is still on disk, but a
plain redeploy does not read it: it converges from the top. Only `--ResumeQuench` picks up a
checkpoint — that's Module 5.)

## Beat 3 — the change an in-place `ALTER` cannot make, and the state that blocks it

Beats 1 and 2 failed loudly. This one is about the phase deciding it needs a bigger hammer — and then
refusing to swing it.

`beat3-reorder/` declares the same three `Customer` columns in a **different order**: `CustomerId`,
`FullName`, `Email`, where the deployed table has `Email` second. Column order is not something an
`ALTER` can change on any of these engines. The only way to get it is to build the table fresh, copy
the rows, and swap — a **rebuild** — and that is what `RebuildPolicy` governs. `beat3-reorder`'s
`Customer` table file already carries it; nothing to edit:

```json
{ "RebuildPolicy": { "Mode": "NEVER", "OnOrderMismatch": true } }
```

Read those two fields carefully, because they are not alternatives. `Mode` is `NEVER` (the default —
always alter in place), `ALWAYS`, or `THRESHOLD` (rebuild once the pending-change count reaches
`Threshold`, which is then required). `OnOrderMismatch` is a **separate trigger that composes with
`Mode`** rather than replacing it — deliberately not a fourth `Mode` value, so a table can ask for
"THRESHOLD: 3" *and* order-mismatch at once. Here `Mode` stays `NEVER`, and order drift alone elects
the rebuild.

Deploy the baseline, then the reorder, on any engine — from your engine's folder, then check the column
order from the lab folder:

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.baseline.json
schemaquench --ConfigFile:quench.settings.beat3-reorder.json
cd ..
../lab-sql.sh sqlserver diag_structure "SELECT c.name, c.column_id FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id WHERE t.name='Customer' ORDER BY c.column_id"
../lab-sql.sh postgres diag_structure "SELECT column_name, ordinal_position FROM information_schema.columns WHERE table_schema='public' AND table_name='customer' ORDER BY ordinal_position"
../lab-sql.sh mysql diag_structure "SELECT COLUMN_NAME, ORDINAL_POSITION FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='diag_structure' AND TABLE_NAME='Customer' ORDER BY ORDINAL_POSITION"
../lab-sql.sh mariadb diag_structure "SELECT COLUMN_NAME, ORDINAL_POSITION FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='diag_structure' AND TABLE_NAME='Customer' ORDER BY ORDINAL_POSITION"
# → CustomerId:1  FullName:2  Email:3 (lowercase on PostgreSQL)   and SELECT COUNT(*) FROM Customer still returns 4
```

On SQL Server and PostgreSQL the log names the two phases doing it:

```text
      Detect declared-vs-deployed column order drift
      Elect tables for rebuild
```

MySQL and MariaDB log no `Elect tables for rebuild` line; the rebuild follows the drift check inside
`ModifiedTableQuench`:

```text
    ModifiedTableQuench: Detect declared-vs-deployed column order drift
      Rebuilding table `diag_structure`.`Customer`
```

**Try it without the trigger first.** Deploy `beat3-reorder` with `OnOrderMismatch` removed and the run
is green — exit 0 — and the column order does not move. Nothing failed; the engine simply had no
in-place way to honour the declaration and did not reach for the rebuild you never asked for. That is
the quiet case Course 8 keeps warning you about, and the reason the trigger is opt-in.

### The refusal (SQL Server)

A rebuild replaces the table with a copy, so any state the *database* holds about that table — and
that your package does not describe — would be discarded. SchemaSmith refuses instead. To see it, put `Customer` back in its baseline
order, enable Change Data Capture, and ask for the same reorder. Start over first: the reorder above
already happened, so re-running it as-is has nothing left to rebuild and exits 0.

```bash
# from course8-module-02
bash setup-databases.sh --reset          # Windows: .\setup-databases.ps1 -Reset
```

Deploy the baseline again, from the `sqlserver` folder:

```bash
# from course8-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.baseline.json
```

Enable CDC on the database and on `Customer`, from the lab folder:

```bash
# from course8-module-02
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_enable_db"
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_enable_table @source_schema='dbo', @source_name='Customer', @role_name=NULL"
```

Then ask for the reorder, from the `sqlserver` folder:

```bash
# from course8-module-02/sqlserver
schemaquench --ConfigFile:quench.settings.beat3-reorder.json
```

```text
Table rebuild refused for [dbo].[Customer]: Change Data Capture is enabled. A rebuild replaces the
table with a shadow copy, and that state lives outside the schema package -- the copy discards it and
no re-deploy can put it back. Move this table with Before/After migration scripts, or clear the
blocking state first and re-run.
```

Exit **2**, and nothing was attempted. Read the second sentence again — it is the whole design in one
line: *that state lives outside the schema package, and no re-deploy can put it back.* A declarative
tool can always rebuild what it declares. It must never destroy what it does not.

The same guard fires for system versioning, replication, Change Tracking, inheritance and partitioning
— whichever of those the engine in front of it supports. And it fires under `--WhatIf` too (set
`"WhatIfONLY": true` and re-run: still exit 2), because there is no safe preview of a change that
cannot be made.

Clear the blocking state when you are done:

```bash
# from course8-module-02
../lab-sql.sh sqlserver diag_structure "EXEC sys.sp_cdc_disable_table @source_schema='dbo', @source_name='Customer', @capture_instance='all'; EXEC sys.sp_cdc_disable_db"
```

> **No SQL Agent needed.** CDC's capture *jobs* need SQL Server Agent, and the lab sandbox does not run
> it — you will see "SQLServerAgent is not currently running" when you enable CDC. That is fine here:
> everything this beat teaches is metadata-level, and the guard fires on the CDC configuration, not on
> captured rows.

## Beat 4 — three column changes, and the policy that decides how they land

Beat 3's rebuild was the only way through. This one is a choice. `beat4-reshape/` is the baseline with three of
`Product`'s columns changed at once: `Sku` goes Unicode on SQL Server (wider elsewhere), `UnitPrice` goes to four
decimal places, and `Name` doubles in length. Its `Product` table file carries a policy that counts them:

```json
{ "RebuildPolicy": { "Mode": "THRESHOLD", "Threshold": 3 } }
```

Three pending column **modifications** reach the threshold, so the table is rebuilt instead of altered. Column adds,
drops and index changes don't count, because a rebuild doesn't save that work.

Start clean, deploy the baseline, and give `Product` some rows to carry:

```bash
# from course8-module-02
bash setup-databases.sh --reset          # Windows: .\setup-databases.ps1 -Reset
```

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.baseline.json
```

```bash
# from course8-module-02
../lab-sql.sh sqlserver diag_structure "INSERT INTO dbo.Product (ProductId, Sku, Name, UnitPrice) VALUES (1,'ANV-01','Anvil',249.00),(2,'TNG-02','Tongs',39.50),(3,'HMR-03','Cross-peen hammer',58.25)"
../lab-sql.sh postgres diag_structure "INSERT INTO product (productid, sku, name, unitprice) VALUES (1,'ANV-01','Anvil',249.00),(2,'TNG-02','Tongs',39.50),(3,'HMR-03','Cross-peen hammer',58.25)"
../lab-sql.sh mysql diag_structure "INSERT INTO Product (ProductId, Sku, Name, UnitPrice) VALUES (1,'ANV-01','Anvil',249.00),(2,'TNG-02','Tongs',39.50),(3,'HMR-03','Cross-peen hammer',58.25)"
../lab-sql.sh mariadb diag_structure "INSERT INTO Product (ProductId, Sku, Name, UnitPrice) VALUES (1,'ANV-01','Anvil',249.00),(2,'TNG-02','Tongs',39.50),(3,'HMR-03','Cross-peen hammer',58.25)"
```

### Below the threshold: what altering in place looks like

Open the `Product` file in your engine's `beat4-reshape/Templates/Main/Tables/` and raise `Threshold` to `4`. Three
changes no longer reach it. Preview the deploy; `quench.settings.beat4-preview.json` is the same settings with
`"WhatIfONLY": true`, so nothing changes:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/beat4-reshape/Templates/Main/Tables/*roduct.json" set RebuildPolicy.Threshold 4 -->

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat4-preview.json --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
```

Every engine lists the three columns as `wouldModify`. What it prints to make them differs, and that difference is
the whole point of the policy. SQL Server alters **one column per statement**:

```text
ALTER TABLE [dbo].[Product] ALTER COLUMN [Name] NVARCHAR(400) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
ALTER TABLE [dbo].[Product] ALTER COLUMN [Sku] NVARCHAR(64) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL;
ALTER TABLE [dbo].[Product] ALTER COLUMN [UnitPrice] DECIMAL(19, 4) NOT NULL;
```

A change that touches every row, like `VARCHAR` to `NVARCHAR` or a new decimal scale, is a pass over the table, so
on a big table that list is several passes. PostgreSQL, MySQL and MariaDB get **one** statement with every change in
it (`ALTER TABLE … ALTER COLUMN "name" SET DATA TYPE …, ALTER COLUMN "sku" …` on PostgreSQL, `ALTER TABLE … MODIFY
COLUMN …, MODIFY COLUMN …` on MySQL and MariaDB), and the engine applies it in a single pass.

### At the threshold: one rebuild

Put `Threshold` back to `3` and deploy for real:

<!-- walk: python -I "$WALK_TOOLS/jedit.py" "<engine>/beat4-reshape/Templates/Main/Tables/*roduct.json" set RebuildPolicy.Threshold 3 -->

```bash
# from course8-module-02/<engine>
schemaquench --ConfigFile:quench.settings.beat4-reshape.json --LogPath:"$PWD/logs"
grep -A1 '"objectName"' "logs/SchemaQuench - Summary.json"
```

The log shows the rebuild (`Elect tables for rebuild` appears on SQL Server and PostgreSQL only):

```text
      Elect tables for rebuild
        Rebuilding table [dbo].[Product]
        Dropping inbound foreign key [dbo].[OrderItem].[FK_OrderItem_Product]
```

and the summary has one `rebuilt` row where the preview had three `wouldModify` rows. On SQL Server:

```text
        "objectName": "[dbo].[OrderItem].[FK_OrderItem_Product]",
        "action": "dropped",
--
        "objectName": "[dbo].[Product]",
        "action": "reapplied",
--
        "objectName": "[dbo].[Product]",
        "action": "rebuilt",
--
        "objectName": "[dbo].[Product].[PK_Product]",
        "action": "created",
--
        "objectName": "[dbo].[OrderItem].[FK_OrderItem_Product]",
        "action": "created",
```

The rebuild built `Product` fresh in its declared shape and copied the rows across once. It dropped the foreign key
`OrderItem` holds on it and added it back, and on SQL Server it put the table's permissions and extended properties
back (`reapplied`). The other engines show the same rows without `reapplied`, and MySQL and MariaDB list no primary
key row. Check the rows and the new types:

```bash
# from course8-module-02
../lab-sql.sh sqlserver diag_structure "SELECT COUNT(*) FROM dbo.Product; SELECT name, TYPE_NAME(user_type_id), max_length, scale FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Product') ORDER BY column_id"
../lab-sql.sh postgres diag_structure "SELECT COUNT(*) FROM product; SELECT column_name, data_type, character_maximum_length, numeric_scale FROM information_schema.columns WHERE table_name = 'product' ORDER BY ordinal_position"
../lab-sql.sh mysql diag_structure "SELECT COUNT(*) FROM Product; SELECT COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'diag_structure' AND TABLE_NAME = 'Product' ORDER BY ORDINAL_POSITION"
../lab-sql.sh mariadb diag_structure "SELECT COUNT(*) FROM Product; SELECT COLUMN_NAME, COLUMN_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'diag_structure' AND TABLE_NAME = 'Product' ORDER BY ORDINAL_POSITION"
# → 3 rows; Name 400 long, Sku NVARCHAR(64) on SQL Server and varchar(100) elsewhere, UnitPrice with scale 4
```

**Which engine it pays on.** The rebuild turned three statements into one copy. That's a win on SQL Server, where
those were three statements. On PostgreSQL, MySQL and MariaDB the in-place path was already one statement, so a
rebuild there saves nothing, and on MySQL and MariaDB it adds the exposure the
[RebuildPolicy reference](https://github.com/Schema-Smith/SchemaSmith/blob/main/docs/end-user/reference/configuration.md#rebuildpolicy)
describes: a row another session writes during the copy can be missed. On those engines, ask for a rebuild to fix
column order (Beat 3), not for speed.

> **Previewing at the threshold.** Run `quench.settings.beat4-preview.json` with `Threshold` at `3` and SQL Server
> and PostgreSQL list one `wouldRebuild` row and print the whole rebuild sequence. MySQL and MariaDB print the
> rebuild sequence too, then also the in-place `ALTER TABLE … MODIFY COLUMN` it replaces, with the three columns as
> `wouldModify`. The real run makes only the rebuild.

## What each folder is

| Path | Purpose |
| --- | --- |
| `baseline/` | Healthy `Shop` + the run-once seed that populates `Customer`. |
| `beat1-broken/` | Baseline + a `NOT NULL LoyaltyTier` column, **no default** — the `4901`/`23502` incident. |
| `beat1-fixed/` | `beat1-broken` + `Default 'Standard'` — the recovery. |
| `beat2-broken/` | `beat1-fixed` + `FullName` narrowed to 10 — the `2628`/`22001`/`1406` incident. |
| `beat3-reorder/` | Baseline with `Customer`'s columns declared in a different order + a table-level `RebuildPolicy` with `OnOrderMismatch` — the rebuild, and the CDC refusal. |
| `beat4-reshape/` | Baseline with three of `Product`'s columns changed + a table-level `RebuildPolicy` of `THRESHOLD` 3 — the rebuild the policy chooses. |
| `quench.settings.<state>.json` | One per package, all targeting `diag_structure`, lab-local `artifacts`/`checkpoints`. |
| `quench.settings.beat4-preview.json` | `beat4-reshape` with `"WhatIfONLY": true`, for the preview. |

Next: **Module 3 — Index, constraint & FK failures**, where the dup-key incident from Module 1 gets
its full diagnosis. The recovery *toolkit* (`--ResumeQuench`, marking a script done) is **Module 5**.
