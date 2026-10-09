// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class ConfigurationLoggerTests
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

    private static IConfigurationRoot ConfigWith(params (string key, string value)[] entries)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in entries) dict[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void LogCommandLine_LogsHeaderAndSwitches()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MinimumVersion=5 --Source__Server=myhost");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(), s => logLines.Add(s));

        Assert.That(logLines, Has.Some.EqualTo("Command line:"));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("MinimumVersion") && s.Contains("5")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("Source__Server") && s.Contains("myhost")));
    }

    [Test]
    public void LogCommandLine_MasksSensitivelyNamedSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Source__Password=secret123");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret123")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "Source__Password: ***"));
    }

    [Test]
    public void LogCommandLine_MasksConnectionStringSwitchWholesale()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --ConnectionString=Server=db;Password=secret");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "ConnectionString: ***"));
    }

    [Test]
    public void LogCommandLine_ScrubsEmbeddedPasswordInNonSensitiveSwitch()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Dsn=Server=db;Password=secret");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("Server=db") && s.Contains("Password=***")));
    }

    [Test]
    public void LogCommandLine_MasksCustomScrubToken()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --MyCustomFlag=sensitive");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(("LogHygiene:ScrubTokens:0", "MyCustomFlag")), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("sensitive")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "MyCustomFlag: ***"));
    }

    [Test]
    public void LogCommandLine_AllowTokenUnmasksDefaultSensitiveName()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Token=visible");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(("LogHygiene:AllowTokens:0", "Token")), s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "Token: visible"));
    }

    [Test]
    public void LogCommandLine_NoSwitches_LogsNone()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");
        var logLines = new List<string>();

        ConfigurationLogger.LogCommandLine(ConfigWith(), s => logLines.Add(s));

        Assert.That(logLines, Has.Some.EqualTo("Command line:"));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "(none)"));
    }

    [Test]
    public void LogCommandLine_HandlesNullLogLine()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Foo=bar");
        Assert.DoesNotThrow(() => ConfigurationLogger.LogCommandLine(ConfigWith(), null));
    }

    [Test]
    public void LogConfiguration_LogsVersionLine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "SomeKey", "SomeValue" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("Version:")));
    }

    [Test]
    public void LogConfiguration_LogsConfigurationHeader()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Key1", "Value1" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.Some.EqualTo("Configuration:"));
    }

    [Test]
    public void LogConfiguration_LogsConfigValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "MyKey", "MyValue" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("MyKey") && s.Contains("MyValue")));
    }

    [Test]
    public void LogConfiguration_MasksPasswordValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Password", "secret123" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret123")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "Password: ***"));
    }

    [Test]
    public void LogConfiguration_MasksPwdValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "ConnectionPwd", "hidden" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("hidden")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "ConnectionPwd: ***"));
    }

    [TestCase("ClientSecret", "topsecret")]
    [TestCase("ApiKey", "ak-12345")]
    [TestCase("AuthToken", "tok-xyz")]
    [TestCase("AwsCredential", "cred-abc")]
    public void LogConfiguration_MasksAllDefaultSensitivePatterns(string key, string secretValue)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { key, secretValue }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains(secretValue)));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == $"{key}: ***"));
    }

    [Test]
    public void LogConfiguration_MasksConnectionStringValueWholesale()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Target:ConnectionString", "Server=db1;User Id=admin;Password=secret" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret")));
        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("admin")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "ConnectionString: ***"));
    }

    [Test]
    public void LogConfiguration_ScrubsEmbeddedPasswordInNonSensitivelyNamedKey()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Target:Dsn", "Server=db1;User Id=admin;Password=secret" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("secret")));
        // Non-sensitive subfields survive; only the embedded password is stripped.
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("Server=db1") && s.Contains("admin") && s.Contains("Password=***")));
    }

    [Test]
    public void LogConfiguration_SkipsDescriptionSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Description", "Should be skipped" },
                { "Other", "Should appear" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("Should be skipped")));
    }

    [Test]
    public void LogConfiguration_HandlesNestedConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Target:Server", "localhost" },
                { "Target:Database", "mydb" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("localhost")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("mydb")));
    }

    [Test]
    public void LogConfiguration_HandlesNullLogLine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Key", "Value" }
            })
            .Build();

        Assert.DoesNotThrow(() => ConfigurationLogger.LogConfiguration(config, null));
    }

    [Test]
    public void LogConfiguration_HandlesArrayConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                { "Items:0:Name", "First" },
                { "Items:0:Value", "V1" },
                { "Items:1:Name", "Second" },
                { "Items:1:Value", "V2" }
            })
            .Build();

        var logLines = new List<string>();
        ConfigurationLogger.LogConfiguration(config, s => logLines.Add(s));

        // Array items with Name sub-keys should use the name as the display key
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("First")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.Contains("Second")));
    }

    // A list's entries are keyed by index ("password:0"), so judging each by its own name logged every entry of a
    // sensitively-named list in clear.
    [Test]
    public void LogConfiguration_MasksTheEntriesOfAListUnderASensitiveName()
    {
        var logLines = new List<string>();

        ConfigurationLogger.LogConfiguration(ConfigWith(("password:0", "hunter2"), ("password:1", "swordfish")), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("hunter2") || s.Contains("swordfish")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "0: ***"));
    }

    // "Token" is a sensitive pattern, so masking a whole object under a sensitive ancestor would hide every token.
    [Test]
    public void LogConfiguration_KeepsScriptTokenValuesReadable_WhileTokenLoggingIsOn()
    {
        var logLines = new List<string>();

        ConfigurationLogger.LogConfiguration(ConfigWith(("ScriptTokens:Env", "Prod")), s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "Env: Prod"));
    }

    // LogTokens: false suppressed only the separate token section; the configuration echo still printed every value.
    [Test]
    public void LogConfiguration_MasksScriptTokenValues_WhenTokenLoggingIsOff()
    {
        var logLines = new List<string>();

        ConfigurationLogger.LogConfiguration(
            ConfigWith(("ScriptTokens:Env", "Prod"), ("LogHygiene:LogTokens", "false")), s => logLines.Add(s));

        Assert.That(logLines, Has.None.Matches<string>(s => s.Contains("Prod")));
        Assert.That(logLines, Has.Some.Matches<string>(s => s.TrimStart() == "Env: ***"));
    }
}
