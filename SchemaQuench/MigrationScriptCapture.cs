// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SchemaQuench;

/// <summary>
/// One migration script that actually executed during this run (#243, E4b). Captured only at the
/// point where a script's run is confirmed (resume-skipped scripts never reach the capture call),
/// so every entry here really ran this run. Outcome is "Ran", or "Skipped" when the script raised the
/// SHOULD NOT APPLY sentinel.
/// </summary>
public sealed record MigrationScriptRun(
    string Server, string Database, string Schema, string Template, string Slot, string Path, string Outcome = "Ran");

/// <summary>
/// Thread-safe sink for the migration scripts that actually ran this run (#243, E4b). Work units
/// run concurrently, so captures use a <see cref="ConcurrentBag{T}"/>. Purely additive
/// instrumentation — never a behavior dependency for script execution, skip logic, or checkpoint
/// tracking.
/// </summary>
public sealed class MigrationScriptCapture
{
    private readonly ConcurrentBag<MigrationScriptRun> _runs = new();

    public void Record(string server, string database, string schema, string template, string slot, string path, string outcome = "Ran")
        => _runs.Add(new MigrationScriptRun(server, database, schema, template, slot, path, outcome));

    public IReadOnlyList<MigrationScriptRun> Snapshot() => _runs.ToArray();
}
