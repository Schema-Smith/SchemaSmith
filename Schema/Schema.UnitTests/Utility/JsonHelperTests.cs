// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Newtonsoft.Json;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class JsonHelperTests
{
    private IFile _mockFile;

    [SetUp]
    public void SetUp()
    {
        _mockFile = Substitute.For<IFile>();
        FactoryContainer.Register(_mockFile);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
    }

    // --- Load<T> ---

    [Test]
    public void Load_FileDoesNotExist_ThrowsException()
    {
        _mockFile.Exists(@"C:\missing.json").Returns(false);

        var ex = Assert.Throws<RunFailedException>(() => JsonHelper.Load<Table>(@"C:\missing.json"));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void Load_FileExists_DeserializesObject()
    {
        _mockFile.Exists(@"C:\table.json").Returns(true);
        _mockFile.ReadAllText(@"C:\table.json").Returns("{\"Name\":\"TestTable\"}");

        var result = JsonHelper.Load<Table>(@"C:\table.json");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("TestTable"));
    }

    // --- ProductLoad ---

    [Test]
    public void ProductLoad_FileDoesNotExist_ThrowsException()
    {
        _mockFile.Exists(@"C:\missing.json").Returns(false);

        var ex = Assert.Throws<RunFailedException>(() => JsonHelper.ProductLoad(@"C:\missing.json"));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void ProductLoad_DeserializesProduct_WithPlatform()
    {
        _mockFile.Exists(@"C:\Product.json").Returns(true);
        _mockFile.ReadAllText(@"C:\Product.json").Returns("{\"Name\":\"Test\",\"Platform\":\"SQL Server\"}");

        var result = JsonHelper.ProductLoad(@"C:\Product.json");

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Name, Is.EqualTo("Test"));
        Assert.That(result.Platform, Is.EqualTo(Platform.SqlServer));
    }

    [Test]
    public void ProductLoad_AcceptsAllPlatforms_NoValidation()
    {
        // Unified tools accept all platforms — no platform mismatch error
        _mockFile.Exists(@"C:\Product.json").Returns(true);
        _mockFile.ReadAllText(@"C:\Product.json").Returns("{\"Name\":\"Test\",\"Platform\":\"PostgreSQL\"}");

        var result = JsonHelper.ProductLoad(@"C:\Product.json");

        Assert.That(result.Platform, Is.EqualTo(Platform.PostgreSQL));
    }

    [TestCase("MySQL", Platform.MySQL)]
    [TestCase("mysql", Platform.MySQL)]
    [TestCase("SQL Server", Platform.SqlServer)]
    [TestCase("sqlserver", Platform.SqlServer)]
    [TestCase("PostgreSQL", Platform.PostgreSQL)]
    [TestCase("postgresql", Platform.PostgreSQL)]
    public void ProductLoad_HandlesLegacyPlatformStrings(string platformString, Platform expectedPlatform)
    {
        _mockFile.Exists(@"C:\Product.json").Returns(true);
        _mockFile.ReadAllText(@"C:\Product.json").Returns($"{{\"Name\":\"Test\",\"Platform\":\"{platformString}\"}}");

        var result = JsonHelper.ProductLoad(@"C:\Product.json");

        Assert.That(result.Platform, Is.EqualTo(expectedPlatform));
    }

    [Test]
    public void ProductLoad_UsesProductFileWrapper()
    {
        // ProductLoad should use ProductFileWrapper (which supports zip files)
        _mockFile.Exists(@"C:\Product.json").Returns(true);
        _mockFile.ReadAllText(@"C:\Product.json").Returns("{\"Name\":\"Test\",\"Platform\":\"MySQL\"}");

        JsonHelper.ProductLoad(@"C:\Product.json");

        _mockFile.Received().ReadAllText(@"C:\Product.json");
    }

    [Test]
    public void ProductLoad_UnrecognizedProperty_ThrowsNamingPropertyAndFile()
    {
        // Matches the editor's generated .json-schemas (additionalProperties:false) and
        // --Validate — deploy used to silently drop an unknown property via Newtonsoft's default
        // MissingMemberHandling.Ignore; it must now fail loudly, naming both the property and file.
        _mockFile.Exists(@"C:\Product.json").Returns(true);
        _mockFile.ReadAllText(@"C:\Product.json").Returns("{\"Name\":\"Test\",\"Platform\":\"MySQL\",\"Bogus\":1}");

        var ex = Assert.Throws<JsonSerializationException>(() => JsonHelper.ProductLoad(@"C:\Product.json"));

        Assert.That(ex.Message, Does.Contain("Bogus"));
        Assert.That(ex.Message, Does.Contain(@"C:\Product.json"));
    }

    // --- TableLoad ---

    [Test]
    public void TableLoad_FileDoesNotExist_ThrowsException()
    {
        _mockFile.Exists(@"C:\table.json").Returns(false);

        var ex = Assert.Throws<RunFailedException>(() => JsonHelper.TableLoad(@"C:\table.json", Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void TableLoad_SqlServer_ReturnsSqlServerTable()
    {
        _mockFile.Exists(@"C:\table.json").Returns(true);
        _mockFile.ReadAllText(@"C:\table.json").Returns("{\"Name\":\"TestTable\"}");

        var result = JsonHelper.TableLoad(@"C:\table.json", Platform.SqlServer);

        Assert.That(result, Is.InstanceOf<SqlServerTable>());
        Assert.That(result.Name, Is.EqualTo("TestTable"));
    }

    [Test]
    public void TableLoad_PostgreSQL_ReturnsPostgreSqlTable()
    {
        _mockFile.Exists(@"C:\table.json").Returns(true);
        _mockFile.ReadAllText(@"C:\table.json").Returns("{\"Name\":\"TestTable\"}");

        var result = JsonHelper.TableLoad(@"C:\table.json", Platform.PostgreSQL);

        Assert.That(result, Is.InstanceOf<PostgreSqlTable>());
        Assert.That(result.Name, Is.EqualTo("TestTable"));
    }

    [Test]
    public void TableLoad_MySQL_ReturnsMySqlTable()
    {
        _mockFile.Exists(@"C:\table.json").Returns(true);
        _mockFile.ReadAllText(@"C:\table.json").Returns("{\"Name\":\"TestTable\"}");

        var result = JsonHelper.TableLoad(@"C:\table.json", Platform.MySQL);

        Assert.That(result, Is.InstanceOf<MySqlTable>());
        Assert.That(result.Name, Is.EqualTo("TestTable"));
    }

    [Test]
    public void TableLoad_UsesProductFileWrapper()
    {
        _mockFile.Exists(@"C:\table.json").Returns(true);
        _mockFile.ReadAllText(@"C:\table.json").Returns("{\"Name\":\"TestTable\"}");

        JsonHelper.TableLoad(@"C:\table.json", Platform.SqlServer);

        _mockFile.Received().ReadAllText(@"C:\table.json");
    }

    // --- TemplateLoad ---

    [Test]
    public void TemplateLoad_FileDoesNotExist_ThrowsException()
    {
        _mockFile.Exists(@"C:\template.json").Returns(false);

        var ex = Assert.Throws<RunFailedException>(() => JsonHelper.TemplateLoad(@"C:\template.json", Platform.SqlServer));
        Assert.That(ex.Message, Does.Contain("does not exist"));
    }

    [Test]
    public void TemplateLoad_SqlServer_ReturnsSqlServerTemplate()
    {
        _mockFile.Exists(@"C:\template.json").Returns(true);
        _mockFile.ReadAllText(@"C:\template.json").Returns("{\"Name\":\"TestTemplate\"}");

        var result = JsonHelper.TemplateLoad(@"C:\template.json", Platform.SqlServer);

        Assert.That(result, Is.InstanceOf<SqlServerTemplate>());
        Assert.That(result.Name, Is.EqualTo("TestTemplate"));
    }

    [Test]
    public void TemplateLoad_PostgreSQL_ReturnsPostgreSqlTemplate()
    {
        _mockFile.Exists(@"C:\template.json").Returns(true);
        _mockFile.ReadAllText(@"C:\template.json").Returns("{\"Name\":\"TestTemplate\"}");

        var result = JsonHelper.TemplateLoad(@"C:\template.json", Platform.PostgreSQL);

        Assert.That(result, Is.InstanceOf<PostgreSqlTemplate>());
        Assert.That(result.Name, Is.EqualTo("TestTemplate"));
    }

    [Test]
    public void TemplateLoad_MySQL_ReturnsMySqlTemplate()
    {
        _mockFile.Exists(@"C:\template.json").Returns(true);
        _mockFile.ReadAllText(@"C:\template.json").Returns("{\"Name\":\"TestTemplate\"}");

        var result = JsonHelper.TemplateLoad(@"C:\template.json", Platform.MySQL);

        Assert.That(result, Is.InstanceOf<MySqlTemplate>());
        Assert.That(result.Name, Is.EqualTo("TestTemplate"));
    }

    // --- Serialize ---

    [Test]
    public void Serialize_FormatsWithIndentAndIgnoresNull()
    {
        var table = new Table { Name = "TestTable" };

        var json = JsonHelper.Serialize(table);

        Assert.That(json, Does.Contain("\"Name\": \"TestTable\""));
        Assert.That(json, Does.Contain("\n")); // Indented formatting
    }

    [Test]
    public void Serialize_NullValues_AreOmitted()
    {
        var table = new Table { Name = "TestTable" };

        var json = JsonHelper.Serialize(table);

        Assert.That(json, Does.Not.Contain("\"ContentFile\""));
        Assert.That(json, Does.Not.Contain("\"ShouldApplyExpression\""));
    }

    [Test]
    public void Serialize_DefaultValues_AreOmitted()
    {
        var table = new Table { Name = "TestTable" };

        var json = JsonHelper.Serialize(table);

        // MergeDisableTriggers defaults to false — should be omitted
        Assert.That(json, Does.Not.Contain("\"MergeDisableTriggers\""));
    }

    [Test]
    public void Serialize_Product_WritesPlatformAsCanonicalString()
    {
        var product = new Product { Name = "Test", Platform = Platform.SqlServer };

        var json = JsonHelper.Serialize(product);

        Assert.That(json, Does.Contain("\"Platform\": \"SqlServer\""));
    }

    // --- Write ---

    [Test]
    public void Write_SerializesAndWritesToFile()
    {
        var table = new Table { Name = "WriteTest" };
        JsonHelper.Write(@"C:\output.json", table);

        _mockFile.Received().WriteAllText(@"C:\output.json", Arg.Is<string>(s => s.Contains("WriteTest")));
    }

    [Test]
    public void Write_UsesSerializeSettings()
    {
        var table = new Table { Name = "WriteTest" };
        JsonHelper.Write(@"C:\output.json", table);

        // Verify the written content is indented (not minified)
        _mockFile.Received().WriteAllText(@"C:\output.json", Arg.Is<string>(s => s.Contains("\n")));
    }
}
