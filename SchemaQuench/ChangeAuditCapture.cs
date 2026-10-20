// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;

namespace SchemaQuench;

// Mode is the form a statement ran in under OnlineApply (online, resumable, unavailable); null for everything else.
public sealed record ChangeAuditRow(string ObjectType, string ObjectName, string Action, string Mode = null);

/// <summary>
/// Run-level collector for object-change audit rows (#243 E5). The 4 table quench procs write rows
/// to a session-scoped ChangeAudit table; <see cref="ChangeAuditReader"/> drains them per work unit
/// into this capture. Object scripts (procs/views/functions) are recorded C#-side as Action "ran"
/// (idempotent re-apply — we cannot know they changed anything). <see cref="Instrumented"/> is set
/// true only once a real audit read has succeeded for the run's engine, so a run on an engine whose
/// procs do not yet emit stays honestly not-instrumented.
/// </summary>
public sealed class ChangeAuditCapture
{
    private readonly List<ChangeAuditRow> _rows = new();
    private readonly object _gate = new();
    private volatile bool _instrumented;

    public bool Instrumented => _instrumented;

    public void MarkInstrumented() => _instrumented = true;

    public void Record(string objectType, string objectName, string action, string mode = null)
    {
        lock (_gate) _rows.Add(new ChangeAuditRow(objectType, objectName, action, mode));
    }

    public void RecordRan(string objectType, string objectName) => Record(objectType, objectName, "ran");

    /// <summary>Withdraws one row recorded earlier, for a script skipped as unchanged that this run then runs after all.</summary>
    public void Retract(string objectType, string objectName, string action)
    {
        lock (_gate)
        {
            var index = _rows.FindLastIndex(r => r.ObjectType == objectType && r.ObjectName == objectName && r.Action == action);
            if (index >= 0) _rows.RemoveAt(index);
        }
    }

    public IReadOnlyList<ChangeAuditRow> Snapshot()
    {
        lock (_gate) return _rows.ToArray();
    }
}
