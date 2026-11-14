// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using log4net;
using Newtonsoft.Json;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.Settings;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

[TestFixture]
public class SettingsFileTests
{
    private IFile _file;
    private IDirectory _directory;
    private static readonly string Root = Path.Join("C:", "products");
    private static readonly string ServerFile = Path.Join(Root, "ServerSettings.json");
    private static readonly string TemplateFile = Path.Join(Root, "Templates", "Main", "Template.json");
    private static readonly string DatabaseFile = Path.Join(Root, "Templates", "Main", "DatabaseSettings.json");

    private const string SpecExample = """
        {
          "Settings": [
            { "Kind": "Configuration", "Name": "cost threshold for parallelism", "Value": "50", "Fix": true, "OnDrift": "Warn" },
            { "Kind": "Configuration", "Name": "max degree of parallelism", "Value": "8", "Fix": true, "OnDrift": "Error",
              "ShouldApplyExpression": "SELECT CASE WHEN @@SERVERNAME LIKE 'OLTP%' THEN 1 ELSE 0 END", "VariantName": "OLTP class" },
            { "Kind": "Configuration", "Name": "max degree of parallelism", "Value": "0", "Fix": false, "OnDrift": "Warn",
              "ShouldApplyExpression": "SELECT CASE WHEN @@SERVERNAME LIKE 'DW%' THEN 1 ELSE 0 END", "VariantName": "Warehouse class" }
          ]
        }
        """;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
        _file = Substitute.For<IFile>();
        _directory = Substitute.For<IDirectory>();
        FactoryContainer.Register<IFile>(_file);
        FactoryContainer.Register<IDirectory>(_directory);
        LogFactory.Register("ProgressLog", Substitute.For<ILog>());
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private void Given(string path, string json)
    {
        _file.Exists(path).Returns(true);
        _file.ReadAllText(path).Returns(json);
    }

    private static SettingsFile LoadServer(Platform platform = Platform.SqlServer) =>
        SettingsFile.LoadIfPresent(ServerFile, platform, SettingScope.Server);

    [Test]
    public void Absent_ReturnsNull()
    {
        Assert.That(LoadServer(), Is.Null);
    }

    [Test]
    public void LoadsEverySetting_InOrder()
    {
        Given(ServerFile, SpecExample);

        var file = LoadServer();

        Assert.Multiple(() =>
        {
            Assert.That(file.Settings.Select(s => s.Display), Is.EqualTo(new[]
            {
                "Configuration 'cost threshold for parallelism'", "Configuration 'max degree of parallelism'", "Configuration 'max degree of parallelism'"
            }));
            Assert.That(file.Settings.Select(s => s.VariantName), Is.EqualTo(new[] { null, "OLTP class", "Warehouse class" }));
            Assert.That(file.Settings[1].Fix, Is.True);
            Assert.That(file.Settings[1].OnDrift, Is.EqualTo(DriftLevel.Error));
            Assert.That(file.Settings[2].Value, Is.EqualTo("0"));
            Assert.That(file.FilePath, Is.EqualTo(ServerFile));
            Assert.That(file.Scope, Is.EqualTo(SettingScope.Server));
        });
    }

    [TestCase("""{ "Kind": "Configuration", "Name": "cost threshold for parallelism", "Value": "50", "OnDrift": "Warn" }""")]
    [TestCase("""{ "Kind": "Configuration", "Name": "cost threshold for parallelism", "Value": "50", "Fix": true }""")]
    public void MissingFixOrOnDrift_Refuses(string setting)
    {
        Given(ServerFile, $$"""{ "Settings": [ {{setting}} ] }""");

        Assert.That(() => LoadServer(), Throws.InstanceOf<PackageException>()
            .With.Message.Contains("Configuration 'cost threshold for parallelism'")
            .And.Message.Contains("must state Fix and OnDrift")
            .And.Message.Contains(ServerFile));
    }

    // A missing Value would be applied as an empty one -- SET PERSIST sql_mode = '' turns strict mode off.
    [Test]
    public void MissingValue_Refuses()
    {
        Given(ServerFile, """{ "Settings": [ { "Kind": "Configuration", "Name": "x", "Fix": true, "OnDrift": "Warn" } ] }""");

        Assert.That(() => LoadServer(), Throws.InstanceOf<PackageException>().With.Message.Contains("must state a Value"));
    }

    [Test]
    public void UnknownKind_Refuses_NamingTheKindsThePlatformHas()
    {
        Given(ServerFile, """{ "Settings": [ { "Kind": "Parameter", "Name": "work_mem", "Value": "8MB", "Fix": true, "OnDrift": "Warn" } ] }""");

        Assert.That(() => LoadServer(), Throws.InstanceOf<PackageException>()
            .With.Message.Contains("'Parameter'")
            .And.Message.Contains("Configuration, TraceFlag"));
    }

    [Test]
    public void KindIsMatchedWithoutRegardToCase()
    {
        Given(ServerFile, """{ "Settings": [ { "Kind": "variable", "Name": "max_connections", "Value": "180", "Fix": true, "OnDrift": "Warn" } ] }""");

        Assert.That(LoadServer(Platform.MariaDb).Settings.Single().Kind, Is.EqualTo("variable"));
    }

    [Test]
    public void UnknownMember_Refuses()
    {
        Given(ServerFile, """{ "Settings": [ { "Kind": "Configuration", "Name": "x", "Value": "1", "Fix": true, "OnDrift": "Warn", "Fixx": true } ] }""");

        Assert.That(() => LoadServer(), Throws.InstanceOf<JsonSerializationException>().With.Message.Contains("Fixx"));
    }

    [Test]
    public void SettingWithoutAName_Refuses()
    {
        Given(ServerFile, """{ "Settings": [ { "Kind": "Configuration", "Value": "1", "Fix": true, "OnDrift": "Warn" } ] }""");

        Assert.That(() => LoadServer(), Throws.InstanceOf<PackageException>().With.Message.Contains("has no Name"));
    }

    [TestCase(Platform.SqlServer, SettingScope.Server, new[] { "Configuration", "TraceFlag" })]
    [TestCase(Platform.SqlServer, SettingScope.Database, new[] { "Option", "ScopedConfiguration" })]
    [TestCase(Platform.PostgreSQL, SettingScope.Server, new[] { "Parameter" })]
    [TestCase(Platform.PostgreSQL, SettingScope.Database, new[] { "Parameter" })]
    [TestCase(Platform.MySQL, SettingScope.Server, new[] { "Variable" })]
    [TestCase(Platform.MariaDb, SettingScope.Server, new[] { "Variable" })]
    [TestCase(Platform.MySQL, SettingScope.Database, new[] { "Option" })]
    [TestCase(Platform.MariaDb, SettingScope.Database, new[] { "Option" })]
    public void KindsPerPlatformAndScope(Platform platform, SettingScope scope, string[] kinds)
    {
        Assert.That(SettingKinds.For(platform, scope), Is.EqualTo(kinds));
    }

    [Test]
    public void Product_WithoutServerSettings_HasNone()
    {
        var product = LoadProduct();
        Assert.That(product.ServerSettings, Is.Null);
    }

    [Test]
    public void Product_LoadsServerSettingsBesideProductJson()
    {
        Given(ServerFile, SpecExample);
        var product = LoadProduct();
        Assert.That(product.ServerSettings?.Settings.Count, Is.EqualTo(3));
    }

    [Test]
    public void Template_LoadsDatabaseSettingsInItsFolder()
    {
        Given(DatabaseFile, """{ "Settings": [ { "Kind": "Option", "Name": "RECOVERY", "Value": "SIMPLE", "Fix": true, "OnDrift": "Warn" } ] }""");
        Given(TemplateFile, """{ "Name": "Main", "DatabaseIdentificationScript": "SELECT 'db'" }""");

        var template = Template.Load("Main", Product());

        Assert.That(template.DatabaseSettings?.Settings.Single().Display, Is.EqualTo("Option 'RECOVERY'"));
    }

    // A variant gated on a pipeline token: the token is resolved before the gate reaches the server, as a folder's is.
    [Test]
    public void Product_ResolvesScriptTokensInAServerSettingsGate()
    {
        Given(ServerFile, """
            { "Settings": [ { "Kind": "Configuration", "Name": "max degree of parallelism", "Value": "8", "Fix": true, "OnDrift": "Warn",
              "ShouldApplyExpression": "SELECT CASE WHEN '{{ServerClass}}' = 'OLTP' THEN 1 ELSE 0 END" } ] }
            """);
        var product = LoadProduct("""{ "Name": "TestProduct", "Platform": "SqlServer", "ScriptTokens": { "ServerClass": "OLTP" } }""");

        Assert.That(product.ServerSettings.Settings.Single().ShouldApplyExpression, Is.EqualTo("SELECT CASE WHEN 'OLTP' = 'OLTP' THEN 1 ELSE 0 END"));
    }

    [Test]
    public void Template_ResolvesScriptTokensInADatabaseSettingsGate()
    {
        Given(DatabaseFile, """
            { "Settings": [ { "Kind": "Option", "Name": "RECOVERY", "Value": "SIMPLE", "Fix": true, "OnDrift": "Warn",
              "ShouldApplyExpression": "SELECT CASE WHEN '{{Tier}}' = 'dev' THEN 1 ELSE 0 END" } ] }
            """);
        Given(TemplateFile, """{ "Name": "Main", "DatabaseIdentificationScript": "SELECT 'db'", "ScriptTokens": { "Tier": "dev" } }""");

        var template = Template.Load("Main", Product());

        Assert.That(template.DatabaseSettings.Settings.Single().ShouldApplyExpression, Is.EqualTo("SELECT CASE WHEN 'dev' = 'dev' THEN 1 ELSE 0 END"));
    }

    [Test]
    public void SchemaTemplate_WithDatabaseSettings_Refuses()
    {
        Given(DatabaseFile, """{ "Settings": [ { "Kind": "Option", "Name": "RECOVERY", "Value": "SIMPLE", "Fix": true, "OnDrift": "Warn" } ] }""");
        Given(TemplateFile, """{ "Name": "Main", "DatabaseIdentificationScript": "SELECT 'db'", "SchemaIdentificationScript": "SELECT 'dbo'" }""");

        Assert.That(() => Template.Load("Main", Product()), Throws.InstanceOf<PackageException>()
            .With.Message.Contains("DatabaseSettings.json")
            .And.Message.Contains("runs once per database"));
    }

    private static Product Product() => new()
    {
        Name = "TestProduct",
        Platform = Platform.SqlServer,
        FilePath = Path.Join(Root, "Product.json")
    };

    private Product LoadProduct(string productJson = """{ "Name": "TestProduct", "Platform": "SqlServer" }""")
    {
        var config = Substitute.For<Microsoft.Extensions.Configuration.IConfigurationRoot>();
        config[Schema.Configuration.SettingsKeys.SchemaPackagePath].Returns(Root);
        config.GetSection(Arg.Any<string>()).Returns(Substitute.For<Microsoft.Extensions.Configuration.IConfigurationSection>());
        FactoryContainer.Register(config);
        _directory.Exists(Root).Returns(true);
        Given(Path.Join(Root, "Product.json"), productJson);
        return Schema.Domain.Product.Load();
    }
}
