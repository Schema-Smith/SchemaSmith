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

        // A plain column's declared false is the default every engine's parse assumes for a missing key, so it is left
        // out exactly as package files always left it out; true is not a default and is written.
        [TestCase("false", false)]
        [TestCase("true", true)]
        public void APlainColumnsDeclaredNullable_IsSerializedOnlyWhenItIsNotTheDefault(string declared, bool written)
        {
            var json = "{ \"Name\": \"Qty\", \"DataType\": \"INT\", \"Nullable\": " + declared + " }";
            var column = JsonConvert.DeserializeObject<Column>(json);

            Assert.That(column.NullableDeclared, Is.True);
            NUnit.Framework.Constraints.IResolveConstraint Expectation() =>
                written ? Does.Contain("\"Nullable\": " + declared) : Does.Not.Contain("Nullable");
            Assert.That(SerializedAsTheDeployDoes(json), Expectation());
            Assert.That(Schema.Utility.JsonHelper.SerializeAll(new[] { column }), Expectation());
        }

        // On a computed column a declared false is NOT the default -- omitted means the engine decides -- so it must
        // reach the procedures, where it asks for NOT NULL.
        [Test]
        public void AComputedColumnsDeclaredFalse_ReachesTheProcedures()
        {
            var column = Schema.Domain.PlatformDeserializer.DeserializeColumn(
                "{ \"Name\": \"[Doubled]\", \"DataType\": \"INT\", \"ComputedExpression\": \"[Qty]*(2)\", \"Persisted\": true, \"Nullable\": false }",
                Platform.SqlServer);

            Assert.That(Newtonsoft.Json.Linq.JArray.FromObject(new[] { column }).ToString(), Does.Contain("\"Nullable\": false"));
        }

        // Package files are written with DefaultValueHandling.Ignore, which drops an explicit false. Harmless while an
        // omitted Nullable meant NOT NULL; not once an omitted Nullable on a computed or generated column means "the
        // engine decides" -- a PERSISTED NOT NULL computed column then extracted with no Nullable at all and was
        // rebuilt nullable from its own package. A derived column writes its false; a plain column's file is unchanged.
        [TestCase(Platform.SqlServer, "{ \"Name\": \"[Doubled]\", \"DataType\": \"INT\", \"ComputedExpression\": \"[Qty]*(2)\", \"Persisted\": true, \"Nullable\": false }")]
        [TestCase(Platform.PostgreSQL, "{ \"Name\": \"doubled\", \"DataType\": \"integer\", \"GenerationExpression\": \"qty * 2\", \"Generated\": \"ALWAYS\", \"Nullable\": false }")]
        [TestCase(Platform.MySQL, "{ \"Name\": \"Doubled\", \"DataType\": \"INT\", \"GenerationExpression\": \"Qty * 2\", \"Generated\": \"STORED\", \"Nullable\": false }")]
        public void ADerivedColumnsDeclaredFalse_IsWrittenToThePackageFile(Platform platform, string columnJson)
        {
            var column = Schema.Domain.PlatformDeserializer.DeserializeColumn(columnJson, platform);

            Assert.That(Schema.Utility.JsonHelper.Serialize(column), Does.Contain("\"Nullable\": false"));
        }

        [Test]
        public void APlainColumnsFalse_IsStillLeftOutOfThePackageFile()
        {
            var column = Schema.Domain.PlatformDeserializer.DeserializeColumn(
                "{ \"Name\": \"[Qty]\", \"DataType\": \"INT\", \"Nullable\": false }", Platform.SqlServer);

            Assert.That(Schema.Utility.JsonHelper.Serialize(column), Does.Not.Contain("Nullable"));
        }

        [Test]
        public void SettingNullableInCode_CountsAsDeclaringIt()
        {
            var column = new Column { Name = "Id", DataType = "INT", Nullable = false };

            Assert.That(column.NullableDeclared, Is.True);
            Assert.That(new Column { Name = "Id", DataType = "INT", Nullable = true }.NullableDeclared, Is.True);
        }

    }
}
