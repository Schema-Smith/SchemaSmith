// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Schema.Utility;

public class TaskQueueManager<T> : IDisposable
{
    /// <summary>
    /// Ceiling on how long <see cref="WaitForAll"/> blocks waiting to be pulsed. It is NOT how a waiter is
    /// normally woken — completing work pulses immediately — but the safety net that keeps a missed pulse
    /// from hanging rather than merely slowing. Matches the bound <c>WorkUnitDispatcher</c> uses for the
    /// same reason.
    /// </summary>
    public const int MissedPulseSafetyNetMs = 250;

    private readonly int _maxTasks;
    private readonly int _safetyNetMs;
    private readonly List<WorkerTask> _workingTasks = [];
    private readonly Queue<WorkerTask> _workQueue = new();
    private readonly object _lockObject = new();

    /// <param name="maxTasks">Maximum tasks in flight at once.</param>
    /// <param name="missedPulseSafetyNetMs">
    /// Upper bound on a single wait. Rarely worth overriding — see <see cref="MissedPulseSafetyNetMs"/>.
    /// </param>
    public TaskQueueManager(int maxTasks = 20, int missedPulseSafetyNetMs = MissedPulseSafetyNetMs)
    {
        _maxTasks = maxTasks;
        if (_maxTasks < 1) _maxTasks = 1;
        _safetyNetMs = missedPulseSafetyNetMs < 1 ? 1 : missedPulseSafetyNetMs;
    }

    ~TaskQueueManager() => Dispose();

    public void Dispose()
    {
        lock (_lockObject)
        {
            _workQueue.Clear();
            while (_workingTasks.Count > 0) _workingTasks.Remove(_workingTasks[0]);
        }
    }

    public delegate void TaskDelegate(T item);

    public void AddToQueue(T item, TaskDelegate workProcedure)
    {
        var aWorker = new WorkerTask(item, workProcedure, this);
        ProcessQueue();
        lock (_lockObject)
        {
            if (_workingTasks.Count < _maxTasks)
                StartTask(aWorker);
            else
                _workQueue.Enqueue(aWorker);
        }
    }

    private void TaskComplete(WorkerTask aTask)
    {
        lock (_lockObject)
        {
            _workingTasks.Remove(aTask);
            ProcessQueue();
            // THE INVARIANT WaitForAll DEPENDS ON: every state change that can drain the queue pulses under
            // _lockObject. This is the only place work finishes, so this is the only pulse needed — but a
            // future path that removes a task elsewhere must pulse too, or waiters fall back to the bound.
            Monitor.PulseAll(_lockObject);
        }
    }

    private void StartTask(WorkerTask aTask)
    {
        lock (_lockObject)
        {
            _workingTasks.Add(aTask);
            aTask.StartTask();
        }
    }

    private void ProcessQueue()
    {
        lock (_lockObject)
        {
            while (_workingTasks.Count < _maxTasks && _workQueue.Count > 0)
            {
                StartTask(_workQueue.Peek());
                _workQueue.Dequeue();
            }
        }
    }

    /// <summary>
    /// Blocks until every queued and running task has finished, waiting <see cref="_pollMilliseconds"/>
    /// between checks.
    /// <para>The interval used to be a hard-coded 100ms, which put a 100ms FLOOR on any queue holding even
    /// one item, however fast the work. Measured over the 515 shipped packages, that floor was
    /// <b>135 of the 140 seconds</b> spent loading them, while the file reading it was waiting on accounted
    /// for 0.2%. It is paid by <c>--Validate</c>, extraction, token resolution and the deploy path — not
    /// only by the tests that exposed it.</para>
    /// </summary>
    public void WaitForAll(Action action = null)
    {
        while (true)
        {
            lock (_lockObject)
            {
                ProcessQueue();
                if (_workingTasks.Count == 0 && _workQueue.Count == 0) return;
                // Monitor.Wait releases the lock while blocked, so TaskComplete can take it and pulse.
                // Bounded, so a missed pulse degrades to a poll instead of hanging.
                Monitor.Wait(_lockObject, _safetyNetMs);
            }
            action?.Invoke();
        }
    }

    private class WorkerTask(T item, TaskDelegate workProcedure, TaskQueueManager<T> owner)
    {
        private Task _task;

        private void DoWork()
        {
            // TaskComplete must run unconditionally — otherwise an unhandled exception in
            // workProcedure leaves this WorkerTask in _workingTasks forever, permanently
            // reducing the queue's effective capacity and hanging WaitForAll.
            try
            {
                workProcedure(item);
            }
            finally
            {
                owner.TaskComplete(this);
            }
        }

        public void StartTask()
        {
            if (_task != null) return;
            _task = new Task(DoWork);
            _task.Start();
        }
    }
}
