// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SchemaQuench;

/// <summary>
/// Bounded thread-pool dispatcher for schema-template fan-out work units (design §5.2).
///
/// <para><b>Single-use.</b> Each instance dispatches exactly one batch of work. Calling
/// <see cref="Run"/> a second time on the same instance throws — wrapping <see cref="Run"/>
/// in a retry loop is a misuse pattern, since the queues drain on the first call and a second
/// call would otherwise silently no-op. Construct a fresh dispatcher per dispatch.</para>
///
/// <para><b>Failure modes (design §5.5).</b> Controlled by the <c>continueOnFailure</c>
/// constructor parameter:</para>
/// <list type="bullet">
///   <item><description><b>Continue mode (<c>continueOnFailure = true</c>, default for
///   schema templates):</b> a failing callback records the exception and keeps dispatching
///   remaining work units. At the end of <see cref="Run"/>, all failures are surfaced as an
///   <see cref="AggregateException"/>. Exit code is the caller's responsibility.</description></item>
///   <item><description><b>Abort mode (<c>continueOnFailure = false</c>):</b> the first
///   failure sets an abort flag; no new work units are dequeued. In-flight units drain
///   naturally (no aggressive <c>IDbCommand.Cancel()</c> — partial-transaction risk too high).
///   Failures are surfaced as an <see cref="AggregateException"/>.</description></item>
/// </list>
///
/// <para><b>Scheduling model.</b> Each work unit's template name maps via the
/// <c>allowParallel</c> dictionary to one of two queues:</para>
/// <list type="bullet">
///   <item><description><b>Parallel queue (default):</b> a single shared FIFO. Up to
///   <c>maxThreads</c> workers pull from this queue concurrently.</description></item>
///   <item><description><b>Per-template serial queue:</b> templates with
///   <c>AllowParallel: false</c> get their own queue. At most one of that template's units runs
///   at a time, but parallel-eligible units from other templates may run alongside. This is the
///   "this template touches a shared resource, do not parallelize" escape hatch.</description></item>
/// </list>
///
/// <para><b>Choice rationale (vs <c>TaskQueueManager&lt;T&gt;</c>).</b> The codebase already
/// has <c>Schema.Utility.TaskQueueManager&lt;T&gt;</c>, which is used elsewhere for plain
/// bounded-concurrency work (e.g., table token resolution in <c>Template.cs</c> and database
/// fan-out in <c>ProductQuench.EnumerateAndProvisionWorkUnitsForServer</c>). It was considered for this
/// dispatcher and rejected because (a) it silently swallows worker exceptions (no fail-loud path),
/// and (b) it has no notion of per-template serial sub-queues. Wrapping it to add both behaviors
/// would be more code than implementing the focused dispatcher directly, and would obscure the
/// simpler scheduling model this dispatcher needs.</para>
/// </summary>
public sealed class WorkUnitDispatcher
{
    /// <summary>
    /// Ceiling on how long a worker blocks waiting to be pulsed. Not the wake-up mechanism — a pulse wakes
    /// a worker immediately — but the safety net that keeps a missed pulse from hanging a deploy forever.
    /// See the wait site in <c>WorkerLoop</c> for the invariant it is insuring against.
    /// </summary>
    private const int MissedPulseSafetyNetMs = 1000;

    private readonly int _maxThreads;
    private readonly Action<WorkUnit> _callback;
    private readonly IReadOnlyDictionary<string, bool> _allowParallel;
    private readonly Queue<WorkUnit> _parallelQueue;
    private readonly Dictionary<string, Queue<WorkUnit>> _serialQueues;
    private readonly HashSet<string> _serialBusy = new();
    private readonly object _lock = new();
    private readonly List<Exception> _failures = new();
    private readonly bool _continueOnFailure;
    private bool _abort;
    private bool _hasRun;

    /// <summary>
    /// Creates a dispatcher that will execute <paramref name="callback"/> for each work unit in
    /// <paramref name="units"/>, with at most <paramref name="maxThreads"/> running concurrently.
    /// </summary>
    /// <param name="units">The full flat list of work units enumerated by the caller.</param>
    /// <param name="maxThreads">Maximum concurrent workers. Coerced to a minimum of 1.</param>
    /// <param name="allowParallel">
    /// Per-template parallelism: missing templates default to <c>true</c> (parallel-eligible).
    /// Templates mapped to <c>false</c> get a per-template serial queue.
    /// </param>
    /// <param name="callback">Invoked once per work unit. Throwing engages the failure path.</param>
    /// <param name="continueOnFailure">
    /// When <c>true</c> (continue mode): a failing callback records the exception and continues
    /// dispatching remaining units; all failures surface together at the end via
    /// <see cref="AggregateException"/>. When <c>false</c> (abort mode, the default): the first
    /// failure sets an abort flag, no new units are dequeued, in-flight units drain naturally,
    /// and failures surface via <see cref="AggregateException"/>. Defaults to <c>false</c> to
    /// preserve backward compatibility for callers that do not pass the parameter.
    /// </param>
    public WorkUnitDispatcher(
        IEnumerable<WorkUnit> units,
        int maxThreads,
        IReadOnlyDictionary<string, bool> allowParallel,
        Action<WorkUnit> callback,
        bool continueOnFailure = false)
    {
        if (units == null) throw new ArgumentNullException(nameof(units));
        _callback = callback ?? throw new ArgumentNullException(nameof(callback));
        _allowParallel = allowParallel ?? new Dictionary<string, bool>();
        _maxThreads = maxThreads < 1 ? 1 : maxThreads;
        _continueOnFailure = continueOnFailure;

        _parallelQueue = new Queue<WorkUnit>();
        _serialQueues = new Dictionary<string, Queue<WorkUnit>>();

        foreach (var unit in units)
        {
            if (IsSerial(unit.TemplateName))
            {
                if (!_serialQueues.TryGetValue(unit.TemplateName, out var q))
                {
                    q = new Queue<WorkUnit>();
                    _serialQueues[unit.TemplateName] = q;
                }
                q.Enqueue(unit);
            }
            else
            {
                _parallelQueue.Enqueue(unit);
            }
        }
    }

    /// <summary>
    /// Synchronously dispatches all work units to the bounded worker pool and returns when
    /// every unit has either completed or — in the failure path — every in-flight unit has
    /// drained. Throws an <see cref="AggregateException"/> wrapping the first callback failure
    /// (and any additional in-flight failures observed while draining).
    /// <para>Single-use: a second call on the same instance throws
    /// <see cref="InvalidOperationException"/>.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="Run"/> is called more than once on the same instance.</exception>
    public void Run()
    {
        lock (_lock)
        {
            if (_hasRun)
            {
                throw new InvalidOperationException(
                    "WorkUnitDispatcher is single-use; create a new instance per Run.");
            }
            _hasRun = true;
        }

        if (TotalRemaining() == 0) return;

        var tasks = new List<Task>(_maxThreads);
        for (var i = 0; i < _maxThreads; i++)
        {
            tasks.Add(Task.Run(WorkerLoop));
        }

        Task.WaitAll(tasks.ToArray());

        if (_failures.Count > 0)
        {
            var mode = _continueOnFailure ? "continue mode; all units attempted" : "abort mode; in-flight units drained";
            throw new AggregateException(
                $"One or more work units failed ({mode}).",
                _failures);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            WorkUnit unit;
            string serialClaim;
            lock (_lock)
            {
                if (_abort) return;
                if (!TryDequeue(out unit, out serialClaim))
                {
                    // No work currently available — but a serial queue may unlock when a sibling
                    // unit finishes. Exit only when nothing remains anywhere.
                    if (TotalRemaining() == 0) return;

                    // THE INVARIANT THIS WAIT DEPENDS ON: every state change that could make work
                    // dequeuable must PulseAll under _lock. Today the unit-completion `finally` and the
                    // failure `catch` both do, which is what makes the wait correct.
                    //
                    // The timeout is not how a worker is normally woken — a pulse does that immediately.
                    // It exists because the invariant is not enforced by anything: a later edit that adds
                    // a path releasing a claim (or clearing a queue) without pulsing would, with an
                    // unbounded wait, hang the DEPLOY permanently. Bounded, the same mistake degrades to a
                    // one-second poll. A hang is the worst failure this product has; a slow poll is not.
                    Monitor.Wait(_lock, MissedPulseSafetyNetMs);
                    continue;
                }
            }

            try
            {
                _callback(unit);
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    _failures.Add(ex);
                    if (!_continueOnFailure)
                    {
                        _abort = true;
                        // Explicitly abandon queued work on abort. "Queued units don't run after a
                        // failure" is then a property of the empty queues, not an emergent consequence
                        // of the WorkerLoop short-circuit alone — a future restructure of the loop that
                        // drops the _abort check cannot silently regress it.
                        _parallelQueue.Clear();
                        foreach (var q in _serialQueues.Values) q.Clear();
                    }
                    Monitor.PulseAll(_lock);
                }
            }
            finally
            {
                lock (_lock)
                {
                    if (serialClaim != null) _serialBusy.Remove(serialClaim);
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    private bool TryDequeue(out WorkUnit unit, out string serialClaim)
    {
        // Prefer parallel work to keep the pool saturated while serial queues unblock naturally.
        if (_parallelQueue.Count > 0)
        {
            unit = _parallelQueue.Dequeue();
            serialClaim = null;
            return true;
        }

        foreach (var kvp in _serialQueues)
        {
            if (_serialBusy.Contains(kvp.Key)) continue;
            if (kvp.Value.Count == 0) continue;
            unit = kvp.Value.Dequeue();
            serialClaim = kvp.Key;
            _serialBusy.Add(serialClaim);
            return true;
        }

        unit = default;
        serialClaim = null;
        return false;
    }

    private int TotalRemaining()
    {
        var n = _parallelQueue.Count;
        foreach (var q in _serialQueues.Values) n += q.Count;
        return n;
    }

    private bool IsSerial(string templateName)
    {
        // Missing entries default to parallel-eligible.
        return _allowParallel.TryGetValue(templateName, out var allow) && !allow;
    }
}
