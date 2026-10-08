// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Domain.PostgreSQL;
using Schema.Domain.MySQL;
using Schema.Isolators;
using Schema.Delivery;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class ProductLoadTests
    {
        private IFile _mockFile;
        private IDirectory _mockDirectory;

        [SetUp]
        public void SetUp()
        {
            FactoryContainer.Clear();
            _mockFile = Substitute.For<IFile>();
            _mockDirectory = Substitute.For<IDirectory>();
            FactoryContainer.Register<IFile>(_mockFile);
            FactoryContainer.Register<IDirectory>(_mockDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            FactoryContainer.Clear();
        }

        [Test]
        public void Load_SqlServer_ReturnsProductWithCorrectPlatform()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "TestProduct",
                Platform = "SqlServer",
                TemplateOrder = new List<string>()
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product, Is.Not.Null);
            Assert.That(product.Name, Is.EqualTo("TestProduct"));
            Assert.That(product.Platform, Is.EqualTo(Platform.SqlServer));
        }

        [Test]
        public void Load_PostgreSQL_ReturnsProductWithCorrectPlatform()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "PgProduct",
                Platform = "PostgreSQL",
                TemplateOrder = new List<string>()
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.Platform, Is.EqualTo(Platform.PostgreSQL));
        }

        [Test]
        public void Load_MySQL_ReturnsProductWithCorrectPlatform()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "MySqlProduct",
                Platform = "MySQL",
                TemplateOrder = new List<string>()
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.Platform, Is.EqualTo(Platform.MySQL));
        }

        [Test]
        public void Load_MSSQL_ParsedAsSqlServer()
        {
            var productJson = "{\"Name\":\"Test\",\"Platform\":\"MSSQL\",\"TemplateOrder\":[]}";
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.Platform, Is.EqualTo(Platform.SqlServer));
        }

        [Test]
        public void Load_SetsFilePath_ToDirectoryOfProductJson()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "TestProduct",
                Platform = "SqlServer",
                TemplateOrder = new List<string>()
            });
            var productPath = Path.Combine("C:", "products", "MyProduct", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.FilePath, Is.EqualTo(Path.Combine("C:", "products", "MyProduct")));
        }

        [Test]
        public void Load_PreservesScriptTokens()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "TestProduct",
                Platform = "SqlServer",
                TemplateOrder = new List<string>(),
                ScriptTokens = new Dictionary<string, string>
                {
                    { "MainDB", "TestMain" },
                    { "SecondaryDB", "TestSecondary" }
                }
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.ScriptTokens, Has.Count.EqualTo(2));
            Assert.That(product.ScriptTokens["MainDB"], Is.EqualTo("TestMain"));
        }

        [Test]
        public void Load_PreservesTemplateOrder()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "TestProduct",
                Platform = "SqlServer",
                TemplateOrder = new List<string> { "Main", "Secondary" }
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.TemplateOrder, Has.Count.EqualTo(2));
            Assert.That(product.TemplateOrder[0], Is.EqualTo("Main"));
            Assert.That(product.TemplateOrder[1], Is.EqualTo("Secondary"));
        }

        [Test]
        public void Load_ThrowsWhenFileNotFound()
        {
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(false);

            Assert.Throws<RunFailedException>(() => Product.LoadForDisplay(productPath));
        }

        [Test]
        public void Load_PreservesMinimumVersion()
        {
            var productJson = JsonConvert.SerializeObject(new
            {
                Name = "TestProduct",
                Platform = "PostgreSQL",
                MinimumVersion = "15",
                TemplateOrder = new List<string>()
            });
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.MinimumVersion, Is.EqualTo("15"));
        }

        [Test]
        public void Load_PreservesScriptFolders()
        {
            var productJson = @"{
                ""Name"": ""TestProduct"",
                ""Platform"": ""SqlServer"",
                ""TemplateOrder"": [],
                ""ScriptFolders"": [
                    { ""FolderPath"": ""Jobs"", ""QuenchSlot"": ""After"" }
                ]
            }";
            var productPath = Path.Combine("C:", "products", "Product.json");
            _mockFile.Exists(productPath).Returns(true);
            _mockFile.ReadAllText(productPath).Returns(productJson);

            var product = Product.LoadForDisplay(productPath);

            Assert.That(product.ScriptFolders, Has.Count.EqualTo(1));
            Assert.That(product.ScriptFolders[0].FolderPath, Is.EqualTo("Jobs"));
            Assert.That(product.ScriptFolders[0].QuenchSlot, Is.EqualTo(ProductQuenchSlot.After));
        }

        [Test]
        public void Save_WritesSerializedProductToFile()
        {
            var product = new Product
            {
                Name = "SaveTest",
                Platform = Platform.SqlServer,
                TemplateOrder = new List<string> { "Main" }
            };
            var filePath = Path.Combine("C:", "products", "Product.json");

            Product.Save(filePath, product);

            _mockFile.Received(1).WriteAllText(filePath, Arg.Is<string>(s =>
                s.Contains("SaveTest") && s.Contains("SqlServer")));
        }

        [Test]
        public void Save_SerializesPlatformAsCanonicalString()
        {
            var product = new Product
            {
                Name = "CanonicalTest",
                Platform = Platform.SqlServer
            };
            var filePath = Path.Combine("C:", "products", "Product.json");

            Product.Save(filePath, product);

            _mockFile.Received(1).WriteAllText(filePath, Arg.Is<string>(s =>
                s.Contains("\"SqlServer\"") && !s.Contains("MSSQL")));
        }

        [Test]
        public void Save_OmitsNullAndDefaultValues()
        {
            var product = new Product
            {
                Name = "MinimalTest",
                Platform = Platform.MySQL
            };
            var filePath = Path.Combine("C:", "products", "Product.json");

            Product.Save(filePath, product);

            _mockFile.Received(1).WriteAllText(filePath, Arg.Is<string>(s =>
                !s.Contains("ValidationScript") && !s.Contains("DropUnknownIndexes")));
        }

        [Test]
        public void Load_AcceptsAllPlatforms_NoPlatformValidation()
        {
            // Key design change: unified tools accept all platforms, no platform check against ConfigHelper
            foreach (var platform in new[] { "SqlServer", "PostgreSQL", "MySQL", "MSSQL" })
            {
                var productJson = $"{{\"Name\":\"Test\",\"Platform\":\"{platform}\",\"TemplateOrder\":[]}}";
                var productPath = Path.Combine("C:", "products", "Product.json");
                _mockFile.Exists(productPath).Returns(true);
                _mockFile.ReadAllText(productPath).Returns(productJson);

                Assert.DoesNotThrow(() => Product.LoadForDisplay(productPath),
                    $"Platform {platform} should be accepted without platform validation");
            }
        }
    }
}
