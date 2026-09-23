// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SchemaQuench.Reporting;

/// <summary>
/// Pure mapping from the run-fact captures (#243, E1 <see cref="RunTiming"/>, E4a
/// <see cref="TargetResult"/>, E4b <see cref="MigrationScriptRun"/>, E4c <see cref="WhatIfRun"/>,
/// Group D <see cref="FailureRecord"/>) into the frozen v1 <see cref="DeploymentSummary"/> object
/// graph. No I/O, no clock, no config — every input (including timestamps) is a parameter. Wiring
/// this into the actual run and writing the report to disk is a later slice (E4e); this class only
/// assembles the object graph.
/// </summary>
public static class DeploymentSummaryAssembler
{
    public static DeploymentSummary Assemble(
        string product,
        string platform,
        string toolVersion,
        DateTime startedUtc,
        DateTime finishedUtc,
        RunMode mode,
        RunOutcome outcome,
        int exitCode,
        bool resumedFromCheckpoint,
        IReadOnlyCollection<TargetResult> targets,
        RunTiming timing,
        IReadOnlyList<MigrationScriptRun> migrationScripts,
        IReadOnlyList<WhatIfRun> whatIfEntries,
        IReadOnlyList<FailureRecord> failures,
        long bottleneckThresholdMs,
        ChangeAuditCapture changeAudit,
        bool protectedModeEnabled = false)
    {
        var run = new RunInfo(
            Product: product,
            Platform: platform,
            StartedUtc: startedUtc,
            FinishedUtc: finishedUtc,
            DurationMs: timing.TotalMs,
            Mode: mode,
            Outcome: outcome,
            ExitCode: exitCode,
            ResumedFromCheckpoint: resumedFromCheckpoint);

        var mappedTargets = targets
            .Select(tr => new TargetSummary(
                Server: tr.Server,
                Database: tr.Database,
                Schema: NullIfEmpty(tr.Schema),
                Template: tr.Template,
                Outcome: tr.Outcome,
                DurationMs: tr.DurationMs,
                Slots: timing.SlotsForScope(tr.ScopeKey),
                DatabaseSource: tr.DatabaseSource,
                SchemaSource: tr.SchemaSource))
            .ToList();

        var mappedMigrationScripts = migrationScripts
            .Select(m => new MigrationScriptRecord(
                Path: m.Path,
                Slot: m.Slot,
                Template: m.Template,
                Schema: NullIfEmpty(m.Schema),
                Server: m.Server,
                Database: NullIfEmpty(m.Database),
                Outcome: "Ran"))
            .ToList();

        var timingSummary = new TimingSummary(
            TotalMs: timing.TotalMs,
            BySlot: timing.BySlot(),
            ByDatabase: timing.ByDatabase(),
            Bottlenecks: timing.Bottlenecks(bottleneckThresholdMs));

        var whatIf = mode == RunMode.WhatIf ? BuildWhatIfSummary(whatIfEntries) : null;

        var objectChanges = BuildObjectChanges(changeAudit);

        var preventDrop = protectedModeEnabled ? BuildPreventDrop(changeAudit) : null;

        var unsupportedDowngrade = BuildUnsupportedDowngrade(changeAudit);

        return new DeploymentSummary(
            SchemaVersion: "1.1",
            Tool: "SchemaQuench",
            ToolVersion: toolVersion,
            Run: run,
            Targets: mappedTargets,
            MigrationScripts: mappedMigrationScripts,
            Timing: timingSummary,
            Failures: failures,
            WhatIf: whatIf,
            ObjectChanges: objectChanges,
            PreventDrop: preventDrop,
            UnsupportedDowngrade: unsupportedDowngrade);
    }

    /// <summary>
    /// Unsupported-feature downgrade manifest (Phase 0). Itemizes the objects a version-gated emit
    /// site downgraded because the DETECTED target version could not support a declared feature —
    /// audit rows carry the feature+version descriptor in <c>ObjectType</c> (e.g.
    /// <c>NULLS NOT DISTINCT (PG15)</c>) and the object in <c>ObjectName</c>, under the
    /// <c>downgraded</c> action. Returns <c>null</c> (omitted from the report) unless at least one
    /// downgrade was recorded, so runs that hit no below-floor feature keep the v1 shape. Not gated
    /// on protected mode — a downgrade is orthogonal to no-drop protection.
    /// </summary>
    private static UnsupportedDowngradeSummary BuildUnsupportedDowngrade(ChangeAuditCapture changeAudit)
    {
        if (changeAudit is not { Instrumented: true })
            return null;

        var downgrades = changeAudit.Snapshot()
            .Where(r => r.Action == "downgraded")
            .Select(r => new UnsupportedDowngradeEntry(r.ObjectType, r.ObjectName))
            .ToArray();

        return downgrades.Length == 0 ? null : new UnsupportedDowngradeSummary(downgrades);
    }

    /// <summary>
    /// No-drop protection tier manifest (#270 Slice E). Itemizes the objects the procs reported as
    /// <c>wouldDrop</c> — dropped by absence but suppressed because the environment is protected.
    /// Emitted only when protected mode is enabled; empty <see cref="PreventDropSummary.WouldDrop"/>
    /// means protection was on but nothing was suppressed this run.
    /// </summary>
    private static PreventDropSummary BuildPreventDrop(ChangeAuditCapture changeAudit)
    {
        var wouldDrop = changeAudit is { Instrumented: true }
            ? changeAudit.Snapshot()
                .Where(r => r.Action == "dropSuppressed")
                .Select(r => new WouldDropEntry(r.ObjectType, r.ObjectName))
                .ToArray()
            : Array.Empty<WouldDropEntry>();
        return new PreventDropSummary(Enabled: true, WouldDrop: wouldDrop);
    }

    /// <summary>
    /// Aggregates the object-change audit (#243 E5) into the report's objectChanges block. The block
    /// is <c>instrumented:false</c> (all zeros) unless the run's engine produced a real audit read —
    /// see <see cref="ChangeAuditCapture.Instrumented"/>. created/modified/dropped counts are
    /// verified change from the 4 table procs (object types table/column/index/constraint/foreignKey);
    /// object scripts (procedures/views/functions) never "created" — they "ran" (scriptsRan).
    /// </summary>
    private static ObjectChangeSummary BuildObjectChanges(ChangeAuditCapture changeAudit)
    {
        if (changeAudit is not { Instrumented: true })
            return new ObjectChangeSummary(
                Instrumented: false,
                Created: new CreatedCounts(0, 0, 0, 0, 0, 0, 0, 0),
                Modified: new ModifiedCounts(0, 0),
                Dropped: new DroppedCounts(0, 0, 0, 0),
                ScriptsRan: 0,
                Details: Array.Empty<ObjectChangeDetail>());

        var rows = changeAudit.Snapshot();

        int Count(string type, string action) =>
            rows.Count(r => r.ObjectType == type && r.Action == action);

        // WhatIf preview actions (#363) map into the same buckets as their executed counterparts:
        // wouldCreate -> created, wouldModify -> modified, wouldDrop -> dropped. The run's mode field
        // tells a reader whether these are previews or executed changes.
        int Count2(string type, string executed, string previewed) =>
            rows.Count(r => r.ObjectType == type && (r.Action == executed || r.Action == previewed));

        var created = new CreatedCounts(
            Tables: Count2("table", "created", "wouldCreate"),
            Columns: Count2("column", "created", "wouldCreate"),
            Indexes: Count2("index", "created", "wouldCreate"),
            Constraints: Count2("constraint", "created", "wouldCreate"),
            ForeignKeys: Count2("foreignKey", "created", "wouldCreate"),
            Procedures: 0,
            Views: 0,
            Functions: 0);

        var modified = new ModifiedCounts(
            Tables: Count2("table", "modified", "wouldModify") + Count("table", "renamed"),
            Columns: Count2("column", "modified", "wouldModify"));

        var dropped = new DroppedCounts(
            Tables: Count2("table", "dropped", "wouldDrop"),
            Indexes: Count2("index", "dropped", "wouldDrop"),
            Constraints: Count2("constraint", "dropped", "wouldDrop"),
            ForeignKeys: Count2("foreignKey", "dropped", "wouldDrop"));

        var scriptsRan = rows.Count(r => r.Action == "ran");
        // 'dropSuppressed' rows are protection-suppressed drops, not changes that would occur — they
        // belong to the PreventDropSummary manifest, not the object-change detail. 'downgraded' rows
        // likewise belong to the UnsupportedDowngradeSummary manifest, not the detail. WhatIf 'would*'
        // rows ARE previewed changes and stay in the detail.
        var details = rows
            .Where(r => r.Action != "dropSuppressed" && r.Action != "downgraded")
            .Select(r => new ObjectChangeDetail(r.ObjectType, r.ObjectName, r.Action))
            .ToArray();

        return new ObjectChangeSummary(true, created, modified, dropped, scriptsRan, details);
    }

    /// <summary>
    /// Object-script WhatIf logging (<c>WhatIfLogScripts</c> in <see cref="DatabaseQuench"/>) runs
    /// once per dependency-resolution pass with no checkpoint gate — unlike the real run, where
    /// <c>QuenchDatabaseObjectsWithCheckpoint</c> only records a script as "ran" the first time its
    /// <c>HasBeenQuenched</c> flips true and excludes it from every later pass. So the same
    /// (scope, script) pair can land in <paramref name="whatIfEntries"/> multiple times for one
    /// scope. Dedupe on first occurrence, preserving encounter order, for all three categories —
    /// not just Apply — so there is one dedup rule instead of a category-specific special case
    /// that could drift.
    /// </summary>
    private static WhatIfSummary BuildWhatIfSummary(IReadOnlyList<WhatIfRun> whatIfEntries)
    {
        IReadOnlyList<WhatIfEntry> ForCategory(WhatIfCategory category)
        {
            var seen = new HashSet<(string Scope, string Script)>();
            var result = new List<WhatIfEntry>();
            foreach (var w in whatIfEntries)
            {
                if (w.Category != category || !seen.Add((w.Scope, w.Script)))
                    continue;
                result.Add(new WhatIfEntry(w.Scope, w.Script));
            }
            return result;
        }

        return new WhatIfSummary(
            WouldApply: ForCategory(WhatIfCategory.Apply),
            WouldSkip: ForCategory(WhatIfCategory.Skip),
            WouldDeliver: ForCategory(WhatIfCategory.Deliver));
    }

    private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
