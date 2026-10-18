// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// A column change under a schema-bound view, with DropSchemaBoundDependents on. The view was dropped whether or not the
/// package declared it, and one it did not declare was gone at exit 0 (#542); one it did declare came back without its
/// grants. An undeclared view now stops the deploy by name before anything is dropped, and a declared one keeps its grants.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class SchemaBoundColumnChangeTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "SchemaBoundColumnChange";

    private const string ViewScript =
        "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\n" +
        "CREATE OR ALTER VIEW [dbo].[vw_sbc_actors] WITH SCHEMABINDING AS SELECT id, actor FROM dbo.sbc_audit\r\nGO\r\n";

    private static readonly string[] Drops =
    [
        "DROP VIEW IF EXISTS [dbo].[vw_sbc_actors]", "DROP TABLE IF EXISTS [dbo].[sbc_audit]",
        "DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'SchemaBoundColumnChange'"
    ];

    [SetUp]
    public void SetUp()
    {
        foreach (var drop in Drops) Exec(drop);
    }

    [OneTimeTearDown]
    public void DropObjects()
    {
        foreach (var drop in Drops) Exec(drop);
    }

    private void WritePackage(int actorLength, bool withView)
    {
        WritePackage("""{ "FolderPath": "SchemaBound Views", "QuenchSlot": "AfterTablesObjects", "ObjectType": "SchemaBoundViews" }""", "SchemaBound Views");
        WriteFile("Tables", "sbc_audit.json", $$"""
            { "Name": "[sbc_audit]", "Schema": "[dbo]",
              "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false },
                           { "Name": "[actor]", "DataType": "VARCHAR({{actorLength}})", "Nullable": false } ],
              "Indexes": [ { "Name": "[PK_sbc_audit]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" } ] }
            """);
        if (withView) WriteFile("SchemaBound Views", "vw_sbc_actors.sql", ViewScript);
    }

    private long ActorLength() => Count("SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('dbo.sbc_audit') AND name = 'actor'");

    private long ViewCount() => Count("SELECT COUNT(*) FROM sys.views WHERE object_id = OBJECT_ID('dbo.vw_sbc_actors')");

    [Test]
    public void AColumnChange_UnderASchemaBoundViewThePackageDoesNotDeclare_StopsByName_AndTheViewSurvives()
    {
        WritePackage(50, withView: false);
        Deploy();
        Exec("CREATE VIEW [dbo].[vw_sbc_actors] WITH SCHEMABINDING AS SELECT id, actor FROM dbo.sbc_audit");

        WritePackage(100, withView: false);
        var result = Deploy(expectSuccess: false, dropSchemaBoundDependents: true);

        Assert.Multiple(() =>
        {
            Assert.That(ViewCount(), Is.EqualTo(1), "nothing in the run could re-create the view, so it must not be dropped");
            Assert.That(result.Log.Any(l => l != null && l.Contains("dbo.vw_sbc_actors") && l.Contains("does not declare")), Is.True,
                "the stop names the view and why; log: " + string.Join("\n", result.Log.Where(l => l != null && l.StartsWith("ERROR"))));
            Assert.That(ActorLength(), Is.EqualTo(50), "and the column is left as it was");
            Assert.That(result.Audit.Any(r => r.Action == "dropped"), Is.False, "nothing is dropped; audit: " + result.DescribeAudit());
        });
    }

    [Test]
    public void AColumnChange_UnderADeclaredSchemaBoundView_PutsTheViewBackWithItsGrant()
    {
        WritePackage(50, withView: true);
        Deploy();
        Exec("IF USER_ID('sbc_reader') IS NULL CREATE USER sbc_reader WITHOUT LOGIN; GRANT SELECT ON [dbo].[vw_sbc_actors] TO sbc_reader");

        WritePackage(100, withView: true);
        var result = Deploy(dropSchemaBoundDependents: true);

        Assert.Multiple(() =>
        {
            Assert.That(ActorLength(), Is.EqualTo(100), "the column change lands");
            Assert.That(ViewCount(), Is.EqualTo(1), "the view is back");
            Assert.That(Count("SELECT COUNT(*) FROM sys.database_permissions WHERE class = 1 AND major_id = OBJECT_ID('dbo.vw_sbc_actors') AND grantee_principal_id = USER_ID('sbc_reader') AND permission_name = 'SELECT'"), Is.EqualTo(1),
                "and so is the grant the DROP took off it");
            Assert.That(result.Audit.Select(r => $"{r.ObjectType} {r.Action}"), Is.SupersetOf(new[] { "schemaBoundModule dropped", "view reapplied" }), result.DescribeAudit());
        });
    }
}
