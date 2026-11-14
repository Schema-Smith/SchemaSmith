// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;
using SchemaQuench.Reporting;

namespace SchemaQuench.UnitTests;

// The schema for the exact tool version a consumer runs, with no repository and no settings file.
[NonParallelizable]
public class WriteSummarySchemaSwitchTests
{
    private string _dir;

    [SetUp]
    public void SetUp() => _dir = Path.Join(Path.GetTempPath(), $"summary-schema-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static (IEnvironment Environment, string Out, string Error) RunMain(string argument)
    {
        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns($"SchemaQuench.exe {argument}");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var (oldOut, oldError) = (Console.Out, Console.Error);
        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register(environment);
            Console.SetOut(stdout);
            Console.SetError(stderr);
            try
            {
                global::SchemaQuench.Program.Main([argument]);
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldError);
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
        return (environment, stdout.ToString(), stderr.ToString());
    }

    [Test]
    public void WritesTheSchema_CreatingTheFolder_AndExitsZero()
    {
        var path = Path.Join(_dir, "nested", "summary.schema.json");

        var (environment, output, _) = RunMain($"--WriteSummarySchema:{path}");

        Assert.Multiple(() =>
        {
            Assert.That(File.ReadAllText(path), Is.EqualTo(DeploymentSummarySchema.Render()));
            environment.Received(1).Exit(0);
            environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));
            Assert.That(output, Does.Contain(path));
        });
    }

    [Test]
    public void APathThatCannotBeWritten_ExitsTwo_NamingIt()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Join(_dir, "a-file");
        File.WriteAllText(blocker, "");
        var path = Path.Join(blocker, "summary.schema.json");

        var (environment, _, error) = RunMain($"--WriteSummarySchema:{path}");

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            environment.DidNotReceive().Exit(0);
            Assert.That(error, Does.Contain(path));
        });
    }

    [Test]
    public void NoPath_ExitsTwo_SayingHowToGiveOne()
    {
        var (environment, _, error) = RunMain("--WriteSummarySchema");

        environment.Received(1).Exit(2);
        Assert.That(error, Does.Contain("--WriteSummarySchema:<path>"));
    }
}
