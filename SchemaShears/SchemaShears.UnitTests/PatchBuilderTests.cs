// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SchemaShears;

namespace SchemaShears.UnitTests;

[TestFixture]
public class PatchBuilderTests
{
    private string _root;
    private string _source;
    private string _output;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Join(Path.GetTempPath(), "shears-" + System.Guid.NewGuid().ToString("N"));
        _source = Path.Join(_root, "product");
        _output = Path.Join(_root, "patch");
        var tables = Path.Join(_source, "Templates", "Main", "Tables");
        Directory.CreateDirectory(tables);
        File.WriteAllText(Path.Join(tables, "dbo.Orders.json"), "{ \"Name\": \"Orders\" }");
        File.WriteAllText(Path.Join(_source, "Templates", "Main", "Template.json"), "{}");
        File.WriteAllText(Path.Join(_source, "Product.json"), "{ \"Name\": \"Acme\" }");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void Build_ProducesSubsetWithScaffoldingAndLatch()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = _source, ManifestPath = manifest, OutputPath = _output, Zip = false
        });

        Assert.Multiple(() =>
        {
            Assert.That(File.Exists(Path.Join(_output, "Templates", "Main", "Tables", "dbo.Orders.json")), Is.True);
            Assert.That(File.Exists(Path.Join(_output, "Templates", "Main", "Template.json")), Is.True);
            Assert.That(File.Exists(Path.Join(_output, "patch-build-report.txt")), Is.True);
            var product = JObject.Parse(File.ReadAllText(Path.Join(_output, "Product.json")));
            Assert.That(product["DropTablesRemovedFromProduct"].Value<bool>(), Is.False);
        });
    }

    // A patch carries only the templates its manifest touches; an order entry with no folder stopped --Validate and the deploy.
    [Test]
    public void Build_OrdersOnlyTheTemplatesThePatchCarries()
    {
        Directory.CreateDirectory(Path.Join(_source, "Templates", "Initialize"));
        File.WriteAllText(Path.Join(_source, "Templates", "Initialize", "Template.json"), "{}");
        File.WriteAllText(Path.Join(_source, "Product.json"), "{ \"Name\": \"Acme\", \"TemplateOrder\": [ \"Initialize\", \"Main\" ] }");
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        new PatchBuilder().Build(new PatchBuildRequest { SourcePath = _source, ManifestPath = manifest, OutputPath = _output });

        var order = JObject.Parse(File.ReadAllText(Path.Join(_output, "Product.json")))["TemplateOrder"]!.Values<string>();
        Assert.That(order, Is.EqualTo(new[] { "Main" }));
    }

    [Test]
    public void Build_SourceMissingProductJson_Throws()
    {
        var bareSource = Path.Join(_root, "bare");
        Directory.CreateDirectory(bareSource);
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "# empty-ish\n");

        Assert.Throws<PatchBuildException>(() => new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = bareSource, ManifestPath = manifest, OutputPath = _output
        }));
    }

    [Test]
    public void Build_SourcePathNullOrWhitespace_Throws()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var ex = Assert.Throws<PatchBuildException>(() => new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = "   ", ManifestPath = manifest, OutputPath = _output
        }));
        Assert.That(ex.Message, Does.Contain("Source product folder not found"));
    }

    [Test]
    public void Build_SourcePathDoesNotExist_Throws()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var ex = Assert.Throws<PatchBuildException>(() => new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = Path.Join(_root, "does-not-exist"), ManifestPath = manifest, OutputPath = _output
        }));
        Assert.That(ex.Message, Does.Contain("Source product folder not found"));
    }

    [Test]
    public void Build_OutputPathMissing_Throws()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        var ex = Assert.Throws<PatchBuildException>(() => new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = _source, ManifestPath = manifest, OutputPath = "  "
        }));
        Assert.That(ex.Message, Does.Contain("Output path is required"));
    }

    [Test]
    public void Build_WithZipTrue_AlsoProducesZipFile()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = _source, ManifestPath = manifest, OutputPath = _output, Zip = true
        });

        Assert.That(File.Exists(_output + ".zip"), Is.True);
    }

    [Test]
    public void Build_WithAllowDrops_StampsProductJsonAccordingly()
    {
        var manifest = Path.Join(_root, "m.txt");
        File.WriteAllText(manifest, "Templates/Main/Tables/dbo.Orders.json\n");

        new PatchBuilder().Build(new PatchBuildRequest
        {
            SourcePath = _source, ManifestPath = manifest, OutputPath = _output, AllowDrops = new[] { "Columns" }
        });

        var product = JObject.Parse(File.ReadAllText(Path.Join(_output, "Product.json")));
        Assert.That(product["DropColumnsRemovedFromProduct"], Is.Null);
    }
}
