// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// Slice 2.4: the final `--Validate` check. Presents a fake package tree via IFile/IDirectory
/// isolator mocks (schema text + JSON text in memory — no real disk, no DB), same convention as
/// TokenCheckTests. NJsonSchema validation and SchemaGenerator run FOR REAL against the mocked
/// content, so these tests exercise the actual staleness-compare and structural-validation logic,
/// not a stand-in.
/// </summary>
[TestFixture]
public class JsonSchemaCheckTests
{
    private const string PackagePath = @"C:\pkg";
    private static readonly string SchemaDir = Path.Combine(PackagePath, ".json-schemas");
    private static readonly string TablesSchemaPath = Path.Combine(SchemaDir, "tables.sqlserver.schema");
    private static readonly string ProductsSchemaPath = Path.Join(SchemaDir, "products.sqlserver.schema");

    private IFile _mockFile;
    private IDirectory _mockDirectory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register(_mockFile);
        FactoryContainer.Register(_mockDirectory);

        _mockDirectory.Exists(SchemaDir).Returns(true);
        _mockDirectory.GetFiles(PackagePath, "*.json", SearchOption.AllDirectories).Returns(System.Array.Empty<string>());
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static Product Product() => new() { Name = "Acme", Platform = Platform.SqlServer };

    private static ValidationContext Context() => new(Product(), new List<Template>(), PackagePath);

    // Mirrors JsonSchemaCheck's own private PlatformElementResolver for Platform.SqlServer — used
    // here only to build the "current model" fresh schema fixtures, exactly as the check itself
    // would generate it, so committed-vs-fresh comparisons in these tests are meaningful.
    private static System.Func<System.Type, System.Type> SqlServerElementResolver() => t =>
        t == typeof(Column) ? typeof(SqlServerColumn)
        : t == typeof(Schema.Domain.Index) ? typeof(SqlServerIndex)
        : t == typeof(ForeignKey) ? typeof(SqlServerForeignKey)
        : t; // CheckConstraint has no SqlServer subclass (PlatformDeserializer.GetCheckConstraintType)

    // Platform-scoped, exactly as RepositoryHelper writes it: a committed schema is per-platform, so
    // a stand-in generated without one carries properties the real file does not and reads as stale.
    private static JObject FreshTablesSchema() =>
        SchemaGenerator.GenerateSchema(typeof(SqlServerTable), SqlServerElementResolver(), Platform.SqlServer);

    // Product has no Column/Index/ForeignKey/CheckConstraint members needing platform-subclass
    // resolution, so the identity resolver mirrors what JsonSchemaCheck's own
    // PlatformElementResolver would do for it.
    private static JObject FreshProductsSchema() =>
        SchemaGenerator.GenerateSchema(typeof(Product), t => t, Platform.SqlServer);

    private static JObject ColumnItemsProperties(JObject tableSchema) =>
        (JObject)tableSchema["properties"]!["Columns"]!["items"]!["properties"]!;

    private void CommitTablesSchema(JObject schema) =>
        FileContent(TablesSchemaPath, schema.ToString(Formatting.None));

    private void CommitProductsSchema(JObject schema) =>
        FileContent(ProductsSchemaPath, schema.ToString(Formatting.None));

    private void JsonFiles(params string[] files) =>
        _mockDirectory.GetFiles(PackagePath, "*.json", SearchOption.AllDirectories).Returns(files);

    private void FileContent(string path, string content)
    {
        _mockFile.Exists(path).Returns(true);
        _mockFile.ReadAllText(path).Returns(content);
    }

    private static string TableFilePath(string name = "Customer.json") =>
        Path.Combine(PackagePath, "Templates", "Main", "Tables", name);

    [Test]
    public void MisnamedProperty_IsError()
    {
        CommitTablesSchema(FreshTablesSchema());
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Nam"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Some.Matches<Finding>(f => f.Code == "SS-JSON-001" && f.Message.Contains("Nam")));
        Assert.That(findings, Has.All.Matches<Finding>(f => f.Severity == Severity.Error && f.Category == "JsonSchema" && f.Location == tableFile));
    }

    [Test]
    public void MissingRequiredProperty_IsError()
    {
        CommitTablesSchema(FreshTablesSchema());
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-JSON-001"));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Category, Is.EqualTo("JsonSchema"));
        Assert.That(findings[0].Location, Is.EqualTo(tableFile));
        Assert.That(findings[0].Message, Does.Contain("DataType"));
    }

    [Test]
    public void CustomPropertyGovernanceViolation_IsError()
    {
        // Simulates a hand-authored Extensions fragment: the committed schema (not the bare
        // generated one) constrains a custom Column-level "DataClassification" property to an
        // enum. MergeExtensionsDefinition preserves exactly this kind of fragment across
        // regeneration, so validating the JSON file against the COMMITTED schema is what makes
        // custom-property governance work automatically.
        var schema = FreshTablesSchema();
        ColumnItemsProperties(schema)["Extensions"] = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["DataClassification"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Public", "PII", "Confidential") }
            },
            ["additionalProperties"] = false
        };
        CommitTablesSchema(schema);

        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"", ""Extensions"": { ""DataClassification"": ""Nope"" } } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-JSON-001"));
        Assert.That(findings[0].Message, Does.Contain("DataClassification"));
    }

    [Test]
    public void ValidFileAgainstFreshSchema_NoFindings()
    {
        CommitTablesSchema(FreshTablesSchema());
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    // A stale committed schema is an error, and structural validation NO LONGER SKIPS THE TYPE.
    // It is validated against the CURRENT model merged with the governance recovered from the stale
    // file -- so the user's own Extensions rules keep being enforced, while structure is judged by
    // the model that will actually deploy.
    //
    // This test previously asserted the opposite (SkipsStructural). The skip existed so a stale
    // SCHEMA would not produce misleading findings; that reasoning does not apply to the merged
    // schema, whose structure is the current model. The consequence, accepted deliberately: a
    // package authored against the old model now surfaces findings it did not before -- which is
    // the honest report, because those files really are invalid against what ships.
    [Test]
    public void StaleCommittedSchema_IsError_AndStillValidatesAgainstTheCurrentModel()
    {
        // Committed schema is deliberately out of date: the current model generates "DataType" as
        // a Column property (Required), but the committed file omits it entirely.
        var schema = FreshTablesSchema();
        var columnProps = ColumnItemsProperties(schema);
        columnProps.Remove("DataType");
        var required = (JArray)schema["properties"]!["Columns"]!["items"]!["required"]!;
        required.Remove(required.Single(t => t.ToString() == "DataType"));
        CommitTablesSchema(schema);

        // Missing "Name", so it is genuinely invalid against the CURRENT model.
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        var stale = findings.SingleOrDefault(f => f.Code == "SS-STALE-001");
        Assert.Multiple(() =>
        {
            Assert.That(stale, Is.Not.Null, "a stale committed schema must still be reported");
            Assert.That(stale.Severity, Is.EqualTo(Severity.Error));
            Assert.That(stale.Category, Is.EqualTo("Staleness"));
            Assert.That(stale.Location, Is.EqualTo(TablesSchemaPath));
            Assert.That(findings.Any(f => f.Code == "SS-JSON-001"), Is.True,
                "structural validation must still run for a stale type -- the file is missing Name, "
                + "which is invalid against the model that will deploy. Skipping it hid that");
        });
    }

    // A malformed committed schema falls back to a freshly generated one, which carries NO
    // hand-authored Extensions fragment -- so any custom-property governance the user wrote is not
    // enforced on that run. The failure is directional: validation still PASSES, so the rule reads
    // as satisfied when it was never evaluated. SS-STALE-002 must therefore name that consequence,
    // not just report that the file is malformed -- "your governance did not run" is what a reader
    // acts on, "your schema is malformed" is not.
    //
    // Unlike the STALE path, nothing can be recovered here: a fragment cannot be parsed out of JSON
    // that does not parse. Saying so is the whole remedy.
    [Test]
    public void MalformedCommittedSchema_SaysAuthoredGovernanceDidNotRun()
    {
        // FileContent sets both Exists and ReadAllText, so this is a committed file that is present
        // and unparseable -- the SS-STALE-002 path, distinct from a missing file.
        FileContent(TablesSchemaPath, "{ this is not valid json");

        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        var malformed = findings.SingleOrDefault(f => f.Code == "SS-STALE-002");
        Assert.That(malformed, Is.Not.Null, "an unparseable committed schema must be reported");
        Assert.Multiple(() =>
        {
            Assert.That(malformed.Message, Does.Contain("governance"),
                "the finding must name the governance consequence -- a reader who is only told the file "
                + "is malformed has no reason to suspect their own Extensions rules stopped being enforced");
            Assert.That(malformed.Message, Does.Contain("NOT applied"));
        });
    }

    // THE POINT OF RECOVERING GOVERNANCE FROM A STALE FILE. The committed schema is out of date
    // structurally AND carries a hand-authored Extensions rule. The rule must still be enforced:
    // skipping the type meant a package could violate its own governance and be told nothing, while
    // the only finding pointed at the schema file rather than at the violation.
    [Test]
    public void StaleCommittedSchema_StillEnforcesItsAuthoredGovernance()
    {
        var schema = FreshTablesSchema();
        ColumnItemsProperties(schema)["Extensions"] = new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["DataClassification"] = new JObject { ["type"] = "string", ["enum"] = new JArray("Public", "PII") }
            },
            ["additionalProperties"] = false
        };
        // ...and make it stale as well, by dropping a property the current model generates.
        var columnProps = ColumnItemsProperties(schema);
        columnProps.Remove("DataType");
        var required = (JArray)schema["properties"]!["Columns"]!["items"]!["required"]!;
        required.Remove(required.Single(t => t.ToString() == "DataType"));
        CommitTablesSchema(schema);

        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"", ""Extensions"": { ""DataClassification"": ""Nope"" } } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(findings.Any(f => f.Code == "SS-STALE-001"), Is.True,
                "the file is still stale and must still say so");
            Assert.That(findings.Any(f => f.Code == "SS-JSON-001" && f.Message.Contains("DataClassification")), Is.True,
                "the authored governance rule must be enforced even though the file is stale -- that is "
                + "the whole reason a stale file is still read as a governance source");
        });
    }

    // ---- Regression: #326 — a table named "Product" (MySQL-style file layout: no schema
    // prefix, so the table file is just Tables/Product.json) must classify as a TABLE, never
    // as the product manifest, purely on directory context. ----

    [Test]
    public void TableNamedProductUnderTablesFolder_ClassifiesAsTable_NotProductManifest()
    {
        CommitTablesSchema(FreshTablesSchema());
        CommitProductsSchema(FreshProductsSchema());
        var tableFile = TableFilePath("Product.json");
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Product"", ""Columns"": [ { ""Name"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        // Valid table JSON, validated against the tables schema — if misclassified as the product
        // manifest instead, this would spuriously fail with "missing required property
        // 'ValidationScript'" (a Product-only required field).
        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void RealRootProductJson_StillClassifiesAsProductManifest()
    {
        CommitProductsSchema(FreshProductsSchema());
        var productFile = Path.Join(PackagePath, "Product.json");
        JsonFiles(productFile);
        // TemplateOrder is a Product-only array. Validated against the products schema this is a TYPE
        // error; misclassified as a table it would instead be an unknown-property error, so the
        // expected-Array wording is what proves the classification. (This used to lean on
        // ValidationScript being required, which it no longer is.)
        FileContent(productFile, @"{ ""Name"": ""Acme"", ""TemplateOrder"": ""not-an-array"" }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Code, Is.EqualTo("SS-JSON-001"));
        Assert.That(findings[0].Location, Is.EqualTo(productFile));
        Assert.That(findings[0].Message, Does.Contain("TemplateOrder"));
        Assert.That(findings[0].Message, Does.Contain("Array"));
    }

    // ---- Extra coverage beyond the brief's mandatory list ----

    [Test]
    public void NoJsonSchemasDirectory_ViolationStillReported()
    {
        // No .json-schemas directory at all — nothing committed for ANY type. Decorative only:
        // the SUT no longer queries directory existence directly (it drives entirely off
        // per-file IFile.Exists), but this keeps the mock state honestly matching the scenario
        // this test names. The domain model is the authority regardless of what's committed, so
        // a table file is still validated against a schema generated fresh in memory — this is
        // the headline case of the "validation must not depend on a committed artifact" decision.
        _mockDirectory.Exists(SchemaDir).Returns(false);
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Nam"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Some.Matches<Finding>(f => f.Code == "SS-JSON-001" && f.Message.Contains("Nam") && f.Location == tableFile));
        Assert.That(findings.Any(f => f.Code is "SS-STALE-001" or "SS-STALE-002"), Is.False,
            "Nothing was ever committed, so there is nothing to compare for staleness.");
    }

    [Test]
    public void NoCommittedTablesSchema_TableFileStillValidatedAgainstGeneratedSchema()
    {
        // .json-schemas/ exists and carries a current committed schema for another type
        // (products), but tables.sqlserver.schema specifically was never committed (e.g. a
        // package that ran --WriteSchemasOnly before a new type existed, or just never had one
        // added). Distinct from NoJsonSchemasDirectory_ViolationStillReported's missing-
        // everything case: this is the missing-one-file case. The table file must still be
        // validated, against a schema generated fresh in memory for the tables type.
        CommitProductsSchema(FreshProductsSchema());
        var tableFile = TableFilePath();
        JsonFiles(tableFile);
        FileContent(tableFile, @"{ ""Name"": ""Customer"", ""Columns"": [ { ""Nam"": ""Id"", ""DataType"": ""int"" } ] }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Some.Matches<Finding>(f => f.Code == "SS-JSON-001" && f.Message.Contains("Nam") && f.Location == tableFile));
        Assert.That(findings.Any(f => f.Code is "SS-STALE-001" or "SS-STALE-002"), Is.False,
            "tables.sqlserver.schema was never committed, so there is nothing to compare for staleness — and " +
            "products.sqlserver.schema, which IS committed, is current and must not report stale either.");
    }

    [Test]
    public void JsonSchemasDirectoryFile_ExcludedFromStructuralScan()
    {
        CommitTablesSchema(FreshTablesSchema());
        var schemaFileAsJson = Path.Combine(SchemaDir, "tables.sqlserver.schema.json");
        JsonFiles(schemaFileAsJson);
        FileContent(schemaFileAsJson, @"{ ""whatever"": true }");

        var findings = new JsonSchemaCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }
}
