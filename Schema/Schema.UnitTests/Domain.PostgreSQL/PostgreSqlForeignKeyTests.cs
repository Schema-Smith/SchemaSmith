// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain;
using Schema.Domain.PostgreSQL;

namespace Schema.UnitTests.Domain.PostgreSQL
{
    [TestFixture]
    public class PostgreSqlForeignKeyTests
    {
        [Test]
        public void InheritsFromForeignKey()
        {
            Assert.That(new PostgreSqlForeignKey(), Is.InstanceOf<ForeignKey>());
        }

        [Test]
        public void DefaultValues_AreCorrect()
        {
            var fk = new PostgreSqlForeignKey();

            // Slice 1 (Schema Templates): bare-constructor RelatedTableSchema is null;
            // SchemaDefaultResolver fills it. See PostgreSqlForeignKeyDefaultingTests.
            Assert.That(fk.RelatedTableSchema, Is.Null);
            Assert.That(fk.Deferrable, Is.False);
            Assert.That(fk.InitiallyDeferred, Is.False);
            Assert.That(fk.MatchType, Is.EqualTo("SIMPLE"), "the engine's own default, so an omitted MatchType deploys what CREATE TABLE gives");
        }

        [Test]
        public void JsonRoundTrip_PreservesAllProperties()
        {
            var fk = new PostgreSqlForeignKey
            {
                Name = "fk_order_customer",
                Columns = "customer_id",
                RelatedTableSchema = "sales",
                RelatedTable = "customer",
                RelatedColumns = "id",
                Deferrable = true,
                InitiallyDeferred = true,
                MatchType = "PARTIAL"
            };

            var json = JsonConvert.SerializeObject(fk);
            var deserialized = JsonConvert.DeserializeObject<PostgreSqlForeignKey>(json);

            Assert.That(deserialized.RelatedTableSchema, Is.EqualTo("sales"));
            Assert.That(deserialized.Deferrable, Is.True);
            Assert.That(deserialized.InitiallyDeferred, Is.True);
            Assert.That(deserialized.MatchType, Is.EqualTo("PARTIAL"));
        }

        [Test]
        public void NoPhantomProperties_FromOtherPlatforms()
        {
            var fk = new PostgreSqlForeignKey { Name = "fk_test", Columns = "col1", RelatedTable = "t2", RelatedColumns = "id" };
            var json = JsonConvert.SerializeObject(fk);

            // Should not have any SQL Server or MySQL only properties beyond what's defined
            Assert.That(json, Does.Not.Contain("\"Comment\""));
        }
    }
}
