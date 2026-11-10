// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Checkpointing;

namespace SchemaQuench;

/// <summary>
/// What a preview tracks progress with: every step and script runs, and nothing is read from, written to or deleted from
/// the checkpoint files. A preview that recorded progress left a resume point a later real run trusted and skipped
/// table changes on, and one that loaded progress discarded a failed deploy's resume point.
/// </summary>
internal sealed class NoCheckpointing : ICheckpointing
{
    public static readonly NoCheckpointing Instance = new();

    private NoCheckpointing() { }

    public void Track(TrackingScope scope, string stepName, Action work) => work();

    public bool HasCompleted(TrackingScope scope, string stepName) => false;

    public bool HasCompletedScript(TrackingScope scope, string slot, string scriptPath) => false;

    public DatabaseCheckpointSummary GetDatabaseCheckpointSummary(TrackingScope scope) => DatabaseCheckpointSummary.Empty;

    public void MarkStepCompleted(TrackingScope scope, string stepName) { }

    public void MarkScriptCompleted(TrackingScope scope, string slot, string scriptPath) { }

    public void DeleteCheckpoints(string productName) { }

    public void DeleteCheckpointsOfThisRun(string productName) { }
}
