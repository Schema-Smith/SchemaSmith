// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using System.Linq;
using log4net;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

/// <summary>
/// The MySQL column-level <c>CheckExpression</c> alias is RETIRED (2.7.0). It was deprecated on introduction
/// because MySQL and MariaDB cannot round-trip a column-level check — the catalog has no link from a check
/// constraint back to a column — so authoring is table-level <c>CheckConstraints</c> and extraction always
/// emitted that form.
/// <para><b>What retirement actually does, and it is not what the deprecation notice feared.</b> Table JSON
/// deserializes with <c>MissingMemberHandling.Error</c> (<c>PlatformDeserializer.StrictSettings</c>), so a
/// package still carrying the key fails the load, naming the property and the file. The feared outcome — the
/// key ignored, the deployed <c>CK_&lt;table&gt;_&lt;column&gt;</c> left as an orphan and dropped by the next
/// by-absence pass — cannot happen through this path. These tests pin the loud failure, because the value of
/// the breaking change is that it is loud.</para>
/// </summary>
[TestFixture]
public class MySqlColumnCheckExpressionRetiredTests
{
    private IFile _mockFile;
    private IDirectory _mockDirectory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register<IFile>(_mockFile);
        FactoryContainer.Register<IDirectory>(_mockDirectory);
        LogFactory.Register("ProgressLog", Substitute.For<ILog>());
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private static string TemplateFile => Path.Join("C:", "products", "Templates", "Main", "Template.json");
    private static string TablesPath => Path.Join("C:", "products", "Templates", "Main", "Tables");
    private static string TableFile => Path.Join(TablesPath, "Orders.json");

    private Template LoadWith(Platform platform, string tableJson)
    {
        _mockFile.Exists(TemplateFile).Returns(true);
        _mockFile.ReadAllText(TemplateFile).Returns(@"{ ""Name"": ""Main"", ""DatabaseIdentificationScript"": ""SELECT 1"" }");
        _mockDirectory.Exists(TablesPath).Returns(true);
        _mockDirectory.GetFiles(TablesPath, "*.json", SearchOption.AllDirectories).Returns([TableFile]);
        _mockFile.Exists(TableFile).Returns(true);
        _mockFile.ReadAllText(TableFile).Returns(tableJson);

        return Template.Load("Main", new Product
        {
            Name = "TestProduct",
            Platform = platform,
            FilePath = Path.Join("C:", "products", "Product.json")
        });
    }

    private const string ColumnLevelCheck = @"{
        ""Name"": ""`Orders`"",
        ""Columns"": [
            { ""Name"": ""`Status`"", ""DataType"": ""INT"", ""Nullable"": true, ""CheckExpression"": ""`Status` >= 0"" }
        ]
    }";

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void AColumnStillDeclaringCheckExpression_FailsTheLoad_NamingThePropertyAndTheFile(Platform platform)
    {
        var ex = Assert.Catch<Exception>(() => LoadWith(platform, ColumnLevelCheck));

        Assert.That(ex!.Message, Does.Contain("CheckExpression"), "the message must name the retired property: " + ex.Message);
        Assert.That(ex.Message, Does.Contain("Orders.json"), "and the file that carries it: " + ex.Message);
    }

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void TheTableLevelForm_IsUnaffected(Platform platform)
    {
        var template = LoadWith(platform, @"{
            ""Name"": ""`Orders`"",
            ""Columns"": [ { ""Name"": ""`Status`"", ""DataType"": ""INT"", ""Nullable"": true } ],
            ""CheckConstraints"": [ { ""Name"": ""CK_Orders_Status"", ""Expression"": ""`Status` >= 0"" } ]
        }");

        var table = template.Tables.Single();
        Assert.That(table.CheckConstraints.Single().Name, Is.EqualTo("CK_Orders_Status"));
        Assert.That(table.CheckConstraints.Single().Expression, Is.EqualTo("`Status` >= 0"));
    }

    [Test]
    public void TheAliasPropertyIsGone_FromTheModel()
    {
        Assert.That(typeof(MySqlColumn).GetProperty("CheckExpression"), Is.Null,
            "MySqlColumn.CheckExpression is retired; leaving the property would keep the generated .json-schemas "
            + "offering it, which is how a package keeps being authored against a retired key.");
    }

    // PostgreSQL keeps column-level authoring: pg_constraint.conkey attributes a single-column check back to
    // its column, so it round-trips. Retiring the MySQL alias must not touch it.
    [Test]
    public void PostgreSqlColumnCheckExpression_StillLoads()
    {
        var template = LoadWith(Platform.PostgreSQL, @"{
            ""Schema"": ""\""public\"""",
            ""Name"": ""\""Orders\"""",
            ""Columns"": [ { ""Name"": ""\""Status\"""", ""DataType"": ""INT"", ""Nullable"": true, ""CheckExpression"": ""\""Status\"" >= 0"" } ]
        }");

        Assert.That(template.Tables.Single().Columns.Single().GetType().GetProperty("CheckExpression"), Is.Not.Null);
    }
}
