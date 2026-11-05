# Conditional Deployment Demos

Five runnable demos showing `ShouldApplyExpression` on real engines. One schema package per demo, two or three targets per demo, observable difference in the deployed shape — all driven by the SQL expression in the schema package, not by branching the CI script.

| Demo | Targets | Gated component | Condition |
|---|---|---|---|
| [PostgreSQL Version-Gate](PostgreSQL-VersionGate) | PG15 ↔ PG18 | Virtual generated column | `current_setting('server_version_num')::int >= 180000` |
| [MySQL Version-Gate](MySQL-VersionGate) | MySQL 8.0 ↔ MySQL 9 | `VECTOR(384)` column | `CAST(SUBSTRING_INDEX(VERSION(), '.', 1) AS UNSIGNED) >= 9` |
| [MariaDB Version-Gate](MariaDB-VersionGate) | MariaDB 10.6 ↔ 11.4 | Native `UUID` column | major·100 + minor `>= 1007` — the feature landed **mid-major** (10.7) |
| [SQL Server Compat-Level Gate](SqlServer-CompatLevelGate) | One 2022 server, 2 DBs at compat 130 / 160 | Whole view implementation (folder-gated) | `(SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()) >= 160` |
| [SQL Server Rolling-Rollout](SqlServer-RollingRollout) | SQL Server 2022 × 3 tenant DBs | Nonclustered columnstore index | `EXISTS (SELECT 1 FROM dbo.RolloutControl WHERE feature = 'OrderHistoryColumnstore' AND status = 'Ready')` |

### Which one should you read first?

They answer different questions, and none is the "advanced" version of another:

- **Gating on server version** (PostgreSQL / MySQL / MariaDB) — the common case. Compare the MySQL and MariaDB pair specifically: MySQL's feature landed exactly on a major boundary so a major-only test suffices, while MariaDB's landed at 10.7 and a major-only test is wrong in both directions. Most features land mid-major.
- **Gating on compatibility level** (SQL Server Compat-Level) — the SQL Server-specific trap. A current binary can host an old-compat database, so `SERVERPROPERTY('ProductMajorVersion')` does **not** answer "what syntax can I use". Gate syntax on compat level, features on server version.
- **Gating on state** (SQL Server Rolling-Rollout) — nothing to do with capability. The engine *can* do it; nobody has said yes for this target yet. Reach for this only when the condition is something the target **cannot detect** — see below.

Each demo is self-contained: schema package, `docker-compose.yml` with the engine pair (or single instance + multi-DB for the SQL Server one), launcher scripts (`run-demo.sh` / `run-demo.cmd`), and a one-page README with the expected output.

## Running a demo

```bash
cd PostgreSQL-VersionGate      # or MySQL-VersionGate / SqlServer-RollingRollout
./run-demo.sh                  # macOS / Linux
run-demo.cmd                   # Windows
```

Each launcher pulls the published SchemaQuench image, brings up the engines under `docker compose`, runs the deploy, and emits a verification block showing what landed where. Pass `--from-source` to run a build of this checkout instead (needs the .NET SDK); see [`../README.md`](../README.md#quick-start).

Cleanup is `docker compose down --volumes` from inside the demo directory.

## What you're seeing

Three **version-gate** shapes (PostgreSQL, MySQL, MariaDB), one **compatibility-level** shape (SQL Server), and one **state-gate** shape (SQL Server):

- **Version-gate:** the same JSON column declaration carries an engine-version expression. The older engine evaluates it as false and skips the column; the newer one evaluates it as true and applies it. The deploy artifact is identical; only the target engine's version differs.
- **Compatibility-level gate:** the same package deploys to two databases on **one** server, and a whole view implementation is swapped by folder-level gating. This exists because SQL Server decouples syntax availability from the server binary — a current server can host an old-compat database where newer syntax will not parse. It is the one case where a server-version test gives the wrong answer.
- **State-gate:** the same JSON index declaration carries an `EXISTS`-against-control-table expression. The row in `dbo.RolloutControl` is the approval lever — flip it to `Ready` when a tenant is cleared, leave it `Pending` otherwise. Same package every run; only approved tenants take the index. This shape exists for conditions the server has no way to detect: a maintenance-window approval for expensive DDL, a customer's opt-in to a feature, an environment signed off as ready.

**These are peers, not a progression.** The state gate is not the mature form of a version gate. Pick by one question:

> **Can the target answer this itself?**

If yes — server version, compatibility level, an installed extension, an object that already exists — gate on that answer. It resolves automatically, needs nobody's attention, and converges on its own as targets qualify.

If no, use a control table. Two families genuinely qualify:

- **Cost / scheduling.** The DDL is long-running or lock-heavy, so it has to be scheduled per tenant per maintenance window. (That is this demo bundle's Rolling-Rollout.)
- **Facts that live outside the database.** Customer opt-in to a feature, environmental readiness sign-off, contractual entitlement. No query answers "has this customer bought the module?"

**The anti-pattern: a control table for something easily detectable.** If the server can tell you, ask the server. A hand-maintained row standing in for a detectable fact is toil you invented, and it drifts — eventually the row says `Ready` for something that isn't, or `Pending` for something that shipped months ago. Control tables are for what the engine *cannot* know, not for what someone would prefer to control.

Each shape puts the conditional logic **in the schema package**, in the language the target engine already speaks, instead of in a CI wrapper.

## Article

The PostgreSQL, MySQL and SQL Server Rolling-Rollout demos were built as the runnable companions to an article on deploying to a production server that cannot be upgraded; each is a "run it in under ten minutes" case from that piece. The MariaDB and compatibility-level demos were added later to close engine and gating-shape gaps.
