# Course 9 · Module 5 — Capstone: an independent release done right

This is the capstone. Everything in the course — native packages, connection config, organized package structure, independent pipelines — comes together here in a pattern that solves one of the hardest problems in multi-service schema management: how do you ship a reference-data change that multiple services depend on, without coordinating everyone at once?

The answer is expand / contract, and you're about to deploy it.

> **Engine floor:** this capstone runs Catalog, Orders and Sessions together, and all three sides use DataDelivery — so on your own server the SQL Server service needs **2016+** and the MySQL service **8.0+** (`OPENJSON` / `JSON_TABLE`). PostgreSQL runs at any supported version. Course 9 spans three engines at once, so the Docker sandbox is the easier path here regardless.

## What you're learning

**An independent release for one service.** Catalog (PostgreSQL) owns the canonical `shipping_region` reference table and manages it with DataDelivery. When Catalog needs to add a new region or retire an old one, it ships that change on its own timeline — Orders and Sessions don't move, don't coordinate, and don't even know it happened until they're ready.

**Each service keeps its own synced copy.** Orders (SQL Server) and Sessions (MySQL) each carry their own `valid_region` reference table, kept in sync with Catalog's canonical list — also via DataDelivery. A consumer "adopts" a change by re-delivering its own copy with the new set of regions, on its own schedule. No cross-engine foreign key, no shared database — just the same reference data, delivered independently to each service.

**Expand before contract.** You can't remove a value consumers depend on until they've stopped using it. The expand / contract sequence makes this safe without a deployment window: Catalog adds the new region additively (expand), each consumer re-syncs its own copy to include it and drop the old one (adopt), and only then does Catalog remove the retired region from the canonical list (contract). Independent deploys, zero coordination.

**DataDelivery MergeType controls what happens.** `"Insert/Update"` is additive — it inserts and updates the rows in the file and leaves everything else alone. `"Insert/Update/Delete"` converges the table to exactly the file's rows, deleting anything not listed. Expand uses the additive form; baseline, adopt, and contract use the converging form. Re-running any delivery is a clean no-op once the data already matches.

## Prerequisites

- Sandbox up and `course9-setup` run (creates the `orders`, `catalog`, `sessions` databases).
- `schemaquench --version` **2.7.0** or later on your PATH.

## The scenario

Catalog currently carries three shipping regions: `NA`, `EMEA`, and `LEGACY`. The `LEGACY` region is being retired in favor of a new `APAC` region. Here's what that looks like as independent deploys:

| Step | Who | What | MergeType |
|------|-----|------|-----------|
| Baseline | All three | Initial state — NA, EMEA, LEGACY | `Insert/Update/Delete` |
| Expand | Catalog alone | Add APAC (keep LEGACY) | `Insert/Update` |
| Adopt | Orders, then Sessions (own cadence) | Re-sync own copy to NA, EMEA, APAC | `Insert/Update/Delete` |
| Contract | Catalog alone | Remove LEGACY | `Insert/Update/Delete` |

## Step 1 — Deploy all three baselines

Start with every service at its initial state. These three deploys are independent of each other and can run in any order.

### Catalog (PostgreSQL)

macOS / Linux:
```bash
cd postgres
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd postgres
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

This creates the canonical `public.shipping_region` with NA, EMEA, and LEGACY. DataDelivery `Insert/Update/Delete` converges the table to exactly those three rows.

### Orders (SQL Server)

macOS / Linux:
```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd sqlserver
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

This creates `dbo.valid_region` — Orders' own copy of the valid regions — delivered with NA, EMEA, and LEGACY.

### Sessions (MySQL)

macOS / Linux:
```bash
cd mysql
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd mysql
schemaquench --ConfigFile:quench.settings.baseline.json --LogPath:"$PWD\logs"
```

This creates `valid_region` — Sessions' own copy — delivered with the same three regions.

## Step 2 — Expand: Catalog alone adds APAC

Catalog ships its expand release. This is a release for **one service only** — Orders and Sessions are not touched, not notified, and continue to function exactly as before.

macOS / Linux:
```bash
cd postgres
schemaquench --ConfigFile:quench.settings.expand.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd postgres
schemaquench --ConfigFile:quench.settings.expand.json --LogPath:"$PWD\logs"
```

The expand package uses `MergeType: "Insert/Update"`. DataDelivery inserts the APAC row and updates the others — LEGACY is still in the file, so it survives. After this deploy, `shipping_region` has four rows: NA, EMEA, LEGACY, APAC. APAC is now official; LEGACY is still valid for anyone who hasn't migrated. Nothing in Orders or Sessions changes. The window is open for consumers to migrate.

## Step 3 — Adopt: each consumer on its own cadence

Each consumer re-syncs its own `valid_region` copy whenever it's ready. Orders and Sessions don't coordinate with each other or with Catalog.

### Orders adopts (SQL Server)

macOS / Linux:
```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.adopt.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd sqlserver
schemaquench --ConfigFile:quench.settings.adopt.json --LogPath:"$PWD\logs"
```

The adopt package re-delivers `dbo.valid_region` with NA, EMEA, and APAC. `Insert/Update/Delete` adds APAC and removes LEGACY — the Orders service has migrated off LEGACY. Re-run it and it's a clean no-op; the copy already matches.

### Sessions adopts (MySQL)

macOS / Linux:
```bash
cd mysql
schemaquench --ConfigFile:quench.settings.adopt.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd mysql
schemaquench --ConfigFile:quench.settings.adopt.json --LogPath:"$PWD\logs"
```

Same change to Sessions' own copy: NA, EMEA, APAC. Sessions has migrated off LEGACY independently of Orders — they don't coordinate.

## Step 4 — Contract: Catalog removes LEGACY

Only run this **after** both consumers have adopted. Once nobody's copy carries LEGACY, Catalog can safely remove it from the canonical list.

macOS / Linux:
```bash
cd postgres
schemaquench --ConfigFile:quench.settings.contract.json --LogPath:"$PWD/logs"
```

Windows PowerShell:
```powershell
cd postgres
schemaquench --ConfigFile:quench.settings.contract.json --LogPath:"$PWD\logs"
```

The contract package uses `MergeType: "Insert/Update/Delete"` and the tabledata contains only NA, EMEA, and APAC. DataDelivery deletes the LEGACY row. The retire is complete.

This is a release for **one service only**. Orders and Sessions are unaffected — their copies already dropped LEGACY at adopt time.

## What to look for

- **Expand is safe at any time.** Run step 2 before or after consumers are ready — it doesn't matter. The additive MergeType means nothing is removed. LEGACY stays until the contract explicitly removes it.
- **The order that matters is expand before contract.** Consumers must adopt the new region before Catalog contracts. The sequence within consumers is flexible — Orders can adopt before Sessions, or after, or on the same day. But nobody contracts before everyone adopts.
- **Each service's copy is its declaration of what it accepts.** The expand / contract sequence works because each consumer controls its own reference copy. Catalog never dictates when consumers move — it only controls when it's safe to retire the old value from the canonical source.
- **Artifacts show the convergence plan.** After each deploy, open `logs/` and review the artifact files. For the baseline, adopt, and contract runs, `Insert/Update/Delete` generates a delete for any row not in the file. For the expand run, no deletes appear — the additive mode leaves existing rows alone.

## The discipline

Expand-before-adopt-before-contract is what makes independent services share reference data without a coordinated release window. The pattern works for regions, status codes, event types, or any reference table where consumers need advance notice before a value disappears.

Never deploy a contract before consumers have adopted. That's the one hard rule. Everything else — timing, order between consumers, how long the window stays open — is the consuming team's call.

## Wrapping up Course 9

You've built the full arc: native packages on three engines, file-less connection config, organized package structure, independent CI pipelines, and now an expand / contract reference-data release with no coordination tax. Module 0 frames the whole course if you want to revisit the big picture before moving on.
