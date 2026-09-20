// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class WorkUnitDispatcherTests
{
    [Test]
    public void Run_AllUnitsProcessed_ExactlyOnce()
    {
        var units = new[]
        {
            new WorkUnit("s", "db1", "Core", ""),
            new WorkUnit("s", "db2", "Core", ""),
            new WorkUnit("s", "db3", "Core", "")
        };
        var processed = new ConcurrentBag<WorkUnit>();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 2, allowParallel: new Dictionary<string, bool>(),
            unit => processed.Add(unit));

        dispatcher.Run();

        Assert.That(processed.OrderBy(u => u.DatabaseName).ToList(),
            Is.EqualTo(units.OrderBy(u => u.DatabaseName).ToList()));
    }

    [Test]
    public void Run_RespectsMaxThreads_ParallelEligibleWork()
    {
        const int maxThreads = 2;
        const int unitCount = 6;
        var units = Enumerable.Range(0, unitCount)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();

        var inFlight = 0;
        var peak = 0;
        var lockObj = new object();
        var release = new ManualResetEventSlim(false);

        var dispatcher = new WorkUnitDispatcher(units, maxThreads, new Dictionary<string, bool>(),
            _ =>
            {
                lock (lockObj)
                {
                    inFlight++;
                    if (inFlight > peak) peak = inFlight;
                }
                // Hold each worker briefly so concurrency is observable.
                release.Wait(TimeSpan.FromSeconds(2));
                lock (lockObj) { inFlight--; }
            });

        // Release after we've had a chance to fill up the pool.
        var releaser = new Thread(() =>
        {
            Thread.Sleep(150);
            release.Set();
        }) { IsBackground = true };
        releaser.Start();

        dispatcher.Run();
        releaser.Join();

        Assert.That(peak, Is.LessThanOrEqualTo(maxThreads),
            $"Peak concurrency {peak} exceeded MaxThreads {maxThreads}.");
        Assert.That(peak, Is.GreaterThan(1), "Expected at least 2 concurrent workers to confirm parallelism is active.");
    }

    [Test]
    public void Run_SerialTemplate_TwoUnitsRunSequentially()
    {
        var units = new[]
        {
            new WorkUnit("s", "db", "Serial", "tenant_a"),
            new WorkUnit("s", "db", "Serial", "tenant_b")
        };
        var allowParallel = new Dictionary<string, bool> { ["Serial"] = false };

        var firstStarted = new ManualResetEventSlim(false);
        var firstHold = new ManualResetEventSlim(false);
        var anyOverlap = false;
        var inFlight = 0;
        var lockObj = new object();
        var first = true;

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 4, allowParallel,
            _ =>
            {
                bool isFirst;
                lock (lockObj)
                {
                    isFirst = first;
                    first = false;
                    inFlight++;
                    if (inFlight > 1) anyOverlap = true;
                }

                if (isFirst)
                {
                    firstStarted.Set();
                    firstHold.Wait(TimeSpan.FromSeconds(2));
                }

                lock (lockObj) { inFlight--; }
            });

        var releaser = new Thread(() =>
        {
            // Wait until the first serial unit has started.
            firstStarted.Wait(TimeSpan.FromSeconds(2));
            // Give the dispatcher a window to incorrectly start the second unit if it would.
            Thread.Sleep(150);
            firstHold.Set();
        }) { IsBackground = true };
        releaser.Start();

        dispatcher.Run();
        releaser.Join();

        Assert.That(anyOverlap, Is.False,
            "Two AllowParallel=false units of the same template ran concurrently.");
    }

    [Test]
    public void Run_MixedSerialAndParallel_ParallelWorkProgressesAlongsideSerial()
    {
        // One held serial unit + several parallel units. Each parallel unit also synchronizes
        // on the serial-started event before completing, so the dispatcher cannot "drain" the
        // parallel queue before the serial unit takes a slot. With maxThreads=4, we expect at
        // least one parallel unit running concurrently with the in-flight serial unit.
        var units = new List<WorkUnit>
        {
            new("s", "db", "Serial", "tenant_a"),
            new("s", "db1", "Parallel", ""),
            new("s", "db2", "Parallel", ""),
            new("s", "db3", "Parallel", "")
        };
        var allowParallel = new Dictionary<string, bool> { ["Serial"] = false, ["Parallel"] = true };

        var serialStarted = new ManualResetEventSlim(false);
        var releaseAll = new ManualResetEventSlim(false);
        var concurrent = 0;
        var peakConcurrent = 0;
        var lockObj = new object();
        var serialInFlight = false;
        var parallelStartedWhileSerialHeld = 0;

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 4, allowParallel,
            unit =>
            {
                if (unit.TemplateName == "Serial")
                {
                    lock (lockObj) { serialInFlight = true; }
                    serialStarted.Set();
                    releaseAll.Wait(TimeSpan.FromSeconds(2));
                    lock (lockObj) { serialInFlight = false; }
                }
                else
                {
                    // Wait until the serial unit is in flight before doing the concurrency check,
                    // so the test is deterministic regardless of which worker grabs which unit first.
                    serialStarted.Wait(TimeSpan.FromSeconds(2));
                    lock (lockObj)
                    {
                        concurrent++;
                        if (concurrent > peakConcurrent) peakConcurrent = concurrent;
                        if (serialInFlight) parallelStartedWhileSerialHeld++;
                    }
                    // Hold briefly so concurrent peak is observable.
                    Thread.Sleep(50);
                    lock (lockObj) { concurrent--; }
                }
            });

        var releaser = new Thread(() =>
        {
            serialStarted.Wait(TimeSpan.FromSeconds(2));
            // Give the dispatcher time to pick up parallel work alongside the held serial unit.
            Thread.Sleep(200);
            releaseAll.Set();
        }) { IsBackground = true };
        releaser.Start();

        dispatcher.Run();
        releaser.Join();

        Assert.That(parallelStartedWhileSerialHeld, Is.GreaterThan(0),
            "Parallel-eligible units should run alongside an in-flight serial unit.");
    }

    [Test]
    public void Run_CallbackThrows_PropagatesAndStopsAcceptingNewWork()
    {
        var units = new List<WorkUnit>
        {
            new("s", "db1", "Core", ""),
            new("s", "db2", "Core", ""),
            new("s", "db3", "Core", ""),
            new("s", "db4", "Core", ""),
            new("s", "db5", "Core", ""),
            new("s", "db6", "Core", "")
        };
        var startedAfterFailure = 0;
        var failureSignal = new ManualResetEventSlim(false);
        var lockObj = new object();
        var bailed = false;

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, new Dictionary<string, bool>(),
            unit =>
            {
                if (unit.DatabaseName == "db1")
                {
                    // safe: maxThreads=1, all callback invocations run on the single worker thread
                    bailed = true;
                    failureSignal.Set();
                    throw new InvalidOperationException("boom");
                }
                lock (lockObj)
                {
                    if (bailed) startedAfterFailure++;
                }
            });

        Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(startedAfterFailure, Is.EqualTo(0),
            "Dispatcher should not start new units after a callback fails.");
        Assert.That(failureSignal.IsSet, Is.True);
    }

    [Test]
    public void Run_AbortMode_DrainsQueuedParallelAndSerialWork()
    {
        // On abort, both the shared parallel queue and every per-template serial queue are cleared,
        // so no queued unit — parallel or serial — runs after the first failure. Guards the explicit
        // drain independently of the worker-loop abort short-circuit; the existing abort tests queue
        // only parallel work, so the serial-queue drain would otherwise be uncovered. maxThreads=1
        // makes the dispatch order deterministic: the throwing parallel unit is dequeued first.
        var units = new List<WorkUnit>
        {
            new("s", "db1", "Core", ""),    // throws first (parallel, dequeued first)
            new("s", "db2", "Core", ""),    // queued parallel — must not run
            new("s", "db3", "Core", ""),    // queued parallel — must not run
            new("s", "db", "Serial", "t1"), // queued serial — must not run
            new("s", "db", "Serial", "t2")  // queued serial — must not run
        };
        var allowParallel = new Dictionary<string, bool> { ["Serial"] = false };
        var ranAfterFailure = new ConcurrentBag<string>();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, allowParallel,
            unit =>
            {
                if (unit is { DatabaseName: "db1", TemplateName: "Core" })
                    throw new InvalidOperationException("boom");
                ranAfterFailure.Add($"{unit.TemplateName}/{unit.DatabaseName}");
            });

        Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(ranAfterFailure, Is.Empty,
            "Abort must drain both the parallel queue and all serial queues — no queued unit may run after the first failure.");
    }

    [Test]
    public void Run_MissingTemplateKeyInAllowParallel_DefaultsToParallel()
    {
        // No "Core" entry in the AllowParallel map — should default to allow parallel.
        var units = Enumerable.Range(0, 4)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();

        var peak = 0;
        var inFlight = 0;
        var lockObj = new object();
        var release = new ManualResetEventSlim(false);

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 3, allowParallel: new Dictionary<string, bool>(),
            _ =>
            {
                lock (lockObj)
                {
                    inFlight++;
                    if (inFlight > peak) peak = inFlight;
                }
                release.Wait(TimeSpan.FromSeconds(2));
                lock (lockObj) { inFlight--; }
            });

        var releaser = new Thread(() =>
        {
            Thread.Sleep(150);
            release.Set();
        }) { IsBackground = true };
        releaser.Start();

        dispatcher.Run();
        releaser.Join();

        Assert.That(peak, Is.GreaterThan(1),
            "Templates with no AllowParallel entry should default to parallel-eligible.");
    }

    [Test]
    public void Run_NoUnits_ReturnsImmediately()
    {
        var dispatcher = new WorkUnitDispatcher(
            new List<WorkUnit>(), maxThreads: 4, new Dictionary<string, bool>(),
            _ => Assert.Fail("Callback should not be invoked for an empty work set."));

        Assert.DoesNotThrow(() => dispatcher.Run());
    }

    [Test]
    public void Run_SecondCallOnSameInstance_Throws()
    {
        // Dispatcher is single-use — wrapping Run() in a retry loop would otherwise silently
        // no-op because the queues drain on the first call.
        var units = new[] { new WorkUnit("s", "db1", "Core", "") };
        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, new Dictionary<string, bool>(),
            _ => { });

        dispatcher.Run();

        var ex = Assert.Throws<InvalidOperationException>(() => dispatcher.Run());
        Assert.That(ex!.Message, Does.Contain("single-use"));
    }

    [Test]
    public void Run_SecondCallOnSameInstance_EvenWithEmptyUnits_Throws()
    {
        // Even when the first Run is a no-op (no units), a second call still throws — the
        // single-use guard catches the caller's intent independently of whether any work
        // happened on the first run.
        var dispatcher = new WorkUnitDispatcher(
            new List<WorkUnit>(), maxThreads: 1, new Dictionary<string, bool>(),
            _ => { });

        dispatcher.Run();

        Assert.Throws<InvalidOperationException>(() => dispatcher.Run());
    }

    // -----------------------------------------------------------------------
    // continueOnFailure = true (slice 4 continue mode)
    // -----------------------------------------------------------------------

    [Test]
    public void Run_ContinueMode_AllUnitsAttempted_EvenAfterFailure()
    {
        // With continueOnFailure=true, a single failing unit does not stop the dispatcher;
        // all remaining units still run. The AggregateException surfaces at the end.
        var units = Enumerable.Range(1, 5)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();
        var processed = new ConcurrentBag<string>();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, new Dictionary<string, bool>(),
            unit =>
            {
                if (unit.DatabaseName == "db2")
                    throw new InvalidOperationException("boom");
                processed.Add(unit.DatabaseName);
            },
            continueOnFailure: true);

        var ex = Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(ex!.InnerExceptions.Count, Is.EqualTo(1));

        // All 4 non-failing units were processed.
        Assert.That(processed.Count, Is.EqualTo(4),
            "Continue mode must attempt all units even after one fails.");
    }

    [Test]
    public void Run_ContinueMode_MultipleFailures_AllCollected()
    {
        // With continueOnFailure=true, every exception from every failing unit is collected
        // in the AggregateException — none are silently swallowed.
        var units = Enumerable.Range(1, 5)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, new Dictionary<string, bool>(),
            unit =>
            {
                if (unit.DatabaseName is "db1" or "db3")
                    throw new InvalidOperationException($"boom {unit.DatabaseName}");
            },
            continueOnFailure: true);

        var ex = Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(ex!.InnerExceptions.Count, Is.EqualTo(2),
            "Both failures must appear in the AggregateException.");
        Assert.That(ex.InnerExceptions.Select(e => e.Message),
            Is.EquivalentTo(new[] { "boom db1", "boom db3" }));
    }

    [Test]
    public void Run_ContinueMode_ParallelFailures_AllCollectedThreadSafe()
    {
        // Three workers throw simultaneously — no exception should be lost to a race on the
        // collection path. maxThreads:3 with 6 units where the first three fail concurrently.
        const int failingCount = 3;
        var units = Enumerable.Range(1, 6)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();

        // A barrier that holds each failing worker until all three are ready to throw at once,
        // maximising the chance of concurrent writes to the exception collection.
        var barrier = new Barrier(failingCount);

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 3, new Dictionary<string, bool>(),
            unit =>
            {
                var n = int.Parse(unit.DatabaseName[2..]);
                if (n <= failingCount)
                {
                    barrier.SignalAndWait(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException($"boom {unit.DatabaseName}");
                }
            },
            continueOnFailure: true);

        var ex = Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(ex!.InnerExceptions.Count, Is.EqualTo(failingCount),
            "All three concurrent failures must be captured — none lost to a collection race.");
    }

    [Test]
    public void Run_AbortMode_IsDefaultBehavior()
    {
        // The default (no continueOnFailure param) still aborts on first failure —
        // backward-compatible, no caller changes needed for abort-mode callers.
        var units = Enumerable.Range(1, 4)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();
        var processed = new ConcurrentBag<string>();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 1, new Dictionary<string, bool>(),
            unit =>
            {
                if (unit.DatabaseName == "db1")
                    throw new InvalidOperationException("boom");
                processed.Add(unit.DatabaseName);
            });

        Assert.Throws<AggregateException>(() => dispatcher.Run());
        Assert.That(processed.Count, Is.EqualTo(0),
            "Abort mode (default) must not process any units after the first failure.");
    }

    [Test]
    public void Run_ContinueMode_NoFailures_CompletesNormally()
    {
        // When continueOnFailure=true but nothing fails, Run() returns without throwing.
        var units = Enumerable.Range(1, 3)
            .Select(i => new WorkUnit("s", $"db{i}", "Core", ""))
            .ToList();
        var processed = new ConcurrentBag<string>();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 2, new Dictionary<string, bool>(),
            unit => processed.Add(unit.DatabaseName),
            continueOnFailure: true);

        Assert.DoesNotThrow(() => dispatcher.Run());
        Assert.That(processed.Count, Is.EqualTo(3));
    }

    /// <summary>
    /// Contention stress: many units across several serial templates plus a parallel pool, run on more
    /// threads than templates so workers are forced to block on serial claims and be woken by siblings.
    /// <para>This exists because the dispatcher's wait is only correct while EVERY state change that can
    /// make work dequeuable pulses under the lock — an invariant nothing enforces. Reading the code cannot
    /// prove the absence of a deadlock; running it under contention can at least catch one. The wait is
    /// bounded, so a genuine missed pulse shows up here as a test that takes far longer than it should
    /// rather than one that hangs the suite forever, which is the whole point of bounding it.</para>
    /// </summary>
    [Test]
    [Repeat(5)]
    public void Run_UnderSerialAndParallelContention_CompletesEveryUnitWithoutDeadlock()
    {
        var units = new List<WorkUnit>();
        for (var i = 0; i < 40; i++)
        {
            units.Add(new WorkUnit("s", $"db{i}", "SerialA", ""));
            units.Add(new WorkUnit("s", $"db{i}", "SerialB", ""));
            units.Add(new WorkUnit("s", $"db{i}", "Parallel", ""));
        }

        // SerialA and SerialB may each run only one unit at a time; Parallel is unconstrained.
        var allowParallel = new Dictionary<string, bool>
        {
            ["SerialA"] = false,
            ["SerialB"] = false,
            ["Parallel"] = true
        };

        var processed = new ConcurrentBag<WorkUnit>();
        var inFlight = new Dictionary<string, int>();
        var concurrencyViolations = new ConcurrentBag<string>();
        var gate = new object();

        var dispatcher = new WorkUnitDispatcher(units, maxThreads: 8, allowParallel, unit =>
        {
            lock (gate)
            {
                inFlight.TryGetValue(unit.TemplateName, out var n);
                inFlight[unit.TemplateName] = n + 1;
                // A serial template must never have two units in flight at once.
                if (n + 1 > 1 && !allowParallel[unit.TemplateName])
                    concurrencyViolations.Add(unit.TemplateName);
            }

            Thread.Sleep(1);
            processed.Add(unit);

            lock (gate) { inFlight[unit.TemplateName] -= 1; }
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        dispatcher.Run();
        watch.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(processed.Count, Is.EqualTo(units.Count),
                "every queued unit must run exactly once, however the workers interleaved");
            Assert.That(concurrencyViolations, Is.Empty,
                "a template declared serial must never have two units in flight at once");
            // 120 units of ~1ms on 8 threads is well under a second. The bounded wait is 1s, so a run that
            // needed the safety net to make progress lands far above this -- which is what we want to catch.
            Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(20),
                "the run took long enough to suggest workers were waiting out the missed-pulse timeout "
                + "rather than being pulsed -- the pulse/wait pairing has regressed");
        });
    }
}
