// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using NUnit.Framework;
using Schema.DataAccess;
using Schema.Delivery;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;
using Index = Schema.Domain.Index;

namespace SchemaQuench.IntegrationTests.Shared;

/// <summary>
/// Integration tests for ProductQuench product loading functionality.
/// Tests loading Product.json and basic validation.
/// Uses dynamically created test databases via FixtureSetup.
/// </summary>
[Category("Integration")]
public abstract class ProductQuenchTestsSharedTests
{
    protected abstract Platform Platform { get; }
    protected abstract string MainDb { get; }
    protected abstract string MainConnectionString { get; }
    // TestFixtures product folder for the engine — its Product.json declares the matching Platform,
    // so Product.Load parse assertions (product.Platform == Platform) hold on both engines.
    protected abstract string FixtureProductFolder { get; }

    private string _testFixturePath = null!;
    private string _testDb = null!;
    private IConfigurationRoot _savedConfig;
    private bool _lockTaken;

    // Each test in this fixture temporarily Registers its own in-memory IConfigurationRoot
    // to drive Product.Load(). To avoid racing with other fixtures (notably ProductUpdateTests
    // below, which relies on the FixtureSetup-provided config being stable), the entire test
    // body runs while holding FactoryContainer.SharedLockObject. Monitor.Enter in SetUp and
    // Monitor.Exit in TearDown brackets the whole Test method. Save/restore preserves the
    // FixtureSetup-provided config for the next fixture.
    [SetUp]
    public void SetUp()
    {
        _lockTaken = false;
        Monitor.Enter(FactoryContainer.SharedLockObject, ref _lockTaken);

        _savedConfig = FactoryContainer.Resolve<IConfigurationRoot>();

        _testDb = MainDb;

        var assemblyLocation = Path.GetDirectoryName(typeof(ProductQuenchTestsSharedTests).Assembly.Location);
        _testFixturePath = Path.Combine(assemblyLocation!, "TestFixtures", FixtureProductFolder);
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            if (_savedConfig != null)
                FactoryContainer.Register(_savedConfig);
            else
                FactoryContainer.Unregister<IConfigurationRoot>();
            _savedConfig = null;
        }
        finally
        {
            if (_lockTaken)
            {
                Monitor.Exit(FactoryContainer.SharedLockObject);
                _lockTaken = false;
            }
        }
    }

    [Test]
    public void Product_Load_LoadsProductFromTestFixtures()
    {
        // Arrange - Set up configuration with test fixture path
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        // Act
        var product = Product.Load();

        // Assert
        Assert.That(product, Is.Not.Null);
        Assert.That(product.Name, Is.EqualTo("TestProduct"));
        Assert.That(product.Platform, Is.EqualTo(Platform));
        Assert.That(product.TemplateOrder, Has.Count.EqualTo(1));
        Assert.That(product.TemplateOrder[0], Is.EqualTo("TestTemplate"));
    }

    [Test]
    public void Product_Load_ThrowsForMissingPath()
    {
        // Arrange - Set up configuration with non-existent path
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", @"C:\NonExistent\Path")
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        // Act & Assert
        Assert.Throws<RunFailedException>(() => Product.Load());
    }

    [Test]
    public void Product_Load_SetsFilePath()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        // Act
        var product = Product.Load();

        // Assert
        Assert.That(product.FilePath, Does.EndWith("Product.json"));
        Assert.That(product.FilePath, Does.Contain("TestProduct"));
    }

    [Test]
    public void Product_Load_AddsProductNameToken()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        // Act
        var product = Product.Load();

        // Assert
        Assert.That(product.ScriptTokens, Contains.Key("ProductName"));
        Assert.That(product.ScriptTokens["ProductName"], Is.EqualTo("TestProduct"));
    }

    [Test]
    public void Template_Load_LoadsTemplateFromProduct()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        var product = Product.Load();

        // Act
        var template = Template.Load("TestTemplate", product);

        // Assert
        Assert.That(template, Is.Not.Null);
        Assert.That(template.Name, Is.EqualTo("TestTemplate"));
        Assert.That(template.RequireAtLeastOneTarget, Is.True);
    }

    [Test]
    public void Template_Load_SetsProductReference()
    {
        // Arrange
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        var product = Product.Load();

        // Act
        var template = Template.Load("TestTemplate", product);

        // Assert
        Assert.That(template.Product, Is.SameAs(product));
    }

    [Test]
    public void Product_ScriptTokens_CanBeOverriddenByConfig()
    {
        // Arrange - Create a product with a token and override it in config
        var productJsonPath = Path.Combine(_testFixturePath, "Product.json");
        var originalContent = File.ReadAllText(productJsonPath);

        try
        {
            // Add a token to the product
            var productWithToken = originalContent.Replace(
                "\"ScriptTokens\": {}",
                "\"ScriptTokens\": { \"TestToken\": \"OriginalValue\" }");
            File.WriteAllText(productJsonPath, productWithToken);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new[]
                {
                    new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath),
                    new KeyValuePair<string, string>("ScriptTokens:TestToken", "OverriddenValue")
                })
                .Build();
            FactoryContainer.Register<IConfigurationRoot>(config);

            // Act
            var product = Product.Load();

            // Assert - Token should be overridden by config
            Assert.That(product.ScriptTokens["TestToken"], Is.EqualTo("OverriddenValue"));
        }
        finally
        {
            // Restore original content
            File.WriteAllText(productJsonPath, originalContent);
        }
    }

    [Test]
    public void Template_MultiDatabase_IdentificationScriptCanReturnMultipleDatabases()
    {
        // Arrange - Create a template with multi-database identification script
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new[]
            {
                new KeyValuePair<string, string>("SchemaPackagePath", _testFixturePath)
            })
            .Build();
        FactoryContainer.Register<IConfigurationRoot>(config);

        var product = Product.Load();
        var template = Template.Load("TestTemplate", product);

        // Modify the identification script to return multiple databases (simulation)
        template.DatabaseIdentificationScript = $"SELECT '{_testDb}' AS DatabaseName UNION SELECT 'information_schema'";

        // Act - Execute the identification script
        using var connection = DbConnectionFactory.ForPlatform(Platform).GetDbConnection(MainConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = template.DatabaseIdentificationScript;

        var databases = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            databases.Add(reader.GetString(0));
        }

        // Assert - Should return multiple databases
        Assert.That(databases, Has.Count.EqualTo(2));
        Assert.That(databases, Contains.Item(_testDb));
        Assert.That(databases, Contains.Item("information_schema"));
    }

    [Test]
    public void Template_TablesWithMergeType_AreFilteredCorrectly()
    {
        // Arrange
        var template = new Template { Name = "TestTemplate" };
        template.Tables.Add(new Table { Name = "table1", DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "Insert/Update/Delete" }
                ] });
        template.Tables.Add(new Table { Name = "table2", DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "None" }
                ] });
        template.Tables.Add(new Table { Name = "table3", DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "Insert/Update" }
                ] });
        template.Tables.Add(new Table { Name = "table4", DataDelivery = null });
        template.Tables.Add(new Table { Name = "table5", DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "Insert" }
                ] });
        template.Tables.Add(new Table { Name = "table6", DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "" }
                ] });

        // Act - Filter tables with data delivery
        var tablesWithData = template.Tables
            .Where(t => t.DataDelivery != null &&
                        t.DataDelivery.Any(d => !string.IsNullOrWhiteSpace(d.MergeType) &&
                                     !d.MergeType.Equals("none", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Assert
        Assert.That(tablesWithData, Has.Count.EqualTo(3));
        Assert.That(tablesWithData.Select(t => t.Name), Is.EquivalentTo(new[] { "table1", "table3", "table5" }));
    }

    [Test]
    public void Table_WithContentFile_LoadsDataFromRelativePath()
    {
        // Arrange - Create temporary test fixture with table data
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_product_{Guid.NewGuid():N}");
        var tableDataDir = Path.Combine(tempDir, "TableData");
        Directory.CreateDirectory(tableDataDir);

        var tableDataPath = Path.Combine(tableDataDir, "test_data.json");
        File.WriteAllText(tableDataPath, @"[{""id"":1,""name"":""Test""}]");

        try
        {
            var table = new Table
            {
                Name = "test_table",
                DataDelivery =
                [
                    new DataDelivery
                    { MergeType = "Replace", ContentFile = "TableData/test_data.json" }
                ]
            };

            // Act - Load content file using ProductFileWrapper (simulating what DatabaseQuench does)
            var contentPath = Path.Join(tempDir, table.DataDelivery[0].ContentFile);
            var fileWrapper = ProductFileWrapper.GetFromFactory();

            // Assert
            Assert.That(fileWrapper.Exists(contentPath), Is.True);
            var content = fileWrapper.ReadAllText(contentPath);
            Assert.That(content, Does.Contain("Test"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Test]
    public void Table_DataDeliveryProperties_AreSerializedCorrectly()
    {
        // Arrange
        var table = new Table
        {
            Name = "config_data",
            DataDelivery =
                [
                    new DataDelivery
                    {
                MergeType = "Insert/Update",
                ContentFile = "TableData/config.json",
                MatchColumns = "`key`,`environment`",
                MergeFilter = "`active` = 1",
                MergeDisableTriggers = true
            }
                ]
        };

        // Act - Round-trip through JSON serialization
        var json = JsonHelper.Serialize(new List<Table> { table });
        var deserialized = JsonConvert.DeserializeObject<List<Table>>(json);

        // Assert
        Assert.That(deserialized, Has.Count.EqualTo(1));
        var result = deserialized[0];
        Assert.That(result.DataDelivery, Is.Not.Null);
        Assert.That(result.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
        Assert.That(result.DataDelivery[0].ContentFile, Is.EqualTo("TableData/config.json"));
        Assert.That(result.DataDelivery[0].MatchColumns, Is.EqualTo("`key`,`environment`"));
        Assert.That(result.DataDelivery[0].MergeFilter, Is.EqualTo("`active` = 1"));
        Assert.That(result.DataDelivery[0].MergeDisableTriggers, Is.True);
    }

    [Test]
    public void MergeScriptHelper_IntegrationWithTemplate_GeneratesCorrectSQL()
    {
        // Arrange - Create a complete table definition as it would appear in a product
        var table = new Table
        {
            Name = "lookup_values",
            DataDelivery =
                [
                    new DataDelivery
                    {
                MergeType = "Insert/Update/Delete",
                ContentFile = "TableData/lookup_values.json"
            }
                ],
            Columns =
            [
                new Column { Name = "id", DataType = "INT" },
                new Column { Name = "code", DataType = "VARCHAR(50)", Nullable = false },
                new Column { Name = "name", DataType = "VARCHAR(100)", Nullable = false },
                new Column { Name = "sort_order", DataType = "INT", Default = "0" }
            ],
            Indexes =
            [
                new Index { Name = "PRIMARY", PrimaryKey = true, IndexColumns = "`id`" },
                new Index { Name = "idx_code", Unique = true, IndexColumns = "`code`" }
            ]
        };

        // Act - Verify table can be serialized and properties are accessible
        var json = JsonHelper.Serialize(new List<Table> { table });
        Assert.That(json, Does.Contain("lookup_values"));
        Assert.That(json, Does.Contain("Insert/Update/Delete"));

        // Assert - Verify table properties for data delivery
        Assert.That(table.DataDelivery, Is.Not.Null);
        Assert.That(table.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update/Delete"));
        Assert.That(table.DataDelivery[0].ContentFile, Is.EqualTo("TableData/lookup_values.json"));
        Assert.That(!string.IsNullOrWhiteSpace(table.DataDelivery[0].MergeType) &&
                    !table.DataDelivery[0].MergeType.Equals("none", StringComparison.OrdinalIgnoreCase), Is.True);
    }
}
