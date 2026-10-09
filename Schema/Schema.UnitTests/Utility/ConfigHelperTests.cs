// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NSubstitute;
using Microsoft.Extensions.Configuration;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class ConfigHelperTests
{
    private IEnvironment _mockEnvironment;

    [SetUp]
    public void SetUp()
    {
        _mockEnvironment = Substitute.For<IEnvironment>();
        _mockEnvironment.CommandLine.Returns("app.exe");
        FactoryContainer.Register<IEnvironment>(_mockEnvironment);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_ReturnsConfigurationRoot()
    {
        var logLines = new List<string>();
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", s => logLines.Add(s));

        Assert.That(config, Is.Not.Null);
        Assert.That(config, Is.InstanceOf<IConfigurationRoot>());
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_RegistersConfigInFactoryContainer()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

        var resolved = FactoryContainer.Resolve<IConfigurationRoot>();
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved, Is.SameAs(config));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_ReturnsCachedConfig_WhenCalledTwice()
    {
        var config1 = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });
        var config2 = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

        Assert.That(config2, Is.SameAs(config1));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_ReturnsPreviouslyRegisteredConfig()
    {
        var mockConfig = Substitute.For<IConfigurationRoot>();
        FactoryContainer.Register(mockConfig);

        var result = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

        Assert.That(result, Is.SameAs(mockConfig));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_LogsAppName()
    {
        var logLines = new List<string>();
        ConfigHelper.GetAppSettingsAndUserSecrets("MyTool", s => logLines.Add(s));

        Assert.That(logLines, Has.Some.Matches<string>(s => s == "MyTool"));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_AppLogLine_DoesNotContainHardcodedPlatform()
    {
        // Platform is read from Product.json at runtime, not hardcoded in the startup banner.
        // The log line containing the app name must not carry a hardcoded engine string.
        var logLines = new List<string>();
        ConfigHelper.GetAppSettingsAndUserSecrets("MyTool", s => logLines.Add(s));

        var appLine = logLines.FirstOrDefault(s => s.Contains("MyTool"));
        Assert.That(appLine, Is.Not.Null);
        Assert.That(appLine, Does.Not.Contain("SqlServer"));
        Assert.That(appLine, Does.Not.Contain("PostgreSQL"));
        Assert.That(appLine, Does.Not.Contain("MySQL"));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_UsesSmithySettingsPrefix()
    {
        // Set an environment variable with SmithySettings_ prefix and verify it's picked up
        _mockEnvironment.CommandLine.Returns("app.exe");
        Environment.SetEnvironmentVariable("SmithySettings_TestKey", "TestValue");
        try
        {
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });
            var value = config["TestKey"];
            Assert.That(value, Is.EqualTo("TestValue"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SmithySettings_TestKey", null);
        }
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_DoesNotUseQuenchSettingsPrefix()
    {
        // Set an environment variable with QuenchSettings_ prefix and verify it's NOT picked up
        // This is the key behavioral change - QuenchSettings_ prefix has been removed
        _mockEnvironment.CommandLine.Returns("app.exe");
        Environment.SetEnvironmentVariable("QuenchSettings_TestKey2", "ShouldNotAppear");
        try
        {
            var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });
            var value = config["TestKey2"];
            Assert.That(value, Is.Null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QuenchSettings_TestKey2", null);
        }
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_UsesConfigFileSwitch()
    {
        // Create a temp config file with a known value
        var tempFile = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tempFile, """{"CustomKey": "CustomValue"}""");
            _mockEnvironment.CommandLine.Returns($"app.exe --ConfigFile:{tempFile}");

            var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });
            Assert.That(config["CustomKey"], Is.EqualTo("CustomValue"));
        }
        finally
        {
            System.IO.File.Delete(tempFile);
        }
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_CommandLineOverrideWinsOverConfigFile()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """{"MinimumVersion": "10", "Untouched": "keepme"}""");
            _mockEnvironment.CommandLine.Returns($"app.exe --ConfigFile:{tempFile} --MinimumVersion=99");

            var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

            Assert.That(config["MinimumVersion"], Is.EqualTo("99"));
            Assert.That(config["Untouched"], Is.EqualTo("keepme"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_CommandLineNestedOverride_UsesColonPath()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --Source__Server=cli-host");

        var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

        Assert.That(config["Source:Server"], Is.EqualTo("cli-host"));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_HandlesNullLogLine()
    {
        // Should not throw when logLine is null
        Assert.DoesNotThrow(() => ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", null));
    }

    [Test]
    public void GetAppSettingsAndUserSecrets_UsesToolSpecificSettingsFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "TestApp.settings.json");
        try
        {
            File.WriteAllText(settingsFile, """{"ToolSpecificKey": "FoundIt"}""");
            var originalDir = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(tempDir);
            try
            {
                var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });
                Assert.That(config["ToolSpecificKey"], Is.EqualTo("FoundIt"));
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDir);
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void ConfigHelper_DoesNotHavePlatformConstant()
    {
        // Verify via reflection that ConfigHelper no longer has a Platform constant
        var field = typeof(ConfigHelper).GetField("Platform",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.That(field, Is.Null, "ConfigHelper should not have a Platform constant - platform is read from Product");
    }

    [Test]
    public void ResolveLogPath_RelativePath_ResolvesAgainstProcessCwd()
    {
        _mockEnvironment.CommandLine.Returns("app.exe --LogPath:rellogs");

        var resolved = ConfigHelper.ResolveLogPath();

        Assert.That(Path.IsPathRooted(resolved), Is.True, "relative --LogPath must resolve to an absolute path");
        Assert.That(resolved, Is.EqualTo(Path.GetFullPath("rellogs").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
    }

    [Test]
    public void ResolveLogPath_Unspecified_FallsBackToToolDir()
    {
        _mockEnvironment.CommandLine.Returns("app.exe");

        var expected = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.That(ConfigHelper.ResolveLogPath(), Is.EqualTo(expected));
    }

    // A --ConfigFile that does not exist used to run with no settings, or with a same-named file from the tool's own
    // folder, and say nothing.
    [Test]
    public void CheckStartupSettings_ExplicitConfigFileThatDoesNotExist_StopsTheRun_NamingThePath()
    {
        var name = $"missing-{Guid.NewGuid():N}.json";
        _mockEnvironment.CommandLine.Returns($"app.exe --ConfigFile:{name}");
        var config = ConfigHelper.GetAppSettingsAndUserSecrets("TestApp", _ => { });

        var ex = Assert.Throws<RunFailedException>(() => ConfigHelper.CheckStartupSettings(config, _ => { }));

        Assert.That(ex!.Message, Does.Contain(Path.GetFullPath(name)));
    }

    [Test]
    public void CheckStartupSettings_DefaultSettingsFileAbsent_IsNotAnError()
    {
        var config = ConfigHelper.GetAppSettingsAndUserSecrets($"NoSuchTool{Guid.NewGuid():N}", _ => { });

        Assert.DoesNotThrow(() => ConfigHelper.CheckStartupSettings(config, _ => { }));
    }

    [TestCase("LogHygiene:ScrubTokens", "DeployKey", "ScrubTokens")]
    [TestCase("LogHygiene:ScrubToken:0", "DeployKey", "ScrubToken")]
    [TestCase("LogHygiene:LogTokens", "no", "LogTokens")]
    [TestCase("LogHygiene:ApiKey", "true", "ApiKey")]
    public void CheckStartupSettings_LogHygieneTheToolsCannotRead_WarnsNamingTheKey(string key, string value, string named)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string>(key, value)]).Build();
        var warnings = new List<string>();

        ConfigHelper.CheckStartupSettings(config, warnings.Add);

        Assert.That(warnings, Has.Count.EqualTo(1).And.Some.Contains($"LogHygiene:{named} "));
    }

    [Test]
    public void CheckStartupSettings_ValidLogHygiene_DoesNotWarn()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["LogHygiene:LogTokens"] = "False",
            ["LogHygiene:ScrubTokens:0"] = "DeployKey",
            ["LogHygiene:ScrubPatterns:0"] = "*Salt*",
            ["LogHygiene:AllowTokens:0"] = "PublicToken"
        }).Build();
        var warnings = new List<string>();

        ConfigHelper.CheckStartupSettings(config, warnings.Add);

        Assert.That(warnings, Is.Empty);
    }

    [TestCase(null, ".")]
    [TestCase("", ".")]
    [TestCase("   ", ".")]
    [TestCase(" out ", "out")]
    [TestCase("out", "out")]
    public void PathSetting_TrimsTheValue_AndTreatsBlankAsTheCurrentDirectory(string value, string expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string>("Product:Path", value)]).Build();

        Assert.That(ConfigHelper.PathSetting(config, "Product:Path"), Is.EqualTo(expected));
    }
}
