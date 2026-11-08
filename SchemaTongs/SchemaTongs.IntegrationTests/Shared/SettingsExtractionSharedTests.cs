// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using log4net;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Domain.Settings;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaTongs.IntegrationTests.Shared;

/// <summary>
/// D12 settings extraction against a real server, into a real folder. Each engine creates a database with one setting
/// changed from what a new database gets; capturing it is the configuration under test, so the database is the fixture's
/// own. Server settings are only read, so they are extracted from the suite's server as it is.
/// </summary>
[NonParallelizable]
public abstract class SettingsExtractionSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string AdminDatabase { get; }

    protected abstract void CreateSourceDatabase(IDbCommand cmd, string name);
    protected abstract void DropSourceDatabase(IDbCommand cmd, string name);

    // The database setting the fixture changed, as extraction writes it, and one the database left at its default.
    protected abstract (string Kind, string Name, string Value) ChangedSetting { get; }
    protected abstract (string Kind, string Name) DefaultSetting { get; }

    // A server setting that names the machine, which is left out unless allowlisted.
    protected abstract (string Kind, string Name) MachineIdentitySetting { get; }

    private string _connectionString;
    private string _sourceDb;
    private string _productDir;
    private readonly List<string> _warnings = [];

    [OneTimeSetUp]
    public void CreateSource()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
        _connectionString = ConnectionString.Build(Platform, config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1", AdminDatabase, config[$"{ConfigPrefix}:User"],
            config[$"{ConfigPrefix}:Password"], config[$"{ConfigPrefix}:Port"], ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"));
        _sourceDb = $"settingsx_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        CreateSourceDatabase(cmd, _sourceDb);
    }

    [OneTimeTearDown]
    public void DropSource()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        DropSourceDatabase(cmd, _sourceDb);
    }

    [SetUp]
    public void FreshProductFolder()
    {
        _productDir = Path.Join(Path.GetTempPath(), $"SettingsCast_{Guid.NewGuid():N}");
        _warnings.Clear();
    }

    [TearDown]
    public void DeleteProductFolder()
    {
        if (Directory.Exists(_productDir)) Directory.Delete(_productDir, true);
    }

    private IDbConnection Open()
    {
        var conn = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void Extract(IReadOnlyDictionary<string, string> settings)
    {
        var progress = Substitute.For<ILog>();
        progress.When(l => l.Warn(Arg.Any<object>())).Do(ci => _warnings.Add(ci.ArgAt<object>(0)?.ToString()));
        lock (FactoryContainer.SharedLockObject)
        {
            try
            {
                LogFactory.Register("ErrorLog", Substitute.For<ILog>());
                LogFactory.Register("ProgressLog", progress);
                FactoryContainer.Register(Substitute.For<IEnvironment>());
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
                config["Source:Server"] = config[$"{ConfigPrefix}:Server"] ?? "127.0.0.1";
                config["Source:Port"] = config[$"{ConfigPrefix}:Port"];
                config["Source:User"] = config[$"{ConfigPrefix}:User"];
                config["Source:Password"] = config[$"{ConfigPrefix}:Password"];
                config["Source:Database"] = _sourceDb;
                config["Product:Path"] = _productDir;
                config["Product:Name"] = "SettingsCast";
                config["Template:Name"] = "Main";
                foreach (var prop in ConnectionString.ReadProperties(config, $"{ConfigPrefix}:ConnectionProperties"))
                    config[$"Source:ConnectionProperties:{prop.Key}"] = prop.Value;
                foreach (var (key, value) in settings) config[key] = value;
                FactoryContainer.Register<IConfigurationRoot>(config);
                new SchemaTongs(Platform).CastTemplate();
            }
            finally
            {
                FactoryContainer.Clear();
                LogFactory.Clear();
            }
        }
    }

    private string TemplateDir => Path.Join(_productDir, "Templates", "Main");
    private SettingsFile Load(string path, SettingScope scope) => SettingsFile.LoadIfPresent(path, Platform, scope);

    [Test]
    public void WithoutTheSwitches_NoSettingsFileOrSchemaIsWritten()
    {
        Extract(new Dictionary<string, string>());

        var written = Directory.GetFiles(_productDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.That(written, Has.None.Contains("Settings.json").And.None.Contains("settings.").IgnoreCase, string.Join(", ", written));
    }

    [Test]
    public void DatabaseSettings_AreWrittenAsJson_WithTheirSchema_AndLoad()
    {
        Extract(new Dictionary<string, string> { ["ShouldCast:DatabaseSettings"] = "true" });

        var path = Path.Join(TemplateDir, SettingsFile.DatabaseFileName);
        var file = Load(path, SettingScope.Database);
        var (kind, name, value) = ChangedSetting;
        Assert.Multiple(() =>
        {
            Assert.That(file, Is.Not.Null, "DatabaseSettings.json was written");
            Assert.That(file!.Settings.Select(s => $"{s.Kind} {s.Name} {s.Value} {s.Fix} {s.OnDrift}"), Has.Member($"{kind} {name} {value} False Warn"),
                File.ReadAllText(path));
            Assert.That(file.Settings.Select(s => s.Name), Has.No.Member(DefaultSetting.Name), "a setting at its default is left out");
            Assert.That(File.ReadAllText(path), Does.Contain("\"$schema\""), "the file points at its editor schema");
            Assert.That(File.Exists(Path.Join(_productDir, ".json-schemas", $"databasesettings.{Platform.ToString().ToLowerInvariant()}.schema")), Is.True);
        });
    }

    [Test]
    public void AnAllowlist_CapturesExactlyItsSettings_DefaultOrNot()
    {
        var allowlist = Path.Join(Path.GetTempPath(), $"allow_{Guid.NewGuid():N}.json");
        File.WriteAllText(allowlist, $$"""{ "Database": [ { "Kind": "{{DefaultSetting.Kind}}", "Name": "{{DefaultSetting.Name}}" } ] }""");
        try
        {
            Extract(new Dictionary<string, string>
            {
                ["ShouldCast:DatabaseSettings"] = "true", ["ShouldCast:SettingsAllowlist"] = allowlist, ["ShouldCast:SettingsDefaultPolicy"] = "EnforceError"
            });

            var file = Load(Path.Join(TemplateDir, SettingsFile.DatabaseFileName), SettingScope.Database);
            Assert.That(file!.Settings.Select(s => $"{s.Name} {s.Fix} {s.OnDrift}"), Is.EqualTo(new[] { $"{DefaultSetting.Name} True Error" }));
        }
        finally
        {
            File.Delete(allowlist);
        }
    }

    [Test]
    public void ScriptOutput_WritesARunnableFolder_RegisteredAsSetup_AndNoJson()
    {
        Extract(new Dictionary<string, string> { ["ShouldCast:DatabaseSettings"] = "true", ["ShouldCast:SettingsOutput"] = "Script" });

        var (kind, name, _) = ChangedSetting;
        var template = File.ReadAllText(Path.Join(TemplateDir, "Template.json"));
        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(TemplateDir, SettingsFile.DatabaseFileName)), Is.False);
            Assert.That(Directory.GetFiles(Path.Join(TemplateDir, "DatabaseSettings")).Select(Path.GetFileName), Has.Member($"{kind}.{name}.sql"));
            Assert.That(template, Does.Contain("\"DatabaseSettings\"").And.Contain("\"Setup\""), template);
        });
    }

    [Test]
    public void ServerSettings_LeaveOutTheMachine_UnlessAllowlisted()
    {
        var allowlist = Path.Join(Path.GetTempPath(), $"allow_{Guid.NewGuid():N}.json");
        File.WriteAllText(allowlist, $$"""{ "Server": [ { "Kind": "{{MachineIdentitySetting.Kind}}", "Name": "{{MachineIdentitySetting.Name}}" } ] }""");
        try
        {
            Extract(new Dictionary<string, string> { ["ShouldCast:ServerSettings"] = "true" });
            var plain = Load(Path.Join(_productDir, SettingsFile.ServerFileName), SettingScope.Server);
            DeleteProductFolder();

            Extract(new Dictionary<string, string> { ["ShouldCast:ServerSettings"] = "true", ["ShouldCast:SettingsAllowlist"] = allowlist });
            var allowed = Load(Path.Join(_productDir, SettingsFile.ServerFileName), SettingScope.Server);

            Assert.Multiple(() =>
            {
                Assert.That(plain, Is.Not.Null, "ServerSettings.json was written and loads");
                Assert.That(plain!.Settings.Select(s => s.Name.ToLowerInvariant()), Has.No.Member(MachineIdentitySetting.Name.ToLowerInvariant()));
                Assert.That(allowed!.Settings.Select(s => s.Name.ToLowerInvariant()), Is.EqualTo(new[] { MachineIdentitySetting.Name.ToLowerInvariant() }));
            });
        }
        finally
        {
            File.Delete(allowlist);
        }
    }
}
