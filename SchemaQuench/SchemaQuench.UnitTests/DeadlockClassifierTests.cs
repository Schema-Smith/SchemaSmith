// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using MySqlConnector;
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

    // ---- lock-wait timeout on the other two engines -------------------------
    // The retry shipped recognising only SQL Server's 1222, which left the engine MOST likely to raise a
    // lock-wait timeout as the one that never retried: SQL Server's LOCK_TIMEOUT is infinite by default
    // and PostgreSQL's lock_timeout is 0, but InnoDB's innodb_lock_wait_timeout is 50 seconds out of the
    // box, so a MySQL deploy contending with concurrent DDL reaches it with nothing configured.

    [Test]
    public void MySqlLockWaitTimeout_1205_IsALockTimeout()
    {
        // 1205 is MySQL's LOCK WAIT TIMEOUT, not its deadlock (that is 1213) -- the same number SQL
        // Server uses for a deadlock. That collision is exactly why each engine is matched on its own
        // client's typed code and never on a bare integer.
        Assert.That(DeadlockClassifier.IsMySqlLockTimeoutCode(MySqlErrorCode.LockWaitTimeout), Is.True);
    }

    [Test]
    public void MySqlLockDeadlock_1213_IsNotALockTimeout()
    {
        // Retryable either way, but it must not be classified as the wrong one: the message an operator
        // reads names the contention that was hit.
        Assert.That(DeadlockClassifier.IsMySqlLockTimeoutCode(MySqlErrorCode.LockDeadlock), Is.False);
    }

    [Test]
    public void AnUnrelatedMySqlError_IsNotALockTimeout()
    {
        Assert.That(DeadlockClassifier.IsMySqlLockTimeoutCode(MySqlErrorCode.DuplicateKeyEntry), Is.False);
    }

    // ---- the MySQL counterparts of the PostgreSQL relation-cache race ------
    // Found by the full integration suite at two workers: three failures across four runs, every one an
    // EXCEPTION rather than a wrong answer, and none of them classified as retryable.

    [Test]
    public void MySqlConcurrentDdlSkippedTable_1684_IsRetryableContention()
    {
        // "Table '...' was skipped since its definition is being modified by concurrent DDL statement".
        // The same event PostgreSQL reports as "could not open relation with OID" -- already retried
        // there, and not here, which is the single-engine gap parity is meant to catch.
        Assert.That(DeadlockClassifier.IsMySqlTransientConcurrencyCode((MySqlErrorCode)1684), Is.True);
    }

    [Test]
    public void MySqlAutoIncrementReadFailed_1467_IsRetryableContention()
    {
        // The convergence procs create and drop several AUTO_INCREMENT temp tables per call; under
        // concurrent work units that read can fail outright, and a re-run recreates them.
        Assert.That(DeadlockClassifier.IsMySqlTransientConcurrencyCode(MySqlErrorCode.AutoIncrementReadFailed), Is.True);
        Assert.That(MySqlErrorCode.AutoIncrementReadFailed, Is.EqualTo((MySqlErrorCode)1467));
    }

    [Test]
    public void AnOrdinaryMySqlError_IsNotTransientConcurrency()
    {
        // Guards the widening in the direction that matters: re-running a real failure ten times just
        // hides it behind attempts and reports the wrong cause at the end.
        Assert.Multiple(() =>
        {
            Assert.That(DeadlockClassifier.IsMySqlTransientConcurrencyCode(MySqlErrorCode.DuplicateKeyEntry), Is.False);
            Assert.That(DeadlockClassifier.IsMySqlTransientConcurrencyCode(MySqlErrorCode.NoSuchTable), Is.False);
            Assert.That(DeadlockClassifier.IsMySqlTransientConcurrencyCode(MySqlErrorCode.ParseError), Is.False);
        });
    }

    [Test]
    public void PostgresException_LockNotAvailable_55P03_IsRetryableContention()
    {
        var ex = new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", "55P03");
        Assert.That(DeadlockClassifier.IsLockTimeout(ex), Is.True);
        Assert.That(DeadlockClassifier.IsRetryableContention(ex), Is.True);
    }

    [Test]
    public void PostgresException_QueryCanceled_57014_IsNotRetryableContention()
    {
        // statement_timeout is a caller's deliberate cap on how long a statement may run, not contention.
        // Retrying it fights the intent that set it, and a statement slow enough to exceed the cap would
        // exceed it again on all twenty attempts.
        var ex = new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", "57014");
        Assert.That(DeadlockClassifier.IsLockTimeout(ex), Is.False);
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
        // The message fallback, which is all a wrapper that lost the client's exception type leaves us.
        var ex = new Exception("Deadlock found when trying to get lock; try restarting transaction");
        Assert.That(DeadlockClassifier.IsDeadlock(ex), Is.True);
    }

    [Test]
    public void MySqlDeadlock_IsClassifiedByCode_NotOnlyByEnglishMessage()
    {
        // MySQL translates server messages (lc_messages), so relying on "Deadlock found ..." meant a
        // deploy against a non-English server classified nothing -- while SQL Server and PostgreSQL were
        // locale-independent on their codes. Proven through the enum because MySqlConnector's exception
        // cannot be constructed here; the switch arm in IsDeadlock matches this same member.
        Assert.That(MySqlErrorCode.LockDeadlock, Is.EqualTo((MySqlErrorCode)1213));
        Assert.That(DeadlockClassifier.IsMySqlLockTimeoutCode(MySqlErrorCode.LockDeadlock), Is.False);
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
