---
name: preview
description: Validate a SchemaSmith package and preview what a deployment would do to a database, with --Validate and --WhatIf, explained in plain words. Use before any deployment, or when the user asks what a deploy would change.
---

# Preview a deployment

Read `${CLAUDE_PLUGIN_ROOT}/reference/settings.md` and `${CLAUDE_PLUGIN_ROOT}/reference/outputs.md` first.

1. Run `SchemaQuench --version`. If it does not answer, use the `install` skill.
2. Make sure SchemaQuench has its settings: `SchemaPackagePath`, `Target:Server`, the user, and the database each template deploys to (`Target:TemplateTargets:<template>:Databases`, unless the package's templates name their own). If not, use the `connect` skill. The password comes from `SmithySettings_Target__Password` (see `passwords.md`).
3. Validate first: `SchemaQuench --Validate --LogPath:./schemasmith-logs`. With errors, report them with their codes and stop: a preview of a package that does not validate says nothing useful.
4. Show the command, then run the preview: `SchemaQuench --WhatIf --LogPath:./schemasmith-logs`. A preview changes nothing of the user's; on a writable target it installs or refreshes SchemaSmith's own helper objects, as every run does.
5. Read `./schemasmith-logs/SchemaQuench - Summary.json` and say, per target (server, database, schema):
   - what would be created, modified and dropped (`objectChanges`, with the actions `wouldCreate`, `wouldModify`, `wouldDrop`), naming the objects;
   - which scripts would run (`whatIf.wouldApply`) and which data would be delivered (`whatIf.wouldDeliver`);
   - each change that would remove data, and whether the destructive-change policy would refuse it (`destructive.changes`, outcome `wouldRefuse`). Name the setting that governs it, `Target:DestructiveChangePolicy`, and let the user decide; never change it to get a deployment through;
   - anything the target cannot take (`unsupportedDowngrade`).
6. Exit `2`: read the logs and say what failed.

Done when the user has the plan in plain words. The preview of this package against this target is what the `deploy` skill asks for before it runs.
