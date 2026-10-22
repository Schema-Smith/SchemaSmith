// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using Newtonsoft.Json.Linq;
using Schema.Domain;
using Schema.Domain.MySQL;
using Schema.Domain.PostgreSQL;
using Schema.Domain.SqlServer;
using Schema.Validation;
using Schema.Validation.Checks;

namespace Schema.UnitTests.Validation.Checks;

// Description is one key on every engine, and the two older spellings still deploy: a package that keeps
// using one is told once per table, and one that says two different things is stopped before the deploy
// quietly applies one of them.
[TestFixture]
public class DescriptionCheckTests
{
    private static ValidationContext Context(Platform platform, params Table[] tables)
    {
        var template = new Template { Name = "Main" };
        template.Tables.AddRange(tables);
        return new ValidationContext(new Product { Name = "Acme", Platform = platform }, [template], "pkg");
    }

    private static ValidationContext MySqlContext(Platform platform, MySqlEvent ev)
    {
        var template = new Template { Name = "Main" };
        template.Events.Add(ev);
        return new ValidationContext(new Product { Name = "Acme", Platform = platform }, [template], "pkg");
    }

    private static JObject LegacyMsDescription(string value) =>
        new() { ["ExtendedProperties"] = new JObject { ["MS_Description"] = value } };

    [TestCase(Platform.MySQL)]
    [TestCase(Platform.MariaDb)]
    public void MySqlFamily_CommentOnAnyComponent_IsOneWarningPerTable_NamingTheComponents(Platform platform)
    {
        var table = new MySqlTable { Name = "`orders`", Comment = "all the orders" };
        table.Columns.Add(new MySqlColumn { Name = "`id`", DataType = "INT", Comment = "the key" });
        table.Columns.Add(new MySqlColumn { Name = "`v`", DataType = "INT", Description = "fine as it is" });
        table.Indexes.Add(new MySqlIndex { Name = "`ix_v`", IndexColumns = "`v`", Comment = "lookup" });
        table.FullTextIndexes.Add(new Schema.Domain.MySQL.FullTextIndex { Name = "`ft_v`", Columns = "`v`", Comment = "search" });

        var findings = new DescriptionCheck().Run(Context(platform, table)).ToList();

        Assert.That(findings, Has.Exactly(1).Items, "one finding per table, not per component");
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Code, Is.EqualTo("SS-DESC-002"));
            Assert.That(findings[0].Severity, Is.EqualTo(Severity.Warning));
            Assert.That(findings[0].Location, Is.EqualTo("Template 'Main' / Table '`orders`'"));
            Assert.That(findings[0].Message, Does.Contain("`Comment`").And.Contain("the table").And.Contain("column `id`")
                .And.Contain("index `ix_v`").And.Contain("full-text index `ft_v`").And.Not.Contain("`v`,"));
        });
    }

    [Test]
    public void MySqlFamily_CommentAndDescriptionThatDisagree_IsAnErrorNamingBothValues()
    {
        var table = new MySqlTable { Name = "`orders`" };
        table.Columns.Add(new MySqlColumn { Name = "`id`", DataType = "INT", Description = "new words", Comment = "old words" });

        var findings = new DescriptionCheck().Run(Context(Platform.MySQL, table)).ToList();

        var error = findings.Single(f => f.Code == "SS-DESC-001");
        Assert.Multiple(() =>
        {
            Assert.That(error.Severity, Is.EqualTo(Severity.Error));
            Assert.That(error.Location, Is.EqualTo("Template 'Main' / Table '`orders`'"));
            Assert.That(error.Message, Does.Contain("column `id`").And.Contain("'new words'").And.Contain("'old words'"));
            Assert.That(findings.Count(f => f.Code == "SS-DESC-002"), Is.EqualTo(1), "the deprecated spelling is still reported alongside");
        });
    }

    [Test]
    public void MySqlFamily_CommentAndDescriptionThatAgree_IsOnlyTheWarning()
    {
        var table = new MySqlTable { Name = "`orders`", Description = "same", Comment = "same" };

        var findings = new DescriptionCheck().Run(Context(Platform.MySQL, table)).ToList();

        Assert.That(findings.Select(f => f.Code), Is.EqualTo(new[] { "SS-DESC-002" }));
    }

    [Test]
    public void MySqlFamily_AnEventStillUsingComment_IsReported_AndADisagreementIsAnError()
    {
        var findings = new DescriptionCheck().Run(MySqlContext(Platform.MySQL,
            new MySqlEvent { Name = "`nightly`", Definition = "DO 1", Description = "runs nightly", Comment = "runs at night" })).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(findings.Select(f => f.Code), Is.EquivalentTo(new[] { "SS-DESC-001", "SS-DESC-002" }));
            Assert.That(findings.All(f => f.Location == "Template 'Main' / Event '`nightly`'"), Is.True);
        });
    }

    [Test]
    public void SqlServer_AnMsDescriptionLeftInsideExtendedProperties_IsTheDeprecatedSpelling()
    {
        var table = new SqlServerTable { Name = "[Orders]", Extensions = LegacyMsDescription("all the orders") };
        table.Columns.Add(new SqlServerColumn { Name = "[Id]", DataType = "INT", Extensions = LegacyMsDescription("the key") });
        table.Indexes.Add(new SqlServerIndex { Name = "[IX_Id]", IndexColumns = "[Id]", Extensions = new JObject { ["ExtendedProperties"] = new JObject { ["Owner"] = "Billing" } } });

        var findings = new DescriptionCheck().Run(Context(Platform.SqlServer, table)).ToList();

        Assert.That(findings, Has.Exactly(1).Items);
        Assert.Multiple(() =>
        {
            Assert.That(findings[0].Code, Is.EqualTo("SS-DESC-002"));
            Assert.That(findings[0].Message, Does.Contain("`Extensions.ExtendedProperties.MS_Description`")
                .And.Contain("the table").And.Contain("column [Id]").And.Not.Contain("index"),
                "an extended property with another name is not a description and is not reported");
        });
    }

    [Test]
    public void SqlServer_DescriptionAndLegacyMsDescriptionThatDisagree_IsAnError()
    {
        var table = new SqlServerTable { Name = "[Orders]", Description = "new words", Extensions = LegacyMsDescription("old words") };

        var findings = new DescriptionCheck().Run(Context(Platform.SqlServer, table)).ToList();

        Assert.That(findings.Select(f => f.Code), Is.EquivalentTo(new[] { "SS-DESC-001", "SS-DESC-002" }));
        Assert.That(findings.Single(f => f.Code == "SS-DESC-001").Message, Does.Contain("'new words'").And.Contain("'old words'"));
    }

    [Test]
    public void SqlServer_AnMsDescriptionThatIsNotAString_IsIgnored()
    {
        var table = new SqlServerTable
        {
            Name = "[Orders]",
            Extensions = new JObject { ["ExtendedProperties"] = new JObject { ["MS_Description"] = new JObject { ["nested"] = 1 } } }
        };

        Assert.That(new DescriptionCheck().Run(Context(Platform.SqlServer, table)), Is.Empty);
    }

    [Test]
    public void PostgreSql_HasNoOlderSpelling_SoNothingIsReported()
    {
        var table = new PostgreSqlTable { Name = "orders", Description = "all the orders", Extensions = LegacyMsDescription("ignored") };
        table.Columns.Add(new PostgreSqlColumn { Name = "id", DataType = "integer", Description = "the key" });

        Assert.That(new DescriptionCheck().Run(Context(Platform.PostgreSQL, table)), Is.Empty);
    }

    [Test]
    public void OnlyDescription_OnEveryEngine_IsClean()
    {
        var mysql = new MySqlTable { Name = "`t`", Description = "d" };
        mysql.Columns.Add(new MySqlColumn { Name = "`c`", DataType = "INT", Description = "d" });
        var sqlServer = new SqlServerTable { Name = "[T]", Description = "d" };
        sqlServer.Indexes.Add(new SqlServerIndex { Name = "[IX]", IndexColumns = "[C]", Description = "d" });

        Assert.Multiple(() =>
        {
            Assert.That(new DescriptionCheck().Run(Context(Platform.MySQL, mysql)), Is.Empty);
            Assert.That(new DescriptionCheck().Run(Context(Platform.SqlServer, sqlServer)), Is.Empty);
        });
    }
}
