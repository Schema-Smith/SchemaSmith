// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Schema.Configuration;

namespace Schema.UnitTests.Configuration;

[TestFixture]
public class SettingsContractTests
{
    private static IConfigurationRoot Config(params (string Key, string Value)[] entries)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in entries) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void MistypedKeyInAnOwnedSection_IsReported()
    {
        // The motivating case: Target:Sever binds nothing and the run proceeds as though the setting
        // were absent — the config-side twin of the unrecognised-CLI-argument defect.
        var unrecognized = SettingsContract.UnrecognizedKeys(
            Config(("Target:Server", "srv"), ("Target:Sever", "typo")), SettingsTool.SchemaQuench);

        Assert.That(unrecognized, Is.EquivalentTo(new[] { "Target:Sever" }));
    }

    [Test]
    public void CorrectKeys_ReportNothing()
    {
        var unrecognized = SettingsContract.UnrecognizedKeys(
            Config(("Target:Server", "srv"), ("Target:User", "u"), ("Target:Password", "p"),
                   ("Target:IntegratedSecurity", "true")), SettingsTool.SchemaQuench);

        Assert.That(unrecognized, Is.Empty);
    }

    // SchemaTongs and DataTongs connect with Target:* when no Source:* is given, so those keys are read -- the warning
    // that they "will have no effect" was false.
    [TestCase(SettingsTool.SchemaTongs)]
    [TestCase(SettingsTool.DataTongs)]
    public void TheTongsTargetConnectionFallbacks_AreRecognized(SettingsTool tool)
    {
        var unrecognized = SettingsContract.UnrecognizedKeys(
            Config(("Target:Platform", "SqlServer"), ("Target:Server", "srv"), ("Target:User", "u"), ("Target:Password", "p"),
                   ("Target:Port", "1433"), ("Target:IntegratedSecurity", "false"), ("Target:ConnectionProperties:Encrypt", "true")),
            tool);

        Assert.That(unrecognized, Is.Empty);
    }

    [Test]
    public void DataTongsReadsTheTargetCompatEncodingFallback()
    {
        Assert.That(SettingsContract.UnrecognizedKeys(Config(("Target:Platform", "SqlServer"), ("Target:CompatEncoding", "Xml")),
            SettingsTool.DataTongs), Is.Empty);
    }

    // ScriptTokens are read only by a Product load, which neither Tongs tool performs; LogHygiene is read by the shared
    // configuration logger, so every tool reads it.
    [Test]
    public void ScriptTokensAndLogHygiene_AreClaimedByTheToolsThatReadThem()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SettingsContract.AcceptedKeys(SettingsTool.SchemaTongs), Does.Not.Contain(SettingsKeys.ScriptTokens));
            Assert.That(SettingsContract.AcceptedKeys(SettingsTool.DataTongs), Does.Not.Contain(SettingsKeys.ScriptTokens));
            Assert.That(SettingsContract.AcceptedKeys(SettingsTool.SchemaQuench), Does.Contain(SettingsKeys.ScriptTokens));
            foreach (var tool in Enum.GetValues<SettingsTool>())
                Assert.That(SettingsContract.AcceptedKeys(tool), Does.Contain(SettingsKeys.LogHygiene), tool.ToString());
        });
    }

    [Test]
    public void ForeignTopLevelSection_IsIgnored()
    {
        // The integration harness's test.settings.json carries SqlServer/PostgreSQL/MySQL/MariaDB
        // sections that are not product settings at all. Reporting them would bury the real case and
        // would break the existing DidNotReceive().Warn(Arg.Any<string>()) assertions.
        var unrecognized = SettingsContract.UnrecognizedKeys(
            Config(("SqlServer:Server", "127.0.0.1"), ("PostgreSQL:Password", "pw"),
                   ("MySQL:Port", "3306"), ("MariaDB:User", "u")), SettingsTool.SchemaQuench);

        Assert.That(unrecognized, Is.Empty);
    }

    [TestCase("ScriptTokens:MainDB")]
    [TestCase("Target:ConnectionProperties:Encrypt")]
    [TestCase("Target:TemplateTargets:TenantBody:Databases:0")]
    public void OpenSectionChildren_AreNeverUnrecognized(string key)
    {
        Assert.That(SettingsContract.UnrecognizedKeys(Config((key, "v")), SettingsTool.SchemaQuench), Is.Empty);
    }

    [TestCase("Target:Databases:0")]
    [TestCase("Target:Templates:2")]
    [TestCase("Target:Schemas:11")]
    public void ArrayElements_AreNotTreatedAsMembers(string key)
    {
        Assert.That(SettingsContract.UnrecognizedKeys(Config((key, "v")), SettingsTool.SchemaQuench), Is.Empty);
    }

    [Test]
    public void AnotherToolsSection_DoesNotWarn()
    {
        // SchemaQuench does not own Source: — a shared or repurposed file must not produce noise.
        Assert.That(SettingsContract.UnrecognizedKeys(
            Config(("Source:Server", "srv")), SettingsTool.SchemaQuench), Is.Empty);
    }

    [Test]
    public void MistypedShouldCastKey_IsReportedForTheTongs()
    {
        var unrecognized = SettingsContract.UnrecognizedKeys(
            Config(("ShouldCast:Tables", "true"), ("ShouldCast:Tabels", "true")), SettingsTool.SchemaTongs);

        Assert.That(unrecognized, Is.EquivalentTo(new[] { "ShouldCast:Tabels" }));
    }

    [Test]
    public void WarnOnUnrecognizedKeys_NamesTheKeyAndTheTool()
    {
        var warnings = new List<string>();
        SettingsContract.WarnOnUnrecognizedKeys(
            Config(("Target:Sever", "typo")), SettingsTool.SchemaQuench, warnings.Add);

        Assert.That(warnings, Has.Count.EqualTo(1));
        Assert.That(warnings[0], Does.Contain("Target:Sever"));
        Assert.That(warnings[0], Does.Contain("SchemaQuench"));
    }

    [Test]
    public void WarnOnUnrecognizedKeys_SilentWhenEverythingIsRecognized()
    {
        var warnings = new List<string>();
        SettingsContract.WarnOnUnrecognizedKeys(
            Config(("Target:Server", "srv")), SettingsTool.SchemaQuench, warnings.Add);

        Assert.That(warnings, Is.Empty);
    }

    [Test]
    public void NullConfigOrWarner_IsSafe()
    {
        Assert.That(SettingsContract.UnrecognizedKeys(null, SettingsTool.SchemaQuench), Is.Empty);
        Assert.DoesNotThrow(() => SettingsContract.WarnOnUnrecognizedKeys(Config(("Target:Sever", "x")), SettingsTool.SchemaQuench, null));
    }

    [Test]
    public void AcceptedKeys_AreToolScoped()
    {
        var quench = SettingsContract.AcceptedKeys(SettingsTool.SchemaQuench);
        var shears = SettingsContract.AcceptedKeys(SettingsTool.SchemaShears);

        Assert.That(quench, Does.Contain(SettingsKeys.Target.Server));
        Assert.That(quench, Does.Not.Contain(SettingsKeys.SourcePath), "SchemaShears' package-carving keys are not SchemaQuench settings");
        Assert.That(shears, Does.Contain(SettingsKeys.SourcePath));
        Assert.That(SettingsContract.AllAcceptedKeys(), Is.SupersetOf(quench));
    }

    [Test]
    public void EveryKeyInTheShippedSamples_IsRegistered()
    {
        // A typo in a shipped sample is inert and invisible today; this makes it a test failure.
        var samples = new[]
            {
                (Tool: SettingsTool.SchemaQuench, File: "SchemaQuench/SchemaQuench.settings.json"),
                (Tool: SettingsTool.SchemaTongs, File: "SchemaTongs/SchemaTongs.settings.json"),
                (Tool: SettingsTool.DataTongs, File: "DataTongs/DataTongs.settings.json"),
                (Tool: SettingsTool.SchemaShears, File: "SchemaShears/SchemaShears.settings.json")
            }
            .Select(s => (s.Tool, s.File, Path: System.IO.Path.Join(RepoRoot(), s.File)))
            .Where(s => System.IO.File.Exists(s.Path));

        foreach (var (tool, file, path) in samples)
        {
            var config = new ConfigurationBuilder().AddJsonFile(path).Build();
            Assert.That(SettingsContract.UnrecognizedKeys(config, tool), Is.Empty,
                $"{file} contains keys {tool} does not read");
        }
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Join(dir.FullName, "SchemaSmith.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? "";
    }
    
    // The ShouldCast keys live in one class but are read by two different tools. Handing both tools the whole
    // set meant a real key in the wrong settings file did nothing and warned about nothing -- the unrecognised-
    // key check missing inside its own problem space. A new key with no ReadBy would silently rejoin that
    // behaviour, so the attribute is required rather than optional.
    [Test]
    public void EveryShouldCastKey_DeclaresExactlyOneReadingTool()
    {
        var unattributed = typeof(SettingsKeys.ShouldCast).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)
                        && f.Name != nameof(SettingsKeys.ShouldCast.Section))
            .Where(f => f.GetCustomAttribute<ReadByAttribute>() == null)
            .Select(f => f.Name)
            .ToList();

        Assert.That(unattributed, Is.Empty,
            "every ShouldCast key must name the tool that reads it with [ReadBy]; without one it is accepted "
            + "by no tool and silently does nothing: " + string.Join(", ", unattributed));
    }

    [Test]
    public void ShouldCastKeys_ArePartitionedBetweenTheTwoExtractionTools()
    {
        var schemaTongs = SettingsContract.AcceptedKeys(SettingsTool.SchemaTongs)
            .Where(k => k.StartsWith("ShouldCast:", StringComparison.OrdinalIgnoreCase)).ToHashSet();
        var dataTongs = SettingsContract.AcceptedKeys(SettingsTool.DataTongs)
            .Where(k => k.StartsWith("ShouldCast:", StringComparison.OrdinalIgnoreCase)).ToHashSet();

        Assert.Multiple(() =>
        {
            Assert.That(schemaTongs.Intersect(dataTongs), Is.Empty,
                "no ShouldCast key is read by both tools, so none should be accepted by both");
            Assert.That(schemaTongs, Is.Not.Empty);
            Assert.That(dataTongs, Is.Not.Empty);
            Assert.That(dataTongs, Does.Not.Contain(SettingsKeys.ShouldCast.Collations),
                "SchemaTongs reads this one; DataTongs accepting it is the over-claim");
            Assert.That(schemaTongs, Does.Not.Contain(SettingsKeys.ShouldCast.MergeUpdate),
                "DataTongs reads this one");
        });
    }
}
