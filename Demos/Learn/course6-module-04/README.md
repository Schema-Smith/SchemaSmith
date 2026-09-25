# Course 6, Module 4 — Runtime validation gates (validation scripts) (lab)

**Goal:** make a deployment refuse the wrong server and refuse to run an older release over a newer one — before it touches the schema. This lab exercises SchemaQuench's runtime validation gates: the product-level `ValidationScript` (a server-identity / dependency / version gate) and the template-level `BaselineValidationScript` + `VersionStampScript` (the anti-rollback pairing). SQL Server, PostgreSQL, MySQL, and MariaDB.

## The scenario

You cut releases as versioned packages and deploy them across a fleet of tenant databases. Two things must never happen: a package must not deploy to the *wrong* server, and an *older* package must not run over a database that already has a *newer* release. Both are enforceable with SQL the engine runs *inside* the quench, against live state — no extra tooling. Where Module 2's pre-flight (`--TestConnection` / `--PreviewTargets`) checks the target from the outside, validation scripts run against real database state and abort the quench when it doesn't match.

This lab ships two package versions per engine — `v1/` and `v2/` — that differ only in their baseline threshold and the version they stamp.

## How the gates work here

- **Product `ValidationScript`** (in `Product.json`) runs first, against the server's admin database (`master` / `postgres` / `information_schema`). It checks that an expected dependency database exists and that the server meets a version floor. A falsy result aborts with `Invalid server for this product`.
- **Template `BaselineValidationScript`** (in `Template.json`) runs per target database, before that database's quench. It reads a **version registry** and passes only when the stamped version is at or below what this package expects (v1 requires ≤ 1, v2 requires ≤ 2). A falsy result aborts that database with `Invalid baseline for this release`.
- **Template `VersionStampScript`** runs per database after a successful quench and records this package's version into the registry.

The **version registry** (`SchemaVersion` / `schema_version`) is standing infrastructure — provisioned once when the database is stood up, *not* shipped inside the versioned package. Every release reads it (baseline) and writes it (stamp). This is why the lab provisions it in *Before you start*, and why the package itself manages only the business table (`LabWidget`) — there is deliberately no `SchemaVersion` table file in the package, so it won't appear in your editor's package view.

## Before you start

1. The shared sandbox must be running and `course6-setup` must have seeded `shop_tenant_a/b/c` on all four engines (see [`../course6-setup/README.md`](../course6-setup/README.md)).
2. **Provision the version registry** in each tenant database. It is standing infrastructure, so you create it once. On the sandbox:

   **SQL Server** (run for `shop_tenant_a`, `_b`, `_c`):
   ```sql
   IF OBJECT_ID('dbo.SchemaVersion') IS NULL
     CREATE TABLE dbo.SchemaVersion (Product SYSNAME NOT NULL PRIMARY KEY, Version INT NOT NULL);
   ```
   **PostgreSQL** (each tenant):
   ```sql
   CREATE TABLE IF NOT EXISTS public.schema_version (product text PRIMARY KEY, version int NOT NULL);
   ```
   **MySQL** (each tenant):
   ```sql
   CREATE TABLE IF NOT EXISTS schema_version (product VARCHAR(128) NOT NULL PRIMARY KEY, version INT NOT NULL);
   ```
   **MariaDB** (each tenant):
   ```sql
   CREATE TABLE IF NOT EXISTS schema_version (product VARCHAR(128) NOT NULL PRIMARY KEY, version INT NOT NULL);
   ```
3. The `quench.settings.json` files carry `"KindleTheForge": true` (on by default; shown here for clarity). The first deploy into a fresh database installs SchemaSmith's helper objects ("kindles the forge") before it does anything else.
4. **The CLI is on your PATH** — `schemaquench --version` answers **2.7.0** or later. The validation-script family itself is long-shipped, but this lab's package carries a `$schema` reference, which SchemaSmith only learned to read in 2.7.0; an older CLI refuses to load the package at all. Each command below is run from an engine/version directory, e.g. `sqlserver/v1`.

## Scenario 1 — ValidationScript pass

From `sqlserver/v1` (swap `sqlserver` for `postgres` / `mysql` / `mariadb`):

```
schemaquench --ConfigFile:quench.settings.json
```

The log shows `Validate Server`, the forge is kindled on first run, each tenant's baseline passes (the registry is empty, so the stamped version is treated as 0), the `LabWidget` table is created, and each tenant is stamped to version 1. Exit code 0. The pre-existing Shop tables (`Customer`, `Product`, `SalesOrder`, `OrderItem`) are left untouched — the package adds its table without removing what it doesn't manage.

## Scenario 2 — ValidationScript fail (wrong server)

Point the dependency check at a database that isn't there — in `v1/Package/Product.json`, temporarily change the `DependencyDb` token to `nonexistent_db` — and re-run:

```
schemaquench --ConfigFile:quench.settings.json
```

The gate fails immediately:

```
Validate Server
Invalid server for this product
```

Exit code 3. Nothing is deployed — the quench never reaches a target database. Restore `DependencyDb` to `shop_tenant_a`.

## Scenario 3 — the truthy contract

A validation script must return a **truthy scalar** or the gate fails: `NULL` counts as *false*. Each engine spells the check natively — `SELECT CAST(... AS BIT)` on SQL Server, `SELECT EXISTS(...)` (native boolean) on PostgreSQL, `SELECT EXISTS(...)` (0/1) on MySQL and MariaDB — but the contract is identical: non-zero / true passes, everything else aborts.

## Scenario 4 — anti-rollback (the money shot)

With all tenants at version 1 from Scenario 1, deploy v2, then try to re-run v1:

```
cd ../v2 && schemaquench --ConfigFile:quench.settings.json   # baseline sees 1 (≤ 2) → pass, stamps 2
cd ../v1 && schemaquench --ConfigFile:quench.settings.json   # baseline sees 2 (> 1) → ABORT
```

The v2 deploy succeeds (exit 0) and moves every tenant to version 2. Re-running the *older* v1 package now fails its baseline on every tenant:

```
Validate Baseline
Invalid baseline for this release
```

Exit code 2. Nothing is touched — the registry stays at 2. An older release cannot run over a newer database.

## Scenario 5 — the gate is per database

The baseline runs per target database, so it can pass on some tenants and block on others. Set every tenant to version 1, then bump only `shop_tenant_a` to 2:

```sql
-- all tenants
UPDATE dbo.SchemaVersion SET Version = 1 WHERE Product = 'Shop';
-- shop_tenant_a only
UPDATE dbo.SchemaVersion SET Version = 2 WHERE Product = 'Shop';
```

(PostgreSQL: `UPDATE public.schema_version SET version = … WHERE product = 'Shop';`. MySQL/MariaDB: `UPDATE schema_version SET version = … WHERE product = 'Shop';`.)

Run v1 across the fleet. `shop_tenant_a` aborts (`Invalid baseline for this release`) while `shop_tenant_b` and `shop_tenant_c` pass and re-stamp. The run continues past the blocked tenant and reports the failure; exit code 2. The gate is evaluated independently for each database.

## Scenario 6 — the same gates at product scope (note)

`BaselineValidationScript` and `VersionStampScript` also exist at **product** level (`Product.json`), where they run once against the admin database instead of per target database — for a single server-wide version registry rather than a per-database one. This lab uses the template-level (per-database) gates because they carry the version alongside the schema they guard and behave identically on all four engines; the product-level equivalents are there when you want one server-wide gate.

## Resetting the lab

To return to a clean first-deploy state:

1. Re-run `course6-setup` (re-seeds the Shop tables).
2. **Empty the version registry** — `course6-setup` does not touch it. In each tenant: `DELETE FROM dbo.SchemaVersion;` (SQL Server), `DELETE FROM public.schema_version;` (PostgreSQL), or `DELETE FROM schema_version;` (MySQL / MariaDB) — or drop and re-create it per *Before you start*.
3. **Clear the checkpoint cache** — delete the `schemaquench-checkpoints` directory in your temp folder (`%TEMP%\schemaquench-checkpoints` on Windows, `$TMPDIR/schemaquench-checkpoints` or `/tmp/schemaquench-checkpoints` on macOS/Linux). SchemaQuench keeps per-run checkpoints there; if a database is reset out from under a checkpoint, the next run may skip steps it believes are already done.

## Cross-platform

The workflow is identical on all four engines; only the native SQL spelling differs. The admin database the product `ValidationScript` runs against is `master` (SQL Server), `postgres` (PostgreSQL), or `information_schema` (MySQL, MariaDB). Identifier quoting and types follow each engine (`dbo` + `SYSNAME`/`INT`; lowercase `public` + `text`/`integer`; backtick-quoted + `VARCHAR`/`INT` on MySQL and MariaDB). The gate names, the abort messages, and the exit codes (3 for a failed server validation, 2 for a failed baseline) are the same everywhere.
