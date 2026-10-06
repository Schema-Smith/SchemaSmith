// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using Schema.DataAccess;
using Schema.Domain;
using System;
using System.Collections.Generic;
using System.Threading;

using NUnit.Framework;
using Schema.Utility;

namespace Schema.IntegrationTests.Shared;

/// <summary>
/// Shared StatusMessageMonitor integration tests for the MySQL/MariaDb family. The MySQL and MariaDb
/// subclasses supply the platform + fixture accessors; every [Test] body here runs on both engines.
/// Verifies that the monitor can capture messages written by stored procedures.
/// </summary>
public abstract class StatusMessageMonitorSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }

    private IDbConnection _connection = null!;
    private long _sessionId;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        _connection.Open();

        // Get this connection's session ID
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT CONNECTION_ID()";
        _sessionId = Convert.ToInt64(cmd.ExecuteScalar());
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up any test messages
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = {_sessionId}";
            cmd.ExecuteNonQuery();
        }
        catch { /* ignore */ }

        _connection?.Close();
        _connection?.Dispose();
    }

    [Test]
    public void StatusMessageMonitor_CapturesMessages_WrittenByOtherConnection()
    {
        // Arrange - Insert messages directly (simulating what a procedure would do)
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'Test message 1')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'Test message 2')";
        cmd.ExecuteNonQuery();

        // Act - Create a monitor on a separate connection and flush
        var capturedMessages = new List<string>();
        using var monitor = new StatusMessageMonitor(
            MainConnectionString,
            _sessionId,
            msg => capturedMessages.Add(msg));

        monitor.Flush();

        // Assert
        Assert.That(capturedMessages, Has.Count.EqualTo(2));
        Assert.That(capturedMessages[0], Is.EqualTo("Test message 1"));
        Assert.That(capturedMessages[1], Is.EqualTo("Test message 2"));
    }

    [Test]
    public void StatusMessageMonitor_OnlyReadsOwnSessionMessages()
    {
        // Arrange - Insert messages for this session and a fake other session
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'My message')";
        cmd.ExecuteNonQuery();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES (999999999, 'Other session message')";
        cmd.ExecuteNonQuery();

        // Act
        var capturedMessages = new List<string>();
        using var monitor = new StatusMessageMonitor(
            MainConnectionString,
            _sessionId,
            msg => capturedMessages.Add(msg));

        monitor.Flush();

        // Assert - only captured own session's message
        Assert.That(capturedMessages, Has.Count.EqualTo(1));
        Assert.That(capturedMessages[0], Is.EqualTo("My message"));

        // Cleanup other session's message
        cmd.CommandText = "DELETE FROM SchemaSmith_StatusMessages WHERE SessionId = 999999999";
        cmd.ExecuteNonQuery();
    }

    [Test]
    public void StatusMessageMonitor_Flush_CapturesRemainingMessages()
    {
        // Arrange
        var capturedMessages = new List<string>();
        using var monitor = new StatusMessageMonitor(
            MainConnectionString,
            _sessionId,
            msg => capturedMessages.Add(msg),
            pollIntervalMs: 60000); // Very long poll interval to ensure only Flush captures

        // Insert message after monitor is created but before timer fires
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'Flushed message')";
        cmd.ExecuteNonQuery();

        // Act - flush should capture immediately
        monitor.Flush();

        // Assert
        Assert.That(capturedMessages, Has.Count.EqualTo(1));
        Assert.That(capturedMessages[0], Is.EqualTo("Flushed message"));
    }

    [Test]
    public void StatusMessageMonitor_Dispose_CleansUpSessionMessages()
    {
        // Arrange
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'Will be cleaned up')";
        cmd.ExecuteNonQuery();

        // Act - create and immediately dispose monitor
        var monitor = new StatusMessageMonitor(
            MainConnectionString,
            _sessionId,
            _ => { });
        monitor.Dispose();

        // Assert - messages should be deleted
        cmd.CommandText = $"SELECT COUNT(*) FROM SchemaSmith_StatusMessages WHERE SessionId = {_sessionId}";
        var count = Convert.ToInt64(cmd.ExecuteScalar());
        Assert.That(count, Is.EqualTo(0));
    }

    [Test]
    public void StatusMessageMonitor_TimerPolling_CapturesMessages()
    {
        // Arrange
        var capturedMessages = new List<string>();
        using var monitor = new StatusMessageMonitor(
            MainConnectionString,
            _sessionId,
            msg => { lock (capturedMessages) capturedMessages.Add(msg); },
            pollIntervalMs: 100);

        // Act - insert a message and wait for timer to pick it up
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"INSERT INTO SchemaSmith_StatusMessages (SessionId, Message) VALUES ({_sessionId}, 'Timer picked up')";
        cmd.ExecuteNonQuery();

        // Wait for the timer to poll (at most 500ms)
        for (var i = 0; i < 10; i++)
        {
            Thread.Sleep(50);
            lock (capturedMessages)
            {
                if (capturedMessages.Count > 0) break;
            }
        }

        // Assert
        lock (capturedMessages)
        {
            Assert.That(capturedMessages, Has.Count.EqualTo(1));
            Assert.That(capturedMessages[0], Is.EqualTo("Timer picked up"));
        }
    }

    [Test]
    public void StatusMessages_Table_ExistsAfterKindling()
    {
        // ForgeKindler is deployed by FixtureSetup - verify the table exists
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $@"
            SELECT TABLE_NAME
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_SCHEMA = '{MainDb}'
            AND TABLE_NAME = 'SchemaSmith_StatusMessages'";
        var result = cmd.ExecuteScalar();
        Assert.That(result, Is.Not.Null);
        // A server that folds names (lower_case_table_names >= 1) reports them in lowercase.
        Assert.That(result!.ToString(), Is.EqualTo("SchemaSmith_StatusMessages").IgnoreCase);
    }
}
