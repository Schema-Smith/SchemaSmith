// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;

namespace SchemaQuench.UnitTests;

/// <summary>
/// Covers the end-of-work-unit ChangeAudit drain's best-effort contract at the
/// <see cref="ChangeAuditReader"/> boundary. The drain runs in the quench's
/// <c>finally</c> on the shared table connection; its documented contract is that an audit-read
/// failure must NEVER disrupt the run. A broken/closed connection surfaces as
/// <see cref="InvalidOperationException"/> ("Connection is not open") — not a
/// <see cref="DbException"/> — so the swallow must be broad enough to cover it, otherwise the
/// drain masks the real result and fails an otherwise-successful quench under concurrency.
/// </summary>
[TestFixture]
public class ChangeAuditReaderTests
{
    private static IDbCommand CommandWhoseReaderThrows(Exception error)
    {
        var cmd = Substitute.For<IDbCommand>();
        cmd.When(c => c.ExecuteReader()).Do(_ => throw error);
        return cmd;
    }

    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void ReadAndDrain_BrokenConnection_ReturnsNullInsteadOfThrowing(Platform platform)
    {
        // "Connection is not open" from a connection reset under concurrency (Npgsql throws
        // InvalidOperationException, not a DbException). The best-effort drain must swallow it.
        var cmd = CommandWhoseReaderThrows(new InvalidOperationException("Connection is not open"));

        IReadOnlyList<ChangeAuditRow> rows = null;
        Assert.DoesNotThrow(() => rows = ChangeAuditReader.ReadAndDrain(platform, cmd));
        Assert.That(rows, Is.Null, "A broken connection during the best-effort drain must leave the run not-instrumented, not throw.");
    }

    [Test]
    public void ReadAndDrain_MissingAuditTable_ReturnsNull()
    {
        // Regression guard on the pre-existing DbException path (audit table absent / kindling
        // suppressed) — must still return null.
        var cmd = CommandWhoseReaderThrows(new TestDbException("relation \"SchemaSmith\".\"ChangeAudit\" does not exist"));

        IReadOnlyList<ChangeAuditRow> rows = null;
        Assert.DoesNotThrow(() => rows = ChangeAuditReader.ReadAndDrain(Platform.PostgreSQL, cmd));
        Assert.That(rows, Is.Null);
    }

    // A recycled session id carries the rows of the session that last had it; deleting only this session's rows before
    // the work unit writes any is what keeps them out of its summary.
    [TestCase(Platform.SqlServer, "@@SPID")]
    [TestCase(Platform.PostgreSQL, "pg_backend_pid()")]
    [TestCase(Platform.MySQL, "CONNECTION_ID()")]
    [TestCase(Platform.MariaDb, "CONNECTION_ID()")]
    public void DiscardStale_DeletesOnlyThisSessionsRows(Platform platform, string sessionFunction)
    {
        var cmd = Substitute.For<IDbCommand>();
        var executed = new List<string>();
        cmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(cmd.CommandText));

        ChangeAuditReader.DiscardStale(platform, cmd);

        Assert.That(executed, Has.Count.EqualTo(1));
        Assert.That(executed[0], Does.Contain("DELETE FROM").And.Contains("ChangeAudit").And.EndsWith($"= {sessionFunction}"));
    }

    // On SQL Server a missing table is not an exception the reader can catch: the table connection turns the error into an
    // info message that the run logs as a failure. The delete is skipped when the table is not there.
    [Test]
    public void DiscardStale_SqlServer_ChecksTheTableExistsBeforeDeleting()
    {
        var cmd = Substitute.For<IDbCommand>();
        var executed = new List<string>();
        cmd.When(c => c.ExecuteNonQuery()).Do(_ => executed.Add(cmd.CommandText));

        ChangeAuditReader.DiscardStale(Platform.SqlServer, cmd);

        Assert.That(executed.Single(), Does.StartWith("IF OBJECT_ID(N'SchemaSmith.ChangeAudit', N'U') IS NOT NULL DELETE FROM"));
    }

    [Test]
    public void DiscardStale_MissingAuditTable_DoesNotThrow()
    {
        // Before the first kindle the table does not exist, and then there is nothing stale to discard.
        var cmd = Substitute.For<IDbCommand>();
        cmd.When(c => c.ExecuteNonQuery()).Do(_ => throw new TestDbException("Invalid object name 'SchemaSmith.ChangeAudit'."));

        Assert.DoesNotThrow(() => ChangeAuditReader.DiscardStale(Platform.SqlServer, cmd));
    }

    private sealed class TestDbException : DbException
    {
        public TestDbException(string message) : base(message) { }
    }
}
