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
    // A session whose message table holds the given rows, read through the session's own connection (the monitor's own
    // cannot open here). Every statement the monitor sends is recorded.
    private static (IDbConnection Session, System.Collections.Generic.List<string> Statements) SessionWith(System.Func<(long Id, string Message)[]> table)
    {
        var statements = new System.Collections.Generic.List<string>();
        var session = Substitute.For<IDbConnection>();
        session.State.Returns(ConnectionState.Open);
        session.CreateCommand().Returns(_ =>
        {
            var command = Substitute.For<IDbCommand>();
            command.CommandText = Arg.Do<string>(statements.Add);
            command.CreateParameter().Returns(_ => Substitute.For<IDbDataParameter>());
            var reader = Substitute.For<IDataReader>();
            var rows = table();
            var at = -1;
            reader.Read().Returns(_ => ++at < rows.Length);
            reader.GetInt64(0).Returns(_ => rows[at].Id);
            reader.GetString(1).Returns(_ => rows[at].Message);
            command.ExecuteReader().Returns(reader);
            return command;
        });
        return (session, statements);
    }

    [Test]
    public void Dispose_DeliversTheMessagesLeft_AndClearsTheSessionsRows()
    {
        var (session, statements) = SessionWith(() => [(1, "first"), (2, "second")]);
        var delivered = new System.Collections.Generic.List<string>();
        var monitor = new StatusMessageMonitor("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1", 7, delivered.Add,
            pollIntervalMs: 600_000, sessionConnection: session, warn: _ => { });

        monitor.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(delivered, Is.EqualTo(new[] { "first", "second" }), "the final flush delivers what the last poll did not");
            Assert.That(statements, Has.Some.StartsWith("DELETE FROM").And.Contain("SessionId"), "and the session's rows are removed");
        });
    }

    [Test]
    public void AFlushAfterDispose_DeliversNothing()
    {
        (long, string)[] rows = [];
        var (session, _) = SessionWith(() => rows);
        var delivered = new System.Collections.Generic.List<string>();
        var monitor = new StatusMessageMonitor("Server=127.0.0.1;Port=1;User ID=x;Password=x;Connection Timeout=1", 7, delivered.Add,
            pollIntervalMs: 600_000, sessionConnection: session, warn: _ => { });
        monitor.Dispose();
        rows = [(3, "late")];

        monitor.Flush();

        Assert.That(delivered, Is.Empty);
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
