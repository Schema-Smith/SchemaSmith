// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// D12: DatabaseSettings.json against a real database on every engine. Each engine supplies one harmless, dynamic database
/// setting and two values for it; the run's own main database is changed and put back after each test.
/// </summary>
[NonParallelizable]
public abstract class DatabaseSettingsSharedTests : ObjectScriptDeployFixture
{
    protected sealed override string ProductName => "DatabaseSettingsProduct";

    protected abstract string Kind { get; }
    protected abstract string Name { get; }
    protected abstract string ValueA { get; }
    protected abstract string ValueB { get; }

    // The value as the test reads it back, in the same spelling as ValueA / ValueB.
    protected abstract string ReadCurrent();
    protected abstract string SetSql(string value);
    protected void SetCurrent(string value) => Exec(SetSql(value));
    protected abstract void Restore();
    protected abstract string EquivalentSpellingOfA { get; }
    protected abstract (string Name, string Value, string Action) Refused { get; }

    private const string Folders = """
        { "FolderPath": "Setup", "QuenchSlot": "Setup" },
        { "FolderPath": "Objects", "QuenchSlot": "Objects" }
        """;

    [SetUp]
    public void StartFromA()
    {
        SetCurrent(ValueA);
        Exec("DROP TABLE IF EXISTS settings_probe");
    }

    [TearDown]
    public void PutItBack()
    {
        Restore();
        Exec("DROP TABLE IF EXISTS settings_probe");
    }

    private void Package(string settingsJson, bool withObject = false)
    {
        WritePackage(Folders, "Setup", "Objects");
        if (withObject) WriteFile("Objects", "01-probe.sql", "CREATE TABLE settings_probe (id INT NOT NULL)");
        if (settingsJson != null) File.WriteAllText(Path.Join(TemplateDir, "DatabaseSettings.json"), settingsJson);
    }

    private string One(string value, bool fix, string onDrift, string name = null) => $$"""
        { "Settings": [ { "Kind": "{{Kind}}", "Name": "{{name ?? Name}}", "Value": "{{value}}", "Fix": {{(fix ? "true" : "false")}}, "OnDrift": "{{onDrift}}" } ] }
        """;

    private static string[] SettingRows(DeployResult run) =>
        run.Audit.Where(r => r.ObjectType == "DatabaseSetting").Select(r => $"{r.ObjectName} {r.Action}").ToArray();

    private static string Lines(DeployResult run) => string.Join("\n", run.Log);

    [Test]
    public void NoSettingsFile_NoStep_NoAudit_NoLog()
    {
        Package(null);

        var run = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(run.Log, Has.None.Contains("setting").IgnoreCase.And.None.Contains("Database settings"), Lines(run));
            Assert.That(SettingRows(run), Is.Empty);
            Assert.That(ReadCurrent(), Is.EqualTo(ValueA));
        });
    }

    [Test]
    public void ReportOnlyDrift_WarnsAndChangesNothing()
    {
        Package(One(ValueB, fix: false, "Warn"));

        var run = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(ReadCurrent(), Is.EqualTo(ValueA), "report-only changes nothing");
            Assert.That(SettingRows(run), Is.EqualTo(new[] { $"{Kind} {Name} drift" }));
            Assert.That(run.Log, Has.Some.StartsWith("WARN").And.Contains($"Database setting {Kind} '{Name}' is ").And.Contains($", declared {ValueB} (not fixed)"), Lines(run));
        });
    }

    [Test]
    public void EnforceFix_ChangesIt_AndARedeployIsSilent()
    {
        Package(One(ValueB, fix: true, "Error"));

        var first = Deploy();
        var afterFirst = ReadCurrent();
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(afterFirst, Is.EqualTo(ValueB));
            Assert.That(SettingRows(first), Is.EqualTo(new[] { $"{Kind} {Name} changed" }));
            Assert.That(first.Log, Has.Some.Contains($"Database setting {Kind} '{Name}': ").And.Contains($" -> {ValueB}"), Lines(first));
            Assert.That(SettingRows(second), Is.Empty, "a setting that matches is silent");
        });
    }

    [Test]
    public void AnEquivalentSpelling_IsNotDrift()
    {
        Package(One(EquivalentSpellingOfA, fix: false, "Error"));

        var run = Deploy();

        Assert.That(SettingRows(run), Is.Empty, Lines(run));
    }

    [Test]
    public void ReportErrorDrift_StopsTheDeploy_BeforeAnyObject()
    {
        Package(One(ValueB, fix: false, "Error"), withObject: true);

        var run = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'settings_probe'"), Is.Zero, "no object ran");
            Assert.That(ReadCurrent(), Is.EqualTo(ValueA));
            Assert.That(run.Log, Has.Some.StartsWith("ERROR").And.Contains("(not fixed)"), Lines(run));
        });
    }

    [Test]
    public void WhatIf_SaysWhatWouldChange_AndChangesNothing()
    {
        Package(One(ValueB, fix: true, "Warn"));

        var run = Deploy(whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(ReadCurrent(), Is.EqualTo(ValueA));
            Assert.That(SettingRows(run), Is.EqualTo(new[] { $"{Kind} {Name} wouldChange" }));
            Assert.That(run.Log, Has.Some.Contains($"would change: ").And.Contains($" -> {ValueB}"), Lines(run));
        });
    }

    [Test]
    public void AFixTheEngineRefuses_IsReported_AndTheDeployGoesOnUnderWarn()
    {
        var (name, value, action) = Refused;
        Package(One(value, fix: true, "Warn", name), withObject: true);

        var run = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(SettingRows(run), Is.EqualTo(new[] { $"{Kind} {name} {action}" }), Lines(run));
            Assert.That(Count("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'settings_probe'"), Is.EqualTo(1), "the deploy went on");
        });
    }

    // Setup runs first, so a script can prepare what a setting relies on: here it sets the very value the file declares.
    [Test]
    public void TheStepRunsAfterSetup()
    {
        Package(One(ValueB, fix: false, "Error"));
        WriteFile("Setup", "01-prepare.sql", SetSql(ValueB));

        var run = Deploy();

        Assert.That(SettingRows(run), Is.Empty, "the setting already matched when the step read it: " + Lines(run));
    }

    // Extraction's script output for this setting, run as a package would run it: twice, the second time changing nothing.
    [Test]
    public void TheRenderedScript_SetsTheValue_AndIsSafeToRunAgain()
    {
        var major = Schema.Settings.SettingDescriptors.MajorVersion(Platform, VersionComparable());
        var script = Schema.Settings.SettingDescriptors.For(Platform, Schema.Domain.Settings.SettingScope.Database, Kind, major).RenderScript(Name, ValueB, MainDb);

        Exec(script);
        var afterFirst = ReadCurrent();
        Exec(script);

        Assert.That(afterFirst, Is.EqualTo(ValueB), script);
        Assert.That(ReadCurrent(), Is.EqualTo(ValueB));
    }

    private int VersionComparable()
    {
        using var cmd = Connection.CreateCommand();
        return Schema.Utility.TargetVersionDetector.Detect(cmd, Platform).ServerComparable;
    }

    // No checkpoint: the step re-runs on resume even when the stopped run had already finished it, so a value changed
    // between the two runs (someone set it by hand, or the file was corrected) is still put right.
    [Test]
    public void AResumedRun_ReRunsTheStep()
    {
        var checkpointDir = Path.Join(Path.GetTempPath(), $"SettingsResume_{Guid.NewGuid():N}");
        try
        {
            Package(One(ValueB, fix: true, "Error"), withObject: true);
            WriteFile("Setup", "01-noop.sql", "SELECT 1");
            WriteFile("Objects", "02-broken.sql", "THIS IS NOT SQL");
            Deploy(expectSuccess: false, checkpointDir: checkpointDir);
            Assert.That(ReadCurrent(), Is.EqualTo(ValueB), "the first run fixed the setting before it stopped");
            Assert.That(Directory.Exists(checkpointDir) && Directory.GetFiles(checkpointDir, "*", SearchOption.AllDirectories).Length > 0, Is.True,
                "the stopped run left a resume point, so the next deploy really resumes");

            File.Delete(Path.Join(TemplateDir, "Objects", "02-broken.sql"));
            File.WriteAllText(Path.Join(TemplateDir, "DatabaseSettings.json"), One(ValueA, fix: true, "Error"));
            var resumed = Deploy(checkpointDir: checkpointDir, resume: true);

            Assert.That(ReadCurrent(), Is.EqualTo(ValueA), "the resumed run read and fixed the setting again\n" + Lines(resumed));
            Assert.That(SettingRows(resumed), Is.EqualTo(new[] { $"{Kind} {Name} changed" }));
        }
        finally
        {
            if (Directory.Exists(checkpointDir)) Directory.Delete(checkpointDir, true);
        }
    }
}
