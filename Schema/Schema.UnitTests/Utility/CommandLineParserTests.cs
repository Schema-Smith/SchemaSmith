// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class CommandLineParserTests
{
    private IEnvironment _mockEnvironment;

    [SetUp]
    public void SetUp()
    {
        _mockEnvironment = Substitute.For<IEnvironment>();
        FactoryContainer.Register<IEnvironment>(_mockEnvironment);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
    }

    [Test]
    public void Arguments_ParsesSimpleArguments()
    {
        _mockEnvironment.CommandLine.Returns("app.exe arg1 arg2 arg3");

        var args = CommandLineParser.Arguments;

        Assert.That(args, Has.Count.EqualTo(4));
        Assert.That(args[0], Is.EqualTo("app.exe"));
        Assert.That(args[1], Is.EqualTo("arg1"));
        Assert.That(args[2], Is.EqualTo("arg2"));
        Assert.That(args[3], Is.EqualTo("arg3"));
    }

    [Test]
    public void Arguments_ParsesQuotedArguments()
    {
        _mockEnvironment.CommandLine.Returns("app.exe \"arg with spaces\" arg2");

        var args = CommandLineParser.Arguments;

        Assert.That(args, Has.Count.EqualTo(3));
        Assert.That(args[0], Is.EqualTo("app.exe"));
        Assert.That(args[1], Is.EqualTo("arg with spaces"));
        Assert.That(args[2], Is.EqualTo("arg2"));
    }

    [Test]
    public void Arguments_HandlesEmptyCommandLine()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        var args = CommandLineParser.Arguments;

        Assert.That(args, Has.Count.EqualTo(1));
        Assert.That(args[0], Is.EqualTo("app.exe"));
    }

    [Test]
    public void Arguments_HandlesMultipleSpaces()
    {
        _mockEnvironment.CommandLine.Returns("app.exe   arg1   arg2");

        var args = CommandLineParser.Arguments;

        Assert.That(args, Has.Count.EqualTo(3));
        Assert.That(args[1], Is.EqualTo("arg1"));
        Assert.That(args[2], Is.EqualTo("arg2"));
    }

    [Test]
    public void SwitchesAndValues_ParsesDashSwitchWithColonValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --ConfigFile:settings.json");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("ConfigFile"), Is.True);
        Assert.That(switches["ConfigFile"], Is.EqualTo("settings.json"));
    }

    [Test]
    public void SwitchesAndValues_ParsesDashSwitchWithEqualsValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --ConfigFile=settings.json");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("ConfigFile"), Is.True);
        Assert.That(switches["ConfigFile"], Is.EqualTo("settings.json"));
    }

    [Test]
    public void SwitchesAndValues_ParsesSlashSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe /verbose");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("verbose"), Is.True);
        Assert.That(switches["verbose"], Is.EqualTo(string.Empty));
    }

    [Test]
    public void SwitchesAndValues_ParsesSingleDashSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe -v");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("v"), Is.True);
    }

    [Test]
    public void SwitchesAndValues_HandlesValueWithEmbeddedColon()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Server:localhost:1433");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches["Server"], Is.EqualTo("localhost:1433"));
    }

    [Test]
    public void SwitchesAndValues_IsCaseInsensitive()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --CONFIGFILE:test.json");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("configfile"), Is.True);
        Assert.That(switches.ContainsKey("CONFIGFILE"), Is.True);
        Assert.That(switches.ContainsKey("ConfigFile"), Is.True);
    }

    [Test]
    public void SwitchesAndValues_IgnoresNonSwitchArguments()
    {
        _mockEnvironment.CommandLine.Returns("app.exe arg1 --switch:value arg2");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches, Has.Count.EqualTo(1));
        Assert.That(switches.ContainsKey("switch"), Is.True);
    }

    [Test]
    public void ContainsSwitch_ReturnsTrueForExistingSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --verbose");

        Assert.That(CommandLineParser.ContainsSwitch("verbose"), Is.True);
    }

    [Test]
    public void ContainsSwitch_ReturnsFalseForMissingSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --verbose");

        Assert.That(CommandLineParser.ContainsSwitch("debug"), Is.False);
    }

    [Test]
    public void ValueOfSwitch_ReturnsValueForExistingSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:c:\\logs");

        Assert.That(CommandLineParser.ValueOfSwitch("LogPath"), Is.EqualTo("c:\\logs"));
    }

    [Test]
    public void ValueOfSwitch_ReturnsDefaultForMissingSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        Assert.That(CommandLineParser.ValueOfSwitch("LogPath", "default"), Is.EqualTo("default"));
    }

    [Test]
    public void ValueOfSwitch_ReturnsEmptyStringDefaultWhenNoDefault()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        Assert.That(CommandLineParser.ValueOfSwitch("LogPath"), Is.EqualTo(string.Empty));
    }

    [Test]
    public void ValueOfSwitch_TrimsQuotesFromValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:\"c:\\my logs\"");

        Assert.That(CommandLineParser.ValueOfSwitch("LogPath"), Is.EqualTo("c:\\my logs"));
    }

    [Test]
    public void IntValueOfSwitch_ReturnsIntValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MaxTasks:5");

        Assert.That(CommandLineParser.IntValueOfSwitch("MaxTasks"), Is.EqualTo(5));
    }

    [Test]
    public void IntValueOfSwitch_ReturnsDefaultForNonNumeric()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MaxTasks:abc");

        Assert.That(CommandLineParser.IntValueOfSwitch("MaxTasks", 10), Is.EqualTo(10));
    }

    [Test]
    public void IntValueOfSwitch_ReturnsNegativeOneDefaultWhenNoDefault()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        Assert.That(CommandLineParser.IntValueOfSwitch("MaxTasks"), Is.EqualTo(-1));
    }

    [Test]
    public void HandleCommonSwitches_CallsExitOnVersionSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --version");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void HandleCommonSwitches_CallsExitOnHelpSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --help");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void HandleCommonSwitches_CallsExitOnQuestionMarkSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe -?");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void HandleCommonSwitches_CallsExitOnVSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe -v");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void HandleCommonSwitches_CallsExitOnHSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe -h");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.Received().Exit(0);
    }

    [Test]
    public void HandleCommonSwitches_DoesNotExitWithoutSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --other");

        CommandLineParser.HandleCommonSwitches("TestApp");

        _mockEnvironment.DidNotReceive().Exit(Arg.Any<int>());
    }

    [Test]
    public void HandleCommonSwitches_InvokesToolSpecificSwitchesOnHelp()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --help");
        var invoked = false;

        CommandLineParser.HandleCommonSwitches("TestApp", () => invoked = true);

        Assert.That(invoked, Is.True);
    }

    [Test]
    public void SwitchesAndValues_ParsesMultipleSwitches()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --ConfigFile:test.json --LogPath:c:\\logs --verbose");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches, Has.Count.EqualTo(3));
        Assert.That(switches["ConfigFile"], Is.EqualTo("test.json"));
        Assert.That(switches["LogPath"], Is.EqualTo("c:\\logs"));
        Assert.That(switches["verbose"], Is.EqualTo(string.Empty));
    }

    [Test]
    public void SwitchesAndValues_HandlesDoubleDash()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --key:value");

        var switches = CommandLineParser.SwitchesAndValues;

        Assert.That(switches.ContainsKey("key"), Is.True);
        Assert.That(switches["key"], Is.EqualTo("value"));
    }

    [Test]
    public void ConfigOverrides_FlatKey_EqualsForm()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MinimumVersion=5");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["MinimumVersion"], Is.EqualTo("5"));
    }

    [Test]
    public void ConfigOverrides_NestedKey_DoubleUnderscoreBecomesColon()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Source__Server=myhost");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Source:Server"], Is.EqualTo("myhost"));
    }

    [Test]
    public void ConfigOverrides_ValueMayContainColon()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Source__Server=host:1433");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Source:Server"], Is.EqualTo("host:1433"));
    }

    [Test]
    public void ConfigOverrides_ValueMayContainEquals()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Target__ConnProp=Encrypt=true");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Target:ConnProp"], Is.EqualTo("Encrypt=true"));
    }

    [Test]
    public void ConfigOverrides_QuotedValue_IsUnquoted()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Foo=\"a b\"");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Foo"], Is.EqualTo("a b"));
    }

    [Test]
    public void ConfigOverrides_SlashPrefix_Supported()
    {
        _mockEnvironment.CommandLine.Returns("app.exe /Foo__Bar=1");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Foo:Bar"], Is.EqualTo("1"));
    }

    [Test]
    public void ConfigOverrides_ColonForm_FlatKey()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MaxThreads:8");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["MaxThreads"], Is.EqualTo("8"));
    }

    [Test]
    public void ConfigOverrides_ColonForm_NestedKey_DoubleUnderscoreBecomesColon()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Source__Server:myhost");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Source:Server"], Is.EqualTo("myhost"));
    }

    [Test]
    public void ConfigOverrides_EqualsTakesPrecedenceOverColon_SoColonNestsAndEqualsIsTheValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Target:Server=host");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["Target:Server"], Is.EqualTo("host"));
    }

    [Test]
    public void ConfigOverrides_ColonForm_ValueRetainsTrailingColons()
    {
        // First ':' is the value boundary; a colon in the value (e.g. a host:port or c:\path) survives.
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:c:\\logs");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides["LogPath"], Is.EqualTo("c:\\logs"));
    }

    [Test]
    public void ConfigOverrides_BareFlag_IsNotAnOverride()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --WhatIf");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides, Is.Empty);
    }

    [Test]
    public void ConfigOverrides_FlagSwitch_IsNotAnOverride()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --verbose");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides, Is.Empty);
    }

    [Test]
    public void ConfigOverrides_EmptyKey_IsSkipped()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --=foo");
        var overrides = CommandLineParser.ConfigOverrides;
        Assert.That(overrides, Is.Empty);
    }

    [Test]
    public void ConfigOverrides_MixedSwitches_IncludesEqualsAndColon_ExcludesBareFlags()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --A=1 --B__C=2 --LogPath:x --flag");
        var overrides = CommandLineParser.ConfigOverrides;
        // '='-form (A, B:C), ':'-form (LogPath) all count; the bare flag does not.
        Assert.That(overrides, Has.Count.EqualTo(3));
        Assert.That(overrides["A"], Is.EqualTo("1"));
        Assert.That(overrides["B:C"], Is.EqualTo("2"));
        Assert.That(overrides["LogPath"], Is.EqualTo("x"));
    }

    [Test]
    public void ApplyTransportSecurity_NoFlag_LeavesPropertiesUnchanged()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Server:localhost");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.SqlServer, props);

        Assert.That(props, Is.Empty);
    }

    [Test]
    public void ApplyTransportSecurity_NoEncrypt_SqlServer_SetsEncryptFalse()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --NoEncrypt");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.SqlServer, props);

        Assert.That(props["Encrypt"], Is.EqualTo("False"));
    }

    [Test]
    public void ApplyTransportSecurity_Encrypt_SqlServer_SetsEncryptTrue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Encrypt");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.SqlServer, props);

        Assert.That(props["Encrypt"], Is.EqualTo("True"));
    }

    [Test]
    public void ApplyTransportSecurity_Encrypt_PostgreSql_SetsSslModeRequire()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Encrypt");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.PostgreSQL, props);

        Assert.That(props["SSL Mode"], Is.EqualTo("Require"));
    }

    [Test]
    public void ApplyTransportSecurity_NoEncrypt_MySql_SetsSslModeNone()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --NoEncrypt");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.MySQL, props);

        Assert.That(props["SslMode"], Is.EqualTo("None"));
    }

    [Test]
    public void ApplyTransportSecurity_Encrypt_MariaDb_UsesMySqlSslMode()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Encrypt");
        var props = new Dictionary<string, string>();

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.MariaDb, props);

        Assert.That(props["SslMode"], Is.EqualTo("Required"));
    }

    [Test]
    public void ApplyTransportSecurity_Flag_OverwritesExistingPropertyValue()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --NoEncrypt");
        var props = new Dictionary<string, string> { ["Encrypt"] = "True" };

        CommandLineParser.ApplyTransportSecuritySwitch(Platform.SqlServer, props);

        Assert.That(props["Encrypt"], Is.EqualTo("False"));
    }

    [Test]
    public void ApplyTransportSecurity_BothFlags_Throws()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Encrypt --NoEncrypt");
        var props = new Dictionary<string, string>();

        var ex = Assert.Throws<Exception>(() =>
            CommandLineParser.ApplyTransportSecuritySwitch(Platform.SqlServer, props));
        Assert.That(ex.Message, Does.Contain("Encrypt").And.Contain("NoEncrypt"));
    }

    private static readonly string[] KnownFlags = { "TestConnection", "Validate", "Zip" };

    [Test]
    public void Unrecognized_SpaceSeparatedSwitchValue_ReportsTheStrandedValue()
    {
        // The exact trap: --report takes its value attached, so the path lands as a bare
        // positional the run never reads and the report goes to the default location. Both
        // halves are reported — the switch that lost its value, and the value it lost.
        _mockEnvironment.CommandLine.Returns("app.exe --report ./out/deploy-summary");

        var unread = CommandLineParser.UnrecognizedArguments(KnownFlags);

        Assert.That(unread, Is.EqualTo(new[] { "--report", "./out/deploy-summary" }));
    }

    [Test]
    public void Unrecognized_MisspelledBareFlag_IsReported()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --TestConection");

        var unread = CommandLineParser.UnrecognizedArguments(KnownFlags);

        Assert.That(unread, Is.EqualTo(new[] { "--TestConection" }));
    }

    [Test]
    public void Unrecognized_KnownBareFlag_IsAccepted()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --TestConnection --validate");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_SwitchCarryingAValue_IsAlwaysAccepted()
    {
        // Any --Key=value / --Key:value is a legitimate config override, known or not.
        _mockEnvironment.CommandLine.Returns("app.exe --Target__Server=host --LogPath:./logs --report:./out/x");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_ExecutableAndSlashForm_AreNotReported()
    {
        _mockEnvironment.CommandLine.Returns("app.exe /Zip");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_QuotedStrandedPath_IsReportedIntact()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath \"C:\\my logs\"");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags),
            Is.EqualTo(new[] { "--LogPath", "C:\\my logs" }));
    }

    [Test]
    public void Unrecognized_NothingButTheExecutable_IsEmpty()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_KnownFlagsAreMatchedCaseInsensitively()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --TESTCONNECTION");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_CommonFlagsNeedNoDeclaration()
    {
        // version/help variants are handled by HandleCommonSwitches for every tool.
        _mockEnvironment.CommandLine.Returns("app.exe --version --help -h -? --ver --Encrypt --debug --verbose");

        Assert.That(CommandLineParser.UnrecognizedArguments(KnownFlags), Is.Empty);
    }

    [Test]
    public void Unrecognized_DeclaredPositionalCommand_IsAccepted()
    {
        // SchemaQuench takes a positional command; an undeclared positional is still reported.
        _mockEnvironment.CommandLine.Returns("app.exe SkipKindlingForge stray");

        var unread = CommandLineParser.UnrecognizedArguments(new[] { "SkipKindlingForge" });

        Assert.That(unread, Is.EqualTo(new[] { "stray" }));
    }

    // An unread argument stops the run. It used to be a warning, and the run went ahead: `--WhatIf` (no such switch
    // then) deployed for real.
    [Test]
    public void RefuseUnrecognized_StopsTheRun_NamingTheArgumentsAndTheGrammar()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --report ./out/x");

        var ex = Assert.Throws<RunFailedException>(() => CommandLineParser.RefuseUnrecognizedArguments(KnownFlags));

        Assert.That(ex!.Message, Does.Contain("--report").And.Contain("./out/x").And.Contain("never a space").And.Contain("Nothing was run"));
    }

    [Test]
    public void RefuseUnrecognized_CleanCommandLine_LetsTheRunProceed()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --TestConnection --LogPath:./logs");

        Assert.DoesNotThrow(() => CommandLineParser.RefuseUnrecognizedArguments(KnownFlags));
    }
}
