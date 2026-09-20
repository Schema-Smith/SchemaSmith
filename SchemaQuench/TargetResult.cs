// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using SchemaQuench.Reporting;

namespace SchemaQuench;

/// <summary>
/// One work unit's captured outcome + duration (#243 Deployment Summary Report, slice E4a). Purely
/// a passive capture record — nothing here assembles or emits a report; a later slice (E4d) joins
/// <see cref="ScopeKey"/> against <c>RunTiming</c>'s per-slot entries (keyed by the same
/// <c>DatabaseQuench.LogPrefix</c>) to build the report's per-target slot timing.
/// </summary>
public sealed record TargetResult(
    string ScopeKey,
    string Server,
    string Database,
    string Schema,
    string Template,
    TargetOutcome Outcome,
    long DurationMs,
    string DatabaseSource = "",
    string SchemaSource = "")
{
    /// <summary>
    /// Derives a work unit's <see cref="TargetOutcome"/> from <c>DatabaseQuench.QuenchSuccessful</c>
    /// and <c>DatabaseQuench.WasSkipped</c>. Failure dominates: a failed unit is always
    /// <see cref="TargetOutcome.Failed"/> even if the skip flag were somehow also set. A static
    /// method on the record itself (not a separate helper class) — deliberately named to avoid
    /// colliding with <c>ProductQuench.TargetResults</c>, the plural collection-accessor property.
    /// </summary>
    public static TargetOutcome DeriveOutcome(bool quenchSuccessful, bool wasSkipped) =>
        !quenchSuccessful ? TargetOutcome.Failed
        : wasSkipped      ? TargetOutcome.Skipped
        :                   TargetOutcome.Success;
}
