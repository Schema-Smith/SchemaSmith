// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

[TestFixture]
public class RepositoryHelperTests
{
    private IFile _file;
    private IDirectory _directory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _file = Substitute.For<IFile>();
        _directory = Substitute.For<IDirectory>();
        FactoryContainer.Register<IFile>(_file);
        FactoryContainer.Register<IDirectory>(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
    }

    [Test]
    public void GetSchemaFileNames_MySQL_ReturnsPlatformSpecificNames()
    {
        var names = RepositoryHelper.GetSchemaFileNames(Platform.MySQL);

        // Four since scheduled events became a declarative type: without an events schema an author gets
        // no editor completion for an Events/*.json file and --Validate cannot check one at all.
        Assert.That(names, Has.Length.EqualTo(4));
        Assert.That(names[0], Does.StartWith("products."));
        Assert.That(names[1], Does.StartWith("templates."));
        Assert.That(names[2], Does.StartWith("tables."));
        Assert.That(names[3], Does.StartWith("events."));

        var platformStr = Platform.MySQL.ToCanonicalString().ToLower();
        foreach (var name in names)
        {
            Assert.That(name, Does.Contain(platformStr));
            Assert.That(name, Does.EndWith(".schema"));
        }
    }

    [Test]
    public void GetSchemaFileNames_SqlServer_IncludesIndexedViewsSchema()
    {
        var names = RepositoryHelper.GetSchemaFileNames(Platform.SqlServer);

        Assert.That(names, Has.Length.EqualTo(4));
        Assert.That(names[0], Does.StartWith("products."));
        Assert.That(names[1], Does.StartWith("templates."));
        Assert.That(names[2], Does.StartWith("tables."));
        Assert.That(names[3], Is.EqualTo("indexedviews.sqlserver.schema"));

        var platformStr = Platform.SqlServer.ToCanonicalString().ToLower();
        foreach (var name in names)
        {
            Assert.That(name, Does.Contain(platformStr));
            Assert.That(name, Does.EndWith(".schema"));
        }
    }

    [Test]
    public void GetSchemaFileNames_PostgreSQL_IncludesMaterializedViewsSchema()
    {
        var names = RepositoryHelper.GetSchemaFileNames(Platform.PostgreSQL);

        // One per declarative PostgreSQL type. Each promoted type has to land here or an author gets no
        // editor completion for its .json file and --Validate cannot check one -- which is exactly the
        // gap this count exists to catch when a type is promoted and this is forgotten.
        Assert.That(names, Has.Length.EqualTo(7));
        Assert.That(names[0], Does.StartWith("products."));
        Assert.That(names[1], Does.StartWith("templates."));
        Assert.That(names[2], Does.StartWith("tables."));
        Assert.That(names[3], Is.EqualTo("materializedviews.postgresql.schema"));
        Assert.That(names[4], Is.EqualTo("domaintypes.postgresql.schema"));
        Assert.That(names[5], Is.EqualTo("enumtypes.postgresql.schema"));
        Assert.That(names[6], Is.EqualTo("sequences.postgresql.schema"));

        var platformStr = Platform.PostgreSQL.ToCanonicalString().ToLower();
        foreach (var name in names)
        {
            Assert.That(name, Does.Contain(platformStr));
            Assert.That(name, Does.EndWith(".schema"));
        }
    }

    [Test]
    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.MySQL)]
    public void GetSchemaFileNames_NonPostgreSQL_DoesNotIncludeMaterializedViews(Platform platform)
    {
        var names = RepositoryHelper.GetSchemaFileNames(platform);

        Assert.That(names, Has.None.Contain("materializedviews"));
    }

    [Test]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    public void GetSchemaFileNames_NonSqlServer_DoesNotIncludeIndexedViews(Platform platform)
    {
        var names = RepositoryHelper.GetSchemaFileNames(platform);

        Assert.That(names, Has.None.Contain("indexedviews"));
    }

    [Test]
    public void GetValidationScript_SqlServer_ContainsMasterSysDatabases()
    {
        var script = RepositoryHelper.GetValidationScript("MyTemplate", Platform.SqlServer);
        Assert.That(script, Does.Contain("master.sys.databases"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    [Test]
    public void GetValidationScript_PostgreSQL_ContainsPgDatabase()
    {
        var script = RepositoryHelper.GetValidationScript("MyTemplate", Platform.PostgreSQL);
        Assert.That(script, Does.Contain("pg_database"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    [Test]
    public void GetValidationScript_MySQL_ContainsInformationSchema()
    {
        var script = RepositoryHelper.GetValidationScript("MyTemplate", Platform.MySQL);
        Assert.That(script, Does.Contain("information_schema"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    [Test]
    public void GetDatabaseIdentificationScript_SqlServer_ContainsSelect()
    {
        var script = RepositoryHelper.GetDatabaseIdentificationScript("MyTemplate", Platform.SqlServer);
        Assert.That(script, Does.Contain("SELECT"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    [Test]
    public void GetDatabaseIdentificationScript_PostgreSQL_ContainsPgDatabase()
    {
        var script = RepositoryHelper.GetDatabaseIdentificationScript("MyTemplate", Platform.PostgreSQL);
        Assert.That(script, Does.Contain("pg_database"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    [Test]
    public void GetDatabaseIdentificationScript_MySQL_ContainsSchemaName()
    {
        var script = RepositoryHelper.GetDatabaseIdentificationScript("MyTemplate", Platform.MySQL);
        Assert.That(script, Does.Contain("SCHEMA_NAME"));
        Assert.That(script, Does.Contain("{{MyTemplateDb}}"));
    }

    // --- SchemaIdentificationScript stub tests (design §7.5) ---

    [Test]
    public void GetSchemaIdentificationStub_SqlServer_UsesSchemaNameAliasUnquoted()
    {
        // Design §7.5: stub should select the source schema literal as a single-row example
        // aliased as SchemaName (matches the column-name convention used in built-in
        // identification scripts elsewhere in the codebase). SQL Server identifiers are
        // case-insensitive by default; no quoting needed.
        var script = RepositoryHelper.GetSchemaIdentificationStub("tenant_seed", Platform.SqlServer);
        Assert.That(script, Does.Contain("'tenant_seed'"));
        Assert.That(script, Does.Contain("AS SchemaName"));
        Assert.That(script, Does.Not.Contain("schemaname"),
            "SQL Server stub must use the canonical SchemaName casing, not lowercase.");
    }

    [Test]
    public void GetSchemaIdentificationStub_PostgreSQL_UsesQuotedSchemaNameAlias()
    {
        // Design §7.5: PG stub must use the same SchemaName alias as the SQL Server stub
        // for spec-text consistency. PostgreSQL folds unquoted identifiers to lowercase, so
        // the alias is double-quoted to preserve the canonical CamelCase casing.
        var script = RepositoryHelper.GetSchemaIdentificationStub("tenant_seed", Platform.PostgreSQL);
        Assert.That(script, Does.Contain("'tenant_seed'"));
        Assert.That(script, Does.Contain("AS \"SchemaName\""));
        Assert.That(script, Does.Not.Contain("AS schemaname"),
            "PG stub must quote SchemaName to keep the casing — bare schemaname folds and drifts from the SQL Server stub.");
    }

    // --- On-the-fly schema generation tests ---

    [Test]
    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    public void AddMissingSchemaFiles_WritesGeneratedSchemaFromCSharpTypes(Platform platform)
    {
        var writtenFiles = new Dictionary<string, string>();
        _file.Exists(Arg.Any<string>()).Returns(false);
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        RepositoryHelper.AddMissingSchemaFiles("/fake/product", platform);

        Assert.That(writtenFiles, Is.Not.Empty);
        foreach (var (_, content) in writtenFiles)
        {
            var schema = JObject.Parse(content);
            Assert.That(schema["type"]?.ToString(), Is.EqualTo("object"), "Generated schema should be an object");
            Assert.That(schema["properties"], Is.Not.Null, "Generated schema should have properties");
            // No x-core-version since we use on-the-fly generation
            Assert.That(schema["x-core-version"], Is.Null, "On-the-fly schema should not have x-core-version");
        }
    }

    [Test]
    public void WriteSchemaFilesWithResults_NewFile_CreatesFromGeneratedSchema()
    {
        var writtenFiles = new Dictionary<string, string>();
        _file.Exists(Arg.Any<string>()).Returns(false);
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        var results = RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer);

        Assert.That(results, Has.Count.EqualTo(4)); // products, templates, tables, indexedviews
        Assert.That(results, Has.All.Matches<SchemaFileResult>(r => r.WasCreated));
        Assert.That(writtenFiles, Has.Count.EqualTo(4));

        // Verify each written file is valid JSON with schema structure
        foreach (var (_, content) in writtenFiles)
        {
            var schema = JObject.Parse(content);
            Assert.That(schema["type"]?.ToString(), Is.EqualTo("object"));
            Assert.That(schema["properties"], Is.Not.Null);
        }
    }

    [Test]
    public void WriteSchemaFilesWithResults_ExistingFile_MergesExtensionsDefinition()
    {
        var existingSchemaContent = @"{
            ""type"": ""object"",
            ""properties"": {
                ""Name"": { ""type"": ""string"" },
                ""Extensions"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""customProp"": { ""type"": ""boolean"" }
                    }
                }
            }
        }";

        var writtenFiles = new Dictionary<string, string>();

        // Only the tables file "exists" with custom Extensions
        _file.Exists(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(true);
        _file.Exists(Arg.Is<string>(s => !s.Contains("tables.sqlserver"))).Returns(false);
        _file.ReadAllText(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(existingSchemaContent);
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        var results = RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer);

        // tables.sqlserver.schema should NOT be WasCreated (it existed)
        var tablesResult = results.Find(r => r.FileName.Contains("tables"));
        Assert.That(tablesResult, Is.Not.Null);
        Assert.That(tablesResult.WasCreated, Is.False);

        // Merged file should preserve user's Extensions definition
        var mergedContent = writtenFiles["tables.sqlserver.schema"];
        var merged = JObject.Parse(mergedContent);
        var extensions = merged["properties"]?["Extensions"];
        Assert.That(extensions, Is.Not.Null, "Extensions should survive merge");
        Assert.That(extensions["properties"]?["customProp"]?["type"]?.ToString(), Is.EqualTo("boolean"),
            "User's custom Extensions property should be preserved");
    }

    // A malformed committed schema used to take the whole command down with a raw JsonReaderException at
    // exit 3, which made --Validate's SS-STALE-002 advice circular: it tells the user to regenerate via
    // --WriteSchemasOnly, and regenerating is precisely what the malformed file prevented. Deleting the
    // file was the only way out and nothing said so.
    [Test]
    public void WriteSchemaFilesWithResults_MalformedExistingFile_RegeneratesInsteadOfThrowing()
    {
        var writtenFiles = new Dictionary<string, string>();
        _file.Exists(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(true);
        _file.Exists(Arg.Is<string>(s => !s.Contains("tables.sqlserver"))).Returns(false);
        _file.ReadAllText(Arg.Is<string>(s => s.Contains("tables.sqlserver")))
            .Returns(@"{ ""title"": ""unterminated");
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        var warnings = new List<string>();
        var results = RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer, warnings.Add);

        var tablesResult = results.Find(r => r.FileName.Contains("tables"));
        Assert.Multiple(() =>
        {
            // The file is the whole point: a valid schema the user can see is recoverable, a stack trace is not.
            Assert.That(writtenFiles.ContainsKey("tables.sqlserver.schema"), Is.True,
                "the unreadable schema must be rewritten, not left in place for the command to die on again");
            Assert.That(() => JObject.Parse(writtenFiles["tables.sqlserver.schema"]), Throws.Nothing,
                "the regenerated schema must be valid JSON -- regenerating it badly is no better than crashing");
            Assert.That(tablesResult.AuthoredExtensionsLost, Is.True);
            Assert.That(tablesResult.ParseError, Is.Not.Null.And.Not.Empty,
                "the parser's reason is what lets the warning say WHY the file could not be read");
        });
    }

    // Dropping the authored governance QUIETLY would be worse than the crash it replaces: the package would
    // look healthy while enforcing less than the author wrote. The warning is the feature, not a nicety.
    [Test]
    public void WriteSchemaFilesWithResults_MalformedExistingFile_WarnsThatAuthoredGovernanceWasLost()
    {
        _file.Exists(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(true);
        _file.Exists(Arg.Is<string>(s => !s.Contains("tables.sqlserver"))).Returns(false);
        _file.ReadAllText(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns("{ not json at all");

        var warnings = new List<string>();
        RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer, warnings.Add);

        Assert.That(warnings, Has.Count.EqualTo(1), "exactly the one unreadable file should be reported");
        Assert.Multiple(() =>
        {
            Assert.That(warnings[0], Does.Contain("tables.sqlserver.schema"),
                "the user has to re-author the fragment and cannot do that without knowing which file it was");
            Assert.That(warnings[0], Does.Contain("Extensions"),
                "naming what was lost is the difference between a warning and a noise line");
        });
    }

    // The fragment-preservation intent is the reason the existing file is read at all, and it is worth
    // keeping -- the fix must not degrade into "always regenerate", which would silently discard authored
    // governance from every healthy package.
    [Test]
    public void WriteSchemaFilesWithResults_ValidExistingFile_StillPreservesExtensionsAndWarnsNothing()
    {
        var existingSchemaContent = @"{
            ""type"": ""object"",
            ""properties"": {
                ""Extensions"": {
                    ""type"": ""object"",
                    ""properties"": { ""owningTeam"": { ""type"": ""string"" } }
                }
            }
        }";
        var writtenFiles = new Dictionary<string, string>();
        _file.Exists(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(true);
        _file.Exists(Arg.Is<string>(s => !s.Contains("tables.sqlserver"))).Returns(false);
        _file.ReadAllText(Arg.Is<string>(s => s.Contains("tables.sqlserver"))).Returns(existingSchemaContent);
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        var warnings = new List<string>();
        var results = RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer, warnings.Add);

        var merged = JObject.Parse(writtenFiles["tables.sqlserver.schema"]);
        Assert.Multiple(() =>
        {
            Assert.That(merged["properties"]?["Extensions"]?["properties"]?["owningTeam"]?["type"]?.ToString(),
                Is.EqualTo("string"), "a readable file's authored Extensions fragment must still be carried forward");
            Assert.That(results.Find(r => r.FileName.Contains("tables")).AuthoredExtensionsLost, Is.False);
            Assert.That(warnings, Is.Empty, "a healthy package must not be told it lost anything");
        });
    }

    [Test]
    public void WriteSchemaFilesWithResults_ExistingFileWithoutExtensions_WritesGeneratedSchema()
    {
        var existingSchemaContent = @"{
            ""type"": ""object"",
            ""properties"": {
                ""Name"": { ""type"": ""string"" }
            }
        }";

        var writtenFiles = new Dictionary<string, string>();

        _file.Exists(Arg.Is<string>(s => s.Contains("products.sqlserver"))).Returns(true);
        _file.Exists(Arg.Is<string>(s => !s.Contains("products.sqlserver"))).Returns(false);
        _file.ReadAllText(Arg.Is<string>(s => s.Contains("products.sqlserver"))).Returns(existingSchemaContent);
        _file.When(f => f.WriteAllText(Arg.Any<string>(), Arg.Any<string>()))
            .Do(ci => writtenFiles[Path.GetFileName(ci.ArgAt<string>(0))] = ci.ArgAt<string>(1));

        RepositoryHelper.WriteSchemaFilesWithResults("/fake/product", Platform.SqlServer);

        // products file was updated with full generated schema
        var content = writtenFiles["products.sqlserver.schema"];
        var schema = JObject.Parse(content);
        // Should have the full generated schema with Name, ValidationScript, etc.
        Assert.That(schema["properties"]?["Name"], Is.Not.Null);
        Assert.That(schema["properties"]?["ValidationScript"], Is.Not.Null);
    }

    // --- TemplateOrder stable-partition tests (issue #258) ---

    private static string FakeProductPath => Path.Combine("fake", "product");
    private static string FakeProductFile => Path.Combine("fake", "product", "Product.json");

    private static string FakeTemplateFile(string templateName)
        => Path.Combine("fake", "product", "Templates", templateName, "Template.json");

    private void SetupProductFile(Product product)
    {
        _file.Exists(FakeProductFile).Returns(true);
        _file.ReadAllText(FakeProductFile).Returns(JsonHelper.Serialize(product));
    }

    private void SetupTemplateFile(string templateName, Template template)
    {
        _file.Exists(FakeTemplateFile(templateName)).Returns(true);
        _file.ReadAllText(FakeTemplateFile(templateName)).Returns(JsonHelper.Serialize(template));
    }

    [Test]
    public void UpdateOrInitRepository_AddingRegularTemplate_AfterSchemaTemplate_PlacesRegularFirst()
    {
        // Issue #258 — reviewer scenario: schema-template extracted first, regular
        // template extracted second. Expect TemplateOrder = [Regular, SchemaTemplate].
        SetupProductFile(new Product
        {
            Name = "P",
            Platform = Platform.SqlServer,
            TemplateOrder = new List<string> { "TenantSchema" }
        });
        SetupTemplateFile("TenantSchema", new Template
        {
            Name = "TenantSchema",
            SchemaIdentificationScript = "SELECT 'tenant_a' AS SchemaName"
        });

        var writtenProductJson = "";
        _file.When(f => f.WriteAllText(FakeProductFile, Arg.Any<string>()))
            .Do(ci => writtenProductJson = ci.ArgAt<string>(1));

        RepositoryHelper.UpdateOrInitRepository(
            FakeProductPath, productName: "P", templateName: "Shared", dbName: "db",
            platform: Platform.SqlServer, isSchemaTemplate: false);

        var product = JsonConvert.DeserializeObject<Product>(writtenProductJson);
        Assert.That(product.TemplateOrder, Is.EqualTo(new[] { "Shared", "TenantSchema" }));
    }

    [Test]
    public void UpdateOrInitRepository_AddingSchemaTemplate_AfterRegularTemplate_PreservesRegularFirst()
    {
        SetupProductFile(new Product
        {
            Name = "P",
            Platform = Platform.SqlServer,
            TemplateOrder = new List<string> { "Shared" }
        });
        SetupTemplateFile("Shared", new Template { Name = "Shared" });

        var writtenProductJson = "";
        _file.When(f => f.WriteAllText(FakeProductFile, Arg.Any<string>()))
            .Do(ci => writtenProductJson = ci.ArgAt<string>(1));

        RepositoryHelper.UpdateOrInitRepository(
            FakeProductPath, productName: "P", templateName: "TenantSchema", dbName: "db",
            platform: Platform.SqlServer, isSchemaTemplate: true);

        var product = JsonConvert.DeserializeObject<Product>(writtenProductJson);
        Assert.That(product.TemplateOrder, Is.EqualTo(new[] { "Shared", "TenantSchema" }));
    }

    [Test]
    public void UpdateOrInitRepository_MultipleRegularAndSchemaTemplates_PreservesRelativeOrderWithinGroups()
    {
        // Stable partition, not alphabetical sort.
        SetupProductFile(new Product
        {
            Name = "P",
            Platform = Platform.SqlServer,
            TemplateOrder = new List<string> { "TenantBeta", "SharedA", "TenantAlpha", "SharedB" }
        });
        SetupTemplateFile("TenantBeta", new Template { Name = "TenantBeta", SchemaIdentificationScript = "SELECT 'x'" });
        SetupTemplateFile("SharedA", new Template { Name = "SharedA" });
        SetupTemplateFile("TenantAlpha", new Template { Name = "TenantAlpha", SchemaIdentificationScript = "SELECT 'y'" });
        SetupTemplateFile("SharedB", new Template { Name = "SharedB" });

        var writtenProductJson = "";
        _file.When(f => f.WriteAllText(FakeProductFile, Arg.Any<string>()))
            .Do(ci => writtenProductJson = ci.ArgAt<string>(1));

        // Re-running extraction for SharedA should partition without alphabetizing.
        RepositoryHelper.UpdateOrInitRepository(
            FakeProductPath, productName: "P", templateName: "SharedA", dbName: "db",
            platform: Platform.SqlServer, isSchemaTemplate: false);

        var product = JsonConvert.DeserializeObject<Product>(writtenProductJson);
        Assert.That(product.TemplateOrder, Is.EqualTo(new[] { "SharedA", "SharedB", "TenantBeta", "TenantAlpha" }));
    }

    [Test]
    public void UpdateOrInitRepository_AlreadyCorrectOrder_NoChurn()
    {
        // Idempotency: a re-run with everything already partitioned correctly
        // returns the same order and does not duplicate the existing entry.
        SetupProductFile(new Product
        {
            Name = "P",
            Platform = Platform.SqlServer,
            TemplateOrder = new List<string> { "Shared", "TenantSchema" },
            ScriptTokens = new Dictionary<string, string> { { "SharedDb", "db" } }
        });
        SetupTemplateFile("Shared", new Template { Name = "Shared" });
        SetupTemplateFile("TenantSchema", new Template
        {
            Name = "TenantSchema",
            SchemaIdentificationScript = "SELECT 'x'"
        });

        var writtenProductJson = "";
        _file.When(f => f.WriteAllText(FakeProductFile, Arg.Any<string>()))
            .Do(ci => writtenProductJson = ci.ArgAt<string>(1));

        RepositoryHelper.UpdateOrInitRepository(
            FakeProductPath, productName: "P", templateName: "Shared", dbName: "db",
            platform: Platform.SqlServer, isSchemaTemplate: false);

        var product = JsonConvert.DeserializeObject<Product>(writtenProductJson);
        Assert.That(product.TemplateOrder, Is.EqualTo(new[] { "Shared", "TenantSchema" }));
    }

    [Test]
    public void UpdateOrInitRepository_ExistingEntryWithMissingTemplateJson_TreatedAsRegular()
    {
        // ClassifyTemplateEntry fallback: when an existing TemplateOrder entry has no
        // Template.json on disk (e.g., fresh clone before extraction), the helper returns
        // false and treats it as a regular template rather than throwing or corrupting the
        // order. Here MysteryTemplate's Template.json is absent; adding a regular Shared
        // template should produce [MysteryTemplate, Shared] — both in the regular bucket,
        // relative order preserved.
        SetupProductFile(new Product
        {
            Name = "P",
            Platform = Platform.SqlServer,
            TemplateOrder = new List<string> { "MysteryTemplate" }
        });
        // Deliberately no SetupTemplateFile("MysteryTemplate", ...) — IFile.Exists returns
        // false for its Template.json, exercising the missing-file fallback path.

        var writtenProductJson = "";
        _file.When(f => f.WriteAllText(FakeProductFile, Arg.Any<string>()))
            .Do(ci => writtenProductJson = ci.ArgAt<string>(1));

        Assert.DoesNotThrow(() =>
            RepositoryHelper.UpdateOrInitRepository(
                FakeProductPath, productName: "P", templateName: "Shared", dbName: "db",
                platform: Platform.SqlServer, isSchemaTemplate: false));

        var product = JsonConvert.DeserializeObject<Product>(writtenProductJson);
        Assert.That(product.TemplateOrder, Is.EqualTo(new[] { "MysteryTemplate", "Shared" }));
    }
}
