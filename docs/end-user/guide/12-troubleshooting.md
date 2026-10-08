# Troubleshooting

When something goes wrong, let's figure out what happened. This chapter helps you find the answer fast. Issues are organized by symptom so you can jump directly to what you're seeing.

For background on how the tools work, see the individual reference pages: [SchemaTongs](../reference/schematongs.md), [SchemaQuench](../reference/schemaquench.md), [DataTongs](../reference/datatongs.md), [Custom Properties](../reference/custom-properties.md), [Script Tokens](../reference/script-tokens.md), and [Configuration](../reference/configuration.md). For per-platform error codes and how each engine reports faults, see [Error Codes & Reporting](../reference/error-codes-and-reporting.md).

---

## My deployment failed

A failed quench leaves two things for you: a log entry telling you what broke, and a `.sql` artifact file holding the exact SQL that the server rejected. Here is how to move from "deployment failed" to "I know what's wrong and I can fix it."

### 1. Find the artifact

Open the progress log (`SchemaQuench - Progress.log`) and search for `Resolved SQL written to:`. Every script surface -- user and migration scripts, generated quench DDL (table, index, foreign-key, view, and table-JSON steps), product-level `Before`/`After` scripts, validation scripts, and data-delivery merges -- reports its failure the same way, so one search string finds every kind of failure in the log.

```
Unable to quench 'Before/01-seed-config.sql': Invalid column name 'Region'.
    Resolved SQL written to: C:\deploy\SchemaQuench - Failed 01-seed-config prod-db.TargetDB.sql
```

> **Note:** Artifacts land in the `ArtifactPath` directory (default: the working directory you ran SchemaQuench from), not the log directory. That is deliberate — so zipping your logs for a support ticket does not sweep up raw SQL that may contain expanded sensitive values.

### 2. Open and read it

The artifact is a plain `.sql` file. It contains a comment header identifying the server, database, schema, and which script failed, followed by every batch the engine sent, separated by `GO`. The batch SchemaQuench attempted last is marked:

```sql
-- >>> FAILING BATCH (#2) >>>
ALTER TABLE [dbo].[Orders] ADD [Region] NVARCHAR(50) NOT NULL ...
GO
```

> **Note:** The failing-batch marker is a best-effort hint. The engine marks the last batch it attempted, which is usually (but not guaranteed to be) the batch that caused the error.

The artifact contains the real, fully expanded values — tokens already resolved, connection-string parameters already substituted. That is exactly what you need to reproduce the failure. Strip the `--` comment lines and `GO` separators if your query tool needs clean statement text.

### 3. Reproduce and fix

Open the artifact in your query tool, connect to the same target, and run it. You will see the exact same error the engine returned during deployment. Work the fix there — iterate until it succeeds — then apply the fix back to your schema package.

### 4. Common error classes

Most deployment failures fall into one of these categories:

- **Unresolved token.** The batch contains a literal `{{Token}}` instead of the expanded value. Either the token is misspelled, it is not in scope for this script's slot (for example, a `{{SchemaName}}` token in a product-level script), or the token was never defined. Check `ScriptTokens` in your settings and the token reference.
- **Dependency order.** An object references a table, view, or procedure that doesn't exist yet at the point the script runs. SchemaQuench's retry loop resolves many of these automatically across passes — if the same script fails every pass, it may reference an object that is never created, or the dependency is circular.
- **Permission.** The deploy login lacks the right to create or alter the object. Check the login's rights against the target database and grant what is needed.
- **Delivery constraint.** A merge script hit a FK, unique, or check constraint. Open the artifact and look at the VALUES being inserted or the JOIN logic — the data being delivered conflicts with existing rows or references a row that doesn't exist. Fix the source data, adjust the delivery filter, or reorder your delivery scripts.

### 5. Safe to attach?

If you need to attach the artifact to a support ticket or CI build artifact, turn on `ScrubArtifacts: true` in your settings before re-running. With scrubbing on, the artifact file redacts sensitive token values (names matching password, secret, API key, token, etc.) and inline connection-string passwords, producing a variant you can share safely. See `ScrubArtifacts` in the [Configuration Reference](../reference/configuration.md#failure-artifacts) for details.

---

## Reading logs

Every SchemaSmith CLI tool writes two log files during each run. These are the first place to look when something doesn't go as expected:

- **Progress log** (`ToolName - Progress.log`) -- a step-by-step record of what the tool did. Start here.
- **Error log** (`ToolName - Errors.log`) -- detailed exception information when something fails. Check this for stack traces and SQL error details.

Logs are written to the tool's working directory by default. You can redirect them with `--LogPath`:

```
SchemaQuench --LogPath:/var/log/schemasmith
```

### Numbered backup directories

After each run, the tool copies its logs into a numbered backup directory (e.g., `SchemaQuench.0001/`, `SchemaQuench.0002/`). This preserves the history of previous runs so you can compare what changed between deployments. When you're tracking down a regression, these numbered backups are your timeline.

### Sensitive value masking

The progress log records your full configuration -- and SchemaQuench's script tokens -- at the start of each run, but any value whose name looks sensitive (`Password`, `Pwd`, `Secret`, `ApiKey`, `Token`, `ConnectionString`, `Credential`) is masked as `***`, and an embedded password inside a connection string is stripped even when the surrounding name isn't sensitive. If you see `***` where you expected credentials, that's the masking working correctly -- the real value was still used for the connection. You can tune what gets scrubbed (or suppress token logging entirely) via the `LogHygiene` settings block; see [Sensitive value masking](../reference/configuration.md#sensitive-value-masking) in the configuration reference.

---

## Exit codes

Each tool exits with a code that indicates the outcome. Automation scripts should check this value.

| Code | Meaning | What to do |
|------|---------|------------|
| 0 | Success | Nothing -- the run completed normally. |
| 1 | Finished, but incomplete | Something was skipped or downgraded -- a feature the target lacks, a table DataTongs could not extract, an object SchemaTongs left out. The progress log names each one. |
| 2 | Failed | The run stopped for a reason you can fix: an unrecognized argument, a missing setting, a package that will not load, or a failed database quench. The progress log starts the reason with `FAILED`; the error log has details. |
| 3 | Unhandled exception | An unexpected error crashed the tool. Check the error log for the full stack trace. |
| 4 | Unable to back up log files | The tool completed (or failed) but couldn't copy its logs to the backup directory. Check directory permissions and disk space. |

For the full exit code reference, see [Configuration Reference -- Exit Codes](../reference/configuration.md#exit-codes).

---

## Common deployment issues (SchemaQuench)

### "Invalid object name 'SchemaSmith.*'"

**Symptom:** SQL errors referencing `SchemaSmith.MissingTableAndColumnQuench`, `SchemaSmith.ForeignKeyQuench`, or similar objects.

**Cause:** The SchemaSmith helper procedures haven't been installed in the target database. These are created by the KindleTheForge step at the start of each deployment.

**Fix:** Ensure `KindleTheForge` is set to `true` (the default) in your settings. If you explicitly set it to `false`, the tool skips installing the forge procedures, and the database won't have the objects it needs.

```json
{ "KindleTheForge": true }
```

If `KindleTheForge` is already `true` but the helper objects look wrong (someone manually edited one of the `SchemaSmith.*` procedures, for example), set `ForceReKindle: true` once or pass `--ForceReKindle` to re-install the helpers unconditionally on the next run. Once everything is back to a clean state, return the setting to its default.

### Dependency failures that don't resolve

**Symptom:** Scripts fail with errors like "Invalid object name", "relation ... does not exist", or "Unknown column" even though the referenced object is in your package. The progress log shows the same scripts failing on every retry pass.

**Cause:** SchemaQuench retries object scripts in a dependency retry loop -- each pass attempts all unquenched scripts, and the loop continues as long as at least one new script succeeds per pass. Objects-slot scripts get four opportunities to resolve across the deployment sequence. If scripts have circular dependencies, or depend on objects that genuinely do not exist, the retry loop cannot make progress.

**Fix:**

1. Check the progress log for the specific scripts that failed and the SQL errors they produced.
2. Look for circular dependencies between views, functions, or procedures. True circular dependencies can't be resolved by retries -- you need to break the cycle (e.g., use a stub object that the second pass updates).
3. Verify the referenced object actually exists in your schema package. A typo in a schema or object name will fail on every pass.
4. If the failure is in the table-creation boundary (object references a table column that doesn't exist yet), the retry loop should resolve it automatically across passes. If not, check whether the table JSON is valid.

### Foreign key errors during deployment

**Symptom:** Foreign key creation fails because the referenced table or column doesn't exist, or data violates the constraint.

**Cause:** Foreign keys are applied after table modifications. If you need to run data migration scripts between table changes and foreign key creation, the `BetweenTablesAndKeys` migration slot is exactly the right tool for the job.

**Fix:** Declare a custom folder in the `BetweenTablesAndKeys` slot via `Template.ScriptFolders` and put your data fixup or migration scripts there. See [Edge Cases -- Migration Scripts](11-edge-cases.md#migration-scripts) and the [SchemaQuench Reference](../reference/schemaquench.md#database-quench-sequence) for the full sequence.

### Lost connection to the server mid-deployment

**Symptom:** The deployment fails partway through with a message like `Lost connection to <server> during <phase> — the server stopped responding mid-deploy (it may have restarted, crashed, or run out of memory)`, rather than a SQL or credentials error.

**Cause:** The target server went away *after* SchemaQuench had connected and started work — it restarted, crashed, or ran out of memory (common on a memory-constrained container backend). This is an environment problem, not a schema error. An *initial* connect failure — wrong host or credentials, or a server that never came up — is reported separately by the pre-flight connection test, so this message specifically means the connection was live and then dropped.

**Fix:** Check the server/container logs and give the engine more memory (or use a lighter engine image). Then re-run — SchemaQuench is idempotent and will converge cleanly from wherever it stopped; there is nothing to undo.

### Validation script returns false

**Symptom:** The progress log shows `Validate Server` followed by `Invalid server for this product` and the deployment stops.

**Cause:** Your `Product.ValidationScript` ran against the target server and returned a value that isn't truthy. This is the safety gate working as designed -- it prevents quenching to the wrong server.

**Fix:** Check the SQL in your `Product.json` `ValidationScript` field. Run it manually against the target server to see what it returns. Common issues:

- The script checks for a specific server name or database that doesn't exist on this target.
- The script has a logic error that causes it to return `false` or `NULL` (NULL is treated as false).

### Connection failures

**Symptom:** The progress log shows `**CONNECTION FAILED**` and the error log contains a connection exception.

**Cause:** SchemaQuench couldn't connect to the target server.

**Fix:** Walk through the connection settings:

- `Target:Server` -- the server hostname or IP address
- `Target:Port` -- if the server isn't on the platform's default port (SQL Server `1433`, PostgreSQL `5432`, MySQL and MariaDB `3306`)
- `Target:User` and `Target:Password` -- credentials (SQL Server allows blank for Windows auth; PostgreSQL, MySQL, and MariaDB do not)
- `Target:ConnectionProperties` -- platform-specific keys (`TrustServerCertificate` on SQL Server, `SslMode` on PostgreSQL / MySQL / MariaDB)

If using SQL Server Windows authentication, omit `User` and `Password` entirely. For PostgreSQL, MySQL, and MariaDB, always provide explicit credentials.

### WhatIf shows unexpected changes

**Symptom:** Running with `WhatIfONLY: true` shows changes you didn't expect -- tables being modified, columns being added or dropped.

**Cause:** The live database has drifted from what the schema package defines. WhatIf is showing you the delta between your package and the actual database state. This is WhatIf doing exactly what it should.

**Fix:**

1. Compare your schema package against the live database to identify what drifted.
2. If someone made manual changes to the database, decide whether to update your package (cast with SchemaTongs, as described in [Defining Your Schema -- Extracting Changes](04-defining-your-schema.md#extracting-changes-from-a-live-database)) or let SchemaQuench bring the database back in line.
3. If your package has unexpected definitions, check for uncommitted changes or the wrong package version.

### ShouldApplyExpression not skipping a component

**Symptom:** You added `ShouldApplyExpression` to an index, column, or constraint, expecting it to be skipped on the current database, but SchemaQuench deployed it anyway.

**Cause:** The expression returned something truthy. An empty result, `NULL`, or missing value is treated as "apply normally" rather than "skip" -- only an explicit `0`, `false`, or falsy scalar tells SchemaQuench to skip the component.

**Fix:**

1. Run the expression directly against the target database and check its return value.
2. If you're using custom-property tokens from `Extensions`, verify the token actually resolves. Unresolved `{{Table.SomeName}}` tokens are left in place literally and usually cause the SQL to error out rather than return a clean `0` or `1`.
3. Wrap the expression in `SELECT CASE WHEN ... THEN 1 ELSE 0 END` to make the output shape unambiguous.

### Custom property tokens show up literally in generated SQL

**Symptom:** The generated SQL contains literal `{{Table.MyProperty}}` tokens instead of the resolved value.

**Cause:** The expected custom property isn't inside the `Extensions` object on the correct component. Perhaps it's flat on the object instead of wrapped in `Extensions`, or it's on the wrong component (table vs column), or the name is misspelled.

**Fix:**

1. Verify the custom property is inside an `Extensions` object on the intended component. Custom values are **not** flat on the class -- they must live inside `Extensions`. See [Custom Properties](../reference/custom-properties.md) for the shape.
2. Check the scope: bare `{{PropertyName}}` reads from the component's own Extensions; `{{Table.PropertyName}}` reads from the parent table's Extensions.
3. Remember token names are case-insensitive but the path components (`Table.`, nested object keys) must match exactly.

### Secondary server scripts running on the wrong replica

**Symptom:** You configured `SecondaryServers` on the target but scripts are only running on the primary, or scripts meant for the primary are running on secondaries.

**Cause:** The `ServerToQuench` setting on your product-level folders isn't set correctly, or you're using a template-level folder (which always runs against the identified database, not across replicas).

**Fix:** Only `Product`-level folders (declared via `Product.Folders`) participate in secondary-server routing. On each folder, set `ServerToQuench` to `Primary`, `Secondary`, or `Both` explicitly. See [Schema Packages -- Secondary Servers](../reference/schema-packages.md#secondary-servers) for the full pattern.

---

### Many-target deployments

These entries apply to schema-template products (one iteration per tenant schema) and to products that fan out to many databases. Both patterns run many work units in parallel, and the same observability and scoping techniques apply.

#### Log lines interleave

**Symptom:** The progress log is nearly unreadable -- lines from different tenants or databases intermix in timestamp order, so the story of any one deployment is scattered across hundreds of lines.

**Cause:** Each work unit runs in its own thread, and SchemaQuench writes log lines as each step completes. For a schema-template product running 50 tenants at `MaxThreads: 10`, up to 10 tenants are writing lines simultaneously.

**Fix:**

- Filter the progress log to one schema name to isolate that tenant's sequence end-to-end. Every schema-template log line carries a `[Schema: <name>]` prefix; for a many-database fan-out, the prefix is `[server].[database]`. Both formats are greppable without extra tooling.
- Set `AllowParallel: false` on the template in `Template.json` to serialize execution. When set, at most one iteration of that template runs at a time. Other templates' parallel-eligible work units continue to fill the thread pool, so the overall deployment isn't fully serialized -- only that template's iterations are. This is the right trade-off when you need a clean per-tenant log for debugging, or when your schema template creates cross-schema foreign keys where DDL parallelism causes lock contention.

`AllowParallel` is a schema-template property only. For many-database regular templates, use `MaxThreads: 1` in `SchemaQuench.settings.json` to cap global concurrency for that run. See [MaxThreads](../reference/schemaquench.md#maxthreads) and [AllowParallel](../reference/schema-packages.md#allowparallel) for the full interaction.

> **Note:** Setting `AllowParallel: false` on a production schema-template product with hundreds of tenants turns a 5-minute parallel run into a proportionally longer serial one. Use it for a debugging session, not permanently.

#### Which target failed?

**Symptom:** One of many parallel tenants or databases failed, and the failure is buried somewhere in the interleaved progress log -- you don't know which target to grep for.

**Fix:** Read `SchemaQuench - Failures.log`. Whenever a run has any failure, SchemaQuench writes this consolidated, phase-grouped roll-up naming every failed scope -- the tenant (`[server].[database] [Schema: <name>]`), a per-server `Before`/`After` product script, or a product-level `Validate` phase -- each with its error, its resolved-SQL artifact path, and a captured tail of the lines leading up to it. A loud `*** FAILED` banner also marks each failure live in the progress stream, so `grep "*** FAILED"` jumps straight to the failed scopes. It's always on and adds nothing to a clean run; tune the captured-context depth with `FailureContextLines` (default `25`). See [Failure triage roll-up](../reference/error-codes-and-reporting.md#failure-triage-roll-up).

#### WhatIf output is huge

**Symptom:** Running in WhatIf mode against a multi-tenant or many-database product generates thousands of lines of output, making it hard to find the change you're actually looking at.

**Cause:** WhatIf runs the full deployment logic for every work unit -- every tenant schema or every target database -- and logs what would execute for each one. That's the correct behavior when you're auditing an unfamiliar package, but it's more output than you want when you're debugging one tenant's migration.

**Fix:** Scope the run to a single target using the `Target` filters in `SchemaQuench.settings.json`:

```json
{
  "Target": {
    "Templates": ["TenantWorkspace"],
    "Schemas": ["tenant_acme"]
  }
}
```

`Target.Schemas` applies to schema-template iterations. `Target.Databases` applies to regular-template fan-out. Both filters AND together with `Target.Templates`, and SchemaQuench validates each filter value against the discovered universe before dispatching -- a typo surfaces as a clear diagnostic instead of a silent empty run. See [Target](../reference/schemaquench.md#target) for the full filter reference.

#### Reserved schema name

**Symptom:** The progress log shows an error like `SchemaIdentificationScript for template 'X' returned reserved schema name 'dbo'.` The deployment fails for that iteration.

**Cause:** Your `SchemaIdentificationScript` returned a platform-owned schema name that the engine cannot use as a fan-out target. The engine rejects these names because running the full template DDL against `dbo` or `public` would corrupt shared infrastructure.

**Fix:** Review the query and fix what it returns. The reserved names are listed in [Schema Packages -- Reserved schema names](../reference/schema-packages.md#reserved-schema-names); the short version:

- **SQL Server:** `dbo`, `sys`, `INFORMATION_SCHEMA`, `guest`, and the nine `db_*` fixed database role schemas.
- **PostgreSQL:** `public`, `pg_catalog`, `pg_toast`, `information_schema`, and any name matching `pg_temp_*` or `pg_toast_temp_*`.

Shared content (lookup tables, audit logs, shared dimension data) belongs in a regular template that runs earlier in `TemplateOrder`, not in a schema-template iteration. The engine's error message points at this pattern directly.

#### Schema not created

**Symptom:** A tenant is in your discovery source (the table or query that `SchemaIdentificationScript` reads from), but no schema was created and no migration scripts ran for that tenant.

**Cause:** There are two distinct situations that look the same from the outside:

1. The schema doesn't exist in the database and `CreateSchemaIfMissing` is `false` (the default). The engine throws `Schema '<name>' does not exist in database '<db>'` and marks that iteration failed. With `ContinueOnSchemaFailure: true` (the default), the remaining tenants continue; the failed tenant's error appears in the progress log prefixed `[Schema: <name>]`.

2. The `SchemaIdentificationScript` returned zero rows for that tenant. Possible causes: the tenant row exists in the source table but with a status that the script filters out, or the query contains a bug that silently drops rows.

**Diagnosis:** Look in the progress log for a `[Schema: <name>]` error line. If none exists, the schema name was never returned by discovery -- run the `SchemaIdentificationScript` directly against the target database and check what it returns. If an error line exists, read it: the message names the schema; remediate by setting `CreateSchemaIfMissing: true`, pre-creating the schema manually, or running an onboarding procedure.

Check `ContinueOnSchemaFailure` on the template: when `true` (the default), a failed iteration does not stop the deployment -- it logs and moves on. The overall exit code will be 2 if any iteration failed. See [ContinueOnSchemaFailure](../reference/schemaquench.md#continueonschemafailure) for the failure-isolation behavior.

#### `{{SchemaName}}` literal

**Symptom:** Deployed SQL contains the literal text `{{SchemaName}}` -- a table named `{{SchemaName}}.Orders` in the database, a stored procedure referencing `{{SchemaName}}.Customers` verbatim, or similar.

**Cause:** The `{{SchemaName}}` token is substituted at iteration time for scripts and table JSON inside the schema template's own folders. It is NOT substituted for:

- **Product-level folders** (`Before Product`, `After Product`). These run outside the per-database loop before any template iterates. They have no schema-name context.
- **Pre-iteration scripts** that run before the schema-name token is bound. Check which quench slot the affected script lives in.

If a table JSON file has a hard-coded `"Schema": "{{SchemaName}}"`, that's actually the intended value and gets substituted correctly. The failure mode is a script file that ended up in a product-level folder instead of the template's own script folder.

**Diagnosis:** Confirm which folder the script lives in. If it's under `Before Product/` or `After Product/` at the product root, move it to the appropriate folder inside the template directory (e.g., `Templates/TenantWorkspace/Before Scripts/`). If the script should truly run at the product level, it has no schema context by design -- rewrite it without `{{SchemaName}}` and use an explicit schema name or query-driven token instead.

---

## Common extraction issues (SchemaTongs)

### Encrypted objects warning (SQL Server)

**Symptom:** The progress log shows `WARNING: [schema].[object] is encrypted, skipping`.

**Cause:** SQL Server objects created with `WITH ENCRYPTION` can't have their source code retrieved. That's a platform limitation, not a SchemaTongs issue.

**Fix:** No action needed if you expect these objects to be encrypted. If you need to manage them through SchemaSmith, they must be recreated without encryption.

### Objects not appearing in extraction output

**Symptom:** You know an object exists in the database, but SchemaTongs didn't cast it.

**Cause:** SchemaTongs filters extraction based on two settings:

1. **ShouldCast flags** -- Each object type has a flag (`ShouldCast:Views`, `ShouldCast:Procedures`, `ShouldCast:MaterializedViews`, etc.) that defaults to `true`. If set to `false`, that entire category is skipped.
2. **ObjectList filter** -- If `ShouldCast:ObjectList` is set, only the explicitly listed objects are extracted.

**Fix:** Check your settings file:

- Verify the `ShouldCast` flag for the object type isn't set to `false`.
- If you're using `ObjectList`, make sure the object is included in the comma-separated list.
- Remember that per-platform flags are ignored on other platforms. A PostgreSQL-only flag like `Sequences` won't affect a SQL Server extraction.

See [SchemaTongs Reference](../reference/schematongs.md) for the full per-platform flag breakdown.

### Custom properties disappearing on re-extraction

**Symptom:** You added custom data under `Extensions` on a table. After re-extracting with SchemaTongs, the `Extensions` content is gone.

**Cause:** Preservation matches by the component's `Name`. If you renamed a column or a table without setting `OldName`, SchemaTongs sees the new component as brand-new and writes a fresh file without the old Extensions content.

**Fix:**

1. Before renaming, set `OldName` on the component. SchemaTongs uses `OldName` as a fallback when matching components for preservation.
2. If the Extensions data is on a platform-specific component that isn't in the preservation table (check [Custom Properties -- Preservation During Re-extraction](../reference/custom-properties.md#preservation-during-re-extraction)), you'll need to re-add the data manually after the extraction.

### Orphan warnings

**Symptom:** The progress log shows "orphaned file(s)" detected in one or more folders.

**Cause:** SchemaTongs found files in the template directory that don't correspond to any object in the live database. This usually means the object was dropped or renamed in the database since the last extraction.

**Fix:** Review the listed files. Set `OrphanHandling:Mode` to `DetectWithCleanupScripts` to generate DROP scripts, or `DetectDeleteAndCleanup` to also delete the orphan files. Orphan detection is skipped when `ObjectList` is active, since a partial extraction can't determine what's truly orphaned.

### Script validation errors (.sqlerror files)

**Symptom:** Some extracted files have a `.sqlerror` extension instead of `.sql`.

**Cause:** When `ShouldCast:ValidateScripts` is enabled, SchemaTongs checks each extracted script for validity. Scripts that fail validation are saved with the `.sqlerror` extension.

**Fix:**

1. Open the `.sqlerror` file to see the raw extracted content and understand what went wrong.
2. Common causes: the object depends on other objects that don't exist in the validation context, or the object uses syntax that the parser can't validate in isolation.
3. If the scripts are actually valid (false positives from isolated validation), you can set `ShouldCast:SaveInvalidScripts` to `false` to discard them, or disable validation with `ShouldCast:ValidateScripts: false`.

---

## Common DataTongs issues

### "Could not determine key columns"

**Symptom:** The progress log shows a message like "Table [name] has no primary key or unique index and no KeyColumns configured. Skipping table."

**Cause:** DataTongs generates sync scripts that need a key to match source and target rows. It looks for a primary key first, then a unique index. If neither exists, it can't proceed.

**Fix:** Specify key columns manually in your DataTongs configuration:

```json
{
  "Tables": [
    { "Name": "dbo.MyTable", "KeyColumns": "[Column1],[Column2]" }
  ]
}
```

If a key column is nullable, prefix it with `*` to generate NULL-safe comparisons: `"KeyColumns": "*[NullableCol],[NonNullableCol]"`.

### Column types excluded from output

**Symptom:** Certain columns are missing from the generated sync script.

**Cause:** DataTongs automatically excludes columns whose types can't be reliably round-tripped through JSON. The list is platform-specific -- see [Edge Cases -- Complex type handling in DataTongs](11-edge-cases.md#complex-type-handling-in-datatongs).

**Fix:** Expected behavior. If you need data from these columns, handle them with custom scripts outside of DataTongs.

### Empty output (no script generated)

**Symptom:** DataTongs runs without errors but says "No data found -- skipping script generation" for a table.

**Cause:** The table is empty in the source database, or your `Filter` expression excludes all rows.

**Fix:**

- Verify the table has data in the source database.
- If you specified a `Filter`, run the equivalent `WHERE` clause against the source to confirm it matches rows.
- Check that you're connecting to the correct source database (`Source:Database` in settings).

### Table does not exist in source database

**Symptom:** The progress log shows "Table [schema].[name] does not exist in source database. Skipping table."

**Cause:** The table name in your configuration doesn't match any table in the source database.

**Fix:** Check the table name for typos. DataTongs expects the format `schema.tablename`. If the schema is omitted, the platform default is assumed (`dbo` on SQL Server, `public` on PostgreSQL, the connection database on MySQL).

---

## Environment and platform issues

### Cross-platform path issues

**Symptom:** Paths work on one operating system but fail on another.

**Fix:** Use forward slashes (`/`) in configuration files. SchemaSmith normalizes paths internally, but forward slashes are valid on all platforms (Windows, macOS, Linux).

```json
{ "Product": { "Path": "./my-product" }, "ContentPath": "./my-product/data", "ScriptPath": "./my-product/Table Data" }
```

Avoid backslashes in JSON configuration -- they require escaping (`\\`) and reduce portability.

### Docker database server not responding

**Symptom:** Connection failures when targeting a database server running in Docker.

**Fix:**

1. Verify the container is running: `docker ps`
2. Check the port mapping -- if you mapped the container's default port to a non-default host port, specify it in `Target:Port`.
3. Confirm the password meets the engine's complexity requirements (SQL Server's SA password rules are strict).
4. On SQL Server, add `TrustServerCertificate: "True"` to your connection properties since Docker containers typically use self-signed certificates.
5. On PostgreSQL, check the `pg_hba.conf` in the container allows connections from your host (the official image is permissive by default).
6. On MySQL, the official image may require `AllowPublicKeyRetrieval=True` in `ConnectionProperties` for first-run SSL key retrieval.

### Demo databases don't appear / `demoserver` is unhealthy

**Symptom:** `run-demo` finishes and the `demoserver` container appears in Docker Desktop (you can even connect to it), but none of the demo databases (Northwind, AdventureWorks, ...) exist. The console shows `dependency failed to start: container schemasmith-<platform>-demo-demoserver-1 is unhealthy`, and the `quench-*` containers are listed as `Created` but never `Started`.

**Cause:** Each deploy step (`quench-*`) waits for the database-server container (`demoserver`) to pass its healthcheck before it runs. If the server doesn't become healthy within the startup window, every deploy step is skipped -- so the engine is running but no schema was ever applied. Being able to reach the server from a client tool does **not** mean it passed the healthcheck.

On SQL Server the usual reason is a slow first boot on a resource-starved Docker backend. The stock image can run a one-time system-database upgrade on first startup -- it's CPU-bound, can take several minutes on one or two cores, and the engine won't accept working logins until it finishes. On a starved backend that can outlast the healthcheck window even though nothing is actually wrong. (A genuine shortage shows up too: SQL Server 2022 needs at least 2 GB of memory and aborts startup below that, logging that it "requires a machine with at least 2000 megabytes of memory.")

**Fix:**

1. **Read the server log.** See why it didn't come up:
   ```
   docker logs schemasmith-sqlserver-demo-demoserver-1
   ```
   (Swap `sqlserver` for `postgresql` or `mysql` for the other demos.) A run of `Converting database '...' from version N to ...` lines is the slow first-boot upgrade; a "requires ... 2000 megabytes" line is a real memory shortage.
2. **Add CPU and memory.** Give the Docker engine more of both -- the first-boot upgrade is CPU-bound, so cores matter as much as RAM. Aim for 4 CPUs and 8 GB.

   > **Windows (WSL2):** set these in `%UserProfile%\.wslconfig`, not the Docker Desktop Resources pane -- on the WSL2 backend that pane doesn't govern the VM. Add a `[wsl2]` section with `processors=4` and `memory=8GB`, then run `wsl --shutdown` and restart Docker. On the Hyper-V backend, macOS, or Linux, use **Docker Desktop -> Settings -> Resources**.
3. **Re-run cleanly.** Clear the half-initialized volume and start fresh with `docker compose down -v`, then re-run the launcher. The demos retry their startup and self-heal, so a clean re-run usually clears a borderline first-boot timeout.
4. If the log shows a different error (bad password, port already in use), see [Docker database server not responding](#docker-database-server-not-responding) above.

### Environment variables not taking effect

**Symptom:** You set an environment variable but the tool doesn't use the value.

**Cause:** SchemaSmith environment variables require a specific prefix and separator format.

**Fix:**

- Prefix all variables with `SmithySettings_`.
- Use double underscores (`__`) to represent hierarchy levels.
- Restart your shell after setting variables (or use `export` in the current session).

```bash
export SmithySettings_Target__Server=my-server
export SmithySettings_Target__User=deploy_user
export SmithySettings_Target__Password=secret
```

These map to `Target:Server`, `Target:User`, and `Target:Password` in the configuration hierarchy. See [Configuration Reference](../reference/configuration.md) for the full precedence rules.

### Platform mismatch errors

**Symptom:** SchemaQuench reports an error about unknown object types, SQL syntax failures that look like they're from a different database engine, or odd behavior when an extraction doesn't match deployment.

**Cause:** The `Platform` value in `Product.json` doesn't match the target server. If your product was extracted from PostgreSQL but you point SchemaQuench at a SQL Server target, the DDL adapter mismatch will produce very strange errors.

**Fix:** Verify `Product.json` has the right `Platform` value (`SqlServer`, `PostgreSQL`, `MySQL`, or `MariaDb`) and that your target connection points to a matching server. One repository can host products targeting different platforms -- just never mix them at deployment time.

---

## Still stuck?

If your issue isn't covered here, check the [reference documentation](../README.md#reference) for detailed behavior descriptions, or open an issue on [GitHub](https://github.com/Schema-Smith/SchemaSmith/issues).

If you're still stuck and want to talk it through, reach out to Forge directly -- [ForgeBarrett@SchemaSmith.com](mailto:ForgeBarrett@SchemaSmith.com). Real developers on the other end. Real answers. We're happy to help.

---

This is the final chapter of the guide. For a refresher on the basics, head back to [Why SchemaSmith](01-why-schemasmith.md) or jump straight to the [Quick Start](02-quick-start.md).
