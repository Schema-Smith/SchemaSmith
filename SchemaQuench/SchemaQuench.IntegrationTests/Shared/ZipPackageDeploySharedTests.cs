// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Checkpointing;
using Schema.DataAccess;
using Schema.Domain;
using Schema.IntegrationTests;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// SchemaPackagePath may name a .zip of the package folder, read in place. The zip is the engine's ValidProduct folder,
/// so the run has to do what a deploy of the folder does: quench both templates and deliver the table data inside it.
/// </summary>
[NonParallelizable]
public abstract class ZipPackageDeploySharedTests
{
    protected abstract Platform Platform { get; }
    /// <summary>The engine's folder under TestProducts.</summary>
    protected abstract string TestProductsFolder { get; }
    protected abstract string MainConnectionString { get; }
    /// <summary>Counts the rows the zipped Main template's TestTable.tabledata delivers.</summary>
    protected abstract string DeliveredRowsSql { get; }
    protected abstract int ExpectedDeliveredRows { get; }

    private long DeliveredRows(string database)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        conn.Open();
        conn.ChangeDatabase(database);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = DeliveredRowsSql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Test]
    public void AZippedPackage_DeploysEveryTemplateLikeItsFolder()
    {
        var zip = Path.Join(Path.GetTempPath(), $"ValidProduct_{Guid.NewGuid():N}.zip");
        var checkpointDir = Path.Join(Path.GetTempPath(), $"SchemaQuench_Zip_{Guid.NewGuid():N}");
        ZipFile.CreateFromDirectory(TestHelper.GetTestProductPath(TestProductsFolder, "ValidProduct"), zip);
        var errorLog = Substitute.For<ILog>();
        var errors = new System.Collections.Generic.List<string>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString()));
        errorLog.When(l => l.Error(Arg.Any<object>(), Arg.Any<Exception>())).Do(ci => errors.Add(ci.ArgAt<object>(0) + " " + ci.ArgAt<Exception>(1)?.Message));
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString()));
        progressLog.When(l => l.Fatal(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString()));
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => errors.Add("WARN " + ci.ArgAt<object>(0)));
        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns("--SkipKindlingForge");

        lock (FactoryContainer.SharedLockObject)
        {
            var config = FactoryContainer.Resolve<IConfigurationRoot>();
            var packagePath = config["SchemaPackagePath"];
            var checkpoints = config["CheckpointDirectory"];
            FactoryContainer.Register(environment);
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", progressLog);
            config["SchemaPackagePath"] = zip;
            config["CheckpointDirectory"] = checkpointDir;
            try
            {
                Program.Main(["SkipKindlingForge"]);

                Assert.Multiple(() =>
                {
                    var exits = environment.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(IEnvironment.Exit)).Select(c => (int)c.GetArguments()[0]!);
                    Assert.That(exits, Is.EqualTo(new[] { 0 }), "the run failed: " + string.Join(" | ", errors));
                    progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching Template: Main")));
                    progressLog.Received(1).Info(Arg.Is<string>(s => s.Contains("Quenching Template: Secondary")));
                    Assert.That(DeliveredRows(config["ScriptTokens:MainDB"]!), Is.EqualTo(ExpectedDeliveredRows), "the table data inside the zip was delivered");
                });
            }
            finally
            {
                config["SchemaPackagePath"] = packagePath;
                config["CheckpointDirectory"] = checkpoints;
                (FactoryContainer.Resolve<ZipFileWrapper>() as IDisposable)?.Dispose();
                FactoryContainer.Unregister<ZipFileWrapper>();
                FactoryContainer.Unregister<ZipDirectoryWrapper>();
                LogFactory.Clear();
                FactoryContainer.Unregister<IEnvironment>();
                FactoryContainer.Unregister<ICheckpointing>();
                File.Delete(zip);
                if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
            }
        }
    }
}
