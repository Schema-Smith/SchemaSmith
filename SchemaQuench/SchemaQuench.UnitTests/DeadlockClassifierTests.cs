// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Npgsql;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class DeadlockClassifierTests
{
    [Test]
    public void SqlServerWrappedError_1222_IsRetryableContention()
    {
        // Reachable only since the catalog reads stopped using WITH (NOLOCK): a dirty read waits for
        // nothing, a clean one can wait behind concurrent DDL. Locale-independent on the number, because
        // the message arrives in the server's language.
        var ex = new SqlServerErrorException(1222, "Zeitüberschreitung bei Sperranforderung.");
        Assert.That(DeadlockClassifier.IsLockTimeout(ex), Is.True);
        Assert.That(DeadlockClassifier.IsRetryableContention(ex), Is.True);
    }

    [Test]
    public void SqlServerWrappedError_1222_IsNotReportedAsADeadlock()
    {
        // Retryable for the same reason, but it is not a deadlock and must not be described as one --
        // the retry message an operator reads says which contention it hit.
        var ex = new SqlServerErrorException(1222, "Lock request time out period exceeded.");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.False);
    }

    [Test]
    public void AnUnrelatedSqlError_IsNotRetryableContention()
    {
        // Guards the widening: only 1205 and 1222 are transient. Re-running anything else just repeats
        // a real failure and hides it behind attempts.
        var ex = new SqlServerErrorException(2627, "Violation of PRIMARY KEY constraint.");
        Assert.That(DeadlockClassifier.IsRetryableContention(ex), Is.False);
    }

    [Test]
    public void Null_IsNotDeadlock()
    {
        Assert.That(DeadlockClassifier.IsDeadlock(null), Is.False);
    }

    [Test]
    public void SqlServerWrappedError_1205_IsDeadlock()
    {
        // The table-quench connection surfaces deadlocks via InfoMessage wrapped in
        // SqlServerErrorException, preserving the 1205 number locale-independently.
        var ex = new SqlServerErrorException(1205, "Es ist eine Deadlocksituation aufgetreten.");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.True);
    }

    [Test]
    public void SqlServerWrappedError_NonDeadlockNumber_IsNotDeadlock()
    {
        var ex = new SqlServerErrorException(50000, "Custom validation failed via RAISERROR");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.False);
    }

    [Test]
    public void PostgresException_DeadlockState_40P01_IsDeadlock()
    {
        var ex = new PostgresException("deadlock detected", "ERROR", "ERROR",
            PostgresErrorCodes.DeadlockDetected);
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.True);
    }

    [Test]
    public void PostgresException_NonDeadlockState_IsNotDeadlock()
    {
        var ex = new PostgresException("relation does not exist", "ERROR", "ERROR",
            PostgresErrorCodes.UndefinedTable);
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.False);
    }

    [Test]
    public void SqlServerEnglishMessage_IsDeadlock()
    {
        // The thrown-SqlException path can't be constructed in a unit test, but the canonical
        // English message must classify via the message fallback regardless of type.
        var ex = new Exception(
            "Transaction (Process ID 55) was deadlocked on lock resources with another process " +
            "and has been chosen as the deadlock victim. Rerun the transaction.");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.True);
    }

    [Test]
    public void MySqlEnglishMessage_IsDeadlock()
    {
        var ex = new Exception("Deadlock found when trying to get lock; try restarting transaction");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.True);
    }

    [Test]
    public void UnrelatedError_IsNotDeadlock()
    {
        var ex = new Exception("syntax error at or near \"SELCT\"");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.False);
    }

    [Test]
    public void DeadlockInInnerException_IsDeadlock()
    {
        var inner = new PostgresException("deadlock detected", "ERROR", "ERROR",
            PostgresErrorCodes.DeadlockDetected);
        var outer = new Exception("Quench step failed", inner);
        Assert.That(DeadlockClassifier.IsDeadlock(outer), Is.True);
    }

    [Test]
    public void NonDeadlockChain_IsNotDeadlock()
    {
        var inner = new InvalidOperationException("connection reset");
        var outer = new Exception("Quench step failed", inner);
        Assert.That(DeadlockClassifier.IsDeadlock(outer), Is.False);
    }
}
