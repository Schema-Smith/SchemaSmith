# SchemaShears Reference

Surgical deployments demand surgical packages. SchemaShears carves an object-level patch -- a subset of a full product -- from an existing schema package, using a **manifest** that names exactly which files belong in the patch. The result is a stand-alone package you can deploy against any target that already has the full product installed, updating only the objects in scope and leaving everything else undisturbed.

The natural producer of a manifest is your VCS: `git diff --name-only <before> <after>` scoped to the product path is all it takes to turn a commit range into a patch definition. SchemaShears reads that list, resolves the full include set, and hands back a ready-to-quench package.

---

## Invocation

SchemaShears ships as part of the SchemaSmith distribution -- see the [Installation guide](../guide/installation.md) for how to get the binary on your PATH. Run it from the directory containing your `SchemaShears.settings.json`:

```bash
SchemaShears
```

To override the configuration file location:

```bash
SchemaShears --ConfigFile:path/to/SchemaShears.settings.json
```

SchemaShears reads configuration from multiple sources, merged in this precedence order (highest priority last):

1. **Configuration file** -- `SchemaShears.settings.json` in the current directory (or the file specified by `--ConfigFile`)
2. **User secrets** (debug builds only)
3. **Environment variables** with the `SmithySettings_` prefix
4. **Command-line switches** (highest precedence)

For the full list of CLI switches shared by all SchemaSmith tools, see the [Configuration Reference](configuration.md#cli-switch-format).

---

## CLI Switches

| Switch | Settings key | Description |
|--------|-------------|-------------|
| `--Source:<path>` | `SourcePath` | Path to the full schema product that is the source of the patch. Points to the product root (the folder containing `Product.json`). _(Required)_ |
| `--Manifest:<path>` | `ManifestPath` | Path to the manifest file -- a newline-delimited list of paths, relative to `--Source`, that belong in the patch. Lines beginning with `#` are ignored as comments. _(Required)_ |
| `--AlwaysInclude:<path>` | `AlwaysIncludePath` | Path to an always-include file -- a newline-delimited list of paths or directories, relative to `--Source`, that are added to the patch unconditionally regardless of the manifest. Lines beginning with `#` are ignored. _(Optional)_ |
| `--Output:<path>` | `OutputPath` | Directory where the patch package is written. Created automatically if it doesn't exist. _(Required)_ |
| `--Zip` | `Zip` | After writing the patch package, compress the output directory into a `.zip` archive alongside it. _(Optional; default `false`)_ |
| `--AllowDrops:<list>` | `AllowDrops` | Comma-separated list of drop categories the deployed patch is permitted to reconcile-away. By default all drops are suppressed. See [Drop suppression](#drop-suppression) below. _(Optional)_ |

---

## Configuration File

A complete `SchemaShears.settings.json`:

```json
{
  "SourcePath": "./MyProduct",
  "ManifestPath": "./patch.manifest",
  "AlwaysIncludePath": "./always-include.txt",
  "OutputPath": "./MyProduct-patch",
  "Zip": false,
  "AllowDrops": ""
}
```

All keys are optional in the file when the corresponding CLI switch is provided.

---

## Manifest Format

The manifest is a plain-text file, one path per line, where each path is relative to the `--Source` product root:

```
# Emergency patch for the Orders hotfix
Tables/dbo.Orders.json
Procedures/dbo.ProcessOrder.sql
Procedures/dbo.ValidateOrder.sql
# include the audit function it calls
Functions/dbo.AuditChange.sql
```

- Lines beginning with `#` are treated as comments and ignored.
- Blank lines are ignored.
- Paths use forward slashes or backslashes; SchemaShears normalizes them.
- Paths must be relative to the product root (the folder containing `Product.json`).

> **Tip:** The natural producer of a manifest is your VCS. Generate one from a commit range with the shell one-liner below and paste or pipe the output directly into your manifest file.

### Generating a manifest from a commit range

```bash
git diff --name-only <before-sha> <after-sha> -- path/to/MyProduct/
```

Replace `path/to/MyProduct/` with the path from your repository root to the product folder. Each line of output is already relative to that root, so you can feed it directly to `--Manifest`.

For example, to build a patch from all changes between the last release tag and HEAD:

```bash
git diff --name-only v2.3.0 HEAD -- MyProduct/ > patch.manifest
SchemaShears --Source:./MyProduct --Manifest:./patch.manifest --Output:./patch
```

---

## Always-Include File

The always-include file lists paths -- files or directories -- relative to the product root that are copied into every patch regardless of what the manifest contains. Use it for objects that every patch depends on but that may not appear in the manifest itself:

```
# These helpers are referenced by every patch procedure
Functions/dbo.CommonHelpers.sql
Schemas/
```

- Directory entries include all files under that directory recursively.
- Lines beginning with `#` are comments.
- The same path normalization applies as for manifests.

---

## Include Set

The patch package includes exactly:

1. **Manifest entries** -- every file listed in the manifest.
2. **Always-include entries** -- every file (or directory of files) listed in the always-include file.
3. **Scaffolding** -- `Product.json` from the source product root, plus the `Template.json` for every template that contains at least one manifest or always-include file.

Scaffolding is always included automatically. You do not need to list `Product.json` or `Template.json` in your manifest.

---

## Output

SchemaShears writes the patch package to the `--Output` directory, preserving the source product's folder structure for every included file. The output includes a `patch-build-report.txt` in the output root that lists every included file and the reason it was included (manifest entry, always-include entry, or scaffolding).

When `--Zip` is set, SchemaShears additionally compresses the output directory into a `.zip` archive at the same location, ready for artifact handoff or archival.

---

## Drop Suppression

A patch package is a **subset** of a full product. When SchemaQuench deploys a subset against a target that has the full product installed, it would normally interpret every object absent from the patch as "should be dropped." Drop suppression prevents that: SchemaShears stamps drop-control flags into the emitted patch's `Product.json` so that absent objects are preserved rather than reconciled away.

### Default behavior

By default, SchemaShears suppresses all recognized drop categories. The emitted `Product.json` carries:

```json
{
  "DropTablesRemovedFromProduct": false,
  "DropColumnsRemovedFromProduct": false,
  "DropUnknownIndexes": false,
  "DropForeignKeysRemovedFromProduct": false,
  "DropCheckConstraintsRemovedFromProduct": false,
  "DropExcludeConstraintsRemovedFromProduct": false,
  "DropStatisticsRemovedFromProduct": false
}
```

That is the PostgreSQL form. `DropExcludeConstraintsRemovedFromProduct` is written only for PostgreSQL products and `DropStatisticsRemovedFromProduct` only for SQL Server and PostgreSQL products, because those are the only engines that accept them -- stamping either one elsewhere would make the patch fail `--Validate`, and those engines have no such objects to drop anyway.

This is the correct posture for a patch: deploy what's in scope, leave everything else alone.

### Enforcement

**Every stamped flag is enforced.** As of v2.2.0, SchemaQuench's drop-control covers every category SchemaShears suppresses: a table absent from the patch is not dropped, and on the tables the patch does include, columns, foreign keys, check constraints, exclude constraints, and statistics that exist in the target but not in the table JSON are preserved -- along with out-of-band indexes (`DropUnknownIndexes` stays `false`). See the per-flag sections in the [SchemaQuench Reference](schemaquench.md#droptablesremovedfromproduct) for exact semantics.

The user promise is: _a patch won't drop objects you didn't include_ -- unless you explicitly re-enable a category with `--AllowDrops`.

### Selectively re-enabling drops

Use `--AllowDrops` (or the `AllowDrops` settings key) to re-enable specific categories. Pass a comma-separated list of category names -- case-insensitive:

```bash
SchemaShears --Source:./MyProduct --Manifest:./patch.manifest --Output:./patch --AllowDrops:Columns,Indexes
```

Valid category names:

| Category | Controls |
|----------|---------|
| `Tables` | Drop tables absent from the patch |
| `Columns` | Drop columns absent from included tables |
| `Indexes` | Drop indexes absent from included tables |
| `ForeignKeys` | Drop foreign keys absent from included tables |
| `CheckConstraints` | Drop check constraints absent from included tables |
| `ExcludeConstraints` | Drop exclude constraints absent from included tables |
| `Statistics` | Drop statistics absent from included tables |

Any category not listed in `--AllowDrops` continues to be suppressed.

---

## Testing Your Patch

> **Warning:** Always test a patch deployment against a non-production target before deploying to production. Patches are surgical -- they update exactly what's in scope -- but the scope is defined by your manifest, and an incomplete manifest can leave a target in a partially-updated state. Verify the patch in a representative non-prod environment that matches the production schema.

The same SchemaQuench workflow you use for full-product deployments applies to patches -- the patch output is a valid schema package:

```bash
SchemaQuench --SchemaPackagePath=./patch --WhatIfONLY=true
```

Review the WhatIf output to confirm the patch will affect only the objects you intended.

---

## Related Documentation

- [SchemaQuench Reference](schemaquench.md) -- The deployment engine that applies the patch package to a target database
- [Schema Packages Reference](schema-packages.md) -- Product/Template JSON format, including `DropTablesRemovedFromProduct` and the other drop-control fields
- [Power Workflows](../guide/09-power-workflows.md) -- Emergency-patch workflow in action
- [Configuration Reference](configuration.md) -- Shared CLI switches, config hierarchy, environment variables
