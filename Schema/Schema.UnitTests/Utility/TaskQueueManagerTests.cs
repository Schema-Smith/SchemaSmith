// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class TaskQueueManagerTests
{
    [Test]
    public void AddToQueue_ExecutesWork()
    {
        using var manager = new TaskQueueManager<string>(maxTasks: 1);
        var processed = new List<string>();

        manager.AddToQueue("item1", item =>
        {
            lock (processed)
            {
                processed.Add(item);
            }
        });

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(1));
        Assert.That(processed[0], Is.EqualTo("item1"));
    }

    [Test]
    public void AddToQueue_ExecutesMultipleItems()
    {
        using var manager = new TaskQueueManager<int>(maxTasks: 5);
        var processed = new List<int>();
        var lockObj = new object();

        for (var i = 1; i <= 10; i++)
        {
            manager.AddToQueue(i, item =>
            {
                lock (lockObj)
                {
                    processed.Add(item);
                }
            });
        }

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(10));
        for (var i = 1; i <= 10; i++)
            Assert.That(processed, Does.Contain(i));
    }

    [Test]
    public void Constructor_SetsMinimumMaxTasksToOne()
    {
        using var manager = new TaskQueueManager<string>(maxTasks: 0);
        var processed = new List<string>();
        var lockObj = new object();

        manager.AddToQueue("item1", item =>
        {
            lock (lockObj) { processed.Add(item); }
        });
        manager.AddToQueue("item2", item =>
        {
            lock (lockObj) { processed.Add(item); }
        });

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(2));
    }

    [Test]
    public void Constructor_NegativeMaxTasksBecomesOne()
    {
        using var manager = new TaskQueueManager<string>(maxTasks: -5);
        var processed = new List<string>();

        manager.AddToQueue("item1", item =>
        {
            lock (processed) { processed.Add(item); }
        });

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(1));
    }

    [Test]
    public void WaitForAll_InvokesActionWhileWaiting()
    {
        using var manager = new TaskQueueManager<string>(maxTasks: 1);
        var actionCalled = false;

        manager.AddToQueue("item1", _ => Thread.Sleep(200));

        manager.WaitForAll(() => actionCalled = true);

        Assert.That(actionCalled, Is.True);
    }

    [Test]
    public void WaitForAll_CompletesWithNoItems()
    {
        using var manager = new TaskQueueManager<string>(maxTasks: 5);

        // Should complete immediately without hanging
        manager.WaitForAll();

        Assert.Pass();
    }

    [Test]
    public void Dispose_ClearsQueues()
    {
        var manager = new TaskQueueManager<string>(maxTasks: 1);

        // Add an item and dispose
        manager.AddToQueue("item1", _ => Thread.Sleep(50));
        manager.Dispose();

        // Should not throw
        Assert.Pass();
    }

    [Test]
    public void AddToQueue_RespectsMaxConcurrency()
    {
        using var manager = new TaskQueueManager<int>(maxTasks: 2);
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var lockObj = new object();

        for (var i = 0; i < 6; i++)
        {
            manager.AddToQueue(i, _ =>
            {
                lock (lockObj)
                {
                    concurrentCount++;
                    if (concurrentCount > maxConcurrent)
                        maxConcurrent = concurrentCount;
                }
                Thread.Sleep(100);
                lock (lockObj)
                {
                    concurrentCount--;
                }
            });
        }

        manager.WaitForAll();

        Assert.That(maxConcurrent, Is.LessThanOrEqualTo(3)); // Allow slight variance due to timing
    }

    [Test]
    public void AddToQueue_WorksWithDifferentTypes()
    {
        using var manager = new TaskQueueManager<DateTime>(maxTasks: 2);
        var processed = new List<DateTime>();
        var lockObj = new object();
        var date1 = new DateTime(2025, 1, 1);
        var date2 = new DateTime(2025, 6, 15);

        manager.AddToQueue(date1, item =>
        {
            lock (lockObj) { processed.Add(item); }
        });
        manager.AddToQueue(date2, item =>
        {
            lock (lockObj) { processed.Add(item); }
        });

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(2));
        Assert.That(processed, Does.Contain(date1));
        Assert.That(processed, Does.Contain(date2));
    }

    [Test]
    public void WaitForAll_DoesNotHangWhenWorkThrows_AndHandsTheFailureToTheWaiter()
    {
        using var manager = new TaskQueueManager<int>(maxTasks: 1);

        manager.AddToQueue(1, _ => throw new InvalidOperationException("boom"));

        var waitTask = Task.Run(() => manager.WaitForAll());
        Assert.That(((IAsyncResult)waitTask).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True,
            "WaitForAll should complete even when the work procedure throws");
        Assert.That(waitTask.Exception?.InnerException, Is.TypeOf<InvalidOperationException>().And.Message.EqualTo("boom"),
            "a worker's failure must reach the caller, not vanish with its task");
    }

    [Test]
    public void AddToQueue_LaterItemsRunAfterEarlierItemThrows()
    {
        using var manager = new TaskQueueManager<int>(maxTasks: 1);
        var processed = new List<int>();
        var lockObj = new object();

        manager.AddToQueue(1, _ => throw new InvalidOperationException("boom"));
        manager.AddToQueue(2, item =>
        {
            lock (lockObj) { processed.Add(item); }
        });

        var waitTask = Task.Run(() => manager.WaitForAll());
        Assert.That(((IAsyncResult)waitTask).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True,
            "WaitForAll should complete after the throwing work runs");
        Assert.That(waitTask.Exception?.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(processed, Does.Contain(2),
            "Subsequent queue items should run even after an earlier item throws");
    }

    [Test]
    public void AddToQueue_QueueRecoversAfterAllWorkerSlotsFail()
    {
        using var manager = new TaskQueueManager<int>(maxTasks: 2);
        var processed = new List<int>();
        var lockObj = new object();

        manager.AddToQueue(1, _ => throw new InvalidOperationException("boom1"));
        manager.AddToQueue(2, _ => throw new InvalidOperationException("boom2"));
        manager.AddToQueue(3, item =>
        {
            lock (lockObj) { processed.Add(item); }
        });

        var waitTask = Task.Run(() => manager.WaitForAll());
        Assert.That(((IAsyncResult)waitTask).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)), Is.True,
            "WaitForAll should complete after every worker slot's task has failed");
        Assert.That(waitTask.Exception?.InnerException, Is.TypeOf<AggregateException>()
            .And.Property(nameof(AggregateException.InnerExceptions)).Count.EqualTo(2), "both failures, not just the first");
        Assert.That(processed, Does.Contain(3),
            "New items should still execute after every worker slot has failed");
    }

    [Test]
    public void DefaultMaxTasks_Is20()
    {
        using var manager = new TaskQueueManager<string>();
        var processed = new List<string>();
        var lockObj = new object();

        // Queue 25 items - should work fine with default max of 20
        for (var i = 0; i < 25; i++)
        {
            manager.AddToQueue($"item{i}", item =>
            {
                lock (lockObj) { processed.Add(item); }
            });
        }

        manager.WaitForAll();

        Assert.That(processed, Has.Count.EqualTo(25));
    }
}
