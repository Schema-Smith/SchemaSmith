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
/// scripts/run-server-settings-legs.sh starts one per engine and sets SCHEMASMITH_DISPOSABLE_SERVER=1; CI does not run them. They
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

    // Read-only settings, to render a script for at their current value: a number and a boolean that is ON, which the
    // server reads back as 1. Empty where the engine's scripts cannot meet one.
    protected virtual string[] ReadOnlyNames => [];

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

    private const string BeforeScript = "01-before-marker.sql";

    // Runs the product's server settings step against the throwaway server and returns its ServerSetting audit rows. With
    // wholeProduct, it runs the whole deploy instead, with one product Before script, and a stopped deploy logs "STOPPED".
    private string[] Run(string settingsJson, bool whatIf = false, bool wholeProduct = false)
    {
        var folders = wholeProduct ? """, "ScriptFolders": [ { "FolderPath": "Before", "QuenchSlot": "Before" } ]""" : "";
        File.WriteAllText(Path.Join(_packageDir, "Product.json"), $$"""{ "Name": "ServerSettingsProduct", "Platform": "{{Platform}}", "TemplateOrder": []{{folders}} }""");
        File.WriteAllText(Path.Join(_packageDir, "ServerSettings.json"), settingsJson);
        if (wholeProduct)
        {
            Directory.CreateDirectory(Path.Join(_packageDir, "Before"));
            File.WriteAllText(Path.Join(_packageDir, "Before", BeforeScript), "SELECT 1");
        }
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
                if (!wholeProduct) quench.QuenchServerSettings();
                else
                    try { quench.QuenchProduct(suppressKindlingForTesting: true); }
                    catch (RunFailedException e) { _log.Add("STOPPED " + e.Message); }
                return quench.ChangeAudit.Snapshot().Where(r => r.ObjectType == "ServerSetting").Select(r => $"{r.ObjectName} {r.Action}").ToArray();
            }
            finally
            {
                LogFactory.Clear();
                FactoryContainer.Register<IConfigurationRoot>(saved);
            }
        }
    }

    protected string Lines => string.Join("\n", _log);

    protected string[] RunDeclared(string name, string value) => Run(Declare(name, value, fix: true, "Error"));

    private Schema.Settings.ISettingDescriptor Descriptor()
    {
        using var cmd = _admin.CreateCommand();
        var major = Schema.Settings.SettingDescriptors.MajorVersion(Platform, TargetVersionDetector.Detect(cmd, Platform).ServerComparable);
        return Schema.Settings.SettingDescriptors.For(Platform, Schema.Domain.Settings.SettingScope.Server, Kind, major);
    }

    // SchemaTongs' Script output: a script per setting, which a package runs on every deploy.
    [Test]
    public void TheRenderedScript_SetsTheValue_AndIsSafeToRunAgain()
    {
        var script = Descriptor().RenderScript(DynamicName, DynamicValue, null);

        Exec(script);
        Exec(script);

        Assert.That(() => ReadValue(DynamicName), Is.EqualTo(DynamicValue).After(3000, 100), script);
    }

    // A read-only setting already at its value must not fail the deploy that runs its script, every time.
    [Test]
    public void TheRenderedScript_ForAReadOnlySettingAtItsValue_Runs()
    {
        if (ReadOnlyNames.Length == 0) Assert.Ignore("This engine's scripts do not meet read-only settings.");
        var descriptor = Descriptor();
        Assert.Multiple(() =>
        {
            foreach (var name in ReadOnlyNames)
            {
                // The value as extraction writes it, which is how a package declares it.
                using var cmd = _admin.CreateCommand();
                var current = descriptor.Read(cmd, name, null).Running;
                var script = descriptor.RenderScript(name, current, null);

                Assert.DoesNotThrow(() => Exec(script), $"{name} = {current}\n{script}");
                Assert.That(descriptor.Read(cmd, name, null).Running, Is.EqualTo(current), name);
            }
        });
    }

    // The step inside a real deploy: it runs, and before the product's Before scripts.
    [Test]
    public void AWholeDeploy_FixesTheSetting_BeforeTheBeforeScriptsRun()
    {
        var audit = Run(Declare(DynamicName, DynamicValue, fix: true, "Error"), wholeProduct: true);

        var settingLine = _log.FindIndex(l => l != null && l.Contains($"'{DynamicName}'"));
        var beforeLine = _log.FindIndex(l => l != null && l.Contains(BeforeScript));
        Assert.Multiple(() =>
        {
            Assert.That(audit, Is.EqualTo(new[] { $"{Kind} {DynamicName} changed" }), Lines);
            Assert.That(ReadValue(DynamicName), Is.EqualTo(DynamicValue));
            Assert.That(_log, Has.None.StartsWith("STOPPED"), Lines);
            Assert.That(settingLine, Is.GreaterThanOrEqualTo(0), "the setting is logged\n" + Lines);
            Assert.That(beforeLine, Is.GreaterThan(settingLine), "the Before script runs after the setting is fixed\n" + Lines);
        });
    }

    // A setting the deploy enforces and cannot fix stops it before the Before scripts change anything.
    [Test]
    public void AWholeDeploy_StoppedByASetting_RunsNoBeforeScript()
    {
        Run(Declare("schemasmith_no_such_setting", "1", fix: true, "Error"), wholeProduct: true);

        Assert.Multiple(() =>
        {
            Assert.That(_log, Has.Some.StartsWith("STOPPED Server settings stopped the deploy"), Lines);
            Assert.That(_log, Has.None.Contains(BeforeScript), Lines);
        });
    }

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
        if (PendingRestart == null) Assert.Ignore("This engine cannot report a change waiting for a restart.");
        var p = PendingRestart.Value;

        var rows = Run(Declare(p.Name, p.Value, fix: true, "Warn"));

        Assert.That(rows, Is.EqualTo(new[] { $"{Kind} {p.Name} pendingRestart" }), Lines);
        Assert.That(_log, Has.Some.StartsWith("WARN").And.Contains($"set to {p.Value}; takes effect after a restart (running value "), Lines);
    }
}
