// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class ColumnTests
    {
        [Test]
        public void DefaultValues_AreCorrect()
        {
            var column = new Column();

            Assert.That(column.Name, Is.EqualTo(""));
            Assert.That(column.DataType, Is.EqualTo(""));
            Assert.That(column.Nullable, Is.False);
            Assert.That(column.Default, Is.Null);
            Assert.That(column.ShouldApplyExpression, Is.Null);
            Assert.That(column.OldName, Is.Null);
        }

        [Test]
        public void JsonRoundTrip_PreservesAllProperties()
        {
            var column = new Column
            {
                Name = "CustomerId",
                DataType = "int",
                Nullable = true,
                Default = "0",
                ShouldApplyExpression = "SELECT 1",
                OldName = "CustId"
            };

            var json = JsonConvert.SerializeObject(column);
            var deserialized = JsonConvert.DeserializeObject<Column>(json);

            Assert.That(deserialized.Name, Is.EqualTo("CustomerId"));
            Assert.That(deserialized.DataType, Is.EqualTo("int"));
            Assert.That(deserialized.Nullable, Is.True);
            Assert.That(deserialized.Default, Is.EqualTo("0"));
            Assert.That(deserialized.ShouldApplyExpression, Is.EqualTo("SELECT 1"));
            Assert.That(deserialized.OldName, Is.EqualTo("CustId"));
        }

        [Test]
        public void JsonSerialization_OmitsNullProperties()
        {
            var column = new Column { Name = "Id", DataType = "int" };

            var json = JsonConvert.SerializeObject(column, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });

            Assert.That(json, Does.Not.Contain("Default"));
            Assert.That(json, Does.Not.Contain("ShouldApplyExpression"));
            Assert.That(json, Does.Not.Contain("OldName"));
        }

        [Test]
        public void JsonRoundTrip_PreservesVariantName()
        {
            var column = new Column { Name = "RegionCode", DataType = "char(2)", ShouldApplyExpression = "1=1", VariantName = "EU region" };
            var json = JsonConvert.SerializeObject(column);
            var deserialized = JsonConvert.DeserializeObject<Column>(json);
            Assert.That(deserialized.VariantName, Is.EqualTo("EU region"));
        }

        // The deploy hands the procedures the SERIALIZED model (Template.TableSchema, and SerializeAll on MySQL), so
        // whether a package declared Nullable at all only survives if serialization preserves it. It did not: an
        // omitted Nullable went out as an explicit false, and the SQL Server rule that an omitted Nullable on a
        // computed column lets the engine decide could never fire outside a hand-written test -- which is how a
        // nullable PERSISTED column was dropped and could not be put back on a table whose rows made it NULL.
        private static string SerializedAsTheDeployDoes(string columnJson) =>
            Newtonsoft.Json.Linq.JArray.FromObject(new[] { JsonConvert.DeserializeObject<Column>(columnJson) }).ToString();

        [Test]
        public void AnOmittedNullable_IsNotSerialized_SoTheProceduresCanTellItWasOmitted()
        {
            const string json = "{ \"Name\": \"Doubled\", \"DataType\": \"INT\" }";
            var column = JsonConvert.DeserializeObject<Column>(json);

            Assert.That(column.Nullable, Is.False, "an omitted Nullable still reads as false in the model");
            Assert.That(column.NullableDeclared, Is.False);
            Assert.That(SerializedAsTheDeployDoes(json), Does.Not.Contain("Nullable"));
            Assert.That(Schema.Utility.JsonHelper.SerializeAll(new[] { column }), Does.Not.Contain("Nullable"));
        }

        [TestCase("false")]
        [TestCase("true")]
        public void ADeclaredNullable_IsSerialized_WhicheverValueItHas(string declared)
        {
            var json = "{ \"Name\": \"Doubled\", \"DataType\": \"INT\", \"Nullable\": " + declared + " }";
            var column = JsonConvert.DeserializeObject<Column>(json);

            Assert.That(column.NullableDeclared, Is.True);
            Assert.That(SerializedAsTheDeployDoes(json), Does.Contain("\"Nullable\": " + declared));
            Assert.That(Schema.Utility.JsonHelper.SerializeAll(new[] { column }), Does.Contain("\"Nullable\": " + declared));
        }

        [Test]
        public void SettingNullableInCode_CountsAsDeclaringIt()
        {
            var column = new Column { Name = "Id", DataType = "INT", Nullable = false };

            Assert.That(column.NullableDeclared, Is.True);
            Assert.That(JsonConvert.SerializeObject(column), Does.Contain("\"Nullable\":false"));
        }

    }
}
