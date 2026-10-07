// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using Npgsql;
using NUnit.Framework;

namespace SchemaQuench.UnitTests;

[TestFixture]
public class ConnectionLostClassifierTests
{
    // --- Typed-branch coverage via the extracted predicates. The provider exception types
    //     (SqlException / MySqlException) are sealed with no public constructor, so the number/code
    //     sets are verified directly here — this is the per-engine "parity guarantee" (a typo in the
    //     set would otherwise compile and pass every exception-based test silently). ---

    [TestCase(233, true)]   // no process on the other end of the pipe
    [TestCase(64, true)]    // connection dropped by host
    [TestCase(20, true)]    // transport-level error
    [TestCase(10053, true)] // connection aborted
    [TestCase(10054, true)] // connection reset by peer
    [TestCase(-2, false)]   // timeout — NOT a drop
    [TestCase(1205, false)] // deadlock — DeadlockClassifier's job, not a drop
    [TestCase(2601, false)] // duplicate-key — a schema/data error
    public void SqlServerNumber_Classification(int number, bool expected)
        => Assert.That(ConnectionLostClassifier.IsSqlServerConnectionLostNumber(number), Is.EqualTo(expected));

    [TestCase(MySqlErrorCode.ServerShutdown, true)]
    [TestCase(MySqlErrorCode.UnableToConnectToHost, true)]
    [TestCase(MySqlErrorCode.LockDeadlock, false)]        // deadlock — not a drop
    [TestCase(MySqlErrorCode.DuplicateKeyEntry, false)]   // schema/data error
    public void MySqlCode_Classification(MySqlErrorCode code, bool expected)
        => Assert.That(ConnectionLostClassifier.IsMySqlConnectionLostCode(code), Is.EqualTo(expected));

    [TestCase("08006", true)]  // connection failure
    [TestCase("08003", true)]  // connection does not exist
    [TestCase("57P01", true)]  // admin shutdown
    [TestCase("57P02", true)]  // crash shutdown
    [TestCase("40P01", false)] // deadlock — not a drop
    [TestCase("42P01", false)] // undefined table — schema error
    [TestCase(null, false)]
    public void PostgresState_Classification(string sqlState, bool expected)
        => Assert.That(ConnectionLostClassifier.IsPostgresConnectionLostState(sqlState), Is.EqualTo(expected));


    // --- Positive: the server went away mid-deploy ---

    [Test] // the literal #353 surface: a provider error wrapping an inner SocketException
    public void InnerSocketException_IsConnectionLost()
    {
        var ex = new Exception("A network-related or instance-specific error occurred",
            new SocketException(0));
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    [Test] // SQL Server tears the session down mid-command before the socket error surfaces
    public void KillStateMessage_IsConnectionLost()
    {
        var ex = new Exception("Cannot continue the execution because the session is in the kill state.");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    [Test] // PostgreSQL server-initiated shutdown
    public void PostgresAdminShutdown_57P01_IsConnectionLost()
    {
        var ex = new PostgresException("terminating connection due to administrator command",
            "FATAL", "FATAL", "57P01");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    [Test] // PostgreSQL connection-exception class (08*)
    public void PostgresConnectionClass_08006_IsConnectionLost()
    {
        var ex = new PostgresException("connection failure", "FATAL", "FATAL", "08006");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    [Test] // PG/MySQL transport drop surfaces as a provider exception wrapping IOException
    public void InnerIoException_IsConnectionLost()
    {
        var ex = new Exception("Exception while reading from stream", new IOException("Unable to read data"));
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    [Test] // Npgsql and MySqlConnector surface a torn stream as EndOfStreamException
    public void InnerEndOfStream_IsConnectionLost()
    {
        var ex = new Exception("Exception while reading from stream", new EndOfStreamException());
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }

    // A local file the deploy writes (a checkpoint, a log, a debug script) failing is not the server going away, and
    // telling the user to check the server's memory sends them to the wrong place.
    [Test]
    public void MissingDirectory_IsNotConnectionLost()
    {
        var ex = new DirectoryNotFoundException("Could not find a part of the path 'a.checkpoint'.");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    [Test]
    public void MissingFile_IsNotConnectionLost()
        => Assert.That(ConnectionLostClassifier.IsConnectionLost(new FileNotFoundException("gone", "a.sql")), Is.False);

    [Test]
    public void PathTooLong_IsNotConnectionLost()
        => Assert.That(ConnectionLostClassifier.IsConnectionLost(new PathTooLongException()), Is.False);

    [TestCase(unchecked((int)0x80070020))] // ERROR_SHARING_VIOLATION: the file is in use by another process
    [TestCase(unchecked((int)0x80070021))] // ERROR_LOCK_VIOLATION
    [TestCase(unchecked((int)0x80070070))] // ERROR_DISK_FULL
    [TestCase(unchecked((int)0x80070027))] // ERROR_HANDLE_DISK_FULL
    public void LocalFileIoError_IsNotConnectionLost(int hresult)
    {
        var ex = new Exception("outer", new IOException("The process cannot access the file.", hresult));
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    // --- Negative: NOT a connection loss (schema/script errors, contention) ---

    [Test]
    public void OrdinaryError_IsNotConnectionLost()
    {
        var ex = new Exception("Invalid column name 'Widget'.");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    [Test] // a real server-returned error (undefined table) is a schema error, not a drop
    public void PostgresUndefinedTable_42P01_IsNotConnectionLost()
    {
        var ex = new PostgresException("relation \"x\" does not exist", "ERROR", "ERROR", "42P01");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    [Test] // a deadlock is retryable contention (DeadlockClassifier's job), not a connection loss
    public void Deadlock_IsNotConnectionLost()
    {
        var ex = new SqlServerErrorException(1205, "Transaction was deadlocked on lock resources");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    [Test] // PostgreSQL deadlock (40P01) must not be classified as a connection loss
    public void PostgresDeadlock_40P01_IsNotConnectionLost()
    {
        var ex = new PostgresException("deadlock detected", "ERROR", "ERROR", "40P01");
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.False);
    }

    [Test] // walks the inner chain to find the drop
    public void NestedInnerSocketException_IsConnectionLost()
    {
        var ex = new Exception("outer",
            new Exception("Error occurred while kindling 'X.sql'.",
                new SocketException(10054)));
        Assert.That(ConnectionLostClassifier.IsConnectionLost(ex), Is.True);
    }
}
