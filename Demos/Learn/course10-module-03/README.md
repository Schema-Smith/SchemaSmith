# Course 10 · Module 3 — Two shapes of one query

Module 2 gated because the old engine *couldn't* — `GENERATE_SERIES` won't parse at compat
130, so the gate kept broken SQL off the old target. This module is the case where **both
shapes are right.** Two variants of one query, both valid on both engines; you're choosing the
better shape per tier, not dodging an error. And then the harder case: gating on something the
target **can't detect at all.**

The whole module turns on one question:

> **Can the target answer this question itself?**

- **Yes — it's detectable** (server version, compatibility level, an existing object). Gate on
  the answer. It converges automatically as each server upgrades; no human in the loop. This is
  the common case — lead with it.
- **No — it's not detectable** (a rollout-approval decision, a cost/scheduling sign-off, a
  customer opt-in). Gate on **state**: a `RolloutControl` row someone flips per tenant.

And the anti-pattern that falls out of that question: a control table for something the target
*could* detect is manufactured toil that drifts out of sync. The state gate is **only** for
facts that live outside the database.

| Part | Gate | Predicate | Receipt |
| --- | --- | --- | --- |
| **A — auto-converge** | detectable (compat level) | `{{CompatibilityLevel}} >= 160` / `< 160` + `VariantName` | the applied variant prints ` (variant: …)` in the log |
| **B — state gate** | not detectable (approval state) | `EXISTS (… RolloutControl … status='Ready')` | the construct is **present** when Ready, **absent** when Pending |

> **Engine floor:** this lab deploys into the `learn_2022` tier, which is a database at **compatibility level 160** — a level only **SQL Server 2022+** offers. On an older engine [`course10-setup`](../course10-setup/README.md) cannot provision that tier and the lab stops with *"No database targets discovered"*. The sandbox default is 2022, so this only applies if you have pointed `MSSQL_IMAGE` at an older release. The other engines in this lab are unaffected.

## Prerequisites

- The four-engine sandbox is up (`Demos/Learn/docker`).
- **Run [`course10-setup`](../course10-setup/README.md) first** so the mixed fleet is standing:
  `learn_2022` (compat 160), `learn_2016` (compat 130), and `learn_2008` (compat 100) on the SQL
  Server instance at `localhost,11433`. This module deploys into those databases — it does not
  create them.
- `schemaquench --version` answers **2.7.0** or later (for the `{{CompatibilityLevel}}` token).

## Step 0 — stand up the control table (run this first)

Both parts deploy the **one** package, and that package includes `dbo.CustomerActivity`, whose
index gate reads `dbo.RolloutControl`. If that table doesn't exist, the `EXISTS` predicate fails
to bind ("Invalid object name 'dbo.RolloutControl'"). So the control table is infrastructure you
establish **before any deploy** — seeded to `Pending` in every tenant:

```
cd sqlserver
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -i bootstrap/init-rollout.sql
```

This creates `dbo.RolloutControl` and a `CustomerActivityIndex = 'Pending'` row in `learn_2022`,
`learn_2016`, and `learn_2008`. It does **not** create databases (course10-setup already did) and
it never overwrites a row you've flipped to `Ready` — safe to re-run.

```
$ sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -i bootstrap/init-rollout.sql
(1 rows affected)
(1 rows affected)
(1 rows affected)
```

---

## Part A — two shapes, converging automatically (detectable)

`dbo.OrderSummary` declares two variants of one index, `IX_OrderSummary_Placed`:

```json
"Indexes": [
  {
    "Name": "IX_OrderSummary_Placed", "IndexColumns": "PlacedAt, CustomerId",
    "ShouldApplyExpression": "{{CompatibilityLevel}} >= 160",
    "VariantName": "Modern (compat 160+)"
  },
  {
    "Name": "IX_OrderSummary_Placed", "IndexColumns": "PlacedAt",
    "ShouldApplyExpression": "{{CompatibilityLevel}} < 160",
    "VariantName": "Legacy (compat < 160)"
  }
]
```

Both index shapes are **legal on both databases** — this is not Module 2, where the legacy
target would parse-error on the modern form. Here the modern tier gets the wider covering key
its optimizer can exploit, the legacy tier gets the narrower key; each is the better shape for
its own engine. Nobody approves anything — the compat level is the whole input, and the fleet
converges on the modern shape on its own as each database's compatibility level is raised.

### Deploy to `learn_2022` (compat 160) — the modern shape

```
cd sqlserver
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD/logs"     # macOS / Linux
schemaquench --ConfigFile:quench.settings.2022.json --LogPath:"$PWD\logs"     # Windows
```

```
[localhost,11433].[learn_2022]         Creating index [dbo].[OrderSummary].[IX_OrderSummary_Placed] (variant: Modern (compat 160+))
[localhost,11433].[learn_2022] Successfully Quenched
```
`IX_CustomerActivity_LastSeen` is not created in this run — its `RolloutControl` row is still `Pending` (Part B).

### Deploy to `learn_2016` (compat 130) — the legacy shape

```
schemaquench --ConfigFile:quench.settings.2016.json --LogPath:"$PWD/logs"     # macOS / Linux
```

Same package, same server, same binary — only the compatibility level differs, and the log
prints the *other* variant's receipt.

```
[localhost,11433].[learn_2016]         Creating index [dbo].[OrderSummary].[IX_OrderSummary_Placed] (variant: Legacy (compat < 160))
[localhost,11433].[learn_2016] Successfully Quenched
```

The gated-off variant prints nothing. That ` (variant: …)` tag is your proof, straight from the
log, of which shape fired on which tier — no guessing. Only a **component** (index, column,
stat, check, FK, view) emits a `VariantName` receipt; a folder-gated script does not.

---

## Part B — the state gate (not detectable)

Now change one thing about the question. "Which compat level is this?" the server can answer.
"Has this tenant been **approved** to take the new index in this maintenance window?" it cannot —
that decision lives in a change ticket, a customer email, a scheduling spreadsheet. So we put
the answer where the deploy can read it: a `dbo.RolloutControl` row.

`dbo.CustomerActivity` carries an index with **no version floor** — it would build on any tier.
The only thing holding it back is state:

```json
{
  "Name": "IX_CustomerActivity_LastSeen", "IndexColumns": "LastSeenAt, CustomerId",
  "ShouldApplyExpression": "EXISTS (SELECT 1 FROM dbo.RolloutControl WHERE feature = 'CustomerActivityIndex' AND status = 'Ready')"
}
```

`RolloutControl` is **operational state the DBA owns**, not part of the schema package — you
already stood it up in Step 0, seeded to `Pending` in every tenant.

### Deploy while Pending — the construct stays absent

```
schemaquench --ConfigFile:quench.settings.stategate.json --LogPath:"$PWD/logs"
```

`learn_2008` is the "tenant" here. Its `RolloutControl` row is `Pending`, so the EXISTS gate is
false and the index is skipped — the compat level (100) is irrelevant; state is the only
variable.

```
[localhost,11433].[learn_2008]         Creating index [dbo].[OrderSummary].[IX_OrderSummary_Placed] (variant: Legacy (compat < 160))
[localhost,11433].[learn_2008] Successfully Quenched
```
No `IX_CustomerActivity_LastSeen` line — the `EXISTS` gate read `status='Pending'` and skipped it. Compat 100 was irrelevant; state was the only variable.

### Flip the row, re-deploy — the construct appears

Someone signs off on the window. You flip the tenant's row and re-run the *same* package:

```
sqlcmd -S localhost,11433 -U sa -P "Learn!Passw0rd" -C -d learn_2008 -Q "UPDATE dbo.RolloutControl SET status = 'Ready' WHERE feature = 'CustomerActivityIndex';"
schemaquench --ConfigFile:quench.settings.stategate.json --LogPath:"$PWD/logs"
```

```
[localhost,11433].[learn_2008]         Creating index [dbo].[CustomerActivity].[IX_CustomerActivity_LastSeen]
[localhost,11433].[learn_2008] Successfully Quenched
```
The state-gated index lands. `IX_OrderSummary_Placed` gets no create line — it already matches its declared shape, so the state-based engine leaves it untouched.

That's the state gate: the same package every window, the rollout state living in the database,
and the "already-built, won't-recreate" guardrail — `AND NOT EXISTS (SELECT 1 FROM sys.indexes …)` —
implicit in the state-based engine. You never write that predicate yourself.

---

## The anti-pattern — don't gate detectable facts on a control table

A `RolloutControl` row is the right tool **only** because tenant approval is a fact SQL Server
can't see. Turn it on something the server *can* answer — "is this compat 160?", "does this
column exist?", "is the server version 16?" — and you've built manufactured toil:

- Now there are **two sources of truth** — the real server state and your hand-maintained row —
  and they drift. A tenant gets upgraded; the control row still says the old thing.
- You've put a **human back in a loop the engine could close itself.** The detectable gate
  converges for free as servers move; the control-table version needs someone to flip rows
  forever.

So if you came here wanting two versions of a stored procedure gated by a control table: you
almost certainly don't need the table. If the difference is *detectable* — version, compat
level, an existing object — gate on the answer (Part A). Reserve the state gate for the fact
that genuinely lives outside the database.

## An honesty note on "faster"

This lab showcases the **capability** — two valid shapes, a gate that picks one per tier, a
receipt that proves which fired. It does **not** ship a reproduced benchmark. On this sandbox
all three tiers share the one 2022 binary, so a real "measurably faster here, slower there"
boundary would be the **compatibility level** itself (the axis Module 2 proved with
`GENERATE_SERIES`). We don't fake latency numbers. The point stands without them: when two
shapes are both valid, the gate lets the target choose, and the log tells you it did.

## What's in here

| Path | Purpose |
| --- | --- |
| `sqlserver/package/Templates/Main/Tables/dbo.OrderSummary.json` | Part A — two `IX_OrderSummary_Placed` variants, component-gated on `{{CompatibilityLevel}}` with `VariantName`. |
| `sqlserver/package/Templates/Main/Tables/dbo.CustomerActivity.json` | Part B — a zero-floor index state-gated on `RolloutControl`. |
| `sqlserver/bootstrap/init-rollout.sql` | Creates `dbo.RolloutControl` and seeds each tenant's row to `Pending`. Run before deploying. |
| `sqlserver/quench.settings.2022.json` / `…2016.json` | Part A — same package to `learn_2022` (160) / `learn_2016` (130). |
| `sqlserver/quench.settings.stategate.json` | Part B — same package to `learn_2008`, the state-gate tenant. |

## Cross-engine

The state gate is engine-agnostic — it's just a table and an `EXISTS` predicate. The same
`RolloutControl` pattern ports to PostgreSQL, MySQL, and MariaDB unchanged; only the SQL-Server
demo is wired up here to keep the module focused.

## Up next

Module 4 — **the oldest tier.** You've let the engine adapt, gated what it can't, and gated on
state it can't detect. Next: what to do when a tier is so far back that even the gates run thin —
and how the whole scheme retires itself once the last laggard finally catches up.
