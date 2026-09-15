// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

public class SchemaGeneratorTests
{
    [Test]
    public void ShouldGenerateSchemaWithStringProperty()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SimpleStringClass));
        Assert.That(schema["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(schema["additionalProperties"]?.Value<bool>(), Is.False);
        var props = schema["properties"] as JObject;
        Assert.That(props, Is.Not.Null);
        Assert.That(props["Name"]?["type"]?.ToString(), Is.EqualTo("string"));
    }

    [Test]
    public void ShouldMapBooleanType()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(BoolClass));
        Assert.That(schema["properties"]?["IsActive"]?["type"]?.ToString(), Is.EqualTo("boolean"));
    }

    [Test]
    public void ShouldMapIntegerTypes()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(IntegerClass));
        Assert.That(schema["properties"]?["ByteVal"]?["type"]?.ToString(), Is.EqualTo("integer"));
        Assert.That(schema["properties"]?["IntVal"]?["type"]?.ToString(), Is.EqualTo("integer"));
    }

    [Test]
    public void ShouldMapListToArray()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ListClass));
        var listProp = schema["properties"]?["Items"];
        Assert.That(listProp?["type"]?.ToString(), Is.EqualTo("array"));
        Assert.That(listProp?["items"]?["type"]?.ToString(), Is.EqualTo("string"));
    }

    [Test]
    public void ShouldMapDictionaryToObject()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(DictClass));
        Assert.That(schema["properties"]?["Tags"]?["type"]?.ToString(), Is.EqualTo("object"));
    }

    [Test]
    public void ShouldMapNestedClassRecursively()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ParentClass));
        var nested = schema["properties"]?["Child"];
        Assert.That(nested?["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(nested?["properties"]?["Value"]?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(nested?["additionalProperties"]?.Value<bool>(), Is.False);
    }

    [Test]
    public void ShouldMapListOfObjectsToArrayWithObjectItems()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ListOfObjectsClass));
        var items = schema["properties"]?["Children"]?["items"];
        Assert.That(items?["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(items?["properties"]?["Value"]?["type"]?.ToString(), Is.EqualTo("string"));
    }

    [Test]
    public void ShouldExcludeJsonIgnoreProperties()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(IgnoreClass));
        Assert.That(schema["properties"]?["Visible"], Is.Not.Null);
        Assert.That(schema["properties"]?["Hidden"], Is.Null);
    }

    [Test]
    public void ShouldRespectJsonPropertyOrder()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(OrderedClass));
        var propNames = (schema["properties"] as JObject)?.Properties().Select(p => p.Name).ToList();
        Assert.That(propNames, Is.EqualTo(new List<string> { "First", "Second", "Third" }));
    }

    [Test]
    public void ShouldDetectRequiredFromSchemaPropertyAttribute()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(RequiredClass));
        var required = schema["required"]?.ToObject<List<string>>();
        Assert.That(required, Is.Not.Null);
        Assert.That(required, Contains.Item("Name"));
        Assert.That(required, Does.Not.Contain("Optional"));
    }

    // JsonHelper.Serialize drops a value equal to its [DefaultValue], so a property carrying both that and
    // Required is absent from exactly the files the model writes for the common case -- a schema requiring
    // it rejects the engine's own output. Absence already means the default when loaded, so it cannot be
    // required.
    [Test]
    public void ShouldNotRequireAPropertyThatHasADefaultValue()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(RequiredWithDefaultClass));
        var required = schema["required"]?.ToObject<List<string>>();
        Assert.That(required, Is.EqualTo(new List<string> { "Name" }));
    }

    [Test]
    public void ShouldNotConditionallyRequireAPropertyThatHasADefaultValue()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(RequiredUnlessWithDefaultClass));
        Assert.That(schema["allOf"], Is.Null);
    }

    [Test]
    public void ADefaultScheduleEvent_ValidatesAgainstItsOwnGeneratedSchema()
    {
        var json = JsonHelper.Serialize(new Schema.Domain.MySQL.MySqlEvent { Name = "nightly", Definition = "DO 1", Interval = "1 DAY" });
        Assert.That(JObject.Parse(json)["ScheduleType"], Is.Null,
            "precondition: the serializer omits the default EVERY, which is the shape under test");

        var schema = NJsonSchema.JsonSchema.FromJsonAsync(
            SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlEvent), Platform.MySQL).ToString()).GetAwaiter().GetResult();
        var errors = schema.Validate(json).Select(e => e.ToString()).ToList();

        Assert.That(errors, Is.Empty, "the engine's own serialized event must satisfy the schema it generates");
    }

    [Test]
    public void ShouldApplyPatternConstraint()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(PatternClass));
        Assert.That(schema["properties"]?["Action"]?["pattern"]?.ToString(), Is.EqualTo("NO ACTION|CASCADE"));
    }

    [Test]
    public void ShouldApplyMinMaxConstraints()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(MinMaxClass));
        var prop = schema["properties"]?["FillFactor"];
        Assert.That(prop?["minimum"]?.Value<double>(), Is.EqualTo(0));
        Assert.That(prop?["maximum"]?.Value<double>(), Is.EqualTo(100));
    }

    [Test]
    public void ShouldNotIncludeRequiredArrayWhenNoRequiredProperties()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SimpleStringClass));
        Assert.That(schema["required"], Is.Null);
    }

    // --- Domain model integration tests ---

    [Test]
    public void ShouldGenerateProductSchema()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Product));
        var props = schema["properties"] as JObject;
        Assert.That(props?["Name"], Is.Not.Null);
        Assert.That(props?["ValidationScript"], Is.Not.Null);
        Assert.That(props?["ScriptTokens"]?["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(props?["TemplateOrder"]?["type"]?.ToString(), Is.EqualTo("array"));
        Assert.That(props?["Platform"], Is.Not.Null, "Platform should be in schema");
        Assert.That(props?["FilePath"], Is.Null, "FilePath is JsonIgnore");

        var required = schema["required"]?.ToObject<List<string>>();
        Assert.That(required, Contains.Item("Name"));
        // ValidationScript is recommended, not required (Paul, 2026-08-21): the deploy path skips it
        // when unset, so requiring it rejected packages that deploy perfectly well.
        Assert.That(required, Does.Not.Contain("ValidationScript"));
    }

    [Test]
    public void ShouldGenerateTemplateSchemaExcludingIgnoredProperties()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Template));
        var props = schema["properties"] as JObject;
        Assert.That(props?["Name"], Is.Not.Null);
        Assert.That(props?["DatabaseIdentificationScript"], Is.Not.Null);
        // ScriptFolders has [JsonProperty] in v2 so it appears in schema
        Assert.That(props?["ScriptFolders"], Is.Not.Null);
        // JsonIgnore properties should not appear
        Assert.That(props?["BeforeScripts"], Is.Null);
        Assert.That(props?["Tables"], Is.Null);
        Assert.That(props?["TableSchema"], Is.Null);
        Assert.That(props?["FilePath"], Is.Null);
        Assert.That(props?["LogPath"], Is.Null);

        var required = schema["required"]?.ToObject<List<string>>();
        Assert.That(required, Contains.Item("Name"));
        // DatabaseIdentificationScript is no longer schema-required — a template may identify
        // its target via SchemaIdentificationScript instead.
        Assert.That(required, Does.Not.Contain("DatabaseIdentificationScript"));
    }

    [Test]
    public void ShouldGenerateTableSchemaWithNestedTypes()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Table));
        var props = schema["properties"] as JObject;
        Assert.That(props?["Columns"]?["type"]?.ToString(), Is.EqualTo("array"));
        Assert.That(props?["Indexes"]?["type"]?.ToString(), Is.EqualTo("array"));
        Assert.That(props?["ForeignKeys"]?["type"]?.ToString(), Is.EqualTo("array"));
        // FullTextIndex is SQL Server-specific; base Table has no such property
        Assert.That(props?["FullTextIndex"], Is.Null);

        var columnItems = props?["Columns"]?["items"];
        Assert.That(columnItems?["properties"]?["Name"]?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(columnItems?["properties"]?["Nullable"]?["type"]?.ToString(), Is.EqualTo("boolean"));
        Assert.That(columnItems?["additionalProperties"]?.Value<bool>(), Is.False);
    }

    [Test]
    public void ShouldApplyConstraintsOnDomainProperties()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Table));
        // DataDelivery is SingleOrArray -> a oneOf wrapper; the object-shape branch (index 0)
        // carries the per-property constraints, mirroring FullTextIndex's oneOf shape.
        var dataDelivery = schema["properties"]?["DataDelivery"]?["oneOf"]?[0];
        var mergeType = dataDelivery?["properties"]?["MergeType"];
        Assert.That(mergeType?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(mergeType?["pattern"]?.ToString(), Is.EqualTo("Insert|Insert/Update|Insert/Update/Delete"));

        var fkItems = schema["properties"]?["ForeignKeys"]?["items"];
        Assert.That(fkItems?["properties"]?["DeleteAction"]?["pattern"]?.ToString(), Is.EqualTo("^(NO ACTION|RESTRICT|CASCADE|SET NULL|SET DEFAULT)?$"));
    }

    [Test]
    public void ShouldMapStringEnumConverterToStringWithPattern()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(StringEnumClass));
        var prop = schema["properties"]?["Version"];
        Assert.That(prop?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(prop?["pattern"]?.ToString(), Does.Contain("ValueA"));
        Assert.That(prop?["pattern"]?.ToString(), Does.Contain("ValueB"));
    }

    [Test]
    public void ShouldMapNullableEnumProperty()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(NullableEnumClass));
        var prop = schema["properties"]?["Version"];
        Assert.That(prop?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(prop?["pattern"]?.ToString(), Does.Contain("ValueA"));
    }

    [Test]
    public void ShouldGenerateProductSchemaWithMinimumVersion()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Product));
        var prop = schema["properties"]?["MinimumVersion"];
        Assert.That(prop, Is.Not.Null, "MinimumVersion should appear in schema");
        Assert.That(prop?["type"]?.ToString(), Is.EqualTo("string"));
        // MinimumVersion has no [SchemaProperty] pattern in v2 (it's a free-form string)
        Assert.That(prop?["pattern"], Is.Null);
    }

    [Test]
    public void ShouldIncludeUpdateFillFactorInTableSchema()
    {
        // UpdateFillFactor is SQL Server-specific; base Table does not have this property
        var schema = SchemaGenerator.GenerateSchema(typeof(Table));
        Assert.That(schema["properties"]?["UpdateFillFactor"], Is.Null,
            "Base Table does not have UpdateFillFactor; it is SQL Server-specific");
    }

    [Test]
    public void ShouldIncludeUpdateFillFactorInIndexSchema()
    {
        // UpdateFillFactor is SQL Server-specific; base Index does not have this property
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.Index));
        Assert.That(schema["properties"]?["UpdateFillFactor"], Is.Null,
            "Base Index does not have UpdateFillFactor; it is SQL Server-specific");
    }

    [Test]
    public void ShouldMapJTokenExtensionsWithoutRecursion()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Table));
        var ext = schema["properties"]?["Extensions"];
        Assert.That(ext, Is.Not.Null, "Extensions should appear in schema");
        Assert.That(ext["type"], Is.Null, "JToken Extensions should have no type constraint (accepts anything)");
    }

    [Test]
    public void ShouldIncludeIndexedViewPropertiesInSchema()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerIndexedView));
        Assert.Multiple(() =>
        {
            Assert.That(schema["properties"]?["Name"]?["type"]?.ToString(), Is.EqualTo("string"));
            Assert.That(schema["properties"]?["Schema"]?["type"]?.ToString(), Is.EqualTo("string"));
            Assert.That(schema["properties"]?["Definition"]?["type"]?.ToString(), Is.EqualTo("string"));
            Assert.That(schema["properties"]?["Indexes"]?["type"]?.ToString(), Is.EqualTo("array"));
        });
    }

    // --- Fix A: FK action pattern accepts empty string ---

    [Test]
    public void ForeignKeyActionPattern_AcceptsEmptyString()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ForeignKey));
        var pattern = schema["properties"]?["UpdateAction"]?["pattern"]?.ToString();
        Assert.That(pattern, Is.Not.Null, "UpdateAction should have a pattern");
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("", pattern), Is.True,
            "Empty string should match (use-DB-default / unspecified)");
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("CASCADE", pattern), Is.True,
            "CASCADE should still match");
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("BOGUS", pattern), Is.False,
            "Garbage value should not match");
    }

    [Test]
    public void ForeignKeyDeleteActionPattern_AcceptsEmptyString()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ForeignKey));
        var pattern = schema["properties"]?["DeleteAction"]?["pattern"]?.ToString();
        Assert.That(pattern, Is.Not.Null, "DeleteAction should have a pattern");
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("", pattern), Is.True,
            "Empty string should match");
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch("NO ACTION", pattern), Is.True,
            "NO ACTION should still match");
    }

    // --- Fix B: SqlServerTemplate.ServerToQuench is not schema-required ---

    [Test]
    public void SqlServerTemplate_ServerToQuench_IsNotRequired()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerTemplate));
        var required = schema["required"]?.ToObject<List<string>>();
        Assert.That(required, Is.Not.Null, "SqlServerTemplate should have required fields");
        Assert.That(required, Does.Not.Contain("ServerToQuench"),
            "ServerToQuench has a C# default so it must not be schema-required");
        Assert.That(required, Contains.Item("Name"),
            "Name (inherited) must remain required");
        Assert.That(required, Does.Not.Contain("DatabaseIdentificationScript"),
            "DatabaseIdentificationScript is no longer schema-required (SchemaIdentificationScript is a valid alternative)");
    }

    // --- MergeExtensionsDefinition tests ---

    [Test]
    public void MergeExtensionsDefinition_PreservesCustomExtensionsSchema()
    {
        var existingSchema = JObject.Parse(@"{
            'properties': {
                'Name': { 'type': 'string' },
                'Extensions': {
                    'type': 'object',
                    'properties': {
                        'replication': { 'type': 'boolean' },
                        'dataDictionary': {
                            'type': 'object',
                            'properties': {
                                'description': { 'type': 'string' }
                            }
                        }
                    }
                }
            }
        }");

        var generated = SchemaGenerator.GenerateSchema(typeof(Table));
        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existingSchema);

        var extensions = merged["properties"]?["Extensions"];
        Assert.That(extensions, Is.Not.Null);
        Assert.That(extensions["properties"]?["replication"]?["type"]?.ToString(), Is.EqualTo("boolean"));
        Assert.That(extensions["properties"]?["dataDictionary"]?["properties"]?["description"]?["type"]?.ToString(), Is.EqualTo("string"));
    }

    [Test]
    public void MergeExtensionsDefinition_NoExistingExtensions_ReturnsGenerated()
    {
        var existingSchema = JObject.Parse(@"{
            'properties': {
                'Name': { 'type': 'string' }
            }
        }");

        var generated = SchemaGenerator.GenerateSchema(typeof(Table));
        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existingSchema);

        // Extensions should still exist in generated (as empty schema)
        Assert.That(merged["properties"]?["Extensions"], Is.Not.Null);
    }

    [Test]
    public void MergeExtensionsDefinition_NullExisting_ReturnsGenerated()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(Table));
        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, null);

        Assert.That(merged, Is.Not.Null);
        Assert.That(merged["properties"]?["Extensions"], Is.Not.Null);
    }

    // --- MergeExtensionsDefinition: deep-level preservation (every Extensions-bearing component) ---

    // A user edits a regenerated .schema file, tightening the open Extensions bag into a governance
    // fragment. On the next regeneration the fragment must survive at EVERY level it was authored,
    // not just the table root. These build the "existing on-disk" schema by cloning the generated
    // shape and injecting a distinctively-marked fragment, exactly as a round-trip would produce.

    // SQL Server checks use the base CheckConstraint (no SqlServerCheckConstraint subclass); the base
    // still carries Extensions via DynamicBase, so it exercises the check-constraint level fine.
    private static readonly Func<Type, Type> FullSsResolver = SubclassResolver(
        column: typeof(SqlServerColumn),
        index: typeof(SqlServerIndex),
        foreignKey: typeof(SqlServerForeignKey));

    private static JObject Frag(string marker) => new()
    {
        ["type"] = "object",
        ["required"] = new JArray(marker),
        ["properties"] = new JObject { [marker] = new JObject { ["type"] = "string" } }
    };

    private static void InjectAtItems(JObject schema, string collection, JObject frag) =>
        ((JObject)schema["properties"]![collection]!["items"]!["properties"]!)["Extensions"] = frag;

    private static void AssertFrag(JToken ext, string marker)
    {
        Assert.That(ext, Is.Not.Null, $"Extensions fragment marked '{marker}' should be preserved");
        Assert.That(ext!["required"]?[0]?.ToString(), Is.EqualTo(marker));
        Assert.That(ext["properties"]?[marker], Is.Not.Null);
    }

    [Test]
    public void MergeExtensions_PreservesColumnLevelFragment()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        InjectAtItems(existing, "Columns", Frag("Classification"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["Columns"]?["items"]?["properties"]?["Extensions"], "Classification");
    }

    [Test]
    public void MergeExtensions_PreservesIndexLevelFragment()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        InjectAtItems(existing, "Indexes", Frag("IndexGov"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["Indexes"]?["items"]?["properties"]?["Extensions"], "IndexGov");
    }

    [Test]
    public void MergeExtensions_PreservesForeignKeyLevelFragment()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        InjectAtItems(existing, "ForeignKeys", Frag("FkGov"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["ForeignKeys"]?["items"]?["properties"]?["Extensions"], "FkGov");
    }

    [Test]
    public void MergeExtensions_PreservesCheckConstraintLevelFragment()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        InjectAtItems(existing, "CheckConstraints", Frag("CheckGov"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["CheckConstraints"]?["items"]?["properties"]?["Extensions"], "CheckGov");
    }

    [Test]
    public void MergeExtensions_PreservesStatisticLevelFragment()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        InjectAtItems(existing, "Statistics", Frag("StatGov"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["Statistics"]?["items"]?["properties"]?["Extensions"], "StatGov");
    }

    [Test]
    public void MergeExtensions_PreservesFullTextIndexFragment_BothOneOfBranches()
    {
        // FullTextIndex is SingleOrArray -> a oneOf wrapper; the fragment lives on both the
        // single-object branch and the array-items branch after a round-trip.
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        var ftOneOf = (JArray)existing["properties"]!["FullTextIndex"]!["oneOf"]!;
        ((JObject)ftOneOf[0]!["properties"]!)["Extensions"] = Frag("FtObj");
        ((JObject)ftOneOf[1]!["items"]!["properties"]!)["Extensions"] = Frag("FtArr");

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        var mergedOneOf = (JArray)merged["properties"]!["FullTextIndex"]!["oneOf"]!;
        AssertFrag(mergedOneOf[0]["properties"]?["Extensions"], "FtObj");
        AssertFrag(mergedOneOf[1]["items"]?["properties"]?["Extensions"], "FtArr");
    }

    [Test]
    public void MergeExtensions_PreservesMultipleLevelsSimultaneously_NoCrossContamination()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone();
        ((JObject)existing["properties"]!)["Extensions"] = Frag("TableGov");
        InjectAtItems(existing, "Columns", Frag("ColGov"));
        InjectAtItems(existing, "Indexes", Frag("IdxGov"));

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        AssertFrag(merged["properties"]?["Extensions"], "TableGov");
        AssertFrag(merged["properties"]?["Columns"]?["items"]?["properties"]?["Extensions"], "ColGov");
        AssertFrag(merged["properties"]?["Indexes"]?["items"]?["properties"]?["Extensions"], "IdxGov");
        // A level with no authored fragment must stay the empty generated bag — nothing leaks across levels.
        var fkExt = merged["properties"]?["ForeignKeys"]?["items"]?["properties"]?["Extensions"];
        Assert.That(fkExt, Is.Not.Null);
        Assert.That(fkExt!["required"], Is.Null, "FK Extensions had no fragment; must stay empty");
    }

    [Test]
    public void MergeExtensions_ExistingWithoutDeepFragments_LeavesGeneratedEmpty()
    {
        var generated = SchemaGenerator.GenerateSchema(typeof(SqlServerTable), FullSsResolver);
        var existing = (JObject)generated.DeepClone(); // no fragments injected anywhere

        var merged = SchemaGenerator.MergeExtensionsDefinition(generated, existing);

        var colExt = merged["properties"]?["Columns"]?["items"]?["properties"]?["Extensions"] as JObject;
        Assert.That(colExt, Is.Not.Null);
        Assert.That(colExt!.Count, Is.EqualTo(0), "empty Extensions bag stays empty");
    }

    // --- Test helper classes ---
    private class SimpleStringClass { public string Name { get; set; } }
    private class BoolClass { public bool IsActive { get; set; } }
    private class IntegerClass { public byte ByteVal { get; set; } public int IntVal { get; set; } }
    private class ListClass { public List<string> Items { get; set; } }
    private class DictClass { public Dictionary<string, string> Tags { get; set; } }
    private class ChildClass { public string Value { get; set; } }
    private class ParentClass { public ChildClass Child { get; set; } }
    private class ListOfObjectsClass { public List<ChildClass> Children { get; set; } }
    private class IgnoreClass
    {
        public string Visible { get; set; }
        [JsonIgnore] public string Hidden { get; set; }
    }
    private class OrderedClass
    {
        [JsonProperty(Order = 3)] public string Third { get; set; }
        [JsonProperty(Order = 1)] public string First { get; set; }
        [JsonProperty(Order = 2)] public string Second { get; set; }
    }
    private class RequiredClass
    {
        [SchemaProperty(Required = true)] public string Name { get; set; }
        public string Optional { get; set; }
    }
    private class RequiredWithDefaultClass
    {
        [SchemaProperty(Required = true)] public string Name { get; set; }
        [SchemaProperty(Required = true)] [System.ComponentModel.DefaultValue("EVERY")] public string Kind { get; set; } = "EVERY";
    }
    private class RequiredUnlessWithDefaultClass
    {
        public bool Flag { get; set; }
        [SchemaProperty(Required = true, RequiredUnless = "Flag")] [System.ComponentModel.DefaultValue("X")] public string Value { get; set; } = "X";
    }
    private class PatternClass
    {
        [SchemaProperty(Pattern = "NO ACTION|CASCADE")] public string Action { get; set; }
    }
    private class MinMaxClass
    {
        [SchemaProperty(Minimum = 0, Maximum = 100)] public byte FillFactor { get; set; }
    }

    private class ConstrainedLabelClass
    {
        [SchemaProperty(MaxLength = 128, Description = "A short label.")]
        public string Label { get; set; }
    }

    [Test]
    public void ShouldEmitMaxLengthConstraint()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ConstrainedLabelClass));
        Assert.That(schema["properties"]?["Label"]?["maxLength"]?.Value<int>(), Is.EqualTo(128));
    }

    [Test]
    public void ShouldEmitDescription()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(ConstrainedLabelClass));
        Assert.That(schema["properties"]?["Label"]?["description"]?.ToString(), Is.EqualTo("A short label."));
    }

    [Test]
    public void ShouldOmitMaxLengthAndDescriptionWhenUnset()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SimpleStringClass));
        Assert.That(schema["properties"]?["Name"]?["maxLength"], Is.Null);
        Assert.That(schema["properties"]?["Name"]?["description"], Is.Null);
    }

    [JsonConverter(typeof(StringEnumConverter))]
    private enum TestStringEnum { ValueA, ValueB, ValueC }

    private class StringEnumClass { public TestStringEnum Version { get; set; } }
    private class NullableEnumClass { public TestStringEnum? Version { get; set; } }

    [Test]
    public void GenerateSchema_DataDeliveryProperty_IsGeneratedFromAttributes()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerTable));
        var dataDelivery = schema["properties"]?["DataDelivery"];

        Assert.That(dataDelivery, Is.Not.Null);
        // DataDelivery is SingleOrArray -> a oneOf wrapper (bare object OR array), matching the
        // FullTextIndex shape (ShouldGenerateSingleOrArraySchemaForSqlServerFullTextIndex below).
        var oneOf = dataDelivery!["oneOf"] as JArray;
        Assert.That(oneOf, Is.Not.Null, "DataDelivery schema should accept single object OR array");
        Assert.That(oneOf![0]?["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(oneOf![1]?["type"]?.ToString(), Is.EqualTo("array"));
        Assert.That(oneOf![0]?["properties"]?["MergeType"]?["pattern"]?.ToString(),
            Is.EqualTo("Insert|Insert/Update|Insert/Update/Delete"));
    }

    [Test]
    public void ShouldGenerateSingleOrArraySchemaForSqlServerFullTextIndex()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerTable));
        var ft = (schema["properties"] as JObject)?["FullTextIndex"];
        var oneOf = ft?["oneOf"] as JArray;
        Assert.That(oneOf, Is.Not.Null, "FullTextIndex schema should accept single object OR array");
        Assert.That(oneOf, Has.Count.EqualTo(2));
        Assert.That(oneOf[0]?["type"]?.ToString(), Is.EqualTo("object"));
        Assert.That(oneOf[1]?["type"]?.ToString(), Is.EqualTo("array"));
    }

    // --- Element-type resolver overload (platform subclass props in collection element schemas) ---

    // The resolver mirrors the production one in RepositoryHelper: only list ELEMENT types of the
    // base collection types are upgraded to their platform subclass. Root/single-object types pass
    // through unchanged, which is the precise gap the overload closes.
    private static Func<Type, Type> SubclassResolver(
        Type column = null, Type index = null, Type foreignKey = null, Type check = null)
        => t =>
            column != null && t == typeof(Schema.Domain.Column) ? column
            : index != null && t == typeof(Schema.Domain.Index) ? index
            : foreignKey != null && t == typeof(Schema.Domain.ForeignKey) ? foreignKey
            : check != null && t == typeof(Schema.Domain.CheckConstraint) ? check
            : t;

    private static JObject ColumnItemProps(JObject schema) =>
        schema["properties"]?["Columns"]?["items"]?["properties"] as JObject;

    [Test]
    public void ResolverOverload_SqlServerColumns_IncludeSubclassProps()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.SqlServer.SqlServerTable),
            SubclassResolver(column: typeof(Schema.Domain.SqlServer.SqlServerColumn)));
        var colProps = ColumnItemProps(schema);
        Assert.Multiple(() =>
        {
            Assert.That(colProps?["CheckExpression"], Is.Not.Null);
            Assert.That(colProps?["ComputedExpression"], Is.Not.Null);
            Assert.That(colProps?["EncryptionType"], Is.Not.Null);
            Assert.That(schema["properties"]?["Columns"]?["items"]?["additionalProperties"]?.Value<bool>(), Is.False);
        });
    }

    [Test]
    public void ResolverOverload_PostgreSqlColumns_IncludeSubclassProps()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.PostgreSQL.PostgreSqlTable),
            SubclassResolver(column: typeof(Schema.Domain.PostgreSQL.PostgreSqlColumn)));
        var colProps = ColumnItemProps(schema);
        Assert.Multiple(() =>
        {
            Assert.That(colProps?["GenerationExpression"], Is.Not.Null);
            Assert.That(colProps?["CheckExpression"], Is.Not.Null);
        });
    }

    [Test]
    public void ResolverOverload_MySqlColumns_IncludeSubclassProps()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlTable),
            SubclassResolver(column: typeof(Schema.Domain.MySQL.MySqlColumn)));
        var colProps = ColumnItemProps(schema);
        Assert.Multiple(() =>
        {
            Assert.That(colProps?["GenerationExpression"], Is.Not.Null);
            Assert.That(colProps?["CheckExpression"], Is.Not.Null);
        });
    }

    [Test]
    public void ResolverOverload_SubclassIndexForeignKeyAndCheckPropsAppear()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.PostgreSQL.PostgreSqlTable),
            SubclassResolver(
                index: typeof(Schema.Domain.PostgreSQL.PostgreSqlIndex),
                foreignKey: typeof(Schema.Domain.PostgreSQL.PostgreSqlForeignKey),
                check: typeof(Schema.Domain.PostgreSQL.PostgreSqlCheckConstraint)));
        var indexProps = schema["properties"]?["Indexes"]?["items"]?["properties"] as JObject;
        var fkProps = schema["properties"]?["ForeignKeys"]?["items"]?["properties"] as JObject;
        var checkProps = schema["properties"]?["CheckConstraints"]?["items"]?["properties"] as JObject;
        Assert.Multiple(() =>
        {
            Assert.That(indexProps?["AccessMethod"], Is.Not.Null, "PostgreSqlIndex adds AccessMethod");
            Assert.That(fkProps?["MatchType"], Is.Not.Null, "PostgreSqlForeignKey adds MatchType");
            Assert.That(checkProps?["Deferrable"], Is.Not.Null, "PostgreSqlCheckConstraint adds Deferrable");
        });
    }

    [Test]
    public void ResolverOverload_SqlServerIndexForeignKeySubclassPropsAppear()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.SqlServer.SqlServerTable),
            SubclassResolver(
                index: typeof(Schema.Domain.SqlServer.SqlServerIndex),
                foreignKey: typeof(Schema.Domain.SqlServer.SqlServerForeignKey)));
        var indexProps = schema["properties"]?["Indexes"]?["items"]?["properties"] as JObject;
        var fkProps = schema["properties"]?["ForeignKeys"]?["items"]?["properties"] as JObject;
        Assert.Multiple(() =>
        {
            Assert.That(indexProps?["Clustered"], Is.Not.Null, "SqlServerIndex adds Clustered");
            Assert.That(indexProps?["ColumnStore"], Is.Not.Null, "SqlServerIndex adds ColumnStore");
            Assert.That(fkProps?["RelatedTableSchema"], Is.Not.Null, "SqlServerForeignKey adds RelatedTableSchema");
        });
    }

    [Test]
    public void ResolverOverload_MySqlIndexForeignKeySubclassPropsAppear()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlTable),
            SubclassResolver(
                index: typeof(Schema.Domain.MySQL.MySqlIndex),
                foreignKey: typeof(Schema.Domain.MySQL.MySqlForeignKey)));
        var indexProps = schema["properties"]?["Indexes"]?["items"]?["properties"] as JObject;
        var fkProps = schema["properties"]?["ForeignKeys"]?["items"]?["properties"] as JObject;
        Assert.Multiple(() =>
        {
            Assert.That(indexProps?["IndexType"], Is.Not.Null, "MySqlIndex adds IndexType");
            Assert.That(indexProps?["Visible"], Is.Not.Null, "MySqlIndex adds Visible");
            Assert.That(fkProps?["RelatedTableSchema"], Is.Not.Null, "MySqlForeignKey adds RelatedTableSchema");
        });
    }

    [Test]
    public void GenerateSchema_EmitsStringPatternForTemplateQuenchSlot()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.TemplateFolder));
        var slot = schema["properties"]?["QuenchSlot"];
        Assert.That(slot?["type"]?.ToString(), Is.EqualTo("string"));
        Assert.That(slot?["pattern"]?.ToString(), Does.Contain("Objects"));
    }

    [Test]
    public void GenerateSchema_MapsUnsignedLongToInteger()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlTable));
        var autoInc = schema["properties"]?["AutoIncrementValue"];
        Assert.That(autoInc?["type"]?.ToString(), Is.EqualTo("integer"));
    }

    [Test]
    public void GenerateSchema_DatabaseIdentificationScript_IsNotRequired()
    {
        // A template may identify its target via SchemaIdentificationScript instead, so
        // DatabaseIdentificationScript must not be schema-required (only Name is).
        var schema = SchemaGenerator.GenerateSchema(typeof(Template));
        var required = schema["required"]?.ToObject<List<string>>() ?? new List<string>();
        Assert.That(required, Does.Not.Contain("DatabaseIdentificationScript"));
        Assert.That(required, Contains.Item("Name"));
    }

    [Test]
    public void IdentityOverload_TableColumns_OnlyBaseProps_BackCompat()
    {
        // The no-resolver overload must keep emitting ONLY base Column props (identity path),
        // so the new overload can't silently change the historical behavior.
        var withResolver = SchemaGenerator.GenerateSchema(typeof(Table), t => t);
        var noResolver = SchemaGenerator.GenerateSchema(typeof(Table));
        Assert.Multiple(() =>
        {
            Assert.That(ColumnItemProps(withResolver)?["CheckExpression"], Is.Null);
            Assert.That(ColumnItemProps(noResolver)?["CheckExpression"], Is.Null);
            Assert.That(ColumnItemProps(noResolver)?["Name"], Is.Not.Null);
        });
    }

    // A columnstore index has no key columns, so SchemaTongs extracts one with IndexColumns empty and
    // --Validate used to reject the package it had just produced. IndexColumns stays required for every
    // ordinary index -- the editor squiggle is the point -- so the exception is expressed as if/else
    // rather than by dropping the requirement.
    [Test]
    public void IndexColumns_IsRequiredOnlyWhenTheIndexIsNotColumnStore()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(SqlServerIndex));

        var required = schema["required"]?.ToObject<List<string>>() ?? new List<string>();
        var conditional = schema["allOf"] as JArray;

        Assert.Multiple(() =>
        {
            Assert.That(required, Does.Not.Contain("IndexColumns"),
                "an unconditional requirement is what rejected extracted columnstore indexes");
            Assert.That(conditional, Is.Not.Null.And.Count.EqualTo(1));
            var rule = conditional![0];
            Assert.That(rule["if"]!["properties"]!["ColumnStore"]!["const"]!.Value<bool>(), Is.True);
            // Without this the flag's ABSENCE satisfies `properties` vacuously and the rule never fires.
            Assert.That(rule["if"]!["required"]!.ToObject<List<string>>(), Contains.Item("ColumnStore"));
            Assert.That(rule["else"]!["required"]!.ToObject<List<string>>(), Contains.Item("IndexColumns"));
        });
    }

    // RequiredUnless names a sibling that only some engines declare. MySQL has no columnstore concept and
    // therefore no exception to make, so the requirement must stay plain there rather than vanish.
    [Test]
    public void IndexColumns_StaysPlainlyRequired_OnAnEngineWithoutColumnStore()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Schema.Domain.MySQL.MySqlIndex));

        Assert.Multiple(() =>
        {
            Assert.That(schema["required"]?.ToObject<List<string>>() ?? new List<string>(),
                Contains.Item("IndexColumns"));
            Assert.That(schema["allOf"], Is.Null);
        });
    }

    // Recommended, not required: SchemaQuench skips it when unset, so requiring it made --Validate reject
    // packages that deploy perfectly well (5 shipped demo packages omit it).
    [Test]
    public void ValidationScript_IsNotRequiredOnProduct()
    {
        var schema = SchemaGenerator.GenerateSchema(typeof(Product));

        Assert.Multiple(() =>
        {
            Assert.That(schema["required"]?.ToObject<List<string>>() ?? new List<string>(),
                Does.Not.Contain("ValidationScript"));
            Assert.That(schema["properties"]!["ValidationScript"]!["description"]!.Value<string>(),
                Does.Contain("Recommended"));
        });
    }

    // Platform scoping. Template and Product are shared types, so a setting that only applies to one
    // engine was being emitted into every platform's schema file -- editors offered it everywhere and
    // nothing said it did nothing. Platforms on SchemaPropertyAttribute scopes the EMIT.

    [Test]
    public void APlatformScopedProperty_IsOmittedForOtherPlatforms()
    {
        var mysql = SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.MySQL);

        Assert.That((mysql["properties"] as JObject)?.Properties().Select(p => p.Name),
            Does.Not.Contain("SqlServerOnly"),
            "emitting it for MySQL is the bleed this exists to stop -- an editor offers a setting that "
            + "does nothing, and additionalProperties:false cannot flag what it still lists");
    }

    [Test]
    public void APlatformScopedProperty_IsEmittedForItsOwnPlatform()
    {
        var ss = SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.SqlServer);

        Assert.That((ss["properties"] as JObject)?.Properties().Select(p => p.Name),
            Does.Contain("SqlServerOnly"),
            "a filter that dropped it everywhere would pass the omission test while deleting the setting");
    }

    [Test]
    public void APropertyScopedToTwoPlatforms_IsEmittedForBoth()
    {
        // The case single inheritance cannot express without declaring the property twice, and the
        // reason this is an attribute rather than a subclass move.
        Assert.Multiple(() =>
        {
            foreach (var platform in new[] { Platform.SqlServer, Platform.PostgreSQL })
                Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, platform)
                        ["properties"] as JObject)?.Properties().Select(p => p.Name),
                    Does.Contain("TwoEngines"), $"must survive for {platform}");

            Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.MySQL)
                    ["properties"] as JObject)?.Properties().Select(p => p.Name),
                Does.Not.Contain("TwoEngines"), "and must not survive for an engine outside the set");
        });
    }

    [Test]
    public void MariaDb_IsScopedSeparatelyFromMySql()
    {
        // MariaDB folds to MySQL for the TYPE (there is no MariaDbTemplate), so scoping has to key on
        // the actual platform. Folding here would make a MariaDB-only setting unexpressible.
        Assert.Multiple(() =>
        {
            Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.MariaDb)
                    ["properties"] as JObject)?.Properties().Select(p => p.Name),
                Does.Contain("MariaDbOnly"));
            Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.MySQL)
                    ["properties"] as JObject)?.Properties().Select(p => p.Name),
                Does.Not.Contain("MariaDbOnly"));
        });
    }

    [Test]
    public void APlatformScopedProperty_DocumentsItsPlatformsInTheDescription()
    {
        // Paul's explicit requirement: the schema file itself says which engines a decorated setting
        // applies to. Filtering alone leaves a reader of one file unable to tell whether a property is
        // universal or merely happens to apply here.
        var ss = SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(ss["properties"]!["TwoEngines"]!["description"]!.Value<string>(),
                Does.Contain("SQL Server").And.Contains("PostgreSQL"),
                "both engines have to be named, not just the one whose file this is");
            Assert.That(ss["properties"]!["Documented"]!["description"]!.Value<string>(),
                Is.EqualTo("An existing description. SQL Server and PostgreSQL only."),
                "the note is APPENDED to an existing description -- asserting only that the original text survives passes over a bug that silently drops the note whenever a property has its own description, "
                + "which is exactly what a mutation of the append proved");
        });
    }

    [Test]
    public void AnUndecoratedProperty_IsEmittedForEveryPlatform()
    {
        // The negative half: scoping must be opt-in. A filter that treated "no Platforms" as "no
        // platforms" would empty every schema file in the product.
        foreach (var platform in new[] { Platform.SqlServer, Platform.PostgreSQL, Platform.MySQL, Platform.MariaDb })
            Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass), t => t, platform)
                    ["properties"] as JObject)?.Properties().Select(p => p.Name),
                Does.Contain("Everywhere"), $"must survive for {platform}");
    }

    [Test]
    public void WithNoPlatformGiven_EverythingIsEmitted()
    {
        // Back-compat: the platform-less overload is what most callers use, and it must not start
        // dropping properties.
        Assert.That((SchemaGenerator.GenerateSchema(typeof(PlatformScopedClass))["properties"] as JObject)?
                .Properties().Select(p => p.Name),
            Does.Contain("SqlServerOnly").And.Contains("MariaDbOnly"));
    }

    private class PlatformScopedClass
    {
        [SchemaProperty(Platforms = [Platform.SqlServer])]
        public bool? SqlServerOnly { get; set; }

        [SchemaProperty(Platforms = [Platform.SqlServer, Platform.PostgreSQL])]
        public bool? TwoEngines { get; set; }

        [SchemaProperty(Platforms = [Platform.MariaDb])]
        public bool? MariaDbOnly { get; set; }

        [SchemaProperty(Description = "An existing description.", Platforms = [Platform.SqlServer, Platform.PostgreSQL])]
        public bool? Documented { get; set; }

        public bool? Everywhere { get; set; }
    }

}
