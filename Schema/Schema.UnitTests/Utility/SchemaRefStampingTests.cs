// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.SqlServer;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Utility;

/// <summary>
/// The "$schema" reference has to satisfy three surfaces at once, and each one fails differently:
/// the deserializer (MissingMemberHandling.Error -> the package will not LOAD), the generated schema
/// (additionalProperties:false -> --Validate rejects it), and the editor (a wrong relative path
/// validates nothing and says nothing). These pin all three, plus the narrowness of the allowance.
/// </summary>
[TestFixture]
public class SchemaRefStampingTests
{
    // StampSchemaRefs resolves IFile/IDirectory through FactoryContainer, which is global. Clearing
    // it here means these tests run against the real filesystem (which is what the temp-directory
    // cases below need) rather than against substitutes another fixture happened to leave behind.
    [SetUp]
    public void SetUp() => FactoryContainer.Clear();

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    // ---- the relative path an editor actually has to resolve ----

    [Test]
    public void BuildSchemaRef_AtPackageRoot_IsSiblingRelative()
    {
        var reference = RepositoryHelper.BuildSchemaRef(
            "/pkg", "/pkg/Product.json", "products", Platform.SqlServer);

        Assert.That(reference, Is.EqualTo(".json-schemas/products.sqlserver.schema"));
    }

    [Test]
    public void BuildSchemaRef_FromTableFolder_ClimbsToPackageRoot()
    {
        var reference = RepositoryHelper.BuildSchemaRef(
            "/pkg", "/pkg/Templates/Main/Tables/Orders.json", "tables", Platform.SqlServer);

        Assert.That(reference, Is.EqualTo("../../../.json-schemas/tables.sqlserver.schema"));
    }

    [Test]
    public void BuildSchemaRef_FromNestedTableFolder_ClimbsTheExtraLevel()
    {
        // Tables/Core and Tables/Reference are both shipped shapes; the schema covers any depth.
        var reference = RepositoryHelper.BuildSchemaRef(
            "/pkg", "/pkg/Templates/Main/Tables/Core/Orders.json", "tables", Platform.SqlServer);

        Assert.That(reference, Is.EqualTo("../../../../.json-schemas/tables.sqlserver.schema"));
    }

    [Test]
    public void BuildSchemaRef_NeverEmitsABackslash()
    {
        // The failure this guards is silent: a Windows-separator path is present, reads correctly in
        // a diff, and resolves in no editor on any OS.
        var reference = RepositoryHelper.BuildSchemaRef(
            "/pkg", "/pkg/Templates/Main/Tables/Orders.json", "tables", Platform.MySQL);

        Assert.That(reference, Does.Not.Contain("\\"));
    }

    // ---- the deserializer allowance: a stamped package must still LOAD ----

    [Test]
    public void Table_CarryingSchemaRef_StillDeserializes()
    {
        var json = """
                   {
                     "$schema": "../../../.json-schemas/tables.sqlserver.schema",
                     "Name": "Orders",
                     "Columns": []
                   }
                   """;

        var table = PlatformDeserializer.DeserializeTable(json, Platform.SqlServer);

        Assert.Multiple(() =>
        {
            Assert.That(table.Name, Is.EqualTo("Orders"));
            Assert.That(table.SchemaRef, Is.EqualTo("../../../.json-schemas/tables.sqlserver.schema"));
        });
    }

    [Test]
    public void Column_CarryingSchemaRef_StillFailsLoudly()
    {
        // The allowance is deliberately on root-of-file types only. A "$schema" nested inside a
        // column is a mistake, and strictness is the whole reason that mistake surfaces at all.
        var json = """
                   {
                     "Name": "Orders",
                     "Columns": [ { "$schema": "nope", "Name": "Id", "DataType": "int" } ]
                   }
                   """;

        Assert.Catch(() => PlatformDeserializer.DeserializeTable(json, Platform.SqlServer));
    }

    // ---- the generated schema must list it, or --Validate rejects what we just stamped ----

    /// <summary>
    /// EVERY schema kind, driven off <see cref="RepositoryHelper.GetSchemaFileNames"/> rather than a
    /// hand-written list, because a hand-written list is exactly how this was got wrong the first
    /// time: the property went onto Product/Template/Table, the stamper happily stamped indexed
    /// views and materialized views too, and their schemas did not declare it -- so Ajv rejected
    /// four shipped demo files with "must NOT have additional properties". Any object type added
    /// later is covered here the day it gains a schema file.
    /// </summary>
    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.PostgreSQL)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void EveryGeneratedSchema_DeclaresSchemaRef(Platform platform)
    {
        var schemaFiles = RepositoryHelper.GetSchemaFileNames(platform);
        Assert.That(schemaFiles, Is.Not.Empty);

        Assert.Multiple(() =>
        {
            foreach (var fileName in schemaFiles)
            {
                var generated = SchemaGenerator.GenerateSchema(
                    RepositoryHelper.GetTypeForSchemaFile(fileName, platform),
                    RepositoryHelper.PlatformElementResolver(platform), platform);

                Assert.That(generated["additionalProperties"]?.ToObject<bool>(), Is.False,
                    $"{fileName}: the schema still has to reject unknown properties -- that is what "
                    + "makes declaring $schema necessary rather than incidental.");
                Assert.That(generated["properties"]?["$schema"], Is.Not.Null,
                    $"{fileName}: additionalProperties:false plus an undeclared $schema means every "
                    + "package file of this kind that we stamp is rejected by --Validate and by any "
                    + "third-party JSON Schema validator.");
            }
        });
    }

    /// <summary>
    /// The other half of the same contract: the stamper must not stamp a file kind whose schema
    /// cannot accept the key. These two tests only agree if every folder the stamper recognises maps
    /// to a schema kind that declares <c>$schema</c>.
    /// </summary>
    [Test]
    public void EveryStampedFolderKind_HasAGeneratedSchemaThatDeclaresIt()
    {
        // The folder names StampSchemaRefs recognises, and the platform each kind can appear on.
        var folderKinds = new (string Kind, Platform Platform)[]
        {
            ("tables", Platform.SqlServer),
            ("indexedviews", Platform.SqlServer),
            ("materializedviews", Platform.PostgreSQL),
            ("events", Platform.MySQL),
            ("domaintypes", Platform.PostgreSQL),
            ("enumtypes", Platform.PostgreSQL),
            ("sequences", Platform.PostgreSQL)
        };

        Assert.Multiple(() =>
        {
            foreach (var (kind, platform) in folderKinds)
            {
                var fileName = $"{kind}.{platform.ToCanonicalString().ToLower()}.schema";
                Assert.That(RepositoryHelper.GetSchemaFileNames(platform), Does.Contain(fileName),
                    $"{kind} is stamped but {platform} generates no schema for it.");

                var generated = SchemaGenerator.GenerateSchema(
                    RepositoryHelper.GetTypeForSchemaFile(fileName, platform),
                    RepositoryHelper.PlatformElementResolver(platform), platform);

                Assert.That(generated["properties"]?["$schema"], Is.Not.Null, fileName);
            }
        });
    }

    // ---- a reference is only written when the thing it points at exists ----

    [Test]
    public void StampSchemaRefs_SkipsAKindWhoseSchemaFileIsMissing()
    {
        // A package can hold some schemas and not others -- SchemaQuench's PartialCommittedSchemas
        // fixture is built that way on purpose. Stamping on folder presence alone wrote a reference
        // to a file that was not there, which an editor reports as a broken schema and which nothing
        // in the deploy path would ever surface.
        var root = Path.Combine(Path.GetTempPath(), "ss-partial-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".json-schemas"));
            Directory.CreateDirectory(Path.Combine(root, "Templates", "Main", "Tables"));

            // products exists; tables deliberately does not.
            File.WriteAllText(Path.Combine(root, ".json-schemas", "products.sqlserver.schema"), "{}");
            File.WriteAllText(Path.Combine(root, "Product.json"), "{\n  \"Name\": \"P\"\n}");
            File.WriteAllText(Path.Combine(root, "Templates", "Main", "Tables", "dbo.Widget.json"),
                "{\n  \"Name\": \"[Widget]\"\n}");

            RepositoryHelper.StampSchemaRefs(root, Platform.SqlServer, _ => { });

            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(root, "Product.json")),
                    Does.Contain("$schema"), "products schema exists, so Product.json should be stamped.");
                Assert.That(File.ReadAllText(Path.Combine(root, "Templates", "Main", "Tables", "dbo.Widget.json")),
                    Does.Not.Contain("$schema"),
                    "tables schema is absent, so the table file must NOT gain a dangling reference.");
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StampSchemaRefs_NoOpsWhenThePackageHasNoSchemasAtAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-noschemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Product.json"), "{\n  \"Name\": \"P\"\n}");

            var stamped = RepositoryHelper.StampSchemaRefs(root, Platform.SqlServer, _ => { });

            Assert.Multiple(() =>
            {
                Assert.That(stamped, Is.Zero);
                Assert.That(File.ReadAllText(Path.Combine(root, "Product.json")), Does.Not.Contain("$schema"));
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void StampSchemaRefs_IsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss-idem-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".json-schemas"));
            File.WriteAllText(Path.Combine(root, ".json-schemas", "products.sqlserver.schema"), "{}");
            File.WriteAllText(Path.Combine(root, "Product.json"), "{\n  \"Name\": \"P\"\n}");

            var first = RepositoryHelper.StampSchemaRefs(root, Platform.SqlServer, _ => { });
            var afterFirst = File.ReadAllText(Path.Combine(root, "Product.json"));
            var second = RepositoryHelper.StampSchemaRefs(root, Platform.SqlServer, _ => { });
            var afterSecond = File.ReadAllText(Path.Combine(root, "Product.json"));

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(1));
                Assert.That(second, Is.Zero, "A second run must report nothing to do.");
                Assert.That(afterSecond, Is.EqualTo(afterFirst), "and must not touch the bytes.");
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- round-trip: stamping must not disturb anything else ----

    [Test]
    public void SerializedTable_PutsSchemaRefFirst()
    {
        var table = new SqlServerTable { Name = "Orders", SchemaRef = ".json-schemas/tables.sqlserver.schema" };

        var json = JsonConvert.SerializeObject(table, Formatting.Indented);

        Assert.That(json.IndexOf("\"$schema\"", System.StringComparison.Ordinal),
            Is.LessThan(json.IndexOf("\"Name\"", System.StringComparison.Ordinal)),
            "Editors expect $schema to lead the file.");
    }

    [Test]
    public void SerializedTable_WithoutSchemaRef_OmitsTheKeyEntirely()
    {
        // A package that has never been stamped must not gain an empty "$schema": null.
        var json = JsonConvert.SerializeObject(new SqlServerTable { Name = "Orders" }, Formatting.Indented);

        Assert.That(json, Does.Not.Contain("$schema"));
    }
}
