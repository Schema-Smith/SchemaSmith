// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;

namespace Schema.Checkpointing;

/// <summary>
/// Step-tracking for resumable deployments. Records completion to disk and skips
/// work that has been completed in a prior run for the same scope.
/// </summary>
public interface ICheckpointing
{
    /// <summary>
    /// Run a major deployment step under tracking. Skips work() if the step has been
    /// completed in a prior run for the same scope, and records completion after
    /// work() returns successfully.
    /// </summary>
    void Track(TrackingScope scope, string stepName, Action work);

    /// <summary>
    /// Whether a major step has been recorded as complete in a prior run for this scope.
    /// Callers use this to emit user-visible "skipping" logs before (or instead of) calling Track.
    /// </summary>
    bool HasCompleted(TrackingScope scope, string stepName);

    /// <summary>
    /// Whether a specific script has been recorded as complete for this scope and slot.
    /// Callers use this to emit user-visible "skipping" logs before (or instead of) running it.
    /// </summary>
    bool HasCompletedScript(TrackingScope scope, string slot, string scriptPath);

    /// <summary>
    /// Summary of database-level completions for the given scope, for emitting
    /// "Resuming from checkpoint (...)" at the database level. Returns an all-zero
    /// summary if no database-level checkpoint exists for the scope.
    /// </summary>
    DatabaseCheckpointSummary GetDatabaseCheckpointSummary(TrackingScope scope);

    /// <summary>
    /// Record a major step as complete without running any work. Use this when the caller
    /// owns the orchestration (e.g., retry loops around exception-swallowing work) and
    /// wants to record completion only after it has determined the work actually succeeded.
    /// </summary>
    void MarkStepCompleted(TrackingScope scope, string stepName);

    /// <summary>
    /// Record a script as complete without running any work. Use this when the caller
    /// owns the orchestration and only wants to mark success-after-the-fact.
    /// </summary>
    void MarkScriptCompleted(TrackingScope scope, string slot, string scriptPath);

    /// <summary>
    /// Clean up checkpoint files after a successful deployment.
    /// </summary>
    void DeleteCheckpoints(string productName);

    /// <summary>Deletes the database checkpoints this run wrote or took up, and leaves every other target's resume point.</summary>
    void DeleteCheckpointsOfThisRun(string productName) => DeleteCheckpoints(productName);

    /// <summary>Whether any database in this run continued from a checkpoint a failed run left.</summary>
    bool ResumedAny => false;

    /// <summary>Writes this run's id into every checkpoint it saves, so a later resume can name the run it continues.</summary>
    void StampRunId(Guid runId) { }

    /// <summary>The id of the run whose checkpoint this run took up; null when not resuming or the checkpoint predates 2.9.</summary>
    Guid? ResumedRunId => null;
}

/// <summary>
/// Aggregate counts from a database-level checkpoint, used for resume banner text.
/// </summary>
public record DatabaseCheckpointSummary(int CompletedSteps, int TotalCompletedScripts)
{
    public bool HasAnyCompleted => CompletedSteps > 0 || TotalCompletedScripts > 0;

    public static DatabaseCheckpointSummary Empty => new(0, 0);
}
