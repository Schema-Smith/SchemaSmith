// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

[TestFixture]
public class TableFileNameCheckTests
{
    private const string PackagePath = @"C:\pkg";
    private static readonly string TablesDir = Path.Join(PackagePath, "Templates", "Main", "Tables");

    private IFile _file;
    private IDirectory _directory;

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        _file = Substitute.For<IFile>();
        _directory = Substitute.For<IDirectory>();
        FactoryContainer.Register(_file);
        FactoryContainer.Register(_directory);
        _directory.Exists(PackagePath).Returns(true);
    }

    [TearDown]
    public void TearDown() => FactoryContainer.Clear();

    private static ValidationContext Context(Platform platform = Platform.SqlServer) =>
        new(new Product { Name = "Acme", Platform = platform }, new List<Template>(), PackagePath);

    private static string TemplateFolder(string folder) => Path.Join(PackagePath, "Templates", "Main", folder);

    private void StubTableFiles(params (string path, string json)[] files)
    {
        foreach (var (path, json) in files)
        {
            _file.Exists(path).Returns(true);
            _file.ReadAllText(path).Returns(json);
        }
        _directory.GetFiles(PackagePath, "*.json", SearchOption.AllDirectories).Returns(files.Select(f => f.path).ToArray());
    }

    private static string TableJson(string schema, string name, string variantName = "", string gate = "")
    {
        var parts = new List<string> { $"\"Name\":\"{name}\"" };
        if (!string.IsNullOrEmpty(schema)) parts.Add($"\"Schema\":\"{schema}\"");
        if (!string.IsNullOrEmpty(variantName)) parts.Add($"\"VariantName\":\"{variantName}\"");
        if (!string.IsNullOrEmpty(gate)) parts.Add($"\"ShouldApplyExpression\":\"{gate}\"");
        return "{" + string.Join(",", parts) + "}";
    }

    [Test]
    public void CanonicalName_NoFinding()
    {
        StubTableFiles((Path.Join(TablesDir, "dbo.Orders.json"), TableJson("dbo", "Orders")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void QuotedContentIdentifiers_CanonicalFile_NoFinding()
    {
        // Content loads quoted ([dbo], [Widget]); the canonical filename is the bare identifier.
        StubTableFiles((Path.Join(TablesDir, "dbo.Widget.json"), TableJson("[dbo]", "[Widget]")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void CanonicalVariantName_NoFinding()
    {
        StubTableFiles((Path.Join(TablesDir, "dbo.Orders.EU.json"), TableJson("dbo", "Orders", "EU", "region='EU'")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void NonCanonicalVariantFile_WarnsWithCanonicalName_NeverErrors()
    {
        StubTableFiles((Path.Join(TablesDir, "Orders-EU.json"), TableJson("dbo", "Orders", "EU", "region='EU'")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FILE-NAME-003"));
        Assert.That(findings[0].Message, Does.Contain("dbo.Orders.EU.json"));
    }

    [Test]
    public void NonTableJsonOutsideTablesFolder_Ignored()
    {
        StubTableFiles((Path.Join(PackagePath, "Templates", "Main", "TableData", "weird-name.json"), TableJson("dbo", "Orders")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Is.Empty); // not under a Tables/ folder
    }

    // Declared (modeled) objects follow the same <schema>.<name>[.<VariantName>].json convention as tables.
    // They were outside this check entirely, so a renamed object file drifted from its content unreported --
    // and the add-ons look an object up by its derived file name, so that drift let a second same-named
    // object be written beside the first.
    [TestCase("Enum Types")]
    [TestCase("Domain Types")]
    [TestCase("Sequences")]
    [TestCase("Materialized Views")]
    public void PostgreSqlModeledObject_NonCanonicalFile_Warns(string folder)
    {
        StubTableFiles((Path.Join(TemplateFolder(folder), "renamed.json"), TableJson("public", "order_status")));

        var findings = new TableFileNameCheck().Run(Context(Platform.PostgreSQL)).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Code, Is.EqualTo("SS-FILE-NAME-003"));
        Assert.That(findings[0].Message, Does.Contain("'renamed.json'").And.Contain("'public.order_status.json'"));
    }

    [Test]
    public void PostgreSqlModeledObject_CanonicalFile_NoFinding()
    {
        StubTableFiles(
            (Path.Join(TemplateFolder("Enum Types"), "public.order_status.json"), TableJson("public", "order_status")),
            (Path.Join(TemplateFolder("Enum Types"), "public.order_status.EU.json"), TableJson("public", "order_status", "EU", "region='EU'")),
            (Path.Join(TemplateFolder("Sequences"), "order_number.json"), TableJson("", "order_number")));

        var findings = new TableFileNameCheck().Run(Context(Platform.PostgreSQL)).ToList();

        Assert.That(findings, Is.Empty);
    }

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void Event_IsNamedWithoutASchemaSegment(Platform platform)
    {
        StubTableFiles(
            (Path.Join(TemplateFolder("Events"), "nightly_cleanup.json"), TableJson("", "nightly_cleanup")),
            (Path.Join(TemplateFolder("Events"), "cleanup-old.json"), TableJson("", "purge_sessions")));

        var findings = new TableFileNameCheck().Run(Context(platform)).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("'purge_sessions.json'"));
    }

    [Test]
    public void SqlServerIndexedView_NonCanonicalFile_Warns()
    {
        StubTableFiles((Path.Join(TemplateFolder("Indexed Views"), "summary.json"), TableJson("[dbo]", "[vSummary]")));

        var findings = new TableFileNameCheck().Run(Context()).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("'dbo.vSummary.json'"));
    }

    // The loader only reads a modeled folder on its own engine, so a stray one elsewhere is not a declared
    // object and must not be linted as one.
    [Test]
    public void ModeledFolder_OnAnEngineThatDoesNotLoadIt_Ignored()
    {
        StubTableFiles((Path.Join(TemplateFolder("Events"), "renamed.json"), TableJson("", "nightly_cleanup")));

        var findings = new TableFileNameCheck().Run(Context(Platform.PostgreSQL)).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void MalformedModeledObjectFile_IsLeftToOtherChecks()
    {
        StubTableFiles((Path.Join(TemplateFolder("Enum Types"), "broken.json"), "{ not json"));

        Assert.That(new TableFileNameCheck().Run(Context(Platform.PostgreSQL)).ToList(), Is.Empty);
    }
}
