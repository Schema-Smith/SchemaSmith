// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace SchemaQuench.IntegrationTests.ServerSettings;

/// <summary>
/// D12 server settings against a real server. A changed server setting, and above all one waiting for a restart, would
/// outlive the test on a server other sessions use, so these run only against a throwaway container:
/// scripts/run-server-settings-legs.sh starts one per engine and sets SCHEMASMITH_DISPOSABLE_SERVER=1, as CI does. They
/// sit outside the engine namespaces on purpose, so no suite fixture (and no full-text install) is needed: the step only
/// needs a connection, as the server's administrator.
/// </summary>
[NonParallelizable]
[Category("DisposableServer")]
public abstract class ServerSettingsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string ConfigPrefix { get; }
    protected abstract string Kind { get; }

    // A dynamic setting and a value that differs from its default.
    protected abstract string DynamicName { get; }
    protected abstract string DynamicValue { get; }
    protected abstract string ReadValue(string name);
    protected abstract void ResetSetting(string name);

    // False where a fix is in effect now but lost at the next restart (MariaDB's SET GLOBAL), which the run must say.
    protected virtual bool FixPersists => true;

    // A setting whose change waits for a restart; null where the engine cannot say (MariaDB, MySQL 5.7).
    protected abstract (string Name, string Value)? PendingRestart { get; }

    private string _packageDir;
    private IDbConnection _admin;
    private readonly List<string> _log = [];

    [OneTimeSetUp]
    public void RequireAThrowawayServer()
    {
        if (Environment.GetEnvironmentVariable("SCHEMASMITH_DISPOSABLE_SERVER") != "1")
            Assert.Ignore("Server settings change the server: run them with scripts/run-server-settings-legs.sh, never against a shared server.");
        _admin = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(ConnectionString.Build(Platform, Setting("Server") ?? "127.0.0.1",
            null, Setting("User"), Setting("Password"), Setting("Port"), ConnectionString.ReadProperties(TestConfig, $"{ConfigPrefix}:ConnectionProperties")));
        _admin.Open();
    }

    [OneTimeTearDown]
    public void Close() => _admin?.Dispose();

    [SetUp]
    public void Start()
    {
        _packageDir = Path.Join(Path.GetTempPath(), $"ServerSettings_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_packageDir);
        _log.Clear();
        ResetSetting(DynamicName);
        if (PendingRestart is { } p) ResetSetting(p.Name);
    }

    [TearDown]
    public void Finish()
    {
        ResetSetting(DynamicName);
        if (PendingRestart is { } p) ResetSetting(p.Name);
        if (Directory.Exists(_packageDir)) Directory.Delete(_packageDir, true);
    }

    private static IConfigurationRoot TestConfig => ConfigHelper.GetAppSettingsAndUserSecrets("test", null);
    private string Setting(string key) => TestConfig[$"{ConfigPrefix}:{key}"];

    protected object Scalar(string sql)
    {
        using var cmd = _admin.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    protected void Exec(string sql)
    {
        using var cmd = _admin.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string Declare(string name, string value, bool fix, string onDrift) => $$"""
        { "Settings": [ { "Kind": "{{Kind}}", "Name": "{{name}}", "Value": "{{value}}", "Fix": {{(fix ? "true" : "false")}}, "OnDrift": "{{onDrift}}" } ] }
        """;

    // Runs the product's server settings step against the throwaway server and returns its ServerSetting audit rows.
    private string[] Run(string settingsJson, bool whatIf = false)
    {
        File.WriteAllText(Path.Join(_packageDir, "Product.json"), $$"""{ "Name": "ServerSettingsProduct", "Platform": "{{Platform}}", "TemplateOrder": [] }""");
        File.WriteAllText(Path.Join(_packageDir, "ServerSettings.json"), settingsJson);
        lock (FactoryContainer.SharedLockObject)
        {
            var saved = FactoryContainer.Resolve<IConfigurationRoot>();
            try
            {
                var values = new Dictionary<string, string>
                {
                    ["SchemaPackagePath"] = _packageDir,
                    ["Target:Server"] = Setting("Server") ?? "127.0.0.1",
                    ["Target:Port"] = Setting("Port"),
                    ["Target:User"] = Setting("User"),
                    ["Target:Password"] = Setting("Password"),
                    ["CheckpointDirectory"] = Path.Join(_packageDir, "checkpoints"),
                    ["WhatIfONLY"] = whatIf ? "true" : "false"
                };
                foreach (var prop in ConnectionString.ReadProperties(TestConfig, $"{ConfigPrefix}:ConnectionProperties"))
                    values[$"Target:ConnectionProperties:{prop.Key}"] = prop.Value;
                FactoryContainer.Register<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(values!).Build());
                var progress = Substitute.For<log4net.ILog>();
                progress.When(l => l.Info(Arg.Any<object>())).Do(ci => _log.Add(ci.ArgAt<object>(0)?.ToString()));
                progress.When(l => l.Warn(Arg.Any<object>())).Do(ci => _log.Add("WARN " + ci.ArgAt<object>(0)));
                progress.When(l => l.Error(Arg.Any<object>())).Do(ci => _log.Add("ERROR " + ci.ArgAt<object>(0)));
                LogFactory.Register("ProgressLog", progress);
                LogFactory.Register("ErrorLog", Substitute.For<log4net.ILog>());

                var quench = new ProductQuench();
                quench.QuenchServerSettings();
                return quench.ChangeAudit.Snapshot().Where(r => r.ObjectType == "ServerSetting").Select(r => $"{r.ObjectName} {r.Action}").ToArray();
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(saved);
            }
        }
    }

    private string Lines => string.Join("\n", _log);

    [Test]
    public void AnEnforcedDynamicSetting_IsChanged_AndARunAfterIsSilent()
    {
        var first = Run(Declare(DynamicName, DynamicValue, fix: true, "Error"));
        var second = Run(Declare(DynamicName, DynamicValue, fix: true, "Error"));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new[] { $"{Kind} {DynamicName} changed" }), Lines);
            Assert.That(ReadValue(DynamicName), Is.EqualTo(DynamicValue));
            Assert.That(second, Is.Empty, "a setting that matches is silent");
            if (!FixPersists) Assert.That(_log, Has.Some.StartsWith("WARN").And.Contains("reverts at the next restart"), Lines);
        });
    }

    [Test]
    public void AReportOnlySetting_IsReported_AndLeftAlone()
    {
        var before = ReadValue(DynamicName);

        var rows = Run(Declare(DynamicName, DynamicValue, fix: false, "Warn"));

        Assert.That(rows, Is.EqualTo(new[] { $"{Kind} {DynamicName} drift" }), Lines);
        Assert.That(ReadValue(DynamicName), Is.EqualTo(before));
    }

    [Test]
    public void UnderWhatIf_NothingChanges()
    {
        var before = ReadValue(DynamicName);

        var rows = Run(Declare(DynamicName, DynamicValue, fix: true, "Error"), whatIf: true);

        Assert.That(rows, Is.EqualTo(new[] { $"{Kind} {DynamicName} wouldChange" }), Lines);
        Assert.That(ReadValue(DynamicName), Is.EqualTo(before));
    }

    [Test]
    public void ASettingThatWaitsForARestart_SaysSo_WithTheRunningValue()
    {
        if (PendingRestart is not { } p) Assert.Ignore("This engine cannot report a change waiting for a restart.");

        var rows = Run(Declare(p.Name, p.Value, fix: true, "Warn"));

        Assert.That(rows, Is.EqualTo(new[] { $"{Kind} {p.Name} pendingRestart" }), Lines);
        Assert.That(_log, Has.Some.StartsWith("WARN").And.Contains($"set to {p.Value}; takes effect after a restart (running value "), Lines);
    }
}
