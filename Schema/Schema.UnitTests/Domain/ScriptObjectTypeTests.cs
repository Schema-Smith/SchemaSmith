// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Newtonsoft.Json;
using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class ScriptObjectTypeTests
    {
        private class TestObject
        {
            public ScriptObjectType ObjectType { get; set; }
        }

        [Test]
        public void None_IsDefaultValue()
        {
            Assert.That(default(ScriptObjectType), Is.EqualTo(ScriptObjectType.None));
        }

        [TestCase(ScriptObjectType.None, "None")]
        [TestCase(ScriptObjectType.Views, "Views")]
        [TestCase(ScriptObjectType.Functions, "Functions")]
        [TestCase(ScriptObjectType.Procedures, "Procedures")]
        [TestCase(ScriptObjectType.Triggers, "Triggers")]
        [TestCase(ScriptObjectType.Schemas, "Schemas")]
        [TestCase(ScriptObjectType.DataTypes, "DataTypes")]
        [TestCase(ScriptObjectType.FullTextCatalogs, "FullTextCatalogs")]
        [TestCase(ScriptObjectType.FullTextStopLists, "FullTextStopLists")]
        [TestCase(ScriptObjectType.XMLSchemaCollections, "XMLSchemaCollections")]
        [TestCase(ScriptObjectType.DDLTriggers, "DDLTriggers")]
        [TestCase(ScriptObjectType.IndexedViews, "IndexedViews")]
        [TestCase(ScriptObjectType.Synonyms, "Synonyms")]
        [TestCase(ScriptObjectType.DomainTypes, "DomainTypes")]
        [TestCase(ScriptObjectType.EnumTypes, "EnumTypes")]
        [TestCase(ScriptObjectType.CompositeTypes, "CompositeTypes")]
        [TestCase(ScriptObjectType.TriggerFunctions, "TriggerFunctions")]
        [TestCase(ScriptObjectType.WindowFunctions, "WindowFunctions")]
        [TestCase(ScriptObjectType.Aggregates, "Aggregates")]
        [TestCase(ScriptObjectType.Sequences, "Sequences")]
        [TestCase(ScriptObjectType.Rules, "Rules")]
        [TestCase(ScriptObjectType.MaterializedViews, "MaterializedViews")]
        [TestCase(ScriptObjectType.Collations, "Collations")]
        [TestCase(ScriptObjectType.Publications, "Publications")]
        [TestCase(ScriptObjectType.Events, "Events")]
        [TestCase(ScriptObjectType.RangeTypes, "RangeTypes")]
        [TestCase(ScriptObjectType.TextSearchConfigurations, "TextSearchConfigurations")]
        [TestCase(ScriptObjectType.Operators, "Operators")]
        [TestCase(ScriptObjectType.Casts, "Casts")]
        [TestCase(ScriptObjectType.EventTriggers, "EventTriggers")]
        [TestCase(ScriptObjectType.Packages, "Packages")]
        public void AllValues_RoundTripThroughJsonSerialization(ScriptObjectType value, string expectedString)
        {
            var json = JsonConvert.SerializeObject(value);
            Assert.That(json, Is.EqualTo($"\"{expectedString}\""));

            var deserialized = JsonConvert.DeserializeObject<ScriptObjectType>(json);
            Assert.That(deserialized, Is.EqualTo(value));
        }

        [Test]
        public void Serialize_WithDefaultValueIgnore_OmitsNoneProperty()
        {
            var obj = new TestObject { ObjectType = ScriptObjectType.None };
            var json = JsonHelper.Serialize(obj);
            Assert.That(json, Does.Not.Contain("ObjectType"));
        }

        [Test]
        public void Serialize_WithDefaultValueIgnore_IncludesNonNoneProperty()
        {
            var obj = new TestObject { ObjectType = ScriptObjectType.Views };
            var json = JsonHelper.Serialize(obj);
            Assert.That(json, Does.Contain("ObjectType"));
            Assert.That(json, Does.Contain("Views"));
        }
    }
}
