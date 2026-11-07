// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Data;
using NSubstitute;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// StatusMessageMonitor requires a real database connection (MySQL) to function.
/// These tests validate the class design and interface. Full integration tests
/// will cover actual polling behavior with a running MySQL instance.
/// </summary>
[TestFixture]
public class StatusMessageMonitorTests
{
    [Test]
    public void StatusMessageMonitor_ImplementsIDisposable()
    {
        Assert.That(typeof(StatusMessageMonitor).GetInterfaces(), Does.Contain(typeof(System.IDisposable)));
    }

    [Test]
    public void StatusMessageMonitor_HasFlushMethod()
    {
        var method = typeof(StatusMessageMonitor).GetMethod("Flush");
        Assert.That(method, Is.Not.Null);
    }

    // The deploy's command timeout (Target:CommandTimeoutSeconds) reaches the monitor's reads too; before, they always took
    // the provider's 30 s. Its own connection cannot open here, so it reads through the session's, as it does on a flush.
    [TestCase(0)]
    [TestCase(45)]
    public void StatusMessageMonitor_CommandsTakeTheDeploysCommandTimeout(int seconds)
    {
        var session = Substitute.For<IDbConnection>();
        session.State.Returns(ConnectionState.Open);
        var command = Substitute.For<IDbCommand>();
        command.CreateParameter().Returns(_ => Substitute.For<IDbDataParameter>());
        command.ExecuteReader().Returns(_ => Substitute.For<IDataReader>());
        session.CreateCommand().Returns(command);

        using var monitor = new StatusMessageMonitor("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1", 1, _ => { },
            sessionConnection: session, warn: _ => { }, commandTimeoutSeconds: seconds);
        monitor.PollOnce();

        command.Received().CommandTimeout = seconds;
    }

    [Test]
    public void StatusMessageMonitor_HasPollOnceMethod()
    {
        var method = typeof(StatusMessageMonitor).GetMethod("PollOnce",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        Assert.That(method, Is.Not.Null);
    }
}
