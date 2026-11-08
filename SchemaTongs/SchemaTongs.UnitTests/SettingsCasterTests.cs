// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain.Settings;
using Schema.Settings;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class SettingsCasterTests
{
    private static readonly CapturedSetting[] Server =
    [
        new("Configuration", "max degree of parallelism", "8", IsDefault: false),
        new("Configuration", "cost threshold for parallelism", "5", IsDefault: true),
        new("Configuration", "show advanced options", "1", IsDefault: false, IsMachineIdentity: true),
        new("TraceFlag", "1117", "ON", IsDefault: false),
    ];

    [Test]
    public void WithoutAnAllowlist_OnlyWhatDiffersFromTheDefault_AndNoMachineIdentity()
    {
        var (file, _) = SettingsCaster.Capture(Server, SettingScope.Server, null, SettingsPolicyDefault.Report);

        Assert.That(file.Settings.Select(s => s.Display), Is.EqualTo(new[] { "Configuration 'max degree of parallelism'", "TraceFlag '1117'" }));
    }

    [Test]
    public void WithAnAllowlist_ExactlyThose_DefaultOrMachineIdentityOrNot()
    {
        var allow = new[] { ("configuration", "COST THRESHOLD FOR PARALLELISM"), ("Configuration", "show advanced options") };

        var (file, notOnServer) = SettingsCaster.Capture(Server, SettingScope.Server, allow, SettingsPolicyDefault.Report);

        Assert.That(file.Settings.Select(s => $"{s.Name}={s.Value}"), Is.EqualTo(new[] { "cost threshold for parallelism=5", "show advanced options=1" }));
        Assert.That(notOnServer, Is.Empty);
    }

    [Test]
    public void AnAllowlistedSettingTheServerDoesNotHave_IsNamed()
    {
        var (_, notOnServer) = SettingsCaster.Capture(Server, SettingScope.Server, [("Configuration", "no such option")], SettingsPolicyDefault.Report);
        Assert.That(notOnServer, Is.EqualTo(new[] { "Configuration 'no such option'" }));
    }

    [TestCase(SettingsPolicyDefault.Report, false, DriftLevel.Warn)]
    [TestCase(SettingsPolicyDefault.EnforceWarn, true, DriftLevel.Warn)]
    [TestCase(SettingsPolicyDefault.EnforceError, true, DriftLevel.Error)]
    public void EverySetting_StatesThePolicy(SettingsPolicyDefault policy, bool fix, DriftLevel onDrift)
    {
        var (file, _) = SettingsCaster.Capture(Server, SettingScope.Server, null, policy);
        Assert.That(file.Settings, Has.All.Matches<Setting>(s => s.Fix == fix && s.OnDrift == onDrift));
    }

    [Test]
    public void TheOrderIsStable_ByKindThenName()
    {
        var shuffled = Server.Reverse().ToArray();
        var (a, _) = SettingsCaster.Capture(Server, SettingScope.Server, null, SettingsPolicyDefault.Report);
        var (b, _) = SettingsCaster.Capture(shuffled, SettingScope.Server, null, SettingsPolicyDefault.Report);
        Assert.That(b.Settings.Select(s => s.Display), Is.EqualTo(a.Settings.Select(s => s.Display)));
    }

    [Test]
    public void Scripts_OneFilePerSetting_FromTheDescriptor()
    {
        var descriptor = Substitute.For<ISettingDescriptor>();
        descriptor.RenderScript(Arg.Any<string>(), Arg.Any<string>(), "db").Returns(ci => $"-- {ci.ArgAt<string>(0)} = {ci.ArgAt<string>(1)}");
        var (file, _) = SettingsCaster.Capture(Server, SettingScope.Server, null, SettingsPolicyDefault.Report);

        var scripts = SettingsCaster.Scripts(file, _ => descriptor, "db");

        Assert.That(scripts.Select(s => s.FileName), Is.EqualTo(new[] { "Configuration.max degree of parallelism.sql", "TraceFlag.1117.sql" }));
        Assert.That(scripts[0].Sql, Is.EqualTo("-- max degree of parallelism = 8"));
    }

    [Test]
    public void AScriptFileName_HasNoCharacterAFileSystemRefuses()
    {
        var name = SettingsCaster.ScriptFileName(new Setting { Kind = "Configuration", Name = "affinity I/O mask" });
        Assert.That(name.IndexOfAny(Path.GetInvalidFileNameChars().Append('/').Append('\\').Append(':').ToArray()), Is.EqualTo(-1), name);
        Assert.That(name, Is.EqualTo("Configuration.affinity I_O mask.sql"));
    }

    [Test]
    public void Allowlist_LoadsBothScopes()
    {
        var path = Path.Join(Path.GetTempPath(), $"allow_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            { "Server": [ { "Kind": "Configuration", "Name": "max server memory (MB)" } ],
              "Database": [ { "Kind": "Option", "Name": "COMPATIBILITY_LEVEL" } ] }
            """);
        try
        {
            var allow = SettingsAllowlist.Load(path);
            Assert.That(allow.For(SettingScope.Server), Is.EqualTo(new[] { ("Configuration", "max server memory (MB)") }));
            Assert.That(allow.For(SettingScope.Database), Is.EqualTo(new[] { ("Option", "COMPATIBILITY_LEVEL") }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Allowlist_AScopeLeftOut_CapturesNothingAllowlistedThere_NotEverything()
    {
        var path = Path.Join(Path.GetTempPath(), $"allow_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "Server": [ { "Kind": "Configuration", "Name": "x" } ] }""");
        try
        {
            Assert.That(SettingsAllowlist.Load(path).For(SettingScope.Database), Is.Empty);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
