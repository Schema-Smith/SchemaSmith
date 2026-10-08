// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using System.IO;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;

namespace SchemaTongs.UnitTests;

[TestFixture]
public class TableFileResolverTests
{
    private IFile _file;
    private IDirectory _directory;
    private const string TablesDir = "pkg/Tables";

    [SetUp]
    public void SetUp()
    {
        _file = Substitute.For<IFile>();
        _directory = Substitute.For<IDirectory>();
        FactoryContainer.Register(_file);
        FactoryContainer.Register(_directory);
        _directory.Exists(Arg.Any<string>()).Returns(false);
        _directory.GetFiles(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SearchOption>()).Returns([]);
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    // Registers a Tables/ folder holding the given (path, json-content) files so JsonHelper.TableLoad
    // deserializes each. Mirrors the file-read isolation in OrphanHandlerTests.
    private void StubTablesFolder(params (string path, string json)[] files)
    {
        _directory.Exists(TablesDir).Returns(true);
        var paths = new string[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            paths[i] = files[i].path;
            _file.Exists(files[i].path).Returns(true);
            _file.ReadAllText(files[i].path).Returns(files[i].json);
        }
        _directory.GetFiles(TablesDir, "*.json", SearchOption.AllDirectories).Returns(paths);
    }

    private static string TableJson(string schema, string name, string variantName = "", string gate = "")
    {
        var parts = new System.Collections.Generic.List<string> { $"\"Name\":\"{name}\"" };
        if (!string.IsNullOrEmpty(schema)) parts.Add($"\"Schema\":\"{schema}\"");
        if (!string.IsNullOrEmpty(variantName)) parts.Add($"\"VariantName\":\"{variantName}\"");
        if (!string.IsNullOrEmpty(gate)) parts.Add($"\"ShouldApplyExpression\":\"{gate}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    // Non-variant tests never exercise the gate delegate; pass an arbitrary one.
    private static readonly Func<string, bool> AnyGate = _ => true;

    [Test]
    public void Resolve_NoExistingFileForTable_ReturnsCanonicalBarePath()
    {
        _directory.Exists(TablesDir).Returns(true);
        _directory.GetFiles(TablesDir, "*.json", SearchOption.AllDirectories).Returns([]);

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, AnyGate);
        var res = resolver.Resolve("dbo", "Orders");

        Assert.That(res.UngatedEmit, Is.False);
        Assert.That(Path.GetFileName(res.WritePath), Is.EqualTo("dbo.Orders.json"));
    }

    // Extraction omits PostgreSQL's default schema from content; the shipped demos declare it. A re-extract keyed the
    // two apart and wrote a bare duplicate beside every declared file, without its DataDelivery.
    [TestCase("public")]
    [TestCase("\\\"public\\\"")]
    public void Resolve_PostgreSqlFileDeclaringPublic_MatchesTheSchemaLessExtraction_AndReportsItsForm(string declared)
    {
        var existing = Path.Join("pkg", "Tables", "public.categories.json");
        StubTablesFolder((existing, TableJson(declared, "categories")));

        var resolver = new TableFileResolver(TablesDir, Platform.PostgreSQL, isSchemaTemplate: false, AnyGate);
        var res = resolver.Resolve("", "categories");

        Assert.That(res.WritePath, Is.EqualTo(existing));
        Assert.That(res.ExistingSchema, Is.EqualTo(declared.Replace("\\\"", "\"")));
    }

    // Only the default schema folds: a named schema is a different table.
    [Test]
    public void Resolve_PostgreSqlFileInANamedSchema_DoesNotMatchTheDefaultSchema()
    {
        StubTablesFolder((Path.Join("pkg", "Tables", "sales.categories.json"), TableJson("sales", "categories")));

        var resolver = new TableFileResolver(TablesDir, Platform.PostgreSQL, isSchemaTemplate: false, AnyGate);
        var res = resolver.Resolve("", "categories");

        Assert.That(Path.GetFileName(res.WritePath), Is.EqualTo("categories.json"));
    }

    [Test]
    public void Resolve_SingleExistingMatch_NonCanonicalName_ReturnsExistingPath()
    {
        var existing = Path.Join("pkg", "Tables", "legacy-name.json"); // deliberately non-canonical
        StubTablesFolder((existing, TableJson("dbo", "Orders")));

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, AnyGate);
        var res = resolver.Resolve("dbo", "Orders");

        Assert.That(res.UngatedEmit, Is.False);
        Assert.That(res.WritePath, Is.EqualTo(existing)); // refresh in place, do not duplicate
    }

    [Test]
    public void Resolve_VariantSet_OneActive_ResolvesToActiveVariantFile()
    {
        var euPath = Path.Join("pkg", "Tables", "dbo.Orders.EU.json");
        StubTablesFolder(
            (euPath, TableJson("dbo", "Orders", "EU", "region='EU'")),
            (Path.Join("pkg", "Tables", "dbo.Orders.US.json"), TableJson("dbo", "Orders", "US", "region='US'")));

        // EU is active on this source.
        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, expr => expr == "region='EU'");
        var res = resolver.Resolve("dbo", "Orders");

        Assert.That(res.UngatedEmit, Is.False);
        Assert.That(res.WritePath, Is.EqualTo(euPath)); // refresh the active variant's file
    }

    [Test]
    public void Resolve_VariantSet_NoneActive_ResolvesToBareCanonical_UngatedEmit()
    {
        StubTablesFolder(
            (Path.Join("pkg", "Tables", "dbo.Orders.EU.json"), TableJson("dbo", "Orders", "EU", "region='EU'")),
            (Path.Join("pkg", "Tables", "dbo.Orders.US.json"), TableJson("dbo", "Orders", "US", "region='US'")));

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, _ => false);
        var res = resolver.Resolve("dbo", "Orders");

        Assert.That(res.UngatedEmit, Is.True);
        Assert.That(Path.GetFileName(res.WritePath), Is.EqualTo("dbo.Orders.json")); // ungated shape → bare name
    }

    [Test]
    public void Resolve_DifferentSchemaSameName_AreDistinctIdentities()
    {
        StubTablesFolder(
            (Path.Join("pkg", "Tables", "sales.Orders.json"), TableJson("sales", "Orders")),
            (Path.Join("pkg", "Tables", "hr.Orders.json"), TableJson("hr", "Orders")));

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, AnyGate);
        var sales = resolver.Resolve("sales", "Orders");

        Assert.That(sales.UngatedEmit, Is.False); // not a variant set — different schemas
        Assert.That(sales.WritePath, Is.EqualTo(Path.Join("pkg", "Tables", "sales.Orders.json")));
    }

    [Test]
    public void Resolve_SchemaTemplate_MatchesScrubbedFileByNameIgnoringPassedSchema()
    {
        // Schema-template files are schema-scrubbed; resolver keys on name only in template mode.
        var existing = Path.Join("pkg", "Tables", "Orders.json");
        StubTablesFolder((existing, TableJson("", "Orders")));

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: true, AnyGate);
        var res = resolver.Resolve("dbo", "Orders"); // caller passes the source schema; template mode ignores it

        Assert.That(res.WritePath, Is.EqualTo(existing));
    }

    [Test]
    public void Resolve_QuotedContentIdentifiers_MatchUnquotedQuery()
    {
        // Loaded Name/Schema are quoted ([dbo], [Widget]); the INFORMATION_SCHEMA query is unquoted.
        var existing = Path.Join("pkg", "Tables", "dbo.Widget.json");
        StubTablesFolder((existing, TableJson("[dbo]", "[Widget]")));

        var resolver = new TableFileResolver(TablesDir, Platform.SqlServer, isSchemaTemplate: false, AnyGate);
        var res = resolver.Resolve("dbo", "Widget");

        Assert.That(res.WritePath, Is.EqualTo(existing)); // matched despite the quoting mismatch
    }

    [Test]
    public void Resolve_MySqlNoSchemaContent_MatchesByNameAlone()
    {
        var existing = Path.Join("pkg", "Tables", "Documents.json");
        StubTablesFolder((existing, TableJson("", "Documents")));

        var resolver = new TableFileResolver(TablesDir, Platform.MySQL, isSchemaTemplate: true, AnyGate);
        var res = resolver.Resolve("", "Documents");

        Assert.That(res.UngatedEmit, Is.False);
        Assert.That(res.WritePath, Is.EqualTo(existing));
    }
}
