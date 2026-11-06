// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.UnitTests;

/// <summary>
/// H2: statement and lock timeouts. SchemaQuench ran every deploy command with no time limit and set no lock timeout, so a
/// deploy blocked behind another session's lock waited forever -- and on PostgreSQL a queued ALTER TABLE blocks every new
/// reader of that table behind it.
/// </summary>
[TestFixture]
[NonParallelizable]
public class SessionTimeoutsTests
{
    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static IConfigurationRoot Config(Dictionary<string, string> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void WithNothingSet_TheDefaultsAreTodays()
    {
        var timeouts = SessionTimeouts.Read(Config([]));
        Assert.Multiple(() =>
        {
            Assert.That(timeouts.CommandTimeoutSeconds, Is.Zero, "no limit, as every deploy command had");
            Assert.That(timeouts.LockTimeoutSeconds, Is.Null, "the engine's own lock wait");
            Assert.That(timeouts.ContentionRetryAttempts, Is.EqualTo(20));
        });
    }

    [TestCase("Target:CommandTimeoutSeconds", "-1")]
    [TestCase("Target:CommandTimeoutSeconds", "ten")]
    [TestCase("Target:LockTimeoutSeconds", "0")]
    [TestCase("Target:LockTimeoutSeconds", "1.5")]
    [TestCase("Target:ContentionRetryAttempts", "0")]
    [TestCase("Target:ContentionRetryAttempts", "101")]
    public void AnOutOfRangeValue_IsRefusedNamingTheSetting(string key, string value)
    {
        var ex = Assert.Throws<RunFailedException>(() => SessionTimeouts.Read(Config(new() { [key] = value })));
        Assert.That(ex!.Message, Does.Contain(key).And.Contain(value));
    }

    [TestCase(Platform.SqlServer, "SET LOCK_TIMEOUT 7000")]
    [TestCase(Platform.PostgreSQL, "SET lock_timeout = '7s'")]
    [TestCase(Platform.MySQL, "SET SESSION lock_wait_timeout = 7, SESSION innodb_lock_wait_timeout = 7")]
    [TestCase(Platform.MariaDb, "SET SESSION lock_wait_timeout = 7, SESSION innodb_lock_wait_timeout = 7")]
    public void TheLockTimeoutIsTheEnginesSessionSetting(Platform platform, string expected)
    {
        Assert.That(SessionTimeouts.LockTimeoutStatement(platform, 7), Is.EqualTo(expected));
    }

    // Through a whole deploy: what every command the deploy sends was given, and the session statements it sent.
    private static (List<int> Timeouts, List<string> Statements) Deploy(Platform platform, Dictionary<string, string> settings)
    {
        var timeouts = new List<int>();
        var recording = QuenchStepOrderTests.RunWith(platform, QuenchStepOrderTests.RichTemplate(platform), whatIf: false,
            configure: config =>
            {
                foreach (var (key, value) in settings) config[key].Returns(value);
            },
            onExecute: command => timeouts.Add(command.CommandTimeout));
        return (timeouts, recording.Statements);
    }

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void WithNothingSet_EveryCommandRunsWithoutALimitAndNoLockTimeoutIsSent(Platform platform)
    {
        var (timeouts, statements) = Deploy(platform, []);
        Assert.Multiple(() =>
        {
            Assert.That(timeouts.Distinct(), Is.EqualTo(new[] { 0 }), "every command the deploy sent");
            Assert.That(statements.Where(s => s.Contains("lock_timeout", System.StringComparison.OrdinalIgnoreCase)
                                               || s.Contains("LOCK_TIMEOUT") || s.Contains("lock_wait_timeout")), Is.Empty);
        });
    }

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void WithBothSet_EveryCommandHasTheLimitAndEveryConnectionTheLockTimeout(Platform platform)
    {
        var (timeouts, statements) = Deploy(platform, new() { ["Target:CommandTimeoutSeconds"] = "7", ["Target:LockTimeoutSeconds"] = "3" });
        var set = SessionTimeouts.LockTimeoutStatement(platform, 3);
        Assert.Multiple(() =>
        {
            Assert.That(timeouts.Distinct(), Is.EqualTo(new[] { 7 }), "every command the deploy sent, the session's own included");
            Assert.That(statements, Does.Contain(set));
            Assert.That(statements.IndexOf(set), Is.Zero, "set before anything else runs on the connection");
        });
    }
}
