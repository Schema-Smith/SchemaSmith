// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Threading;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Re-runs a test's own call into a SchemaSmith procedure when two test workers collide on the shared database. The
/// deploy re-runs every convergence call on the same contention (<see cref="DeadlockClassifier.IsRetryableContention"/>);
/// a fixture that calls a procedure directly has to do the same, or the suite fails on an event a real deploy recovers
/// from. Anything else is rethrown at once: a retry loop that swallows real failures turns one clear error into ten
/// slow ones.
/// </summary>
internal static class TransientRetry
{
    private const int Attempts = 10;

    public static bool IsTransient(Exception e) =>
        DeadlockClassifier.IsRetryableContention(e)
        // The message forms, for an exception a test helper has wrapped.
        || e.Message.ContainsIgnoringCase("Deadlock found when trying to get lock")
        || e.Message.ContainsIgnoringCase("Lock wait timeout exceeded")
        || e.Message.ContainsIgnoringCase("is being modified by concurrent DDL statement")   // 1684
        || e.Message.ContainsIgnoringCase("Failed to read auto-increment value")             // 1467
        || e.Message.ContainsIgnoringCase("Prepared statement needs to be re-prepared");     // 1615

    // beforeRetry restores the starting state for a test whose claim depends on it: a retried deploy finds what the
    // failed attempt already created, which is a different deploy from the one the test describes.
    public static void Run(Action action, Action beforeRetry = null) => Run(() => { action(); return 0; }, beforeRetry);

    public static T Run<T>(Func<T> func, Action beforeRetry = null)
    {
        for (var attempt = 1; attempt < Attempts; attempt++)
        {
            try
            {
                return func();
            }
            // prepush-allow: generic-catch -- a test helper may have wrapped the engine's exception, so it is matched by message
            catch (Exception e) when (IsTransient(e))
            {
                // A retried call that had already changed something can explain an assertion that fails only in a full run.
                // Out as well as Progress: the console logger hides Progress, and Out lands in the test's TRX output.
                var note = $"TransientRetry: attempt {attempt} of {TestContext.CurrentContext.Test.Name} failed: {e.Message}";
                TestContext.Progress.WriteLine(note);
                TestContext.Out.WriteLine(note);
                Thread.Sleep(1000);
                beforeRetry?.Invoke();
            }
        }
        return func();
    }
}
