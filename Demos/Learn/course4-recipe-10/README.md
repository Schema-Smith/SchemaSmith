# Course 4, Recipe 10 — Extensions-driven replication topology (lab)

Goal: `Extensions` is the source of truth for **which** tables replicate — not a hand-maintained
replica schema, not a second copy of the DDL. Mark a table `"Extensions": { "ReplicationEnabled":
true }` in the publisher's model, and a custom `[ALWAYS]` After Script provisions the subscriber's
schema straight from the whole-graph `{{TableSchema}}` token, calling `TableQuench` against only the
tables the model flagged. This is the SQL-Server-only replication setup demo (#268) — a
cross-database `EXEC` from an After Script only works because SQL Server can address a second
database on the same connection; PostgreSQL and MySQL/MariaDB can't, so this recipe doesn't port.

## Before you start

- The [sandbox](../docker) is up and verified.
- The two databases exist — run [`./setup-dbs.sh`](./setup-dbs.sh) once. It creates `Shop_Primary`
  (the publisher) and `Shop_Replica` (the subscriber), both empty. `--reset` drops and recreates both.
- The CLI is on your PATH — `schemaquench --version` answers **2.4.0** or later.

`sqlserver/Package/Product.json` carries two templates, in order:

```json
{ "Name": "Shop", "MinimumVersion": "2017", "TemplateOrder": [ "ReplicaKindle", "Main" ], "Platform": "SqlServer" }
```

`ReplicaKindle` runs first and just kindles `Shop_Replica` — stands up SchemaSmith's own tracking
objects on the empty subscriber so it's ready to receive `TableQuench` calls. Then `Main` deploys the
full `Shop` schema to `Shop_Primary`, and its `[ALWAYS]` After Script provisions `Shop_Replica`'s
tables from the publisher's model. Order matters: the subscriber has to be kindled *before* anything
tries to call a SchemaSmith procedure on it.

A publisher table declares its intent with an `Extensions` flag — nothing else about the table
changes:

```json
{ "Name": "[Orders]", "...": "...", "Extensions": { "ReplicationEnabled": true, "ReplicationTarget": "Shop_Replica" } }
```

## Step 1: Deploy — publisher gets everything, subscriber gets the marked set

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.json
```

`ReplicaKindle` kindles the subscriber, then `Main` deploys the publisher and runs the `[ALWAYS]`
After Script:

```
[localhost,11433].[Shop_Replica]   Kindling the forge
[localhost,11433].[Shop_Replica] Successfully Quenched
[localhost,11433].[Shop_Primary]   Kindling the forge
[localhost,11433].[Shop_Primary]     Quenching .\Package\Templates\Main\After Scripts\replicate [ALWAYS].sql
[localhost,11433].[Shop_Primary] Successfully Quenched
```

Check what actually landed on each database (`SELECT name FROM sys.tables WHERE name NOT IN
('ChangeAudit','CompletedMigrationScripts','ExpressionMap','KindleStamp')` and `SELECT name FROM sys.foreign_keys`):

```
-- Shop_Primary user tables:        Customers, Inventory, Orders
-- Shop_Replica user tables:        Customers, Orders          (Inventory is ReplicationEnabled:false)
-- Shop_Replica foreign keys:       FK_Orders_Customers        (the FK-closed replicated set materialized)
```

The publisher gets the full schema, exactly as always. The subscriber gets **only** `Customers` and
`Orders` — the tables the model marked — and it gets `FK_Orders_Customers` too, because a foreign key
needs both ends present to exist.

### Why the After Script is named `replicate [ALWAYS].sql`

An `After`-slot script is tracked by default — SchemaSmith runs it once and never again on later
deploys. That's wrong for this job: the replica's shape has to be re-derived from the model on *every*
deploy, because the model can change. The `[ALWAYS]` marker in the filename overrides tracking —
`schema-packages.md`: "Scripts with `[ALWAYS]` in the filename run every time regardless of
tracking." Without it, the subscriber would provision correctly on the very first deploy and then
silently stop following the model. This is the load-bearing detail in the whole recipe.

The script itself filters the whole-graph token down to the flagged tables and hands them to
`TableQuench` on the subscriber:

```sql
DECLARE @graph NVARCHAR(MAX) = N'{{TableSchema}}';
DECLARE @replicated NVARCHAR(MAX);
SELECT @replicated = N'[' + STRING_AGG(CAST(t.[value] AS NVARCHAR(MAX)), N',') + N']'
FROM OPENJSON(@graph) t
WHERE JSON_VALUE(t.[value], '$.Extensions.ReplicationEnabled') = 'true';
IF @replicated IS NOT NULL
  EXEC Shop_Replica.SchemaSmith.TableQuench @ProductName = N'{{ProductName}}',
      @TableDefinitions = @replicated, @WhatIf = 0, @DropUnknownIndexes = 0,
      @DropTablesRemovedFromProduct = 0, @UpdateFillFactor = 1;
```

**Authoring gotcha:** never put `{{TableSchema}}` inside a single-line `--` comment. The token expands
to multi-line JSON, and everything after the first line escapes the comment and runs as live SQL.

## Step 2: Idempotent re-run

Run the exact same command again:

```bash
schemaquench --ConfigFile:quench.settings.json
```

`replicate [ALWAYS].sql` runs again — it's `[ALWAYS]`, so tracking never skips it — and
`TableQuench` on the subscriber is a clean no-op: nothing changed on either database, exit code 0, no
`Adding`/`Creating` lines. The After Script re-reading the model every time is exactly what makes this
safe to run over and over.

## Step 3: Toggle the model — Extensions is the control surface

Edit `sqlserver/Package/Templates/Main/Tables/dbo.Inventory.json` and flip its flag:

```json
"Extensions": { "ReplicationEnabled": true }
```

Redeploy:

```bash
schemaquench --ConfigFile:quench.settings.json
```

Because the After Script is `[ALWAYS]`, it re-reads the model and re-filters — no config edit, no
script edit, nothing but the flag changed:

```
[localhost,11433].[Shop_Primary]     Quenching .\Package\Templates\Main\After Scripts\replicate [ALWAYS].sql
```

The subscriber now has all three tables:

```
-- Shop_Replica user tables:        Customers, Inventory, Orders
```

`Inventory` joined the replica the moment the model said so. **Revert `dbo.Inventory.json` back to
`"ReplicationEnabled": false` and redeploy** before moving on, so the lab is back at the Step 1
baseline.

## Step 4: WhatIf previews, runs nothing

Reset to fresh, empty databases first:

```bash
./setup-dbs.sh --reset
```

Then run against the WhatIf settings, which set `WhatIfONLY: true`:

```bash
schemaquench --ConfigFile:quench.settings.whatif.json
```

```
  WhatIfONLY: True
[localhost,11433].[Shop_Replica]   [WhatIf] Object scripts without unresolved tokens:
[localhost,11433].[Shop_Replica]   [WhatIf] Before database scripts:
[localhost,11433].[Shop_Replica]   [WhatIf] After table scripts:
[localhost,11433].[Shop_Replica]   [WhatIf] After database scripts:
```

Every slot — including the `[ALWAYS]` After Script that would otherwise call `TableQuench` on the
subscriber — is listed as a preview, not executed. Check `Shop_Replica` afterward and it has **no
user tables**: the dry run touched nothing. WhatIf shows what *would* run; it never executes a script,
which is exactly what makes it safe to preview a hook that reaches across databases.

## The FK-closed-set rule

Step 1's foreign-key check wasn't incidental: `Orders` has a foreign key to `Customers`, and both
ended up on the subscriber even though only `Orders` was the table you'd think to mark. Look again —
`Customers` is also flagged `ReplicationEnabled: true` in this package. That's required, not
optional: if you mark a table for replication but leave one of its FK parents unmarked, the replica
ends up with a foreign key pointing at a table that was never provisioned, and the deploy fails. The
rule is to mark the whole FK-closed set — a table and everything it depends on — not a single table in
isolation.

## Cleanup

```bash
./setup-dbs.sh --reset
```

Or drop both databases by hand:

```bash
../lab-sql.sh sqlserver master "DROP DATABASE Shop_Primary"
../lab-sql.sh sqlserver master "DROP DATABASE Shop_Replica"
```

## The principle

`Extensions` declares the topology — which tables belong on which subscriber — and SchemaSmith
provisions the replica straight from that model, not from a second hand-maintained copy of the DDL.
The `[ALWAYS]` marker is what keeps the replica honest: because the After Script re-reads the model on
every deploy instead of running once, toggling a flag is the only edit you ever need to grow or shrink
what replicates. And because the mechanism is a cross-database `EXEC`, it only works where the engine
allows one connection to address two databases — SQL Server, not PostgreSQL or MySQL/MariaDB.
