// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using log4net;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.UnitTests;

// The settings check passing its own test is not enough: in 2.7.2 it threw outside Main's handler, so the process
// exited 3 as a defect. This runs Main itself.
[NonParallelizable]
public class MissingConfigFileExitTests
{
    [Test]
    public void AConfigFileThatDoesNotExist_ExitsTwo_NamingThePath()
    {
        var missing = Path.Join(Path.GetTempPath(), $"no-such-dir-{Guid.NewGuid():N}", "x.json");
        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns($"SchemaTongs.exe --ConfigFile:{missing}");
        var progress = Substitute.For<ILog>();

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register(environment);
            FactoryContainer.Register(Substitute.For<IFile>());
            FactoryContainer.Register(Substitute.For<IDirectory>());
            LogFactory.Register("ProgressLog", progress);
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            try
            {
                global::SchemaTongs.Program.Main([$"--ConfigFile:{missing}"]);
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            environment.DidNotReceive().Exit(3);
            progress.Received(1).Error(Arg.Is<object>(m => m.ToString()!.StartsWith("FAILED: Settings file not found") && m.ToString()!.Contains(missing)));
        });
    }
}
