# Course 10 · Module 5 — Retiring the gates

This is the finale. Across Modules 1–4 you built version gates — the engine adapting its own
DDL, `ShouldApplyExpression` on the scripts you wrote, two shapes of one query, the oldest tier's
encoding switch. Every one of those gates was **debt with a payoff date.** This module collects
the debt.

The last laggard upgrades and you retire a gate in a **three-part move**:

1. **Delete the legacy variant** — the gate and its second shape come out. One shape again.
2. **Raise `MinimumVersion` in `Product.json`** — declare the version you stopped writing for
   unsupported.
3. **Pre-flight now refuses anything below the floor** — a silent runtime branch becomes a loud,
   early, enforced stop.

Part 3 is the one people forget, and it's the whole reason the course kept `MinimumVersion` and
the detected version as separate numbers from Module 0. `MinimumVersion` is **your policy floor**;
it is a different axis from the version SchemaSmith detects to pick its DDL, and different again
from SchemaSmith's own capability floor. Yours can sit higher than the tool's — and raising it is
what turns "we don't support PG12 anymore" from tribal knowledge into a refusal nobody can deploy
past by accident.

> **Engine floor:** this lab deploys into the `learn_2022` tier, which is a database at **compatibility level 160** — a level only **SQL Server 2022+** offers. On an older engine [`course10-setup`](../course10-setup/README.md) cannot provision that tier and the lab stops with *"No database targets discovered"*. The sandbox default is 2022, so this only applies if you have pointed `MSSQL_IMAGE` at an older release. The other engines in this lab are unaffected.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  PostgreSQL 16 (`15432`) and the PostgreSQL 12 floor (`15433`), plus the SQL Server tiers on
  `localhost,11433`. This module deploys into those; it does not create them.
- `schemaquench --version` and `schematongs --version` both answer **2.7.0** or later.

---

## Part 1 — the PostgreSQL retirement (delete the variant, raise the floor, refuse below it)

The `postgres/` folder ships the same package in two states so the retirement is a worked diff:

| | `before/package` | `after/package` |
| --- | --- | --- |
| Legacy view | `Programmability/Legacy/public.v_reading_summary.sql` (`min()`) | **deleted** |
| Modern view | `Programmability/Modern/...` gated `{{ServerMajorVersion}} >= 16` | gate **removed** — one shape |
| `Template.json` | two `ScriptFolders` (Modern + Legacy), each gated | one folder, no `ShouldApplyExpression` |
| `Product.json` | no floor declared | `"MinimumVersion": "16"` |

It's the exact PG12-vs-PG16 view split from Module 2, carried forward so you can retire the gate
around it.

### Before — the two gated variants, both live

Deploy `before/package` to each tier and watch the fleet split, exactly as in Module 2. PG16
takes the modern `any_value()` view; PG12 takes the legacy `min()` view.

```
cd postgres
schemaquench --ConfigFile:quench.settings.before.pg16.json --LogPath:"$PWD/logs"   # macOS / Linux
schemaquench --ConfigFile:quench.settings.before.pg12.json --LogPath:"$PWD/logs"
```

```
# BEFORE -> pg16 (15432): Modern applied, Legacy skipped
[localhost].[learn]   Skipping folder 'Programmability/Legacy' — ShouldApplyExpression evaluated false
[localhost].[learn]     Quenched ./before/package/Templates/Main/Programmability/Modern/public.v_reading_summary.sql
[localhost].[learn] Successfully Quenched

# BEFORE -> pg12 (15433): Legacy applied, Modern skipped
[localhost].[learn]   Skipping folder 'Programmability/Modern' — ShouldApplyExpression evaluated false
[localhost].[learn]     Quenched ./before/package/Templates/Main/Programmability/Legacy/public.v_reading_summary.sql
[localhost].[learn] Successfully Quenched
```

### The retirement, as a diff

Look at what `after/package` changed from `before/package` — it's the whole move in three edits:

- **`Templates/Main/Programmability/Legacy/public.v_reading_summary.sql`** — gone.
- **`Templates/Main/Template.json`** — the `Programmability/Legacy` folder entry is removed, and
  the `ShouldApplyExpression` is stripped off the surviving `Programmability/Modern` folder (a
  blank/absent gate always applies).
- **`Product.json`** — gains `"MinimumVersion": "16"`.

### After — deploy the retired package

Point the retired package at PG16 and it deploys clean — one view, no gate, floor cleared:

```
schemaquench --ConfigFile:quench.settings.after.pg16.json --LogPath:"$PWD/logs"
```

```
[localhost].[learn]     Quenched ./after/package/Templates/Main/Programmability/Modern/public.v_reading_summary.sql
[localhost].[learn] Successfully Quenched
```

Now point the identical retired package at the PG12 floor. `MinimumVersion` refuses it at
pre-flight — **before any change is attempted:**

```
schemaquench --ConfigFile:quench.settings.after.pg12.json --LogPath:"$PWD/logs"
```

```
One or more target servers are below the product's declared MinimumVersion; aborting before any deployment:
  localhost: detected version 12 is below the product's declared MinimumVersion 16
```
Non-zero exit (3); nothing deployed.

That refusal is the point of Part 2 of the retirement. Without the `MinimumVersion` bump, the
gate is simply gone and a forgotten PG12 server would sail past pre-flight and fail deep in the
deploy where `any_value()` doesn't resolve. With it, the stop is loud, early, and enforced.

> **Why the refusal is shown on PostgreSQL, not SQL Server.** `MinimumVersion` compares the
> **detected server version**, not the compatibility level. Our sandbox SQL Server is one 2022
> binary (server major 16) hosting the `learn_2022` / `learn_2016` / `learn_2008` compat tiers —
> so a SQL Server `MinimumVersion` is all-or-nothing across those three (it sees server 16 for all
> of them). PostgreSQL has a real 16-vs-12 version gap, so it's the honest place to watch the floor
> refuse one server and accept another. On SQL Server you can still delete the legacy variant and
> raise `MinimumVersion` — the refusal just can't discriminate across compat tiers on one binary.

---

## Part 2 — what survives a SchemaTongs re-extraction of a gated package (SQL Server)

Retiring gates by hand is one thing. But a gated package also has to survive being **re-extracted**
from a live database — otherwise every round-trip through SchemaTongs would quietly corrupt your
variants. The `sqlserver/` package proves it does.

The package carries one table, `dbo.Gadget`, as **two token-gated component variants**:

| File | `VariantName` | `ShouldApplyExpression` | Shape |
| --- | --- | --- | --- |
| `dbo.Gadget.Modern.json` | `Modern` | `'{{Edition}}'='Modern'` | `Id`, `Label` |
| `dbo.Gadget.Legacy.json` | `Legacy` | `'{{Edition}}'='Legacy'` | `Id` only |

`Product.json` sets the script token `Edition` to `Modern`, so the Modern variant is the active
one. Deploy it:

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2022]         Adding new table [dbo].[Gadget] (variant: Modern)
[localhost,11433].[learn_2022]         Creating constraint [dbo].[Gadget].[PK_Gadget]
[localhost,11433].[learn_2022] Successfully Quenched
```

Now re-extract `dbo.Gadget` from the live database back into the same package with SchemaTongs:

```
schematongs --ConfigFile:SchemaTongs.settings.json
```

The re-extraction resolves the `{{Edition}}` token to `Modern` before evaluating the gate — exactly
as deploy does — so it knows the Modern variant is the active one and folds the extracted shape
into it. Four things hold:

- **The active (Modern) variant is refreshed in place** — same file, its `VariantName` (`Modern`)
  kept.
- **The gate survives raw** — `'{{Edition}}'='Modern'` stays authored, not baked to `'Modern'='Modern'`.
- **The inactive (Legacy) variant is byte-for-byte untouched.**
- **No ungated `dbo.Gadget.json` is written** — the token gate folds instead of falling through,
  so `--Validate` reports **no `SS-DUP-001`.**

```
$ schematongs --ConfigFile:SchemaTongs.settings.json
  Cast Json for dbo.Gadget
    Casting ./package\Templates\Main\Tables\dbo.Gadget.Modern.json
  Tables:     1 extracted, 0 errors
Casting Completed Successfully

$ ls package/Templates/Main/Tables/
dbo.Gadget.Legacy.json
dbo.Gadget.Modern.json

$ schemaquench --Validate --SchemaPackagePath:./package
0 error(s), 0 warning(s)
```

This is why a token-gated variant is safe to keep in a package indefinitely: extracting the live
shape doesn't duplicate it or lose the gate. The gate is transitional by design — you author it,
run with it, re-extract through it, and retire it by hand when the fleet converges (Part 1).

---

## Two retirement stories — and why only one is observable

Module 3 gave you a **state gate** — a `dbo.RolloutControl` row someone flips per tenant. That gate
has a visible, deliberate off-switch: flip the row back, or drop the table, and the gate is
demonstrably retired. You can *see* it turn off.

A **version predicate** is different. `{{ServerMajorVersion}} < 16` doesn't get switched off — it
just quietly stops firing once the last PG12 server upgrades. Nothing announces it. The legacy SQL
sits in the package as dead code that never runs, and no one notices until they're reading a diff
a year later wondering what it's for.

That's exactly why the `MinimumVersion` bump matters: **it's the thing that makes a version-gate
retirement observable.** Deleting the legacy variant is silent; raising the floor turns the
retirement into a loud pre-flight refusal you can point at. One is housekeeping; the other is the
declaration.

## What's in here

| Path | Purpose |
| --- | --- |
| `postgres/before/package/` | Modern/Legacy view split gated on `{{ServerMajorVersion}}`; no floor. |
| `postgres/after/package/` | Legacy deleted, gate removed, `"MinimumVersion": "16"` declared. |
| `postgres/quench.settings.before.pg16.json` / `…before.pg12.json` | before package to PG16 / PG12. |
| `postgres/quench.settings.after.pg16.json` | after package to PG16 — accepted. |
| `postgres/quench.settings.after.pg12.json` | after package to PG12 — **refused at pre-flight.** |
| `sqlserver/package/` | `dbo.Gadget` as two token-gated component variants (`Edition`). |
| `sqlserver/quench.settings.json` | deploys the Modern variant to `learn_2022`. |
| `sqlserver/SchemaTongs.settings.json` | re-extracts `dbo.Gadget` back into the package. |

> The `.json-schemas/` under each `package/` are generated by `schemaquench --WriteSchemasOnly`
> and are not hand-authored. Regenerate them if the domain model changes.

## The arc closes here

The engine adapts (Module 1). You gate what it won't touch (Module 2). You pick between two valid
shapes, or gate on state the server can't see (Module 3). You reach the oldest tier and the wire
format itself bends (Module 4). And here, the farm converges: you delete the gates, declare the
new floor, and the package ends up a single clean shape again — exactly where a single-version
shop starts. The scheme retired itself, which is what made it worth building.
