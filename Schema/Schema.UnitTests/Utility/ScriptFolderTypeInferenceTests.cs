// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using Schema.Domain;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class ScriptFolderTypeInferenceTests
{
    [Test]
    [TestCase("Views", ScriptObjectType.Views)]
    [TestCase("Functions", ScriptObjectType.Functions)]
    [TestCase("Procedures", ScriptObjectType.Procedures)]
    [TestCase("Triggers", ScriptObjectType.Triggers)]
    [TestCase("Schemas", ScriptObjectType.Schemas)]
    [TestCase("DataTypes", ScriptObjectType.DataTypes)]
    [TestCase("FullTextCatalogs", ScriptObjectType.FullTextCatalogs)]
    [TestCase("FullTextStopLists", ScriptObjectType.FullTextStopLists)]
    [TestCase("XMLSchemaCollections", ScriptObjectType.XMLSchemaCollections)]
    [TestCase("DDLTriggers", ScriptObjectType.DDLTriggers)]
    [TestCase("Synonyms", ScriptObjectType.Synonyms)]
    [TestCase("Domain Types", ScriptObjectType.DomainTypes)]
    [TestCase("Enum Types", ScriptObjectType.EnumTypes)]
    [TestCase("Composite Types", ScriptObjectType.CompositeTypes)]
    [TestCase("Trigger Functions", ScriptObjectType.TriggerFunctions)]
    [TestCase("Window Functions", ScriptObjectType.WindowFunctions)]
    [TestCase("Aggregates", ScriptObjectType.Aggregates)]
    [TestCase("Sequences", ScriptObjectType.Sequences)]
    [TestCase("Rules", ScriptObjectType.Rules)]
    [TestCase("Collations", ScriptObjectType.Collations)]
    [TestCase("Publications", ScriptObjectType.Publications)]
    [TestCase("Range Types", ScriptObjectType.RangeTypes)]
    [TestCase("Text Search Configurations", ScriptObjectType.TextSearchConfigurations)]
    [TestCase("Operators", ScriptObjectType.Operators)]
    [TestCase("Casts", ScriptObjectType.Casts)]
    [TestCase("Event Triggers", ScriptObjectType.EventTriggers)]
    [TestCase("Events", ScriptObjectType.Events)]
    public void InferFromFolderName_KnownScriptFolder_ReturnsExpectedType(string folderName, ScriptObjectType expected)
    {
        var result = ScriptFolderTypeInference.InferFromFolderName(folderName);
        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    [TestCase("Before Scripts")]
    [TestCase("After Scripts")]
    [TestCase("Table Data")]
    [TestCase("Tables")]
    [TestCase("UnknownFolder")]
    public void InferFromFolderName_NonScriptFolder_ReturnsNone(string folderName)
    {
        var result = ScriptFolderTypeInference.InferFromFolderName(folderName);
        Assert.That(result, Is.EqualTo(ScriptObjectType.None));
    }

    [Test]
    public void InferFromFolderName_CaseInsensitive_ReturnsCorrectType()
    {
        var result = ScriptFolderTypeInference.InferFromFolderName("views");
        Assert.That(result, Is.EqualTo(ScriptObjectType.Views));
    }
}
