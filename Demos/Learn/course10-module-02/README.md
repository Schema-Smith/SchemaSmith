# Course 10 · Module 2 — Gate it yourself

Module 1 let the engine adapt the DDL *it* generates. This module is the other side of that
boundary: **SchemaSmith will not rewrite the scripts *you* wrote.** When the thing that differs
between two targets lives in a view *you* authored — or an index, or a migration — you gate it
yourself with `ShouldApplyExpression`, and the target engine evaluates the condition at deploy
time.

You'll use all **three levers**, coarsest to finest:

| Lever | Where the gate lives | This lab's example |
| --- | --- | --- |
| **Folder** | `ScriptFolders[].ShouldApplyExpression` in `Template.json` | a `Modern` / `Legacy` split of the same view — one folder applies per target |
| **Component** | `ShouldApplyExpression` + `VariantName` on a table/index/column/… | two variants of one index; the applied variant prints ` (variant: …)` in the log |
| **Sentinel** | a `RAISERROR('SCHEMASMITH: SHOULD NOT APPLY', …)` in the script body | a migration that gates *itself* out below compat 160 |

## The footgun this module turns on

On PostgreSQL, MySQL and MariaDB, "what version is this server?" answers "what syntax can I
use?". **On SQL Server it does not.** A modern 2022 binary can host a database left at an old
*compatibility level*, and a good deal of newer syntax parse-errors there even though the server
is current. So the gate everyone reaches for first is wrong:

```sql
-- WRONG for syntax gating. ProductMajorVersion is 16 on a 2022 server regardless of
-- the database's compatibility level, so it green-lights syntax that will not parse
-- in a compat-130 database on that same server.
SERVERPROPERTY('ProductMajorVersion') >= 16

-- RIGHT. Asks the question that actually governs syntax availability.
(SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) >= 160
```

Rule of thumb: **gate syntax on compatibility level, gate features on server version.** They are
different questions, and only SQL Server makes you ask both. SchemaSmith 2.4.0 gives you a token
for each — `{{CompatibilityLevel}}` and `{{ServerMajorVersion}}` — usable in any
`ShouldApplyExpression`. The tokens expand to plain integers before the gate runs -- the target's actual compatibility level and server major (`{{CompatibilityLevel}} >= 160` becomes `160 >= 160`); the raw forms above are the hand-written equivalents.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  `learn_2016` (compat 130) and `learn_2022` (compat 160) on the SQL Server instance, and the
  PostgreSQL 12 floor beside the current 16. This module deploys into those databases — it does
  not create them. See that lab's ports-and-tiers table.
- `schemaquench --version` answers **2.7.0** or later. Folder/component/sentinel gating shipped
  earlier; the `{{CompatibilityLevel}}` / `{{ServerMajorVersion}}` tokens shipped in 2.4.0.

## SQL Server — one package, two compatibility levels, three levers

The `sqlserver/package/` deploys to two databases on the **same 2022 instance** that differ only
in compatibility level. Nothing else changes between the runs — the compat level is the single
variable.

### Deploy to `learn_2022` (compat 160) — the modern path

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD/logs"     # macOS / Linux
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD\logs"     # Windows
```

At compat 160 all three levers take the modern branch: the `Programmability/Modern` folder
(`GENERATE_SERIES` view) applies, the modern index variant is chosen, and the sentinel migration
runs (it inserts the marker row).

<!-- CERT: schemaquench stdout for learn_2022 (compat 160). Must show:
     (a) Programmability/Modern applied, Programmability/Legacy skipped
         ("Skipping folder 'Programmability/Legacy' ... evaluated false");
     (b) the IX_Reading_TakenAt add line carrying " (variant: Modern (compat 160+))";
     (c) the MigrationScripts/After/SeedDeploymentMarker.sql migration RUNNING (not skipped);
     (d) Successfully Quenched. -->

### Deploy to `learn_2016` (compat 130) — the legacy path

```
schemaquench --ConfigFile:quench.settings.2016.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Same package, same server, same binary — only the compatibility level differs. Now every lever
flips to the legacy branch: the `Programmability/Legacy` folder (recursive-CTE view) applies, the
legacy index variant is chosen, and the sentinel migration gates *itself* out — logged as
`Skipped (ShouldNotApply)`.

<!-- CERT: schemaquench stdout for learn_2016 (compat 130). Must show:
     (a) Programmability/Legacy applied, Programmability/Modern skipped
         ("Skipping folder 'Programmability/Modern' ... evaluated false");
     (b) the IX_Reading_TakenAt add line carrying " (variant: Legacy (compat < 160))";
     (c) SeedDeploymentMarker.sql logged as "Skipped (ShouldNotApply)";
     (d) Successfully Quenched. -->

### Why the gate has to be compatibility level, not server version

Proof that both databases report the **same** server binary, yet `GENERATE_SERIES` still
parse-errors on the compat-130 one — the whole argument for gating syntax on compat level:

<!-- CERT: negative-control transcript. Must show, against learn_2016 (compat 130):
     (a) SELECT SERVERPROPERTY('ProductMajorVersion') = 16 AND compatibility_level = 130
         (and the same ProductMajorVersion = 16 for learn_2022);
     (b) running the Modern view's body (SELECT ... FROM GENERATE_SERIES(0,29)) against
         learn_2016 raising "Msg 208 ... Invalid object name 'GENERATE_SERIES'". -->

## PostgreSQL — the levers are cross-engine

The compat footgun is genuinely SQL-Server-specific — compatibility level is a SQL Server
concept, and no other engine splits "what syntax parses" from "what version is this server." But
the three **levers** are not SQL-Server-only. Here's the same folder-gate lever on PostgreSQL,
splitting a view on the real PG12-vs-PG16 boundary with `{{ServerMajorVersion}}` (off SQL Server,
`{{CompatibilityLevel}}` falls back to the same value, so the gate shape is identical).

### Deploy to PostgreSQL 16 (port 15432) — the modern path

```
cd ../postgres
schemaquench --ConfigFile:quench.settings.pg16.json --LogPath:"$PWD/logs"     # macOS / Linux
```

`{{ServerMajorVersion}} >= 16` is true, so the `Programmability/Modern` view (`any_value()`, a
PG16 aggregate) applies.

<!-- CERT: schemaquench stdout for PG16 (15432). Must show Programmability/Modern applied,
     Programmability/Legacy skipped ("... evaluated false"), Successfully Quenched. -->

### Deploy to PostgreSQL 12 (port 15433) — the legacy path

```
schemaquench --ConfigFile:quench.settings.pg12.json --LogPath:"$PWD/logs"     # macOS / Linux
```

`{{ServerMajorVersion}} >= 16` is false, so the `Programmability/Legacy` view (`min()`, portable
to every supported version) applies instead — the `any_value()` form would have errored on PG12.

<!-- CERT: schemaquench stdout for PG12 (15433). Must show Programmability/Legacy applied,
     Programmability/Modern skipped ("... evaluated false"), Successfully Quenched.
     Optional negative control: running the Modern view body against PG12 raising
     "function any_value(character varying) does not exist". -->

## What each folder is

| Path | Purpose |
| --- | --- |
| `sqlserver/package/Templates/Main/Programmability/Modern/` | `GENERATE_SERIES` view; folder-gated `{{CompatibilityLevel}} >= 160`. |
| `sqlserver/package/Templates/Main/Programmability/Legacy/` | recursive-CTE view; folder-gated `{{CompatibilityLevel}} < 160`. |
| `sqlserver/package/Templates/Main/Tables/dbo.Reading.json` | table carrying two `IX_Reading_TakenAt` variants (component gate + `VariantName`). |
| `sqlserver/package/Templates/Main/MigrationScripts/After/SeedDeploymentMarker.sql` | sentinel-gated migration — self-skips below compat 160. |
| `sqlserver/quench.settings.2022.json` / `…2016.json` | same package; targets `learn_2022` (160) / `learn_2016` (130). |
| `postgres/package/Templates/Main/Programmability/{Modern,Legacy}/` | `any_value()` (PG16) vs `min()` view; folder-gated on `{{ServerMajorVersion}}`. |
| `postgres/quench.settings.pg16.json` / `…pg12.json` | same package; targets PG16 (15432) / PG12 (15433). |

## Up next

Module 3 — **two shapes of one query.** Here you gated on the *engine* (version, compat level).
Next: two shapes that are both correct — one faster on the new engine, one on the old —
converging automatically as each server upgrades, no human in the loop. Then change the question
to something the target *can't* detect — a rollout-approval row someone flips per tenant — and the
same seam gates on state instead of version.
