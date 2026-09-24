# Course 7, Module 2 — Steering the roster (lab)

Goal: keep the same four-table `Shop` package from Module 1, but stop letting the **catalog** decide the
fleet. Here your **deployment config** owns the roster — a dev roster, a prod roster, and a one-tenant
canary — and a **scope filter** narrows any single run to a subset. Two mechanisms, one distinction to
keep straight:

- **`Target.TemplateTargets.Main.Databases`** — *replaces* discovery. The config list **is** the roster;
  the `DatabaseIdentificationScript` is bypassed.
- **`Target.Databases`** — *narrows* whatever was discovered (or overridden) down to a subset for this
  one run. The roster is unchanged; you just touch fewer of it.

All four engines. This builds directly on Module 1 (database-per-tenant fan-out) — do that one first.

## Before you start

- The [sandbox](../docker) is up and verified (all four engines healthy).
- The fleet exists — run [`../course7-setup`](../course7-setup) once (creates `fleet_tenant_001`…`005`,
  on each engine). Module 1 recommended first.
- The CLI is on your PATH — `schemaquench --version` answers **2.4.0** or later.

Each engine folder (`sqlserver/`, `postgres/`, `mysql/`, `mariadb/`) ships the same native `Shop` `Package/` as
Module 1 (the catalog `DatabaseIdentificationScript` is retained — that's what the config *overrides*),
plus **four** settings files:

| Settings file | What it steers |
| --- | --- |
| `quench.settings.json` | Baseline — plain catalog discovery (all five), same as Module 1. |
| `quench.settings.dev.json` | `TemplateTargets` roster of **two** tenants (`001`, `002`). |
| `quench.settings.prod.json` | `TemplateTargets` roster of **all five**. |
| `quench.settings.canary.json` | Discovery + a `Target.Databases` filter narrowing the run to **one** (`001`). |

## Step 1: Hand the roster to config — `TemplateTargets`

Open `sqlserver/quench.settings.dev.json`. The roster lives in the config now, not the catalog:

```json
"TemplateTargets": {
  "Main": { "Databases": [ "fleet_tenant_001", "fleet_tenant_002" ] }
}
```

Preview it — read-only:

```bash
cd sqlserver
schemaquench --ConfigFile:quench.settings.dev.json --PreviewTargets
```

```
[localhost,11433] Template 'Main' Databases sourced from TemplateTargets (2 entries); DatabaseIdentificationScript bypassed.
Template: Main [required]
  db: fleet_tenant_001
  db: fleet_tenant_002
```

Two tenants — even though the catalog holds five. `DatabaseIdentificationScript bypassed` says it plainly:
config **replaced** discovery. Deploy it:

```bash
schemaquench --ConfigFile:quench.settings.dev.json
```

```
[localhost,11433] Template 'Main' Databases sourced from TemplateTargets (2 entries); DatabaseIdentificationScript bypassed.
Template 'Main': 2 units - db: TemplateTargets:Main:Databases; schema: (regular template)
```

`db: TemplateTargets:Main:Databases` — every unit came from your config. Tenants `003`, `004`, `005`
get **no work unit this run**; the catalog still lists them, but the config didn't.

The rollup is per template, not per unit, and that matters for exactly the case this module is about:
when every unit in a template shares a source, you get one line. When they **disagree** — 999 tenants from
the script and one from an override — the deviating units are still named individually, because a count
is precisely the wrong way to report the one that is different. Per-target `databaseSource` and
`schemaSource` are in the deployment summary either way ([Module 6](../course7-module-06)).

## Step 2: One package, many environments

The package never changed — only which settings file you point `--ConfigFile` at. That's the whole idea:
ship one canonical package everywhere, let each environment's config declare its fleet. Deploy the prod
roster:

```bash
schemaquench --ConfigFile:quench.settings.prod.json
```

```
[localhost,11433] Template 'Main' Databases sourced from TemplateTargets (5 entries); DatabaseIdentificationScript bypassed.
```

Five work units this time. Same package, different roster — the config is the only thing that moved.

## Step 3: The gotcha — an override still needs the template marked for fan-out

There's one rule worth knowing before you strip discovery entirely. A `Databases` override **still**
requires the template to declare *some* `DatabaseIdentificationScript` — it's how SchemaSmith knows the
template fans out on the database axis. Remove that line from `Package/Templates/Main/Template.json` and
deploy the dev roster, and the run refuses before it touches anything:

```
SchemaQuench.TemplateTargetValidationException: TemplateTargets.Main.Databases requires Template 'Main' to declare a DatabaseIdentificationScript. Add a placeholder script (e.g., SELECT 'CONFIG-DRIVEN' AS DatabaseName WHERE 1=0) to mark this template as database-fan-out.
```

When your deployment system is the *sole* authority and there's no naming convention to discover, use the
placeholder the error names:

```json
"DatabaseIdentificationScript": "SELECT 'CONFIG-DRIVEN' AS DatabaseName WHERE 1=0"
```

Deploy again — the override drives the fan-out exactly as before, and the placeholder query is never run
(the config bypasses it). The shipped package here keeps the **real** catalog script, so you can see the
override win over a live discovery result; the placeholder is the form to reach for when there's nothing
to discover.

## Step 4: A canary — narrow one run with a scope filter

`TemplateTargets` *replaces* the roster. A **scope filter** does something different — it *narrows*
whatever was discovered (or overridden) to a subset, for this run only. Open
`quench.settings.canary.json`:

```json
"Databases": [ "fleet_tenant_001" ]
```

```bash
schemaquench --ConfigFile:quench.settings.canary.json --PreviewTargets
schemaquench --ConfigFile:quench.settings.canary.json
```

One work unit — `fleet_tenant_001`. Discovery still ran and returned five; the filter intersected it down
to your canary. Deploy the canary, confirm it's healthy, then drop the filter and run the full roster.

The filter fails fast on a typo — name a tenant that isn't there and the run stops before any work, and
tells you what *was* available:

```
Target filter rejection for template 'Main': One or more Target.* filter values do not match the discovered work-unit set. Target.Databases value(s) not discovered: [fleet_tenant_999]. Available: [fleet_tenant_001,fleet_tenant_002,fleet_tenant_003,fleet_tenant_004,fleet_tenant_005].
```

## Step 5: Do it on PostgreSQL, MySQL, and MariaDB

Same four steps in `postgres/`, `mysql/`, and `mariadb/`. The `TemplateTargets` and `Target.Databases`
config is **identical** on all four engines — only the (retained) catalog dialect in `Template.json`
differs, and that's only consulted when you *don't* override. Each engine replaces discovery with a
dev/prod roster,
refuses an override with no script, and narrows to a one-tenant canary exactly the same way.

## Replace vs narrow

| Config | Effect | Reach for it when |
| --- | --- | --- |
| `Target.TemplateTargets.Main.Databases` | **Replaces** discovery — the config list *is* the roster | your deployment system owns the fleet list |
| `Target.Databases` | **Narrows** the discovered/overridden set for this run | a canary, one region, one tenant — right now |

They compose: `TemplateTargets` declares the roster; `Target.Databases` filters it to a canary.

## The principle

Module 1 let the fleet find itself. Module 2 puts you in the driver's seat: the deployment system declares
exactly which tenants a run touches, and a scope filter lets you rehearse against one before you commit to
all. Same package, steered.

Next: **Module 3** — when the roster names a tenant database that doesn't exist yet, `CreateIfMissing`
stands it up as part of the run.
