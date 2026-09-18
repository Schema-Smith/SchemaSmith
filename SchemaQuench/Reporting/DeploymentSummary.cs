// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;

namespace SchemaQuench.Reporting;

/// <summary>
/// Root of the Deployment Summary Report record graph (#243). This is the frozen v1 JSON
/// contract downstream consumers deserialize — field names, casing, nesting, and enum
/// string values must not change without a schema-version bump. Purely a data model: nothing in
/// this file (or <see cref="DeploymentSummaryJson"/>) wires into the run, populates itself, or
/// scrubs content. A later slice (E4) builds and emits instances; E5 fills in
/// <see cref="ObjectChangeSummary"/> detail.
/// </summary>
public sealed record DeploymentSummary(
    string SchemaVersion,
    string Tool,
    string ToolVersion,
    RunInfo Run,
    IReadOnlyList<TargetSummary> Targets,
    IReadOnlyList<MigrationScriptRecord> MigrationScripts,
    TimingSummary Timing,
    IReadOnlyList<FailureRecord> Failures,
    WhatIfSummary WhatIf,
    ObjectChangeSummary ObjectChanges,
    PreventDropSummary PreventDrop = null,
    UnsupportedDowngradeSummary UnsupportedDowngrade = null);

public sealed record RunInfo(
    string Product,
    string Platform,
    DateTime StartedUtc,
    DateTime FinishedUtc,
    long DurationMs,
    RunMode Mode,
    RunOutcome Outcome,
    int ExitCode,
    bool ResumedFromCheckpoint);

public sealed record TargetSummary(
    string Server,
    string Database,
    string Schema,
    string Template,
    TargetOutcome Outcome,
    long DurationMs,
    IReadOnlyList<TargetSlotTiming> Slots);

/// <summary>Per-target slot timing — distinct from E1's aggregate <see cref="SlotTiming"/>.</summary>
public sealed record TargetSlotTiming(string Slot, long DurationMs, int ScriptsRun);

/// <summary>
/// <see cref="Outcome"/> is a plain string (e.g. <c>"Ran"</c>) in Phase 1 — intentionally not an
/// enum yet; do not over-model.
/// </summary>
public sealed record MigrationScriptRecord(
    string Path,
    string Slot,
    string Template,
    string Schema,
    string Server,
    string Database,
    string Outcome);

/// <summary>
/// The three list types (<see cref="SlotTiming"/>, <see cref="DbTiming"/>,
/// <see cref="BottleneckEntry"/>) are E1's existing records from <c>RunTiming.cs</c> — reused
/// verbatim, not redefined or projected.
/// </summary>
public sealed record TimingSummary(
    long TotalMs,
    IReadOnlyList<SlotTiming> BySlot,
    IReadOnlyList<DbTiming> ByDatabase,
    IReadOnlyList<BottleneckEntry> Bottlenecks);

public sealed record WhatIfSummary(
    IReadOnlyList<WhatIfEntry> WouldApply,
    IReadOnlyList<WhatIfEntry> WouldSkip,
    IReadOnlyList<WhatIfEntry> WouldDeliver);

public sealed record WhatIfEntry(string Scope, string Script);

public sealed record ObjectChangeSummary(
    bool Instrumented,
    CreatedCounts Created,
    ModifiedCounts Modified,
    DroppedCounts Dropped,
    int ScriptsRan,
    IReadOnlyList<ObjectChangeDetail> Details);

public sealed record CreatedCounts(
    int Tables,
    int Columns,
    int Indexes,
    int Constraints,
    int ForeignKeys,
    int Procedures,
    int Views,
    int Functions);

public sealed record ModifiedCounts(int Tables, int Columns);

public sealed record DroppedCounts(int Tables, int Indexes, int Constraints, int ForeignKeys);

public sealed record ObjectChangeDetail(string ObjectType, string ObjectName, string Action);

/// <summary>
/// No-drop protection tier manifest (#270 Slice E). Present only when the environment-level
/// <c>PreventDrop</c> is enabled; <c>null</c> (omitted) otherwise, so non-protected runs keep the
/// v1 shape. <see cref="WouldDrop"/> itemizes every object that would have been dropped by absence
/// but was suppressed by protection — tables, columns, foreign keys, check and exclude constraints,
/// statistics, and indexes. <see cref="WouldDropEntry.ObjectType"/> names the kind. Transient
/// drop-then-recreate for a declared change is never listed (only by-absence removals are).
/// </summary>
public sealed record PreventDropSummary(
    bool Enabled,
    IReadOnlyList<WouldDropEntry> WouldDrop);

public sealed record WouldDropEntry(string ObjectType, string ObjectName);

/// <summary>
/// Unsupported-feature downgrade manifest (Phase 0, version-adaptive codegen). Present only when at
/// least one model feature was declared that the DETECTED target version could not support and the
/// run's policy is <c>warn</c> (the default) — the object was emitted WITHOUT the unsupported aspect
/// and each such downgrade recorded. <c>null</c> (omitted) when nothing was downgraded, so the
/// common case keeps the v1 shape. Under the <c>fail</c> policy the run aborts before any emit, so
/// this manifest never carries rows for a failed run. <see cref="UnsupportedDowngradeEntry.Feature"/>
/// names the exact feature + the version it needs (e.g. <c>NULLS NOT DISTINCT (PG15)</c>);
/// <see cref="UnsupportedDowngradeEntry.ObjectName"/> names the affected object.
/// </summary>
public sealed record UnsupportedDowngradeSummary(
    IReadOnlyList<UnsupportedDowngradeEntry> Downgrades);

public sealed record UnsupportedDowngradeEntry(string Feature, string ObjectName);

public enum RunMode
{
    Quench,
    WhatIf,
    Validate
}

public enum RunOutcome
{
    Success,
    PartialFailure,
    Aborted
}

public enum TargetOutcome
{
    Success,
    Failed,
    Skipped
}
