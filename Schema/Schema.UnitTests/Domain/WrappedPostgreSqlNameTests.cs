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
    /// PostgreSQL keeps a quote inside a name, so a table or column Name still wrapped in double quotes deployed under a
    /// different name, quotes included. Load strips one wrapper pair, as the parse does from an OldName, and records it.
    /// </summary>
    [TestFixture]
    public class WrappedPostgreSqlNameTests
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
            var tableFile = Path.Join(tablesPath, "users.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories).Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);
            return Template.Load("Main", new Product { Name = "TestProduct", Platform = platform, FilePath = Path.Join("C:", "products", "Product.json") });
        }

        private static JToken DeployedTable(Template template) => JArray.Parse(template.TableSchema)[0];

        [Test]
        public void AWrappedTableOrColumnName_DeploysUnwrapped_AndSaysSo()
        {
            var template = Load(Platform.PostgreSQL, """
                { "Name": "\"Users\"",
                  "Columns": [ { "Name": "\"Email\"", "DataType": "TEXT" },
                               { "Name": "\"a\"\"b\"", "DataType": "TEXT" },
                               { "Name": "plain", "DataType": "TEXT" } ] }
                """);

            Assert.Multiple(() =>
            {
                Assert.That((string)DeployedTable(template)["Name"], Is.EqualTo("Users"));
                Assert.That(DeployedTable(template)["Columns"]!.Select(c => (string)c["Name"]), Is.EqualTo(new[] { "Email", "a\"b", "plain" }),
                    "one wrapper pair comes off, and a doubled quote inside is one quote, as in an OldName");
                Assert.That(template.LoadNotices.Select(n => (n.Code, n.Location)), Is.EqualTo(new[]
                {
                    ("SS-IDENT-003", "Template 'Main' / Table 'Users'"),
                    ("SS-IDENT-003", "Template 'Main' / Table 'Users' / Column 'Email'"),
                    ("SS-IDENT-003", "Template 'Main' / Table 'Users' / Column 'a\"b'")
                }));
                Assert.That(template.LoadNotices[0].Message, Is.EqualTo(
                    "Table name '\"Users\"' is wrapped in double quotes, which PostgreSQL would keep as part of the name, so it deploys as 'Users'. " +
                    "Remove the quotes so the declaration says what deploys."));
            });
        }

        // A quote inside a name is legal on PostgreSQL; only a pair around the whole name is the wrapped form.
        [TestCase("a\\\"b")]
        [TestCase("\\\"ab")]
        [TestCase("\\\"")]
        [TestCase("\\\"\\\"")]
        public void ANameThatIsNotWrapped_IsLeftAlone(string name)
        {
            var template = Load(Platform.PostgreSQL, $$"""{ "Name": "users", "Columns": [ { "Name": "{{name}}", "DataType": "TEXT" } ] }""");

            Assert.That(template.LoadNotices, Is.Empty);
        }

        [Test]
        public void AnotherEngine_IsNotUnwrapped()
        {
            var template = Load(Platform.SqlServer, """{ "Name": "\"Users\"", "Columns": [ { "Name": "Id", "DataType": "INT" } ] }""");

            Assert.Multiple(() =>
            {
                Assert.That((string)DeployedTable(template)["Name"], Is.EqualTo("\"Users\""));
                Assert.That(template.LoadNotices, Is.Empty);
            });
        }
    }
}
