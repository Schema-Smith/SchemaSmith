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
    /// The deploy tells an omitted constraint attribute from a declared one, and leaves an existing constraint as it is when
    /// it is omitted. The payload it received never omitted one: the attributes were plain bools with defaults, so every
    /// constraint arrived "Enforced": true, "Validated": true, and a deploy re-enabled and re-validated what someone had
    /// disabled. An omitted attribute is now absent from the payload, and a declared one, true included, is present.
    /// </summary>
    [TestFixture]
    public class ConstraintStatePayloadTests
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
            var tableFile = Path.Join(tablesPath, "orders.json");
            _mockDirectory.GetFiles(tablesPath, "*.json", SearchOption.AllDirectories).Returns(new[] { tableFile });
            _mockFile.Exists(tableFile).Returns(true);
            _mockFile.ReadAllText(tableFile).Returns(tableJson);
            return Template.Load("Main", new Product { Name = "TestProduct", Platform = platform, FilePath = Path.Join("C:", "products", "Product.json") });
        }

        private static string Keys(JToken constraint) =>
            string.Join(",", ((JObject)constraint).Properties().Select(p => p.Name)
                .Where(n => n is "Enforced" or "Validated" or "NotForReplication" or "NoInherit").OrderBy(n => n));

        [TestCase(Platform.SqlServer, "Enforced,NotForReplication,Validated")]
        [TestCase(Platform.PostgreSQL, "Enforced,NoInherit,Validated")]
        public void AnOmittedAttribute_IsAbsentFromThePayload_AndADeclaredOneIsThere(Platform platform, string declaredKeys)
        {
            var nfrOrInherit = platform == Platform.SqlServer ? "NotForReplication" : "NoInherit";
            var template = Load(platform, $$"""
                { "Name": "orders",
                  "Columns": [ { "Name": "id", "DataType": "INT" }, { "Name": "parent", "DataType": "INT" } ],
                  "ForeignKeys": [ { "Name": "fk_bare", "Columns": "parent", "RelatedTable": "orders", "RelatedColumns": "id" } ],
                  "CheckConstraints": [ { "Name": "ck_bare", "Expression": "id > 0" },
                                        { "Name": "ck_declared", "Expression": "id > 1", "Enforced": true, "Validated": false, "{{nfrOrInherit}}": false } ] }
                """);

            var table = JArray.Parse(template.TableSchema)[0];
            var checks = table["CheckConstraints"]!.ToDictionary(c => (string)c["Name"], c => c);
            Assert.Multiple(() =>
            {
                Assert.That(Keys(table["ForeignKeys"]![0]), Is.Empty, "an omitted attribute is not sent, so the deploy leaves the key as it is");
                Assert.That(Keys(checks["ck_bare"]), Is.Empty);
                Assert.That(Keys(checks["ck_declared"]), Is.EqualTo(declaredKeys), "a declared one is sent, even at its default");
            });
        }

        // MySQL's payload is written by JsonHelper.SerializeAll, not the template's own serializer.
        [Test]
        public void MySql_AnOmittedEnforced_IsAbsent_AndADeclaredOneIsThere()
        {
            var template = Load(Platform.MySQL, """
                { "Name": "orders",
                  "Columns": [ { "Name": "id", "DataType": "INT" } ],
                  "CheckConstraints": [ { "Name": "ck_bare", "Expression": "id > 0" }, { "Name": "ck_declared", "Expression": "id > 1", "Enforced": true } ] }
                """);

            var checks = JArray.Parse(JsonHelper.SerializeAll(template.Tables))[0]["CheckConstraints"]!;
            Assert.That(checks.Select(Keys), Is.EqualTo(new[] { "", "Enforced" }));
        }
    }
}
