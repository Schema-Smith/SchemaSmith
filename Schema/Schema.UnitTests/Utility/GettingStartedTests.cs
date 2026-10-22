// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class GettingStartedTests
{
    private static readonly FirstRunSetting[] QuenchSettings =
    [
        new("Target:Server", "the server to deploy to"),
        new("SchemaPackagePath", "the folder that holds your package's Product.json")
    ];

    private string _cwd;
    private string _toolDir;
    private IEnvironment _environment;
    private StringWriter _console;
    private TextWriter _originalConsole;

    [SetUp]
    public void SetUp()
    {
        var root = Path.Join(Path.GetTempPath(), $"getting_started_{Guid.NewGuid():N}");
        _cwd = Path.Join(root, "work");
        _toolDir = Path.Join(root, "tool");
        Directory.CreateDirectory(_cwd);
        Directory.CreateDirectory(_toolDir);

        _environment = Substitute.For<IEnvironment>();
        _environment.GetEnvironmentVariables().Returns(new Hashtable { ["PATH"] = "/usr/bin" });
        FactoryContainer.Register(_environment);

        _originalConsole = Console.Out;
        _console = new StringWriter();
        Console.SetOut(_console);
    }

    [TearDown]
    public void TearDown()
    {
        Console.SetOut(_originalConsole);
        _console.Dispose();
        FactoryContainer.Unregister<IEnvironment>();
        Directory.Delete(Path.GetDirectoryName(_cwd)!, true);
    }

    private bool Show(params string[] args) =>
        GettingStarted.ShowIfUnconfigured("SchemaQuench", "deploys a schema package to a database", QuenchSettings, args, _cwd, _toolDir);

    private void WriteSettings(string dir, string json) => File.WriteAllText(Path.Join(dir, "SchemaQuench.settings.json"), json);

    [Test]
    public void StockFileWithBlankSettings_ShowsTheScreenNamingTheFileAndExitsZero()
    {
        WriteSettings(_toolDir, "{ \"Target\": { \"Server\": \"\" }, // comment\n \"SchemaPackagePath\": \"\" }");

        Assert.That(Show(), Is.True);

        _environment.Received(1).Exit(0);
        var output = _console.ToString();
        Assert.That(output, Does.Contain("Nothing is configured yet, so nothing was run."));
        Assert.That(output, Does.Contain(Path.Join(_toolDir, "SchemaQuench.settings.json")));
        Assert.That(output, Does.Contain("Target:Server"));
        Assert.That(output, Does.Contain("SchemaPackagePath"));
        Assert.That(output, Does.Contain("SmithySettings_Target__Server"));
        Assert.That(output, Does.Contain("--ConfigFile"));
        Assert.That(output, Does.Contain("--help"));
    }

    [Test]
    public void NoSettingsFileAnywhere_NamesBothPlacesItLookedAndExitsTwo()
    {
        Assert.That(Show(), Is.True);

        _environment.Received(1).Exit(2);
        var output = _console.ToString();
        Assert.That(output, Does.Contain("No settings file was found, so nothing was run."));
        Assert.That(output, Does.Contain(Path.Join(_cwd, "SchemaQuench.settings.json")));
        Assert.That(output, Does.Contain(Path.Join(_toolDir, "SchemaQuench.settings.json")));
    }

    [Test]
    public void OneFirstRunSettingFilledIn_IsANormalRun()
    {
        WriteSettings(_toolDir, "{ \"Target\": { \"Server\": \"db01\" }, \"SchemaPackagePath\": \"\" }");

        Assert.That(Show(), Is.False);

        _environment.DidNotReceive().Exit(Arg.Any<int>());
        Assert.That(_console.ToString(), Is.Empty);
    }

    [Test]
    public void TheCurrentDirectorysFileWinsOverTheToolFolders()
    {
        WriteSettings(_toolDir, "{ \"Target\": { \"Server\": \"\" }, \"SchemaPackagePath\": \"\" }");
        WriteSettings(_cwd, "{ \"SchemaPackagePath\": \"./pkg\" }");

        Assert.That(Show(), Is.False);
    }

    [Test]
    public void AnyArgument_IsANormalRun()
    {
        WriteSettings(_toolDir, "{ \"Target\": { \"Server\": \"\" }, \"SchemaPackagePath\": \"\" }");

        Assert.That(Show("--WhatIf"), Is.False);
        Assert.That(Show("--ConfigFile:my.settings.json"), Is.False);

        _environment.DidNotReceive().Exit(Arg.Any<int>());
    }

    [Test]
    public void AnySmithySettingsEnvironmentVariable_IsANormalRun()
    {
        _environment.GetEnvironmentVariables().Returns(new Hashtable { ["smithysettings_Target__Port"] = "1433" });

        Assert.That(Show(), Is.False, "a run configured from the environment alone must run, with or without a file");
        WriteSettings(_toolDir, "{ \"Target\": { \"Server\": \"\" }, \"SchemaPackagePath\": \"\" }");
        Assert.That(Show(), Is.False);

        _environment.DidNotReceive().Exit(Arg.Any<int>());
    }

    [Test]
    public void AConfigurationTheHostRegistered_IsANormalRun()
    {
        FactoryContainer.Register<Microsoft.Extensions.Configuration.IConfigurationRoot>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        try
        {
            Assert.That(Show(), Is.False);
            _environment.DidNotReceive().Exit(Arg.Any<int>());
        }
        finally
        {
            FactoryContainer.Unregister<Microsoft.Extensions.Configuration.IConfigurationRoot>();
        }
    }

    [Test]
    public void AnUnreadableSettingsFile_IsLeftToTheNormalRunToReport()
    {
        WriteSettings(_toolDir, "{ \"Target\": ");

        Assert.That(Show(), Is.False);
    }

    [Test]
    public void ASettingWithNoMeaning_CountsAsConfigurationButIsNotListed()
    {
        var settings = new List<FirstRunSetting>(QuenchSettings) { new("Target:Platform", null) };
        var tongsFile = Path.Join(_toolDir, "SchemaTongs.settings.json");

        File.WriteAllText(tongsFile, "{ \"Target\": { \"Platform\": \"SqlServer\" } }");
        Assert.That(GettingStarted.ShowIfUnconfigured("SchemaTongs", "extracts", settings, [], _cwd, _toolDir), Is.False);

        File.WriteAllText(tongsFile, "{ }");
        Assert.That(GettingStarted.ShowIfUnconfigured("SchemaTongs", "extracts", settings, [], _cwd, _toolDir), Is.True);
        Assert.That(_console.ToString(), Does.Not.Contain("Target:Platform"));
    }
}
