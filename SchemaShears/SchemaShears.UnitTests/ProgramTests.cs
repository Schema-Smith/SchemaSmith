// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using log4net;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using NSubstitute;
using NUnit.Framework;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaShears.UnitTests;

[TestFixture]
public class ProgramTests
{
    private string _root;
    private string _source;
    private string _output;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), "shears-prog-" + Guid.NewGuid().ToString("N"));
        _source = Path.Join(_root, "product");
        _output = Path.Join(_root, "patch");

        var tables = Path.Join(_source, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Join(tables, "dbo.Orders.json"), "{ \"Name\": \"Orders\" }");
        File.WriteAllText(Path.Join(_source, "Templates", "Main", "Template.json"), "{}");
        File.WriteAllText(Path.Join(_source, "Product.json"), "{ \"Name\": \"Acme\" }");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void UnhandledException_LogsErrorAndExits()
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var environment = Substitute.For<IEnvironment>();
            var progressLog = Substitute.For<ILog>();
            var errorLog = Substitute.For<ILog>();

            FactoryContainer.Register(environment);
            LogFactory.Register("ProgressLog", progressLog);
            LogFactory.Register("ErrorLog", errorLog);

            var exception = new Exception("Test Exception");
            Program.UnhandledException("TestApp", new UnhandledExceptionEventArgs(exception, false));

            progressLog.Received(1).Error(Arg.Is<string>(s => s.Contains("Test Exception")));
            errorLog.Received(1).Error(exception);
            environment.Received(1).Exit(3);

            FactoryContainer.Clear();
            LogFactory.Clear();
        }
    }

    [Test]
    public void Main_SuccessPath_BuildsPatchAndExitsZero()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var environment = RunMain("SchemaShears.exe", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = manifest,
            ["OutputPath"] = _output
        });

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(0);
            Assert.That(File.Exists(Path.Join(_output, "Templates", "Main", "Tables", "dbo.Orders.json")), Is.True);
            Assert.That(File.Exists(Path.Join(_output, "patch-build-report.txt")), Is.True);
        });
    }

    [Test]
    [NonParallelizable] // mutates the process current directory to exercise relative-path resolution
    public void Main_RelativeSourcePath_ResolvesAgainstCurrentDirectoryAndBuilds()
    {
        File.WriteAllText(Path.Join(_root, "m.txt"), "Templates/Main/Tables/dbo.Orders.json\n");

        var priorCwd = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_root); // as if the user invoked schemashears from the product's parent dir

            var environment = RunMain("SchemaShears.exe", new Dictionary<string, string>
            {
                ["SourcePath"] = "product", // relative — the form documented in the course6-module-05 lab
                ["ManifestPath"] = "m.txt",
                ["OutputPath"] = "patch"
            });

            Assert.Multiple(() =>
            {
                environment.Received(1).Exit(0);
                Assert.That(File.Exists(Path.Join(_output, "Templates", "Main", "Tables", "dbo.Orders.json")), Is.True);
            });
        }
        finally
        {
            Directory.SetCurrentDirectory(priorCwd);
        }
    }

    // The test environment's Exit does not end the process, so after --help prints usage, Main carries on and fails
    // for the missing source.
    [Test]
    public void Main_WithHelpSwitch_ShowsUsageThenFailsForMissingSource()
    {
        var (environment, errorLog) = RunMainExpectingFailure("SchemaShears.exe --help", new Dictionary<string, string>());

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(0);
            environment.Received(1).Exit(2);
            errorLog.Received().Error(Arg.Is<object>(o => o.ToString().Contains("Source product folder not found")));
        });
    }

    // A request the user can correct is a failure, exit 2 with the reason in the error log -- not an unhandled
    // exception (3), which is what every bad input used to produce.
    [Test]
    public void Main_NoSourceConfigured_ExitsTwoAndNamesTheProblem()
    {
        var (environment, errorLog) = RunMainExpectingFailure("SchemaShears.exe", new Dictionary<string, string>());

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            environment.DidNotReceive().Exit(3);
            errorLog.Received().Error(Arg.Is<object>(o => o.ToString().Contains("Source product folder not found")));
        });
    }

    [Test]
    public void Main_ManifestNotFound_ExitsTwoAndNamesTheProblem()
    {
        var (environment, errorLog) = RunMainExpectingFailure("SchemaShears.exe", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = Path.Join(_root, "nope.txt"),
            ["OutputPath"] = _output
        });

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            errorLog.Received().Error(Arg.Is<object>(o => o.ToString().Contains("Manifest file not found")));
        });
    }

    // A typo in --AllowDrops used to be found only after the package was copied, leaving a half-built patch whose
    // presence then made the re-run fail.
    [Test]
    public void Main_UnknownDropCategory_ExitsTwoBeforeWritingAnything()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var (environment, errorLog) = RunMainExpectingFailure("SchemaShears.exe --AllowDrops:Colums", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = manifest,
            ["OutputPath"] = _output
        });

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            errorLog.Received().Error(Arg.Is<object>(o => o.ToString().Contains("Unknown drop category 'Colums'")));
            Assert.That(Directory.Exists(_output), Is.False, "nothing is written for a request that cannot succeed");
        });
    }

    [Test]
    public void Main_ZipAlreadyExists_ExitsTwoBeforeWritingAnything()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");
        File.WriteAllText(_output + ".zip", "an earlier patch");

        var (environment, _) = RunMainExpectingFailure("SchemaShears.exe --Zip", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = manifest,
            ["OutputPath"] = _output
        });

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(2);
            Assert.That(Directory.Exists(_output), Is.False);
        });
    }

    [Test]
    public void Main_WithZipSwitch_ProducesZipFile()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var environment = RunMain("SchemaShears.exe --Zip", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = manifest,
            ["OutputPath"] = _output
        });

        Assert.Multiple(() =>
        {
            environment.Received(1).Exit(0);
            Assert.That(File.Exists(_output + ".zip"), Is.True);
        });
    }

    [Test]
    public void Main_WithAllowDrops_PassesCategoriesThroughToStamp()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var environment = RunMain("SchemaShears.exe --AllowDrops:Columns,Indexes", new Dictionary<string, string>
        {
            ["SourcePath"] = _source,
            ["ManifestPath"] = manifest,
            ["OutputPath"] = _output
        });

        environment.Received(1).Exit(0);
        var product = JObject.Parse(File.ReadAllText(Path.Join(_output, "Product.json")));
        Assert.Multiple(() =>
        {
            Assert.That(product["DropColumnsRemovedFromProduct"], Is.Null,
                "Columns is allowed, so the drop flag should not be stamped false");
            Assert.That(product["DropUnknownIndexes"], Is.Null,
                "Indexes is allowed, so the drop flag should not be stamped false");
            Assert.That(product["DropTablesRemovedFromProduct"].Value<bool>(), Is.False);
        });
    }

    [Test]
    public void ResolveToFullPath_RelativePath_BecomesRooted()
    {
        var result = Program.ResolveToFullPath("product");

        Assert.Multiple(() =>
        {
            Assert.That(Path.IsPathRooted(result), Is.True);
            Assert.That(result, Does.EndWith("product"));
        });
    }

    [Test]
    public void ResolveToFullPath_NullOrBlank_IsReturnedUnchanged()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Program.ResolveToFullPath(null), Is.Null);
            Assert.That(Program.ResolveToFullPath(""), Is.EqualTo(""));
            Assert.That(Program.ResolveToFullPath("   "), Is.EqualTo("   "));
        });
    }

    /// <summary>
    /// Registers a mocked <see cref="IEnvironment"/> with the given command line, an in-memory
    /// config with the given values, and silent loggers, ready for <see cref="Program.Main"/>.
    /// </summary>
    private static IEnvironment Arrange(string commandLine, Dictionary<string, string> configValues)
    {
        FactoryContainer.Clear();
        LogFactory.Clear();

        var environment = Substitute.For<IEnvironment>();
        environment.CommandLine.Returns(commandLine);

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        FactoryContainer.Register(environment);
        FactoryContainer.Register<IConfigurationRoot>(config);

        LogFactory.Register("ProgressLog", Substitute.For<ILog>());
        LogFactory.Register("ErrorLog", Substitute.For<ILog>());

        return environment;
    }

    private static IEnvironment RunMain(string commandLine, Dictionary<string, string> configValues)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var environment = Arrange(commandLine, configValues);
            try
            {
                Program.Main([]);
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
            return environment;
        }
    }

    private static (IEnvironment environment, ILog errorLog) RunMainExpectingFailure(
        string commandLine, Dictionary<string, string> configValues)
    {
        lock (FactoryContainer.SharedLockObject)
        {
            var environment = Arrange(commandLine, configValues);
            var errorLog = Substitute.For<ILog>();
            LogFactory.Register("ErrorLog", errorLog);
            try
            {
                Assert.DoesNotThrow(() => Program.Main([]), "a bad request is reported and exits, it does not throw");
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
            return (environment, errorLog);
        }
    }
}
