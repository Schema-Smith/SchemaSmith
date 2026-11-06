// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

namespace SchemaQuench;

/// <summary>
/// Which objects a deploy may drop because the package no longer declares them. Every lever is off unless set; a protected
/// environment (PreventDrop) passes <see cref="None"/>.
/// </summary>
public sealed record DropPolicy(
    bool Tables = false,
    bool Columns = false,
    bool ForeignKeys = false,
    bool CheckConstraints = false,
    bool ExcludeConstraints = false,
    bool Statistics = false,
    bool Indexes = false,
    bool UnknownIndexes = false)
{
    public static DropPolicy None { get; } = new();
}

/// <summary>What one deploy to one database (and schema) is asked to do.</summary>
public sealed record DatabaseQuenchOptions
{
    public required string Server { get; init; }
    public required string DatabaseName { get; init; }
    /// <summary>The iteration schema of a schema template; empty for a regular template.</summary>
    public string SchemaName { get; init; } = "";
    public bool WhatIf { get; init; }
    public bool SuppressKindling { get; init; }
    public bool ForceReKindle { get; init; }
    public bool RunScriptsTwice { get; init; }
    public bool UpdateTables { get; init; } = true;
    public bool DeliverData { get; init; } = true;
    public bool TrackRunOnceMigrations { get; init; } = true;
    public bool PruneObsoleteMigrationTracking { get; init; } = true;
    public DropPolicy Drops { get; init; } = DropPolicy.None;
}
