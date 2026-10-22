// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    /// <summary>
    /// A primary key's columns cannot hold NULL, so a key column declared Nullable failed the deploy on every engine
    /// (PostgreSQL on every deploy after the first). Load deploys it NOT NULL and records why, so the deploy can warn
    /// before the change and --Validate can report the contradiction.
    /// </summary>
    [TestFixture]
    public class PrimaryKeyColumnNotNullTests
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
        }

        [TearDown]
        public void TearDown()
        {
            FactoryContainer.Clear();
            LogFactory.Clear();
        }

        private Template Load(Platform platform, string tableJson)
        {
            var templatePath = Path.Join("C:", "products", "Templates", "Main", "Template.json");
            _mockFile.Exists(templatePath).Returns(true);
            _mockFile.ReadAllText(templatePath).Returns(@"{ ""Name"": ""Main"" }");
            var tablesPath = Path.Join("C:", "products", "Templates", "Main", "Tables");
            _mockDirectory.Exists(tablesPath).Returns(true);
            var tableFile = Path.Join(tablesPath, "Widget.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories).Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);
            return Template.Load("Main", new Product { Name = "TestProduct", Platform = platform, FilePath = Path.Join("C:", "products", "Product.json") });
        }

        private static JToken DeployedColumn(Template template, string name) =>
            JArray.Parse(template.TableSchema)[0]["Columns"]!.Single(c => (string)c["Name"] == name);

        [TestCase(Platform.SqlServer, "[Id]", "[Id]")]
        [TestCase(Platform.PostgreSQL, "\\\"Id\\\"", "Id")]
        [TestCase(Platform.MySQL, "`Id`", "Id")]
        [TestCase(Platform.MariaDb, "`Id`(10)", "Id")]
        public void ANullableKeyColumn_DeploysNotNull_AndSaysSo(Platform platform, string keyPart, string columnName)
        {
            var template = Load(platform, $@"{{ ""Name"": ""Widget"",
                ""Columns"": [ {{ ""Name"": ""{columnName}"", ""DataType"": ""VARCHAR(20)"", ""Nullable"": true }},
                               {{ ""Name"": ""Note"", ""DataType"": ""VARCHAR(20)"", ""Nullable"": true }} ],
                ""Indexes"": [ {{ ""Name"": ""PK_Widget"", ""PrimaryKey"": true, ""IndexColumns"": ""{keyPart}"" }} ] }}");

            Assert.Multiple(() =>
            {
                Assert.That(DeployedColumn(template, columnName)["Nullable"], Is.Null, "the deployed JSON carries no Nullable: true for the key column, which the engine reads as NOT NULL");
                Assert.That((bool?)DeployedColumn(template, "Note")["Nullable"], Is.True, "a column outside the key keeps its declaration");
                Assert.That(template.LoadNotices.Select(n => n.Location), Is.EqualTo(new[] { "Template 'Main' / Table 'Widget' / Column '" + columnName + "'" }));
                Assert.That(template.LoadNotices.Single().Message, Is.EqualTo(
                    $"Column '{columnName}' of table 'Widget' is declared Nullable but is in primary key 'PK_Widget', so it is deployed NOT NULL. " +
                    "If the column already exists and holds NULLs, that change fails; remove \"Nullable\": true so the declaration says what deploys."));
            });
        }

        [Test]
        public void OnlyTheNullableKeyParts_AreChanged_OrderingAndQuotingIgnored()
        {
            var template = Load(Platform.SqlServer, @"{ ""Name"": ""Widget"",
                ""Columns"": [ { ""Name"": ""A"", ""DataType"": ""INT"" },
                               { ""Name"": ""B"", ""DataType"": ""INT"", ""Nullable"": true },
                               { ""Name"": ""C"", ""DataType"": ""INT"", ""Nullable"": true } ],
                ""Indexes"": [ { ""Name"": ""PK_Widget"", ""PrimaryKey"": true, ""IndexColumns"": ""A, [B] DESC"" },
                               { ""Name"": ""IX_C"", ""IndexColumns"": ""C"" } ] }");

            Assert.Multiple(() =>
            {
                Assert.That(DeployedColumn(template, "B")["Nullable"], Is.Null);
                Assert.That((bool?)DeployedColumn(template, "C")["Nullable"], Is.True, "a nullable column in an ordinary index is left alone");
                Assert.That(template.LoadNotices.Select(n => n.Location), Is.EqualTo(new[] { "Template 'Main' / Table 'Widget' / Column 'B'" }));
            });
        }

        [Test]
        public void PostgreSql_MatchesTheKeyToTheColumnByExactSpelling()
        {
            // PostgreSQL deploys every name quoted, so "id" and "Id" are two columns there and the key does not name "Id".
            var template = Load(Platform.PostgreSQL, @"{ ""Name"": ""Widget"",
                ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""INT"", ""Nullable"": true } ],
                ""Indexes"": [ { ""Name"": ""PK_Widget"", ""PrimaryKey"": true, ""IndexColumns"": ""id"" } ] }");

            Assert.That(template.LoadNotices, Is.Empty);
        }

        [Test]
        public void AConditionalKey_LeavesTheColumnAsDeclared()
        {
            // A key with a ShouldApplyExpression may not deploy at all, and then the column is nullable as declared.
            var template = Load(Platform.SqlServer, @"{ ""Name"": ""Widget"",
                ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""INT"", ""Nullable"": true } ],
                ""Indexes"": [ { ""Name"": ""PK_Widget"", ""PrimaryKey"": true, ""IndexColumns"": ""Id"", ""ShouldApplyExpression"": ""1 = 0"" } ] }");

            Assert.Multiple(() =>
            {
                Assert.That((bool?)DeployedColumn(template, "Id")["Nullable"], Is.True);
                Assert.That(template.LoadNotices, Is.Empty);
            });
        }

        [Test]
        public void ANotNullKeyColumn_IsNotReported()
        {
            var template = Load(Platform.SqlServer, @"{ ""Name"": ""Widget"",
                ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""INT"" } ],
                ""Indexes"": [ { ""Name"": ""PK_Widget"", ""PrimaryKey"": true, ""IndexColumns"": ""Id"" } ] }");

            Assert.That(template.LoadNotices, Is.Empty);
        }
    }
}
