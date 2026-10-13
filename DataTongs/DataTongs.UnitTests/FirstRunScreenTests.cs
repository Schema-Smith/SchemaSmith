// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;
using System.IO;
using log4net;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.UnitTests;

// A fresh install: the shipped settings file sits beside the tool, as it does in this test's output folder, and the
// tool is run from somewhere else with no arguments. It must explain itself and exit 0, not attempt a run.
[NonParallelizable]
public class FirstRunScreenTests
{
    [Test]
    public void BareRunOnTheShippedSettingsFile_ShowsTheGettingStartedScreenAndExitsZero()
    {
        var workDir = Path.Join(Path.GetTempPath(), $"first-run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var originalDir = Directory.GetCurrentDirectory();
        var originalOut = Console.Out;
        using var console = new StringWriter();
        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns("DataTongs.exe");
        environment.GetEnvironmentVariables().Returns(new Hashtable());
        var progress = Substitute.For<ILog>();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register(environment);
            LogFactory.Register("ProgressLog", progress);
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            try
            {
                Directory.SetCurrentDirectory(workDir);
                Console.SetOut(console);
                global::DataTongs.Program.Main([]);
            }
            finally
            {
                Console.SetOut(originalOut);
                Directory.SetCurrentDirectory(originalDir);
                Directory.Delete(workDir, true);
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }

        var output = console.ToString();
        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(0);
            environment.DidNotReceive().Exit(Arg.Is<int>(c => c != 0));
            Assert.That(output, Does.Contain("Nothing is configured yet, so nothing was run."));
            Assert.That(output, Does.Contain(Path.GetFullPath(Path.Join(AppContext.BaseDirectory, "DataTongs.settings.json"))));
            progress.DidNotReceiveWithAnyArgs().Info(default);
        });
    }
}
