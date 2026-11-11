// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using DataTongs.IntegrationTests.Support;
using log4net;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace DataTongs.IntegrationTests.Shared;

/// <summary>
/// A run driven by its settings, as the CLI drives it, on the MySQL family: a table with a key writes its content file and,
/// when asked, its script; <c>OutputScripts</c> false writes the content file alone; a table with no key to match on is
/// skipped by name and makes the run exit 1.
/// </summary>
[NonParallelizable]
public abstract class DataTongsCastSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string Database { get; }
    protected abstract string DatabaseConnectionString { get; }

    private const string Table = "dt_cast_t";
    private IDbConnection _connection = null!;
    private string _outputDir = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(DatabaseConnectionString);
        _connection.Open();
        _outputDir = Path.Join(Path.GetTempPath(), $"DataTongsCast_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outputDir);
        Exec($"DROP TABLE IF EXISTS `{Table}`");
    }

    [TearDown]
    public void TearDown()
    {
        try { Exec($"DROP TABLE IF EXISTS `{Table}`"); }
        finally
        {
            _connection.Dispose();
            if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, true);
        }
    }

    [Test]
    public void ATableWithAKey_WritesItsContentFile_AndItsScript()
    {
        CreateTable(withKey: true);
        var (exitCode, _) = Cast(outputScripts: true);

        var files = Directory.GetFiles(_outputDir).Select(Path.GetFileName).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(File.ReadAllText(Path.Join(_outputDir, $"{Table}.tabledata")), Does.Contain("\"Test Item 1\"").And.Contain("\"Test Item 3\""));
            if (WritesScripts())
                Assert.That(files, Does.Contain($"Populate {Table}.sql"), string.Join(", ", files));
        });
    }

    [Test]
    public void OutputScriptsFalse_WritesTheContentFileAlone()
    {
        CreateTable(withKey: true);
        var (exitCode, _) = Cast(outputScripts: false);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.Zero);
            Assert.That(Directory.GetFiles(_outputDir).Select(Path.GetFileName), Is.EquivalentTo(new[] { $"{Table}.tabledata" }));
        });
    }

    [Test]
    public void ATableWithNoKey_IsSkippedByName_AndTheRunExitsOne()
    {
        CreateTable(withKey: false);
        var (exitCode, errors) = Cast(outputScripts: true);

        Assert.Multiple(() =>
        {
            Assert.That(exitCode, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(_outputDir), Is.Empty, "nothing is written for a skipped table");
            Assert.That(errors, Has.Some.Contains($"No match columns found for {Table}. Skipping table."));
        });
    }

    private bool WritesScripts()
    {
        using var probe = _connection.CreateCommand();
        return !(Platform == Platform.MySQL && TargetVersionDetector.Detect(probe, Platform, Database).ServerComparable is > 0 and < 800);
    }

    private void CreateTable(bool withKey)
    {
        Exec($"CREATE TABLE `{Table}` (`Id` INT NOT NULL{(withKey ? " PRIMARY KEY" : "")}, `Name` VARCHAR(100) NOT NULL, `Description` VARCHAR(500) NULL)");
        Exec($"INSERT INTO `{Table}` (`Id`, `Name`, `Description`) VALUES (1, 'Test Item 1', 'one'), (2, 'Test Item 2', NULL), (3, 'Test Item 3', 'three')");
    }

    private (int ExitCode, List<string> Errors) Cast(bool outputScripts)
    {
        var errors = new List<string>();
        var errorLog = Substitute.For<ILog>();
        errorLog.When(l => l.Error(Arg.Any<object>())).Do(ci => errors.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));
        lock (FactoryContainer.SharedLockObject)
        {
            LogFactory.Register("ErrorLog", errorLog);
            LogFactory.Register("ProgressLog", Substitute.For<ILog>());
            ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
            using var scope = IsolatedConfigScope.Create();
            var config = scope.Config;
            config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
            config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
            config["Source:User"] = config[$"{ConfigPrefix}:User"];
            config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
            config["Source:Database"] = Database;
            foreach (var prop in global::Schema.DataAccess.ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
            config["Tables:0:Name"] = Table;
            config["ShouldCast:OutputContentFiles"] = "true";
            config["ShouldCast:OutputScripts"] = outputScripts ? "true" : "false";
            config["ShouldCast:TokenizeScripts"] = "false";
            config["ContentPath"] = _outputDir;
            config["ScriptPath"] = _outputDir;
            try
            {
                var tongs = new global::DataTongs.DataTongs(Platform);
                tongs.CastData();
                return (tongs.ExitCode, errors);
            }
            finally
            {
                LogFactory.Clear();
            }
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
