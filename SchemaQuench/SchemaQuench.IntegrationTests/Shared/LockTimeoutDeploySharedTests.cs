// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// H2, through a real deploy: another session holds a lock on a table the deploy changes. With no lock timeout the deploy
/// waited for it forever (SQL Server and PostgreSQL have no limit; MySQL's metadata-lock wait is a year). With
/// Target:LockTimeoutSeconds it gives up, retries up to Target:ContentionRetryAttempts times, and either gets through once
/// the lock is free or stops with a message that names the setting.
/// </summary>
[NonParallelizable]
public abstract class LockTimeoutDeploySharedTests : ObjectScriptDeployFixture
{
    /// <summary>The table as the engine's JSON declares it, with column a of the given type.</summary>
    protected abstract string TableJson(string type);
    protected abstract string TableFileName { get; }
    /// <summary>A statement that, inside an open transaction, holds a lock the table change must wait for.</summary>
    protected abstract string HoldLock { get; }
    /// <summary>The declared type of column a, as the catalog reports it.</summary>
    protected abstract string ColumnType();
    protected abstract string DropTable { get; }
    protected abstract string SmallType { get; }
    protected abstract string BigType { get; }

    private const int LockTimeoutSeconds = 2;

    [SetUp]
    public void CreateTheTable()
    {
        Exec(DropTable);
        WritePackage("");
        WriteFile("Tables", TableFileName, TableJson(SmallType));
        Deploy();
    }

    [TearDown]
    public void DropTheTable() => Exec(DropTable);

    // The blocking session, with its lock taken and its transaction left open.
    private (IDbConnection Connection, IDbTransaction Transaction) Block()
    {
        var connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        connection.Open();
        if (Platform.GetBasePlatform() != Platform.MySQL) connection.ChangeDatabase(MainDb);
        var transaction = connection.BeginTransaction();
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = HoldLock;
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) { }
        return (connection, transaction);
    }

    private static void Release((IDbConnection Connection, IDbTransaction Transaction) blocker)
    {
        try { blocker.Transaction.Rollback(); }
        catch (InvalidOperationException) { /* already released */ }
        blocker.Connection.Dispose();
    }

    [Test]
    public void ALockHeldThroughTheDeploy_StopsItWithinTheTimeoutAndNamesTheSetting()
    {
        WriteFile("Tables", TableFileName, TableJson(BigType));
        var blocker = Block();
        var clock = Stopwatch.StartNew();
        // On a task with a deadline: without a lock timeout the deploy waits for the lock forever, which must fail the test
        // rather than hang it. Past the deadline the lock is released so the deploy can finish.
        var deploy = Task.Run(() => Deploy(expectSuccess: false, settings: new Dictionary<string, string>
        {
            ["Target:LockTimeoutSeconds"] = LockTimeoutSeconds.ToString(),
            ["Target:ContentionRetryAttempts"] = "2",
        }));
        var finishedInTime = deploy.Wait(TimeSpan.FromSeconds(60));
        clock.Stop();
        Release(blocker);
        var result = deploy.Result;

        Assert.Multiple(() =>
        {
            Assert.That(finishedInTime, Is.True, $"the deploy was still waiting for the lock after {clock.Elapsed.TotalSeconds:F0} s: the lock timeout was not applied");
            Assert.That(result.Failure?.Kind, Is.EqualTo("lockTimeout"), result.Failure?.Error);
            Assert.That(result.Failure?.Error, Does.Contain("Target:LockTimeoutSeconds").And.Contain("--ResumeQuench"));
            Assert.That(result.LockWaits.Any(w => w.Attempts == 2 && w.Outcome == "failed"), Is.True, "the wait is reported, with its attempts");
            Assert.That(ColumnType(), Is.EqualTo(SmallType).IgnoreCase, "the change did not land");
        });
    }

    [Test]
    public void ALockReleasedDuringTheRetries_LetsTheChangeThrough()
    {
        WriteFile("Tables", TableFileName, TableJson(BigType));
        var blocker = Block();
        var release = Task.Run(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(LockTimeoutSeconds + 2));
            Release(blocker);
        });
        DeployResult result;
        try
        {
            result = Deploy(settings: new Dictionary<string, string>
            {
                ["Target:LockTimeoutSeconds"] = LockTimeoutSeconds.ToString(),
                ["Target:ContentionRetryAttempts"] = "10",
            });
        }
        finally
        {
            release.Wait();
        }

        Assert.Multiple(() =>
        {
            Assert.That(ColumnType(), Is.EqualTo(BigType).IgnoreCase, "the change landed once the lock was free");
            Assert.That(result.LockWaits.Any(w => w.Attempts >= 2 && w.Outcome == "succeeded"), Is.True,
                "the retried statement is reported: " + string.Join(", ", result.LockWaits.Select(w => $"{w.Step}:{w.Attempts}:{w.Outcome}")));
        });
    }
}
