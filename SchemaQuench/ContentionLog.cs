// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SchemaQuench;

/// <summary>A statement that had to wait on another session: retried at least once, then succeeded or gave up.</summary>
public sealed record LockWait(string Target, string Step, int Attempts, long WaitedMs, string Outcome);

/// <summary>The run's lock waits, from every work unit; reported in the deployment summary (lockWaits[]).</summary>
public sealed class ContentionLog
{
    private readonly ConcurrentQueue<LockWait> _waits = new();

    public void Record(LockWait wait) => _waits.Enqueue(wait);

    public IReadOnlyList<LockWait> Snapshot() => _waits.ToArray().ToList();
}
