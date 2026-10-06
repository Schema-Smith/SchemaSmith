// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;

namespace SchemaQuench;

/// <summary>
/// Orthogonal cross-product filter applied to the discovered work-unit set after
/// per-template enumeration (design §9.3). Each filter dimension
/// (<c>Target.Templates</c>, <c>Target.Databases</c>, <c>Target.Schemas</c>) intersects
/// with the discovered set; empty filter arrays mean "no constraint on this dimension".
/// <para>
/// Regular-template work units (with <see cref="WorkUnit.SchemaName"/> == <c>""</c>) bypass
/// the schema filter entirely — Target.Schemas only constrains schema-template iterations.
/// If the discovered set has no schema-template units but <c>Target.Schemas</c> is set, a
/// single warning is emitted via the supplied <c>warn</c> callback (design §9.4).
/// </para>
/// <para>
/// Validation surfaces two errors:
/// (1) any filter value not present in the discovered universe throws
/// <see cref="InvalidOperationException"/> with a per-dimension available-options listing;
/// (2) a non-empty filter set that intersects to zero throws with the input filter diagnostic.
/// </para>
/// </summary>
internal sealed class WorkUnitFilter
{
    private readonly IReadOnlyList<string> _templates;
    private readonly IReadOnlyList<string> _databases;
    private readonly IReadOnlyList<string> _schemas;

    public WorkUnitFilter(IReadOnlyList<string> templates, IReadOnlyList<string> databases, IReadOnlyList<string> schemas,
        bool databaseNamesFold = false)
    {
        _templates = templates ?? [];
        _databases = databases ?? [];
        _schemas = schemas ?? [];
    }

    /// <summary>
    /// Applies the configured filters to <paramref name="discovered"/>. Validates first
    /// (per design §9.4) so a typo surfaces with a useful diagnostic before any work runs.
    /// </summary>
    /// <param name="discovered">The discovered work-unit set across all templates and DBs.</param>
    /// <param name="warn">Receives warning messages (e.g. <c>Target.Schemas</c> set on a
    /// product with no schema templates). Called at most once per <see cref="Apply"/>
    /// invocation per warning kind.</param>
    public List<WorkUnit> Apply(IReadOnlyList<WorkUnit> discovered, Action<string> warn)
    {
        ValidateFilterValues(discovered);
        WarnIfSchemaFilterUnusable(discovered, warn);

        // Template-name matching is case-insensitive across the validator + filter trio.
        // Database / schema name comparisons are LEFT case-sensitive because engine-defined
        // casing rules differ across SQL Server (CI default), MySQL (lower-case-folded on Linux,
        // server-config-dependent on Windows), and PostgreSQL (case-sensitive when unquoted,
        // lowercased when bareword) — unifying those at this layer would mask real engine
        // differences rather than smooth them out.
        var filtered = discovered
            .Where(u => _templates.Count == 0 || _templates.Contains(u.TemplateName, StringComparer.OrdinalIgnoreCase))
            .Where(u => _databases.Count == 0 || _databases.Contains(u.DatabaseName))
            .Where(u => _schemas.Count == 0 || string.IsNullOrEmpty(u.SchemaName) || _schemas.Contains(u.SchemaName))
            .ToList();

        if (filtered.Count == 0)
            throw new InvalidOperationException(BuildZeroResultMessage());

        return filtered;
    }

    private void ValidateFilterValues(IReadOnlyList<WorkUnit> discovered)
    {
        // Collect the discovered universe per dimension once, then compare against each filter
        // array. We accumulate ALL missing names across all three dimensions before throwing so
        // a single error names every typo — saves a round-trip when more than one is wrong.
        var discoveredTemplates = discovered.Select(u => u.TemplateName).Distinct().ToList();
        var discoveredDatabases = discovered.Select(u => u.DatabaseName).Distinct().ToList();
        var discoveredSchemas = discovered.Select(u => u.SchemaName)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        var details = new List<string>();
        // Templates use OrdinalIgnoreCase across the validator + filter trio. Databases and
        // Schemas keep default ordinal comparison — engine-defined casing rules differ across
        // SQL Server / MySQL / PostgreSQL, so unifying at this layer would mask real engine
        // differences rather than smooth them out.
        AppendUnknownDetail(details, "Target.Templates", _templates, discoveredTemplates,
            StringComparer.OrdinalIgnoreCase);
        AppendUnknownDetail(details, "Target.Databases", _databases, discoveredDatabases);
        // Schema-name validation only fires when at least one schema-template unit was
        // discovered. Otherwise Target.Schemas is a no-op (regular-template units bypass it)
        // and we surface that via the warning path in WarnIfSchemaFilterUnusable instead.
        if (discoveredSchemas.Count > 0)
            AppendUnknownDetail(details, "Target.Schemas", _schemas, discoveredSchemas);

        if (details.Count > 0)
            throw new InvalidOperationException(
                "One or more Target.* filter values do not match the discovered work-unit set. " +
                string.Join(" ", details));
    }

    private static void AppendUnknownDetail(List<string> details, string dimensionName,
        IReadOnlyList<string> filter, IReadOnlyList<string> discovered,
        StringComparer comparer = null)
    {
        if (filter.Count == 0) return;
        comparer ??= StringComparer.Ordinal;
        var missing = filter.Where(v => !discovered.Contains(v, comparer)).ToList();
        if (missing.Count == 0) return;
        details.Add(
            $"{dimensionName} value(s) not discovered: [{string.Join(",", missing)}]. " +
            $"Available: [{string.Join(",", discovered)}].");
    }

    private void WarnIfSchemaFilterUnusable(IReadOnlyList<WorkUnit> discovered, Action<string> warn)
    {
        // Target.Schemas set, but every discovered unit is a regular-template unit
        // (SchemaName == ""). The filter expression naturally bypasses these units, but we
        // still warn so the user knows the filter had no effect — they may have intended a
        // different dimension (Target.Databases, perhaps) and the silent no-op would mislead.
        if (_schemas.Count == 0) return;
        if (discovered.Any(u => !string.IsNullOrEmpty(u.SchemaName))) return;
        warn($"Target.Schemas={FormatList(_schemas)} was set, but no schema-template work " +
             $"units were discovered — all units are from regular templates and bypass the " +
             $"schema filter. No effect on this run.");
    }

    private string BuildZeroResultMessage() =>
        $"Target filters produced zero work units to execute. " +
        $"Templates={FormatList(_templates)}, " +
        $"Databases={FormatList(_databases)}, " +
        $"Schemas={FormatList(_schemas)}. " +
        "Verify names match values returned by the identification scripts.";

    internal static string FormatList(IReadOnlyList<string> values) =>
        $"[{string.Join(",", values)}]";
}
