// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using Schema.Domain.PostgreSQL;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

// PostgreSQL rejects DEFERRABLE on a CHECK constraint, and the deploy no longer emits the clause, so a package that
// declares it is stopped rather than deployed without what it asked for.
[TestFixture]
public class ConstraintAttributeCheckTests
{
    private static ValidationContext Context(Platform platform, params Table[] tables)
    {
        var template = new Template { Name = "Main" };
        template.Tables.AddRange(tables);
        return new ValidationContext(new Product { Name = "Acme", Platform = platform }, [template], "pkg");
    }

    private static PostgreSqlTable TableWith(params PostgreSqlCheckConstraint[] checks)
    {
        var table = new PostgreSqlTable { Name = "orders" };
        table.CheckConstraints.AddRange(checks);
        return table;
    }

    [Test]
    public void ADeferrableCheck_IsAnError_NamingTheConstraintAndTheKey()
    {
        var findings = new ConstraintAttributeCheck().Run(Context(Platform.PostgreSQL,
            TableWith(new PostgreSqlCheckConstraint { Name = "ck_qty", Expression = "qty > 0", Deferrable = true }))).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(findings[0].Code, Is.EqualTo("SS-CHK-001"));
            Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / Table 'orders'"));
            Assert.That(findings[0].Message, Does.Contain("ck_qty").And.Contain("`Deferrable`").And.Contain("CHECK constraints cannot be marked DEFERRABLE"));
        });
    }

    [Test]
    public void InitiallyDeferredAlone_IsTheSameError_NamingThatKey()
    {
        var findings = new ConstraintAttributeCheck().Run(Context(Platform.PostgreSQL,
            TableWith(new PostgreSqlCheckConstraint { Name = "ck_qty", Expression = "qty > 0", InitiallyDeferred = true }))).ToList();

        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].Message, Does.Contain("`InitiallyDeferred`"));
    }

    [Test]
    public void OneFindingPerOffendingCheck_AndNoneForACleanOne()
    {
        var findings = new ConstraintAttributeCheck().Run(Context(Platform.PostgreSQL, TableWith(
            new PostgreSqlCheckConstraint { Name = "ck_a", Expression = "a > 0", Deferrable = true },
            new PostgreSqlCheckConstraint { Name = "ck_b", Expression = "b > 0" },
            new PostgreSqlCheckConstraint { Name = "ck_c", Expression = "c > 0", Deferrable = true, InitiallyDeferred = true }))).ToList();

        Assert.That(findings.Select(f => f.Message.Split(' ')[2]), Is.EqualTo(new[] { "ck_a", "ck_c" }));
    }

    [Test]
    public void ACleanPostgreSqlPackage_HasNoFinding() =>
        Assert.That(new ConstraintAttributeCheck().Run(Context(Platform.PostgreSQL,
            TableWith(new PostgreSqlCheckConstraint { Name = "ck_qty", Expression = "qty > 0" }))), Is.Empty);

    [TestCase(Platform.SqlServer)]
    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void OtherEngines_AreNotChecked(Platform platform) =>
        Assert.That(new ConstraintAttributeCheck().Run(Context(platform, new Table { Name = "orders" })), Is.Empty);
}
