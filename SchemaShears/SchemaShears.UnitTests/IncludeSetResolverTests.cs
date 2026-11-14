// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SchemaShears;

namespace SchemaShears.UnitTests;

[TestFixture]
public class IncludeSetResolverTests
{
    private string _source;
    private string _ordersRel;
    private string _mainTemplateRel;
    private string _productRel;

    [SetUp]
    public void SetUp()
    {
        _source = Path.Join(Path.GetTempPath(), "shears-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Join(_source, "Templates", "Main", "Tables"));
        _ordersRel = Path.Join("Templates", "Main", "Tables", "dbo.Orders.json");
        _mainTemplateRel = Path.Join("Templates", "Main", "Template.json");
        _productRel = "Product.json";
        File.WriteAllText(Path.Join(_source, _ordersRel), "{}");
        File.WriteAllText(Path.Join(_source, _mainTemplateRel), "{}");
        File.WriteAllText(Path.Join(_source, _productRel), "{}");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_source, recursive: true);

    [Test]
    public void Resolve_AddsProductAndTouchedTemplateScaffolding()
    {
        var result = IncludeSetResolver.Resolve(new[] { _ordersRel }, new List<string>(), _source);

        Assert.Multiple(() =>
        {
            Assert.That(result[_ordersRel], Is.EqualTo(IncludeReason.Manifest));
            Assert.That(result[_productRel], Is.EqualTo(IncludeReason.Scaffolding));
            Assert.That(result[_mainTemplateRel], Is.EqualTo(IncludeReason.Scaffolding));
        });
    }

    [Test]
    public void Resolve_ManifestWinsOverScaffolding()
    {
        // Product.json explicitly in the manifest keeps the Manifest reason.
        var result = IncludeSetResolver.Resolve(new[] { _productRel }, new List<string>(), _source);

        Assert.That(result[_productRel], Is.EqualTo(IncludeReason.Manifest));
    }

    [Test]
    public void Resolve_AlwaysIncludeEntry_GetsAlwaysIncludeReasonAndScaffolding()
    {
        var result = IncludeSetResolver.Resolve(new List<string>(), new[] { _ordersRel }, _source);

        Assert.Multiple(() =>
        {
            Assert.That(result[_ordersRel], Is.EqualTo(IncludeReason.AlwaysInclude));
            Assert.That(result[_mainTemplateRel], Is.EqualTo(IncludeReason.Scaffolding));
        });
    }

    [Test]
    public void Resolve_ManifestWinsOverAlwaysInclude()
    {
        // Same path present in both lists: alwaysInclude is processed first (weaker precedence)
        // but the stronger Manifest reason must not be downgraded by a later scaffolding/always-include pass.
        var result = IncludeSetResolver.Resolve(new[] { _ordersRel }, new[] { _ordersRel }, _source);

        Assert.That(result[_ordersRel], Is.EqualTo(IncludeReason.Manifest));
    }

    [Test]
    public void Resolve_ManifestEntryUnderTemplatesWithoutTemplateJson_NoScaffoldingAdded()
    {
        var orphanRel = Path.Join("Templates", "Orphan", "dbo.Widget.json");
        Directory.CreateDirectory(Path.Join(_source, "Templates", "Orphan"));
        File.WriteAllText(Path.Join(_source, orphanRel), "{}");
        // Deliberately no Template.json under Templates/Orphan.

        var result = IncludeSetResolver.Resolve(new[] { orphanRel }, new List<string>(), _source);

        var orphanTemplateJson = Path.Join("Templates", "Orphan", "Template.json");
        Assert.Multiple(() =>
        {
            Assert.That(result[orphanRel], Is.EqualTo(IncludeReason.Manifest));
            Assert.That(result.ContainsKey(orphanTemplateJson), Is.False);
        });
    }

    [Test]
    public void Resolve_ManifestEntryOutsideTemplates_NoScaffoldingAttempted()
    {
        var readmeRel = "Readme.txt";
        File.WriteAllText(Path.Join(_source, readmeRel), "hi");

        var result = IncludeSetResolver.Resolve(new[] { readmeRel }, new List<string>(), _source);

        Assert.Multiple(() =>
        {
            Assert.That(result[readmeRel], Is.EqualTo(IncludeReason.Manifest));
            Assert.That(result.Count, Is.EqualTo(2)); // readme + Product.json scaffolding only
        });
    }

    // B27: the Templates folder was matched by exact case, so a "templates/..." manifest line (which resolves on a
    // case-insensitive file system) brought its file without its Template.json.
    [Test]
    public void Resolve_ATemplatesPathInAnotherCase_StillAddsTheTemplateScaffolding()
    {
        var result = IncludeSetResolver.Resolve(new[] { Path.Join("templates", "Main", "Tables", "dbo.Orders.json") }, new List<string>(), _source);

        Assert.That(result.ContainsKey(_mainTemplateRel), Is.True, string.Join(", ", result.Keys));
    }
}
