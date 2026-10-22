// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility
{
    [TestFixture]
    public class ModelXmlSerializerTests
    {
        // B2: the legacy-tier extract emits object ExtendedProperties attribute-encoded
        // (<ExtendedProperties><p n="Name">Value</p>...>) because EP names are arbitrary sysname and
        // cannot be XML element names; FromIngestXml rebuilds the {Name: Value} dict the JSON proc produces.
        private static JToken Ext(string extensionsInnerXml)
        {
            var xml = "<Table xmlns:json=\"http://james.newtonking.com/projects/json\">" +
                      "<Name>[Widget]</Name>" + extensionsInnerXml + "</Table>";
            return JObject.Parse(ModelXmlSerializer.FromIngestXml(xml))["Extensions"]?["ExtendedProperties"];
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_SingleProp_RebuildsArbitraryKeyDict()
        {
            var ep = Ext("<Extensions><ExtendedProperties><p n=\"MS_Description\">A widget</p></ExtendedProperties></Extensions>");
            Assert.That((string)ep["MS_Description"], Is.EqualTo("A widget"));
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_MultipleProps_AllKeysPresent()
        {
            var ep = Ext("<Extensions><ExtendedProperties><p n=\"OwningTeam\">Billing</p><p n=\"DataClassification\">PII</p></ExtendedProperties></Extensions>");
            Assert.Multiple(() =>
            {
                Assert.That((string)ep["OwningTeam"], Is.EqualTo("Billing"));
                Assert.That((string)ep["DataClassification"], Is.EqualTo("PII"));
            });
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_NameWithSpaceAndSpecialCharValue_Preserved()
        {
            // The whole point of the attribute-encoded form: a name a space breaks the element-name shortcut.
            var ep = Ext("<Extensions><ExtendedProperties><p n=\"My Prop\">a &amp; b &lt; c</p></ExtendedProperties></Extensions>");
            Assert.That((string)ep["My Prop"], Is.EqualTo("a & b < c"));
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_EmptyValue_BecomesEmptyString()
        {
            var ep = Ext("<Extensions><ExtendedProperties><p n=\"Flag\"></p></ExtendedProperties></Extensions>");
            Assert.That((string)ep["Flag"], Is.EqualTo(""));
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_NestedOnColumn_AlsoRebuilt()
        {
            const string xml =
                "<Table xmlns:json=\"http://james.newtonking.com/projects/json\">" +
                "<Name>[Widget]</Name>" +
                "<Columns json:Array=\"true\"><Name>[Amount]</Name><DataType>INT</DataType><Nullable>false</Nullable>" +
                "<Extensions><ExtendedProperties><p n=\"Classification\">Financial</p></ExtendedProperties></Extensions></Columns>" +
                "</Table>";
            var col = JObject.Parse(ModelXmlSerializer.FromIngestXml(xml))["Columns"]![0];
            Assert.That((string)col["Extensions"]!["ExtendedProperties"]!["Classification"], Is.EqualTo("Financial"));
        }

        [Test]
        public void FromIngestXml_ExtendedProperties_MaterializesIntoTypedModel()
        {
            const string xml =
                "<Table xmlns:json=\"http://james.newtonking.com/projects/json\">" +
                "<Schema>[dbo]</Schema><Name>[Widget]</Name>" +
                "<Columns json:Array=\"true\"><Name>[Id]</Name><DataType>INT</DataType><Nullable>false</Nullable></Columns>" +
                "<Extensions><ExtendedProperties><p n=\"OwningTeam\">Billing</p></ExtendedProperties></Extensions>" +
                "</Table>";
            var table = PlatformDeserializer.DeserializeTable(ModelXmlSerializer.FromIngestXml(xml), Platform.SqlServer);
            Assert.That(table.GetExtensionProperty("ExtendedProperties"), Is.Not.Null);
            Assert.That((string)((JObject)table.Extensions)["ExtendedProperties"]!["OwningTeam"], Is.EqualTo("Billing"));
        }

        // De-risk spike for the compare-side (GenerateTableXml) design: prove the two SerializeXNode sharp
        // edges are handled — (1) a SINGLE-element container still becomes a 1-element JSON array (via the
        // json:Array hint the proc emits), and (2) string-typed scalars ('true'/'80') coerce into the typed
        // domain model. Uses the hardest case: one column + one index (both singletons).
        [Test]
        public void FromIngestXml_SingleElementArraysAndStringScalars_MaterializeTypedModel()
        {
            const string xml =
                "<Table xmlns:json=\"http://james.newtonking.com/projects/json\">" +
                "<Schema>[dbo]</Schema><Name>[Widget]</Name>" +
                "<Columns json:Array=\"true\"><Name>[Id]</Name><DataType>INT</DataType><Nullable>false</Nullable></Columns>" +
                "<Indexes json:Array=\"true\"><Name>[PK_Widget]</Name><Unique>true</Unique><Clustered>true</Clustered><IndexColumns>[Id]</IndexColumns></Indexes>" +
                "</Table>";

            var json = ModelXmlSerializer.FromIngestXml(xml);
            var table = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

            Assert.Multiple(() =>
            {
                Assert.That(table.Name, Is.EqualTo("[Widget]"));
                // Single <Columns> element survived as a 1-element array, not collapsed to an object.
                Assert.That(table.Columns, Has.Count.EqualTo(1));
                Assert.That(table.Columns[0].Name, Is.EqualTo("[Id]"));
                Assert.That(table.Columns[0].DataType, Is.EqualTo("INT"));
                Assert.That(table.Columns[0].Nullable, Is.False, "string 'false' should coerce to bool");
                Assert.That(table.Indexes, Has.Count.EqualTo(1));
                Assert.That(table.Indexes[0].Unique, Is.True, "string 'true' should coerce to bool");
                Assert.That(table.Indexes[0].IndexColumns, Is.EqualTo("[Id]"));
            });
        }

        [Test]
        public void ToIngestXml_EncodesExtendedProperties_AsNamedPElements()
        {
            // The inverse of FromIngestXml: a property's name need not be an XML element name, so the bag goes
            // out attribute-encoded, on every component that carries one.
            var xml = ModelXmlSerializer.ToIngestXml(
                "[{\"Name\":\"[T]\",\"Extensions\":{\"ExtendedProperties\":{\"My Note\":\"a & b\",\"Gone\":null}},"
                + "\"Columns\":[{\"Name\":\"[C]\",\"Extensions\":{\"ExtendedProperties\":{\"Classification\":\"Key\"}}}]}]",
                "Tables", "Table");

            Assert.Multiple(() =>
            {
                Assert.That(xml, Does.Contain("<ExtendedProperties><p n=\"My Note\">a &amp; b</p><p n=\"Gone\""));
                Assert.That(xml, Does.Contain("<p n=\"Classification\">Key</p>"));
                Assert.That(xml, Does.Not.Contain("<My"), "a spaced name must never become an element name");
            });
        }

        [Test]
        public void ToIngestXml_ThenFromIngestXml_RoundTripsASpacedName()
        {
            var json = "{\"Name\":\"[T]\",\"Extensions\":{\"ExtendedProperties\":{\"My Note\":\"a & b < c\"}}}";

            var back = JObject.Parse(ModelXmlSerializer.FromIngestXml(ModelXmlSerializer.ToIngestXmlObject(json, "Table")));

            Assert.That((string)back["Extensions"]?["ExtendedProperties"]?["My Note"], Is.EqualTo("a & b < c"));
        }

        [Test]
        public void ToIngestXml_WrapsArrayInRoot_WithRepeatedItemElement()
        {
            const string json = "[{\"Schema\":\"dbo\",\"Name\":\"A\"},{\"Schema\":\"dbo\",\"Name\":\"B\"}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            Assert.That(xml, Is.EqualTo(
                "<Tables><Table><Schema>dbo</Schema><Name>A</Name></Table>" +
                "<Table><Schema>dbo</Schema><Name>B</Name></Table></Tables>"));
        }

        [Test]
        public void ToIngestXml_EntityEncodes_SpecialCharacters()
        {
            const string json = "[{\"Name\":\"Fo&o\",\"Expr\":\"X<Y\"}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            Assert.That(xml, Does.Contain("<Name>Fo&amp;o</Name>"));
            Assert.That(xml, Does.Contain("<Expr>X&lt;Y</Expr>"));
        }

        [Test]
        public void ToIngestXml_RendersJsonNull_AsEmptyElement()
        {
            const string json = "[{\"Name\":\"A\",\"OldName\":null}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            // An empty element carries no text node, so .value('(OldName/text())[1]', ...) shreds to NULL.
            Assert.That(xml, Does.Contain("<OldName />"));
        }

        [Test]
        public void ToIngestXml_NestedArray_BecomesRepeatedElementNamedByProperty()
        {
            const string json = "[{\"Name\":\"A\",\"Columns\":[{\"Name\":\"Id\"},{\"Name\":\"X\"}]}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            // Each column is a repeated <Columns> element with its fields inline (Json.NET has no singular name).
            Assert.That(xml, Does.Contain("<Columns><Name>Id</Name></Columns><Columns><Name>X</Name></Columns>"));
        }

        [Test]
        public void ToIngestXml_EmptyNestedArray_IsOmitted()
        {
            const string json = "[{\"Name\":\"A\",\"Indexes\":[]}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            Assert.That(xml, Does.Not.Contain("Indexes"));
        }

        [Test]
        public void ToIngestXml_RendersBooleans_AsLowercaseText()
        {
            // The shred reads these as text and CASEs on 'true'/'false' (a direct BIT cast of 'true' errors).
            const string json = "[{\"Name\":\"A\",\"Nullable\":true,\"Sparse\":false}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            Assert.That(xml, Does.Contain("<Nullable>true</Nullable>"));
            Assert.That(xml, Does.Contain("<Sparse>false</Sparse>"));
        }

        [Test]
        public void ToIngestXml_PreservesUnicode()
        {
            const string json = "[{\"Name\":\"Ünïcödé\"}]";

            var xml = ModelXmlSerializer.ToIngestXml(json, "Tables", "Table");

            Assert.That(xml, Does.Contain("<Name>Ünïcödé</Name>"));
        }

        [Test]
        public void ToIngestXmlObject_WrapsSingleObjectInRoot()
        {
            const string json = "{\"Schema\":\"dbo\",\"Name\":\"T\",\"Columns\":[{\"Name\":\"Id\"},{\"Name\":\"X\"}]}";

            var xml = ModelXmlSerializer.ToIngestXmlObject(json, "Table");

            Assert.That(xml, Is.EqualTo(
                "<Table><Schema>dbo</Schema><Name>T</Name>" +
                "<Columns><Name>Id</Name></Columns><Columns><Name>X</Name></Columns></Table>"));
        }
    }
}
