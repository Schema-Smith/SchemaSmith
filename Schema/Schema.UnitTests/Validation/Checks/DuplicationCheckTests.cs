// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

/// <summary>
/// Slice 2.1: ShouldApply-aware duplication check. A same-name group at any level is a legitimate
/// variant set (no error) only when EVERY member is gated by a non-empty ShouldApplyExpression;
/// otherwise it's an accidental duplicate (SS-DUP-001, Error). A fully-gated variant set missing
/// VariantName labels on any member gets an advisory SS-DUP-VAR-002 Warning.
/// </summary>
[TestFixture]
public class DuplicationCheckTests
{
    private static Product Product(params string[] templateOrder) => new()
    {
        Name = "Acme",
        Platform = Platform.SqlServer,
        TemplateOrder = templateOrder.ToList()
    };

    private static ValidationContext Context(Product product, params Template[] templates) =>
        new(product, templates, "pkg");

    private static Template TemplateWithTable(string templateName, SqlServerTable table)
    {
        var template = new Template { Name = templateName };
        template.Tables.Add(table);
        return template;
    }

    [Test]
    public void DuplicateUngatedColumns_AreError()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int" },
                new SqlServerColumn { Name = "Id", DataType = "int" }
            }
        };
        var ctx = Context(Product(), TemplateWithTable("Main", table));

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
        Assert.That(findings[0].Category, Is.EqualTo("Duplicate"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / Table 'Customer'"));
    }

    [Test]
    public void SameNameColumns_AllGated_AreValidVariantSet()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int", ShouldApplyExpression = "{{IsPartitioned}}", VariantName = "Partitioned" },
                new SqlServerColumn { Name = "Id", DataType = "bigint", ShouldApplyExpression = "{{IsNotPartitioned}}", VariantName = "NotPartitioned" }
            }
        };
        var ctx = Context(Product(), TemplateWithTable("Main", table));

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void SameNameColumns_MixedGatedAndUngated_AreError()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int", ShouldApplyExpression = "{{IsPartitioned}}", VariantName = "Partitioned" },
                new SqlServerColumn { Name = "Id", DataType = "bigint" }
            }
        };
        var ctx = Context(Product(), TemplateWithTable("Main", table));

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
    }

    [Test]
    public void VariantSet_AllGated_MissingVariantName_IsWarning()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Columns =
            {
                new SqlServerColumn { Name = "Id", DataType = "int", ShouldApplyExpression = "{{IsPartitioned}}" },
                new SqlServerColumn { Name = "Id", DataType = "bigint", ShouldApplyExpression = "{{IsNotPartitioned}}" }
            }
        };
        var ctx = Context(Product(), TemplateWithTable("Main", table));

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-VAR-002"));
    }

    [Test]
    public void DuplicateUngatedIndexes_AreError()
    {
        var table = new SqlServerTable
        {
            Name = "Customer",
            Columns = { new SqlServerColumn { Name = "Id", DataType = "int" } },
            Indexes =
            {
                new SqlServerIndex { Name = "IX_Customer_Name", IndexColumns = "Name" },
                new SqlServerIndex { Name = "IX_Customer_Name", IndexColumns = "Name DESC" }
            }
        };
        var ctx = Context(Product(), TemplateWithTable("Main", table));

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / Table 'Customer'"));
    }

    [Test]
    public void DuplicateTablesInTemplate_Ungated_AreError()
    {
        var template = new Template { Name = "Main" };
        template.Tables.Add(new SqlServerTable { Name = "Customer", Schema = "dbo" });
        template.Tables.Add(new SqlServerTable { Name = "Customer", Schema = "dbo" });
        var ctx = Context(Product(), template);

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main'"));
    }

    [Test]
    public void GatedTableVariants_AreValid()
    {
        var template = new Template { Name = "Main" };
        template.Tables.Add(new SqlServerTable
        {
            Name = "Customer", Schema = "dbo",
            ShouldApplyExpression = "{{IsPartitioned}}", VariantName = "Partitioned"
        });
        template.Tables.Add(new SqlServerTable
        {
            Name = "Customer", Schema = "dbo",
            ShouldApplyExpression = "{{IsNotPartitioned}}", VariantName = "NotPartitioned"
        });
        var ctx = Context(Product(), template);

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void DifferentSchemaSameTableName_AreNotDuplicates()
    {
        // Two tables named "Customer" but in different explicit schemas are distinct objects —
        // schema-qualification in the grouping key must prevent a false-positive collision.
        var template = new Template { Name = "Main" };
        template.Tables.Add(new SqlServerTable { Name = "Customer", Schema = "dbo" });
        template.Tables.Add(new SqlServerTable { Name = "Customer", Schema = "sales" });
        var ctx = Context(Product(), template);

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    [Test]
    public void DuplicateTemplateNamesInTemplateOrder_AreError()
    {
        var product = Product("Main", "Reference", "Main");
        var ctx = Context(product);

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
        Assert.That(findings[0].Location, Is.EqualTo("Product 'Acme' / TemplateOrder"));
    }

    [Test]
    public void CleanPackage_NoFindings()
    {
        var pkg = ValidationTestPackages.Minimal(Platform.SqlServer);
        var ctx = new ValidationContext(pkg.Product, pkg.Templates, "pkg");

        var findings = new DuplicationCheck().Run(ctx).ToList();

        Assert.That(findings, Is.Empty);
    }

    // Declared (modeled) objects were never grouped at all, so two files declaring one object reported
    // nothing -- and the quench would try to create it twice. Each list gets the same ShouldApply-aware rule
    // as tables, keyed by schema-qualified identity.
    private static readonly (string Level, System.Action<Template, string, string, string> Add)[] ModeledLists =
    [
        ("enum type", (t, s, n, g) => t.EnumTypes.Add(new PostgreSqlEnumType { Schema = s, Name = n, ShouldApplyExpression = g })),
        ("domain type", (t, s, n, g) => t.DomainTypes.Add(new PostgreSqlDomainType { Schema = s, Name = n, ShouldApplyExpression = g })),
        ("sequence", (t, s, n, g) => t.Sequences.Add(new PostgreSqlSequence { Schema = s, Name = n, ShouldApplyExpression = g })),
        ("materialized view", (t, s, n, g) => t.MaterializedViews.Add(new PostgreSqlMaterializedView { Schema = s, Name = n, ShouldApplyExpression = g })),
        ("indexed view", (t, s, n, g) => t.IndexedViews.Add(new SqlServerIndexedView { Schema = s, Name = n, ShouldApplyExpression = g })),
        ("event", (t, _, n, g) => t.Events.Add(new MySqlEvent { Name = n, ShouldApplyExpression = g })),
    ];

    private static System.Collections.Generic.IEnumerable<TestCaseData> ModeledListCases() =>
        ModeledLists.Select(l => new TestCaseData(l.Level).SetName($"{{m}}({l.Level})"));

    private static (string Level, System.Action<Template, string, string, string> Add) Modeled(string level) =>
        ModeledLists.Single(l => l.Level == level);

    [TestCaseSource(nameof(ModeledListCases))]
    public void DuplicateUngatedModeledObjects_AreError(string level)
    {
        var template = new Template { Name = "Main" };
        Modeled(level).Add(template, "public", "order_status", null);
        Modeled(level).Add(template, "public", "ORDER_STATUS", null);

        var findings = new DuplicationCheck().Run(Context(Product(), template)).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
        Assert.That(findings[0].Code, Is.EqualTo("SS-DUP-001"));
        Assert.That(findings[0].Message, Does.Contain($"Duplicate {level} name"));
        Assert.That(findings[0].Location, Is.EqualTo("Template 'Main'"));
    }

    [TestCaseSource(nameof(ModeledListCases))]
    public void SameNameModeledObjects_AllGated_AreValidVariantSet_WithoutLabelsWarned(string level)
    {
        var template = new Template { Name = "Main" };
        Modeled(level).Add(template, "public", "order_status", "{{IsEU}}");
        Modeled(level).Add(template, "public", "order_status", "{{IsUS}}");

        var findings = new DuplicationCheck().Run(Context(Product(), template)).ToList();

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-DUP-VAR-002" }));
    }

    [TestCase("enum type")]
    [TestCase("indexed view")]
    public void SameNameModeledObjects_InDifferentSchemas_AreNotDuplicates(string level)
    {
        var template = new Template { Name = "Main" };
        Modeled(level).Add(template, "sales", "order_status", null);
        Modeled(level).Add(template, "billing", "order_status", null);

        Assert.That(new DuplicationCheck().Run(Context(Product(), template)).ToList(), Is.Empty);
    }
}
