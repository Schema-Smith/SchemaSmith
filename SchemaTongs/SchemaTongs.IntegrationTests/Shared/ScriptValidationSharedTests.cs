// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// With <c>ValidateScripts</c> on, an extracted script that no longer creates is saved as <c>.sqlerror</c>, not
/// <c>.sql</c>, and the cleanup script names it, so a package never carries an object its deploy would fail on. A view
/// whose table was dropped is the MySQL-family case: the engine keeps the view, and recreating it fails.
/// </summary>
public abstract class ScriptValidationSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string FixtureConnectionString { get; }

    private const string TemplateName = "Main";

    private string _db = "";
    private string _tempProductPath = "";

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _db = $"tongs_validate_{Guid.NewGuid():N}"[..30];
        Exec($"CREATE DATABASE `{_db}`");
        Exec("CREATE TABLE kept_t (id INT NOT NULL PRIMARY KEY)", _db);
        Exec("CREATE TABLE gone_t (id INT NOT NULL PRIMARY KEY)", _db);
        Exec("CREATE VIEW v_valid AS SELECT id FROM kept_t", _db);
        Exec("CREATE VIEW v_invalid AS SELECT id FROM gone_t", _db);
        Exec("DROP TABLE gone_t", _db);
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Exec($"DROP DATABASE IF EXISTS `{_db}`");
        if (!string.IsNullOrEmpty(_tempProductPath) && Directory.Exists(_tempProductPath))
        {
            try { Directory.Delete(_tempProductPath, recursive: true); } catch (IOException) { /* best effort */ }
        }
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    [Test]
    public void AViewThatNoLongerCreates_IsSavedAsSqlError_AndTheCleanupScriptDropsIt()
    {
        _tempProductPath = Path.Join(Path.GetTempPath(), $"TongsValidate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProductPath);
        var warnings = new List<string>();
        var progressLog = Substitute.For<ILog>();
        progressLog.When(l => l.Warn(Arg.Any<object>())).Do(ci => warnings.Add(ci.ArgAt<object>(0)?.ToString() ?? ""));

        lock (FactoryContainer.SharedLockObject)
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
            FactoryContainer.Register<IConfigurationRoot>(BuildConfig());
            FactoryContainer.Register(Substitute.For<IEnvironment>());
            LogFactory.Register("ErrorLog", Substitute.For<ILog>());
            LogFactory.Register("ProgressLog", progressLog);
            try
            {
                new SchemaTongs(Platform).CastTemplate();
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }

        var template = Path.Join(_tempProductPath, "Templates", TemplateName);
        var views = Path.Join(template, "Views");
        var files = Directory.Exists(views) ? Directory.GetFiles(views).Select(Path.GetFileName).ToList() : [];
        var cleanupPath = Path.Join(template, "Logs", "_InvalidObjectCleanup.sql");
        Assert.Multiple(() =>
        {
            Assert.That(files, Does.Contain("v_valid.sql"), "the view that creates is extracted: " + string.Join(", ", files));
            Assert.That(files, Does.Contain("v_invalid.sqlerror").And.Not.Contain("v_invalid.sql"),
                "the view that no longer creates is set aside; warnings: " + string.Join(" | ", warnings));
            Assert.That(File.Exists(cleanupPath) ? File.ReadAllText(cleanupPath) : "<no cleanup script>",
                Does.Contain("DROP VIEW").And.Contain("v_invalid").And.Not.Contain("v_valid"));
        });
    }

    private void Exec(string sql, string database = null)
    {
        using var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(FixtureConnectionString + "Database=information_schema;");
        conn.Open();
        if (database != null) conn.ChangeDatabase(database);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private IConfigurationRoot BuildConfig()
    {
        var root = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        var values = new Dictionary<string, string>
        {
            ["Source:Server"] = root[$"{ConfigPrefix}:Server"] ?? "127.0.0.1",
            ["Source:Port"] = root[$"{ConfigPrefix}:Port"],
            ["Source:User"] = root[$"{ConfigPrefix}:User"],
            ["Source:Password"] = root[$"{ConfigPrefix}:Password"],
            ["Source:Database"] = _db,
            ["Product:Path"] = _tempProductPath,
            ["Product:Name"] = "ValidateProduct",
            ["Template:Name"] = TemplateName,
            ["ShouldCast:Tables"] = "false",
            ["ShouldCast:Views"] = "true",
            ["ShouldCast:Procedures"] = "false",
            ["ShouldCast:Functions"] = "false",
            ["ShouldCast:TableTriggers"] = "false",
            ["ShouldCast:Events"] = "false",
            ["ShouldCast:ValidateScripts"] = "true",
            ["ShouldCast:SaveInvalidScripts"] = "true",
        };
        foreach (var prop in ConnectionString.ReadProperties(root, $"{ConfigPrefix}:ConnectionProperties"))
            values[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
