// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.IO;
using System.Linq;
using log4net;
using NSubstitute;
using Schema.Domain;
using Schema.Isolators;
using Schema.Utility;

namespace Schema.UnitTests.Domain;

/// <summary>
/// #566: a table declared in two files is refused when the deploy loads the template, naming the table and both files.
/// Before, the deploy loaded both definitions and failed partway through with an engine error that named neither
/// (PostgreSQL: 21000, more than one row returned by a subquery). The rule is SS-DUP-001's: a same-named group is a
/// variant set when every member is gated by ShouldApplyExpression, and a duplicate otherwise.
/// </summary>
[TestFixture]
public class TemplateLoadDuplicateTableTests
{
    private IFile _mockFile;
    private IDirectory _mockDirectory;

    private static readonly string TemplateFile = Path.Join("C:", "products", "Templates", "Main", "Template.json");
    private static readonly string TablesPath = Path.Join("C:", "products", "Templates", "Main", "Tables");

    [SetUp]
    public void SetUp()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
        _mockFile = Substitute.For<IFile>();
        _mockDirectory = Substitute.For<IDirectory>();
        FactoryContainer.Register<IFile>(_mockFile);
        FactoryContainer.Register<IDirectory>(_mockDirectory);
        LogFactory.Register("ProgressLog", Substitute.For<ILog>());

        _mockFile.Exists(TemplateFile).Returns(true);
        _mockFile.ReadAllText(TemplateFile).Returns(@"{ ""Name"": ""Main"" }");
        _mockDirectory.Exists(TablesPath).Returns(true);
    }

    [TearDown]
    public void TearDown()
    {
        FactoryContainer.Clear();
        LogFactory.Clear();
    }

    private static Product MakeProduct(Platform platform) => new()
    {
        Name = "TestProduct",
        Platform = platform,
        FilePath = Path.Join("C:", "products", "Product.json")
    };

    private void TableFiles(params (string file, string json)[] files)
    {
        var paths = files.Select(f => Path.Join(TablesPath, f.file)).ToArray();
        _mockDirectory.GetFiles(TablesPath, "*.json", SearchOption.AllDirectories).Returns(paths);
        foreach (var (file, json) in files)
        {
            var path = Path.Join(TablesPath, file);
            _mockFile.Exists(path).Returns(true);
            _mockFile.ReadAllText(path).Returns(json);
        }
    }

    [Test]
    public void ATableInTwoFiles_IsRefusedAtLoad_NamingTheTableAndBothFiles()
    {
        // The issue's repro: the same table under the schema-prefixed name SchemaTongs once wrote and the bare one.
        TableFiles(("public.customer.json", @"{ ""Schema"": ""public"", ""Name"": ""customer"", ""Columns"": [] }"),
                   ("customer.json", @"{ ""Name"": ""customer"", ""Columns"": [] }"));

        var ex = Assert.Throws<PackageException>(() => Template.Load("Main", MakeProduct(Platform.PostgreSQL)));

        Assert.That(ex!.Message, Does.Contain("public.customer"));
        Assert.That(ex.Message, Does.Contain("public.customer.json"));
        Assert.That(ex.Message, Does.Contain("customer.json"));
        Assert.That(ex.Message, Does.Contain("ShouldApplyExpression"), "it says how to make two declarations legitimate");
    }

    // Two names differing only in case are two tables on PostgreSQL, a case-sensitive SQL Server collation and MySQL at
    // lower_case_table_names=0; a server that folds case refuses the pair at deploy, knowing its setting.
    [Test]
    public void TwoTablesDifferingOnlyInCase_StillLoad()
    {
        TableFiles(("a.json", @"{ ""Name"": ""Customer"", ""Columns"": [] }"),
                   ("b.json", @"{ ""Name"": ""customer"", ""Columns"": [] }"));

        var template = Template.Load("Main", MakeProduct(Platform.PostgreSQL));

        Assert.That(template.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public void AVariantSet_EveryMemberGated_StillLoads()
    {
        TableFiles(("customer.v1.json", @"{ ""Name"": ""customer"", ""ShouldApplyExpression"": ""1 = 1"", ""Columns"": [] }"),
                   ("customer.v2.json", @"{ ""Name"": ""customer"", ""ShouldApplyExpression"": ""1 = 0"", ""Columns"": [] }"));

        var template = Template.Load("Main", MakeProduct(Platform.PostgreSQL));

        Assert.That(template.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public void AVariantSet_WithOneMemberUngated_IsRefusedAtLoad()
    {
        TableFiles(("customer.v1.json", @"{ ""Name"": ""customer"", ""ShouldApplyExpression"": ""1 = 1"", ""Columns"": [] }"),
                   ("customer.json", @"{ ""Name"": ""customer"", ""Columns"": [] }"));

        var ex = Assert.Throws<PackageException>(() => Template.Load("Main", MakeProduct(Platform.PostgreSQL)));

        Assert.That(ex!.Message, Does.Contain("customer.v1.json").And.Contain("customer.json"));
    }

    [Test]
    public void TheSameNameInTwoSchemas_IsTwoTables()
    {
        TableFiles(("public.customer.json", @"{ ""Schema"": ""public"", ""Name"": ""customer"", ""Columns"": [] }"),
                   ("sales.customer.json", @"{ ""Schema"": ""sales"", ""Name"": ""customer"", ""Columns"": [] }"));

        var template = Template.Load("Main", MakeProduct(Platform.PostgreSQL));

        Assert.That(template.Tables, Has.Count.EqualTo(2));
    }

    [Test]
    public void TheValidateLoad_KeepsBothSoTheLinterCanReportThem()
    {
        TableFiles(("public.customer.json", @"{ ""Schema"": ""public"", ""Name"": ""customer"", ""Columns"": [] }"),
                   ("customer.json", @"{ ""Name"": ""customer"", ""Columns"": [] }"));

        var template = Template.Load("Main", MakeProduct(Platform.PostgreSQL), tolerateComponentLoadErrors: true);

        Assert.That(template.Tables, Has.Count.EqualTo(2), "--Validate reports SS-DUP-001 itself, with every other finding");
    }
}
