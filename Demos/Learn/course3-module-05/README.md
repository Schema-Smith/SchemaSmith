# Course 3, Module 5 — Capstone: a careless rename, and the safety net that saves it (lab)

Goal: one continuous story, start to finish. Build `OrdersService`, **promote** it dev to staging to
prod exactly as in Module 4, then watch someone ship a **careless declarative rename** that looks clean
in dev and staging — and turns into a silent drop-and-recreate the moment it reaches prod, where the
data actually lives. The **recyclebin** from Module 3 catches the dropped table before it's gone for
good, and rolling back to the known-good package **automatically restores it, rows and all**. Then the
coda: the *right* way to make the same rename — one property, `OldName`, that tells SchemaQuench "same
table, new name," so it renames in place with nothing recycled at all. The takeaway: a rename that
isn't expressed as a rename is a drop in disguise, the recyclebin is what turns that mistake into a bad
afternoon instead of a lost table, and one line is all it takes to avoid the mistake in the first place.

The spine, recapped:

- **Module 2** taught the `{{TargetDb}}` script-token + `SmithySettings_*` env-var mechanics.
- **Module 3** added the **recyclebin** — a registry table plus `CustomTableDrop` / `CustomTableRestore`
  hooks that turn an auto-drop into a recoverable set-aside instead of a destructive `DROP TABLE`.
- **Module 4** taught **promotion** — build one package, deploy the *same* artifact to dev, staging,
  and prod by changing nothing but `SmithySettings_ScriptTokens__TargetDb`.

This module runs all of it together, on a single package that carries the recyclebin from the start,
and puts a real mistake in front of it.

## Layout

```
course3-module-05/
  v1/<engine>/       known-good release: Customer + OrderHeader, FK'd, plus the recyclebin
  v2-bad/<engine>/   the same package, but OrderHeader renamed to SalesOrder the careless way
  v2-fixed/<engine>/ the SAME rename done right — one OldName line makes it an in-place rename
```

`<engine>` is `sqlserver`, `postgres`, `mysql`, or `mariadb`. Each `v1/`, `v2-bad/`, and `v2-fixed/`
carries its own `Package/` plus a shared **`base.settings.json`** (connection details and package-path
default). The target database rides on the `{{TargetDb}}` script token, exactly as in Modules 2 and 4.

`v2-bad` renames `OrderHeader` to `SalesOrder` by simply changing the table name in the package — no
rename hint, nothing telling SchemaQuench "this is the same table, just renamed." SchemaQuench reads two
independent facts: a table it knows about (`OrderHeader`) is no longer in the product, and a table it's
never seen (`SalesOrder`) now is. Declaratively, that's indistinguishable from "drop one table, add
another" — because that's exactly what it is. `v1/<engine>/seed-prod.sql` seeds **prod only** with 3
customers and 4 orders, so there's real data on the line when the rename reaches it.

`v2-fixed` is `v2-bad` with **one line added** to the `SalesOrder` table — `"OldName": "OrderHeader"`.
That single property is the rename hint the careless version was missing: it tells SchemaQuench the new
table *is* the old one under a new name, so instead of drop-and-recreate it issues an in-place rename and
the data never moves. Everything else — columns, keys, the recyclebin — is identical to `v1`.

## Before you start

> **Engine floor:** on your own server this lab needs **SQL Server 2016+** (it uses `CREATE OR ALTER`). It also ships two variants of its table-drop procedure and picks one from the detected version — `STRING_AGG` on 2017+, `FOR XML PATH` below — so the behaviour is the same either way. Other engines run it at any supported version, and the Docker sandbox is already above the floor.

- The [sandbox](../docker) is up (`docker compose up -d`) and all four engines are healthy.
- The target databases exist. Run the Course 3 setup once (idempotent — safe to re-run):

  ```bash
  pwsh ../course3-setup/setup-environments.ps1     # or: ../course3-setup/setup-environments.sh
  ```

  It creates the twelve `ordersservice_{dev,staging,prod}` databases and reports `PASS` for each. This
  lab uses **all three** — dev, staging, and prod.
- The CLI is reachable. The commands below assume `schemaquench` is on your PATH
  (`schemaquench --version` reports `SchemaQuench - Version: 2.7.0.0` or later). If you'd rather not put it on
  the PATH, set `SCHEMAQUENCH` to the full path to the executable and substitute `$SCHEMAQUENCH` for
  `schemaquench` in every command below.

## The flow

Six beats, run in order. Pick an engine — the SQL Server form is fully worked below; the other three
engines follow with the same commands, just a different folder and a different engine name passed to
`lab-sql.sh`.

1. **Establish** — deploy `v1` to `ordersservice_dev`.
2. **Promote** — the SAME `v1` package to `ordersservice_staging`, then `ordersservice_prod` (cross-link
   Module 4). Seed prod with real orders.
3. **Ship the change** — deploy `v2-bad` to dev, then staging. Both succeed cleanly — there were no
   orders there to lose, so nothing looks wrong. This is the trap.
4. **The break reaches prod** — deploy `v2-bad` to prod. It succeeds with no error, but `OrderHeader`
   and its 4 orders are dropped — caught by the recyclebin — and `SalesOrder` is created empty.
5. **Roll back** — WhatIf `v1` against prod first (preview only), then apply it. SchemaQuench recreates
   `OrderHeader`, and the recyclebin's restore hook brings the data back automatically.
6. **Ship it the right way** — deploy `v2-fixed` to dev, staging, then prod. The only difference from
   `v2-bad` is a single `OldName` line, and it changes everything: SchemaQuench renames `OrderHeader` to
   `SalesOrder` *in place*, data intact, nothing recycled.

### SQL Server

```bash
cd v1/sqlserver        # steps 1-2 run from here

# 1. Establish: deploy v1 to dev
SmithySettings_ScriptTokens__TargetDb=ordersservice_dev \
  schemaquench --ConfigFile:./base.settings.json

# 2. Promote: the SAME v1 package to staging, then prod
SmithySettings_ScriptTokens__TargetDb=ordersservice_staging \
  schemaquench --ConfigFile:./base.settings.json
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json

# seed prod with real orders
cd ../..                              # back to the lab folder
../lab-sql.sh sqlserver ordersservice_prod --file v1/sqlserver/seed-prod.sql
cd v1/sqlserver                       # back to the engine folder
```

```bash
cd ../../v2-bad/sqlserver   # steps 3-4 run from here

# 3. Ship the change: v2-bad to dev, then staging — both look clean
SmithySettings_ScriptTokens__TargetDb=ordersservice_dev \
  schemaquench --ConfigFile:./base.settings.json
SmithySettings_ScriptTokens__TargetDb=ordersservice_staging \
  schemaquench --ConfigFile:./base.settings.json

# 4. The break reaches prod: OrderHeader (4 orders) is dropped, SalesOrder is created empty
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json
```

```bash
cd ../../v1/sqlserver   # step 5 runs from here — rolling back to the known-good package

# 5a. WhatIf first — preview the rollback, changes NOTHING
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
SmithySettings_WhatIfONLY=true \
  schemaquench --ConfigFile:./base.settings.json

# 5b. Apply the rollback — OrderHeader is recreated and auto-restored with its 4 orders
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json
```

```bash
cd ../../v2-fixed/sqlserver   # step 6 runs from here — the same rename, done right

# 6. Ship it the right way: v2-fixed to dev, staging, then prod — in-place rename, data preserved
SmithySettings_ScriptTokens__TargetDb=ordersservice_dev \
  schemaquench --ConfigFile:./base.settings.json
SmithySettings_ScriptTokens__TargetDb=ordersservice_staging \
  schemaquench --ConfigFile:./base.settings.json
SmithySettings_ScriptTokens__TargetDb=ordersservice_prod \
  schemaquench --ConfigFile:./base.settings.json
```

PowerShell equivalent (set the vars, run, then clear them):

```powershell
cd v1\sqlserver
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_dev'
schemaquench --ConfigFile:.\base.settings.json                                   # 1. establish (dev)
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_staging'
schemaquench --ConfigFile:.\base.settings.json                                   # 2a. promote (staging)
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_prod'
schemaquench --ConfigFile:.\base.settings.json                                   # 2b. promote (prod)
cd ..\..                                                                          # back to the lab folder
..\lab-sql.ps1 sqlserver ordersservice_prod --file v1\sqlserver\seed-prod.sql
cd v1\sqlserver                                                                   # back to the engine folder

cd ..\..\v2-bad\sqlserver
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_dev'
schemaquench --ConfigFile:.\base.settings.json                                   # 3a. ship (dev) — looks clean
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_staging'
schemaquench --ConfigFile:.\base.settings.json                                   # 3b. ship (staging) — looks clean
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_prod'
schemaquench --ConfigFile:.\base.settings.json                                   # 4. ship (prod) — OrderHeader dropped, SalesOrder empty

cd ..\..\v1\sqlserver
$env:SmithySettings_WhatIfONLY = 'true'
schemaquench --ConfigFile:.\base.settings.json                                   # 5a. WhatIf rollback (prod) — preview only
Remove-Item Env:SmithySettings_WhatIfONLY
schemaquench --ConfigFile:.\base.settings.json                                   # 5b. apply rollback (prod) — auto-restore

cd ..\..\v2-fixed\sqlserver
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_dev'
schemaquench --ConfigFile:.\base.settings.json                                   # 6a. right way (dev) — in-place rename
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_staging'
schemaquench --ConfigFile:.\base.settings.json                                   # 6b. right way (staging)
$env:SmithySettings_ScriptTokens__TargetDb = 'ordersservice_prod'
schemaquench --ConfigFile:.\base.settings.json                                   # 6c. right way (prod) — data intact, nothing recycled
Remove-Item Env:SmithySettings_ScriptTokens__TargetDb
```

### PostgreSQL, MySQL, MariaDB

Same six beats, same commands — swap `sqlserver` for `postgres`, `mysql`, or `mariadb` in every `cd`,
and pass that engine's name to `lab-sql.sh` for the prod seed and any manual verification.

For example, seeding prod on PostgreSQL:

```bash
cd v1/postgres
cd ../..                               # back to the lab folder
../lab-sql.sh postgres ordersservice_prod --file v1/postgres/seed-prod.sql
```

Everything else — the six beats, the env vars, the WhatIf-then-apply rollback, the `v2-fixed` in-place
rename — is identical across all four engines. That's the whole point of the spine: one flow, every
platform.

## What you'll see

**Steps 3 (dev, staging):** an ordinary clean deploy. `OrderHeader` drops, `SalesOrder` is created. No
warning that anything destructive happened, because in dev and staging `OrderHeader` was empty — there
was nothing to lose. This is exactly why the mistake ships: it passes everywhere it's tested.

**Step 4 (prod):** the same clean-looking deploy — SchemaQuench reports success, no error. But
`OrderHeader`, with its 4 orders, is gone from the schema, and `SalesOrder` exists with 0 rows. The
recyclebin's `CustomTableDrop` hook caught the drop on the way out, so the data isn't destroyed — it's
just not where the application expects it anymore. Verify both halves of that:

```bash
# SQL Server — SalesOrder is empty, OrderHeader shows up recycled in the registry
cd ../..                              # back to the lab folder
../lab-sql.sh sqlserver ordersservice_prod \
  "SELECT COUNT(*) FROM dbo.SalesOrder; SELECT OriginalName, RecycledName FROM recyclebin.Registry"

# PostgreSQL
../lab-sql.sh postgres ordersservice_prod \
  "SELECT COUNT(*) FROM public.\"SalesOrder\"; SELECT original_name, recycled_name FROM recyclebin.registry"

# MySQL
../lab-sql.sh mysql ordersservice_prod \
  "SELECT COUNT(*) FROM ordersservice_prod.SalesOrder; SELECT OriginalName, RecycledName FROM ordersservice_prod.recyclebin_Registry"

# MariaDB
../lab-sql.sh mariadb ordersservice_prod \
  "SELECT COUNT(*) FROM ordersservice_prod.SalesOrder; SELECT OriginalName, RecycledName FROM ordersservice_prod.recyclebin_Registry"
cd v2-bad/sqlserver                   # back to the engine folder
```

`SalesOrder` reads `0`. The registry lists `OrderHeader` as the recycled table — its 4 rows are sitting
there intact, just under a different name in a different schema. Dev and staging are green. Prod is
silently missing its order history. (This is the same "reading failures" discipline Course 8 covers in
depth — a success exit code doesn't mean the outcome was what you wanted; cross-link Module 3 for the
recyclebin mechanics themselves.)

**Step 5a (WhatIf):** the rollback preview. SchemaQuench diffs prod against `v1` and reports the
reverting delta — `SalesOrder` gets dropped (empty, nothing lost), `OrderHeader` gets created, and the
recyclebin's `CustomTableRestore` hook is the operation that will bring it back — without touching a row.

**Step 5b (apply):** the real rollback. SchemaQuench recreates `OrderHeader` — but instead of an empty
table, `CustomTableRestore` intercepts and restores the recycled copy: rows and all. Confirm the payoff:

```bash
# SQL Server
cd ../..                              # back to the lab folder
../lab-sql.sh sqlserver ordersservice_prod "SELECT COUNT(*) FROM dbo.OrderHeader"

# PostgreSQL
../lab-sql.sh postgres ordersservice_prod 'SELECT COUNT(*) FROM public."OrderHeader"'

# MySQL
../lab-sql.sh mysql ordersservice_prod "SELECT COUNT(*) FROM ordersservice_prod.OrderHeader"

# MariaDB
../lab-sql.sh mariadb ordersservice_prod "SELECT COUNT(*) FROM ordersservice_prod.OrderHeader"
cd v1/sqlserver                       # back to the engine folder
```

`OrderHeader` is back with all **4** rows, `SalesOrder` is gone, and prod matches the known-good `v1`
package again — data intact, no hand-written recovery script, no restore from backup.

**Step 6 (the right way):** now the same rename, expressed *as* a rename. `v2-fixed`'s `SalesOrder` table
carries one extra line the careless version didn't — `"OldName": "OrderHeader"` — and that's the whole
difference. Promote it dev to staging to prod and SchemaQuench renames the table in place on every engine
(the SQL Server log reads it plainly):

```
Handle Table Renames
  Rename [dbo].[OrderHeader] to [dbo].[SalesOrder]
```

No drop, no data to restore — `SalesOrder` simply *is* the old `OrderHeader`, all 4 rows carried across.
Confirm it on prod:

```bash
# SQL Server — SalesOrder carries the data straight across the rename
cd ../..                              # back to the lab folder
../lab-sql.sh sqlserver ordersservice_prod "SELECT COUNT(*) FROM dbo.SalesOrder"
```

`SalesOrder` reads `4`. Unlike step 4, nothing data-bearing hit the recyclebin this time — `OrderHeader`
wasn't dropped, it was *renamed*, so its rows never left the table. (The bin still holds the empty
`SalesOrder` shells the break and rollback left behind — all zero-row clutter, safe to ignore.) Same
rename, same product, opposite outcome — and the only thing that changed was one line.

## The principle

A declarative rename that isn't expressed as a rename isn't a rename at all — it's a drop and an add,
and SchemaQuench will do exactly that, faithfully, on every environment you point it at. Dev and staging
can't catch the damage because they had nothing to lose; only prod, where the data actually lives, shows
the cost. That's precisely why the recyclebin from Module 3 rides along in every package from the start:
it doesn't know or care whether a drop was intentional, so it catches all of them, careless renames
included. And because SchemaQuench's rollback is just "deploy the package that worked" (Module 3) with
the same promotion discipline that shipped the mistake (Module 4), recovering is the same tool, the same
command shape, no different from any other quench — WhatIf first, then apply, and the recyclebin's
restore hook does the rest.

But the recyclebin is the net, not the fix. The fix for a careless rename was never a bigger net — it was
saying what you meant. `v2-fixed` is `v2-bad` plus one line, `"OldName": "OrderHeader"`, and that single
property is the difference between a drop-and-recreate and an in-place rename. The recyclebin catches the
mistakes you don't see coming; `OldName` is how you don't make this one.

## Teardown

Reset the databases for a clean re-run with `setup-environments --reset` (`-Reset` in PowerShell),
which drops and recreates them empty — plain `setup-environments` is idempotent but won't undo
deployed state. Re-deploying `v1` to each environment also converges back to known-good.

> **The recyclebin stays behind, and that's fine.** This capstone leaves its infrastructure in the
> shared `ordersservice_*` databases — the `recyclebin` schema, its registry, and the drop/restore
> procedures. Go back to an earlier Course 3 module afterwards and its package converges anyway: the
> registry is marked `PreventDrop`, so a package that doesn't declare it leaves it alone rather than
> feeding it to the very hook it records for. Reset only if you want a genuinely clean slate.

After a cert pass, remove disposable build/run output before you're done — a cert pass isn't done until
disposable output is gone:

```bash
git clean -fdX
# or, more targeted:
rm -rf _certcli publish-* **/checkpoints
```
