// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

/// <summary>
/// H2: what the operator sees when a deploy waits on another session. A retry left no trace past one log line, and a
/// lock timeout that ended a deploy arrived as the engine's raw text, naming neither the setting nor the step.
/// </summary>
[TestFixture]
[NonParallelizable]
public class ContentionReportingTests
{
    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static DatabaseQuench NewQuench(ContentionLog log, int attempts = 4)
    {
        var quench = new DatabaseQuench(new Product { Name = "P", Platform = Platform.SqlServer }, new Template { Name = "T" },
            new DatabaseQuenchOptions { Server = "srv", DatabaseName = "db" }, null)
        {
            Contention = log,
            Timeouts = new SessionTimeouts(0, 5, attempts),
        };
        quench.DeadlockRetryBaseMs = 0;
        return quench;
    }

    private static IDbCommand Failing(int failTimes, Func<Exception> error)
    {
        var cmd = Substitute.For<IDbCommand>();
        var calls = 0;
        cmd.When(c => c.ExecuteNonQuery()).Do(_ => { if (++calls <= failTimes) throw error(); });
        return cmd;
    }

    [Test]
    public void ARetryThatSucceeds_IsRecordedWithItsAttempts()
    {
        var log = new ContentionLog();
        var quench = NewQuench(log);
        quench.ExecuteNonQueryHandlingMessages(Failing(1, () => new SqlServerErrorException(1222, "Lock request time out period exceeded.")), retryOnDeadlock: true);

        var wait = log.Snapshot().Single();
        Assert.Multiple(() =>
        {
            Assert.That(wait.Attempts, Is.EqualTo(2));
            Assert.That(wait.Outcome, Is.EqualTo("succeeded"));
            Assert.That(wait.Target, Does.Contain("db"));
        });
    }

    [Test]
    public void ARetryThatRunsOut_IsRecordedAsFailed()
    {
        var log = new ContentionLog();
        var quench = NewQuench(log, attempts: 3);
        Assert.Throws<SqlServerErrorException>(() => quench.ExecuteNonQueryHandlingMessages(
            Failing(int.MaxValue, () => new SqlServerErrorException(1222, "Lock request time out period exceeded.")), retryOnDeadlock: true));

        var wait = log.Snapshot().Single();
        Assert.That((wait.Attempts, wait.Outcome), Is.EqualTo((3, "failed")));
    }

    [Test]
    public void AStatementThatNeverWaited_IsNotRecorded()
    {
        var log = new ContentionLog();
        NewQuench(log).ExecuteNonQueryHandlingMessages(Failing(0, () => new Exception()), retryOnDeadlock: true);
        Assert.That(log.Snapshot(), Is.Empty);
    }

    // Through a whole deploy: the lock timeout ends the unit with a first line that names the setting, the step and the
    // attempts, and the failure says what kind it was.
    [Test]
    public void ALockTimeoutThatEndsTheDeploy_SaysWhichSettingAndStep()
    {
        DatabaseQuench quench = null;
        var recording = QuenchStepOrderTests.RunWith(Platform.SqlServer, QuenchStepOrderTests.RichTemplate(Platform.SqlServer), whatIf: false,
            configure: config =>
            {
                config["Target:LockTimeoutSeconds"].Returns("5");
                config["Target:ContentionRetryAttempts"].Returns("2");
            },
            onExecute: command =>
            {
                if (command.CommandText?.Contains("SchemaSmith.ModifiedTableQuench @") == true)
                    throw new SqlServerErrorException(1222, "Lock request time out period exceeded.");
            },
            expectSuccess: false,
            capture: q => { quench = q; q.DeadlockRetryBaseMs = 0; });

        Assert.Multiple(() =>
        {
            Assert.That(quench.LastFailure.Kind, Is.EqualTo("lockTimeout"));
            Assert.That(quench.LastFailure.Error, Does.Contain("Target:LockTimeoutSeconds").And.Contain("5 s")
                .And.Contain("ModifiedTables").And.Contain("2 attempts").And.Contain("--ResumeQuench"));
            Assert.That(recording.Statements.Count(s => s.Contains("SchemaSmith.ModifiedTableQuench @")), Is.EqualTo(2), "tried as many times as the setting allows");
        });
    }

    [Test]
    public void TheSummaryCarriesTheWaitsTheFailureKindAndTheSettings()
    {
        var timing = new RunTiming();
        var summary = DeploymentSummaryAssembler.Assemble("P", "SqlServer", "2.9.0", DateTime.UtcNow, DateTime.UtcNow, RunMode.Quench,
            RunOutcome.PartialFailure, 2, false, Array.Empty<TargetResult>(), timing, Array.Empty<MigrationScriptRun>(), Array.Empty<WhatIfRun>(),
            [new FailureRecord("Template:T", "[srv].[db]", "gave up waiting", [], null, "lockTimeout")], 0, null,
            lockWaits: [new LockWait("[srv].[db]", "ModifiedTables", 3, 120, "failed")],
            settings: new SessionTimeouts(30, 5, 3));
        var json = JObject.Parse(DeploymentSummaryJson.Serialize(summary));

        Assert.Multiple(() =>
        {
            Assert.That((string)json["schemaVersion"], Is.EqualTo("1.4"));
            Assert.That((string)json.SelectToken("failures[0].kind"), Is.EqualTo("lockTimeout"));
            Assert.That((int)json.SelectToken("lockWaits[0].attempts"), Is.EqualTo(3));
            Assert.That((string)json.SelectToken("lockWaits[0].step"), Is.EqualTo("ModifiedTables"));
            Assert.That((int)json.SelectToken("run.settings.lockTimeoutSeconds"), Is.EqualTo(5));
            Assert.That((int)json.SelectToken("run.settings.commandTimeoutSeconds"), Is.EqualTo(30));
            Assert.That((int)json.SelectToken("run.settings.contentionRetryAttempts"), Is.EqualTo(3));
        });
    }
}
