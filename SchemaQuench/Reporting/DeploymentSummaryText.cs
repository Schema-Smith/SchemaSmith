// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;

namespace SchemaQuench.Reporting;

/// <summary>
/// Pure formatter that renders a <see cref="DeploymentSummary"/> (E2's model) into a
/// human-readable markdown string — the HUMAN channel twin of <see cref="DeploymentSummaryJson"/>'s
/// machine channel. Mirrors the <see cref="PreFlightReporter"/> pattern (a <c>public static
/// class</c>, no side effects, fully unit-testable) except it returns one joined string — built
/// with <c>"\n"</c>, not <c>Environment.NewLine</c>, so output is deterministic across OS — instead
/// of a line list. No wiring, no assembly, no I/O: purely a projection of the model already handed
/// to it.
/// </summary>
public static class DeploymentSummaryText
{
    /// <summary>
    /// Renders the full report. Some sections are unconditional (Targets, Migration Scripts,
    /// Timing, Object Changes always appear, even when empty); others are conditional on the
    /// data actually being present:
    /// <list type="bullet">
    ///   <item>Failures — only when <see cref="DeploymentSummary.Failures"/> is non-empty.</item>
    ///   <item>WhatIf — only when <see cref="DeploymentSummary.WhatIf"/> is non-null (i.e. a
    ///   WhatIf-mode run).</item>
    ///   <item>Bottlenecks (a Timing sub-section) — only when
    ///   <see cref="TimingSummary.Bottlenecks"/> is non-empty.</item>
    ///   <item>Object Changes always renders, but shows the literal <c>(not instrumented)</c>
    ///   marker instead of counts when <see cref="ObjectChangeSummary.Instrumented"/> is false —
    ///   the zeroed counts in that case are placeholders, not measurements, and must not be
    ///   printed as if they were real.</item>
    /// </list>
    /// </summary>
    public static string Render(DeploymentSummary summary)
    {
        var lines = new List<string>();

        RenderHeader(summary, lines);
        RenderTargets(summary, lines);
        RenderMigrationScripts(summary, lines);
        RenderTiming(summary, lines);
        RenderFailures(summary, lines);
        RenderAppliedBeforeTheFailure(summary, lines);
        RenderWhatIf(summary, lines);
        RenderObjectChanges(summary, lines);
        RenderPreventDrop(summary, lines);
        RenderUnsupportedDowngrade(summary, lines);

        return string.Join("\n", lines);
    }

    private static void RenderHeader(DeploymentSummary summary, List<string> lines)
    {
        var run = summary.Run;
        lines.Add("# Deployment Summary");
        lines.Add("");
        lines.Add($"- Product: {run.Product}");
        lines.Add($"- Platform: {run.Platform}");
        lines.Add($"- Mode: {run.Mode}");
        lines.Add($"- Outcome: {run.Outcome}");
        lines.Add($"- Exit code: {run.ExitCode}");
        lines.Add($"- Duration: {run.DurationMs}ms");
        lines.Add($"- Started (UTC): {FormatUtc(run.StartedUtc)}");
        lines.Add($"- Finished (UTC): {FormatUtc(run.FinishedUtc)}");
        lines.Add("");
    }

    private static void RenderTargets(DeploymentSummary summary, List<string> lines)
    {
        var targets = summary.Targets;
        lines.Add($"## Targets ({targets.Count})");
        if (targets.Count == 0)
        {
            lines.Add("- (no targets)");
        }
        else
        {
            foreach (var target in targets)
                lines.Add($"- {FormatScope(target.Server, target.Database, target.Schema)} — {target.Template} — {target.Outcome} — {target.DurationMs}ms");
        }
        lines.Add("");
    }

    private static void RenderMigrationScripts(DeploymentSummary summary, List<string> lines)
    {
        var scripts = summary.MigrationScripts;
        lines.Add($"## Migration Scripts ({scripts.Count})");
        foreach (var script in scripts)
            lines.Add($"- {script.Path} — {FormatScope(script.Server, script.Database, script.Schema)}" +
                      (script.Outcome == "Skipped" ? " — skipped (SHOULD NOT APPLY)" : ""));
        lines.Add("");
    }

    private static void RenderTiming(DeploymentSummary summary, List<string> lines)
    {
        var timing = summary.Timing;
        lines.Add("## Timing");
        lines.Add($"- Total: {timing.TotalMs}ms");
        lines.Add("");

        lines.Add("### By Slot");
        foreach (var slot in timing.BySlot)
            lines.Add($"- {slot.Slot}: {slot.TotalMs}ms ({slot.TargetCount} targets)");
        lines.Add("");

        lines.Add("### By Database");
        foreach (var db in timing.ByDatabase)
            lines.Add($"- {db.Database}: {db.TotalMs}ms");
        lines.Add("");

        if (timing.Bottlenecks.Count > 0)
        {
            lines.Add("### Bottlenecks");
            foreach (var bottleneck in timing.Bottlenecks)
                lines.Add($"- {bottleneck.Scope} / {bottleneck.Slot} — {bottleneck.DurationMs}ms");
            lines.Add("");
        }
    }

    // Nothing is rolled back when a run fails, so a failed run says what it had already applied: the scripts that
    // ran, the schema changes made and the data delivered. Resume continues from there.
    private static readonly HashSet<string> NotAChange = new(StringComparer.Ordinal) { "unchanged", "skipped", "downgraded", "dropSuppressed" };

    private static void RenderAppliedBeforeTheFailure(DeploymentSummary summary, List<string> lines)
    {
        if (summary.Run.Outcome is not (RunOutcome.PartialFailure or RunOutcome.Aborted)) return;
        var migrations = summary.MigrationScripts.Where(m => m.Outcome == "Ran")
            .Select(m => (Target: FormatScope(m.Server, m.Database, m.Schema) ?? "", Item: $"migration script {m.Path}"));
        var details = summary.ObjectChanges?.Details ?? [];
        var changes = details
            .Where(d => !d.Action.StartsWith("would", StringComparison.Ordinal) && !NotAChange.Contains(d.Action))
            .Select(d => (Target: FormatScope(d.Server, d.Database, d.Schema) ?? "", Item: d.Action switch
            {
                "ran" => $"{d.ObjectType} {d.ObjectName}",
                "delivered" when d.ObjectType == "dataDelivery" => $"data {d.ObjectName}",
                _ => $"{d.ObjectType} {d.ObjectName} {d.Action}"
            }));
        var applied = migrations.Concat(changes).ToList();
        lines.Add($"## Applied Before The Failure ({applied.Count})");
        lines.Add("Nothing here was rolled back. `--ResumeQuench` continues from where the run stopped.");
        // Grouped by target, in the order each was first reached: on a fleet run the same change is made in many places,
        // and what matters for recovery is which ones. A change with no recorded target is listed first, ungrouped.
        foreach (var group in applied.GroupBy(a => a.Target).OrderBy(g => g.Key.Length == 0 ? 0 : 1))
        {
            if (group.Key.Length > 0) lines.Add($"### {group.Key}");
            foreach (var item in group)
                lines.Add($"- {item.Item}");
        }
        lines.Add("");
    }

    private static void RenderFailures(DeploymentSummary summary, List<string> lines)
    {
        var failures = summary.Failures;
        if (failures.Count == 0)
            return;

        lines.Add($"## Failures ({failures.Count})");
        foreach (var failure in failures)
            lines.Add($"- {failure.Phase} {failure.ScopeKey}: {failure.Error}");
        lines.Add("");
    }

    private static void RenderWhatIf(DeploymentSummary summary, List<string> lines)
    {
        var whatIf = summary.WhatIf;
        if (whatIf == null)
            return;

        lines.Add("## WhatIf");
        lines.Add($"- Would apply: {whatIf.WouldApply.Count}");
        lines.Add($"- Would skip: {whatIf.WouldSkip.Count}");
        lines.Add($"- Would deliver: {whatIf.WouldDeliver.Count}");
        lines.Add("");
    }

    private static void RenderObjectChanges(DeploymentSummary summary, List<string> lines)
    {
        var objectChanges = summary.ObjectChanges;
        lines.Add("## Object Changes");

        if (!objectChanges.Instrumented)
        {
            lines.Add("- (not instrumented)");
            return;
        }

        var created = objectChanges.Created;
        var modified = objectChanges.Modified;
        var dropped = objectChanges.Dropped;
        lines.Add($"- Created: tables={created.Tables}, columns={created.Columns}, indexes={created.Indexes}, constraints={created.Constraints}, foreignKeys={created.ForeignKeys}, procedures={created.Procedures}, views={created.Views}, functions={created.Functions}");
        lines.Add($"- Modified: tables={modified.Tables}, columns={modified.Columns}");
        lines.Add($"- Dropped: tables={dropped.Tables}, indexes={dropped.Indexes}, constraints={dropped.Constraints}, foreignKeys={dropped.ForeignKeys}");
        lines.Add($"- Ran (object scripts): {objectChanges.ScriptsRan}");
        lines.Add($"- Unchanged (object scripts): {objectChanges.ScriptsUnchanged}");
    }

    private static void RenderPreventDrop(DeploymentSummary summary, List<string> lines)
    {
        var preventDrop = summary.PreventDrop;
        if (preventDrop == null)
            return;

        lines.Add("");
        lines.Add("## PreventDrop (no-drop protection)");
        if (preventDrop.WouldDrop.Count == 0)
        {
            lines.Add("- Protection active — no drops were suppressed this run.");
            return;
        }
        lines.Add($"- Protection active — {preventDrop.WouldDrop.Count} drop(s) suppressed (object removed from product but not dropped):");
        foreach (var entry in preventDrop.WouldDrop)
            lines.Add($"  - {entry.ObjectType}: {entry.ObjectName}");
    }

    private static void RenderUnsupportedDowngrade(DeploymentSummary summary, List<string> lines)
    {
        var downgrade = summary.UnsupportedDowngrade;
        if (downgrade == null)
            return;

        lines.Add("");
        lines.Add("## Unsupported Feature Downgrades");
        lines.Add($"- {downgrade.Downgrades.Count} feature(s) declared but unsupported at the target version — emitted without the unsupported aspect:");
        foreach (var entry in downgrade.Downgrades)
            lines.Add($"  - {entry.Feature}: {entry.ObjectName}");
    }

    private static string FormatScope(string server, string database, string schema)
    {
        // Product-folder scripts are server-scoped (no database) — collapse to just the server
        // rather than emitting a dangling "server / ".
        var scope = string.IsNullOrEmpty(database) ? server : $"{server} / {database}";
        return string.IsNullOrEmpty(schema) ? scope : $"{scope} [{schema}]";
    }

    private static string FormatUtc(DateTime value)
        => value.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
