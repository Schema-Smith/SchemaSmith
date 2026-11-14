// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Utility;

using Schema.Delivery;
namespace Schema.UnitTests.Domain.MySQL
{
    [TestFixture]
    public class MySqlTableTests
    {
        [Test]
        public void InheritsFromTable()
        {
            Assert.That(new MySqlTable(), Is.InstanceOf<Table>());
        }

        [Test]
        public void DefaultValues_AreCorrect()
        {
            var table = new MySqlTable();

            Assert.That(table.Engine, Is.Null, "omitted is unmanaged on an existing table; the CREATE supplies InnoDB");
            Assert.That(table.RowFormat, Is.Null);
            Assert.That(table.CharacterSet, Is.Null);
            Assert.That(table.Collation, Is.Null);
            Assert.That(table.Comment, Is.Null);
            Assert.That(table.AutoIncrementValue, Is.Null);
            Assert.That(table.FullTextIndexes, Is.Not.Null.And.Empty);
        }

        [Test]
        public void DoesNotHaveSchemaProperty()
        {
            // MySQL schemas = databases, no Schema property on Table
            var props = typeof(MySqlTable).GetProperties();
            Assert.That(props.Any(p => p.Name == "Schema" && p.DeclaringType == typeof(MySqlTable)), Is.False);
        }

        [Test]
        public void JsonRoundTrip_PreservesAllProperties()
        {
            var table = new MySqlTable
            {
                Name = "customer",
                Engine = "MyISAM",
                RowFormat = "COMPRESSED",
                CharacterSet = "utf8mb4",
                Collation = "utf8mb4_unicode_ci",
                Comment = "Main customer table",
                AutoIncrementValue = 1000UL,
                DataDelivery = [new Schema.Delivery.DataDelivery { MergeType = "Insert/Update" }]
            };

            table.FullTextIndexes.Add(new FullTextIndex
            {
                Name = "ft_customer_name",
                Columns = "first_name, last_name",
                Parser = "ngram",
                Comment = "Name search"
            });

            var json = JsonConvert.SerializeObject(table);
            var deserialized = JsonConvert.DeserializeObject<MySqlTable>(json);

            Assert.That(deserialized.Engine, Is.EqualTo("MyISAM"));
            Assert.That(deserialized.RowFormat, Is.EqualTo("COMPRESSED"));
            Assert.That(deserialized.CharacterSet, Is.EqualTo("utf8mb4"));
            Assert.That(deserialized.Collation, Is.EqualTo("utf8mb4_unicode_ci"));
            Assert.That(deserialized.Comment, Is.EqualTo("Main customer table"));
            Assert.That(deserialized.AutoIncrementValue, Is.EqualTo(1000UL));
            Assert.That(deserialized.DataDelivery[0].MergeType, Is.EqualTo("Insert/Update"));
            Assert.That(deserialized.FullTextIndexes, Has.Count.EqualTo(1));
            Assert.That(deserialized.FullTextIndexes[0].Name, Is.EqualTo("ft_customer_name"));
            Assert.That(deserialized.FullTextIndexes[0].Parser, Is.EqualTo("ngram"));
        }

        [Test]
        public void UsesStandardizedMergeType()
        {
            // MySQL standardized to None|Insert|Insert/Update|Insert/Update/Delete
            // NOT the old Replace|Upsert pattern
            var table = new MySqlTable
            {
                Name = "test",
                DataDelivery = [new Schema.Delivery.DataDelivery { MergeType = "Insert/Update/Delete" }]
            };

            var json = JsonConvert.SerializeObject(table);
            Assert.That(json, Does.Contain("Insert/Update/Delete"));
        }

        [Test]
        public void GenerateSchema_RowFormatPatternAcceptsMySqlCasing()
        {
            var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlTable));
            var pattern = schema["properties"]?["RowFormat"]?["pattern"]?.ToString();
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch("Dynamic", pattern!), Is.True);
        }

        [Test]
        public void NoPhantomProperties_FromOtherPlatforms()
        {
            var table = new MySqlTable { Name = "test" };
            var json = JsonConvert.SerializeObject(table);

            Assert.That(json, Does.Not.Contain("\"Schema\""));
            Assert.That(json, Does.Not.Contain("CompressionType"));
            Assert.That(json, Does.Not.Contain("IsTemporal"));
            Assert.That(json, Does.Not.Contain("XmlIndexes"));
            Assert.That(json, Does.Not.Contain("EnableCDC"));
            Assert.That(json, Does.Not.Contain("\"ExcludeConstraints\":"));
            Assert.That(json, Does.Not.Contain("RowLevelSecurity"));
            Assert.That(json, Does.Not.Contain("PersistenceType"));
        }
    }
}
