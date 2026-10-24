// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System.Linq;
using NUnit.Framework;
using Schema.Domain;
using SchemaQuench.IntegrationTests.Shared;

namespace SchemaQuench.IntegrationTests.SqlServer;

/// <summary>
/// SQL Server refuses to alter a module that a schema-bound object references (3729). The deploy drops the
/// dependents the package declares -- a schema-bound view it scripts, a CHECK constraint, a computed column and its
/// index in a declared table -- applies the change, and the run puts them back; a dependent the package does not
/// declare stops the deploy with its name, and nothing is dropped. SQL Server only: SCHEMABINDING has no counterpart
/// on PostgreSQL or MySQL, where a function change never locks a dependent in place.
/// </summary>
[Category("SqlServer")]
[Category("Integration")]
[TestFixture]
public class SchemaBoundModuleChangeTests : ObjectScriptDeployFixture
{
    protected override Platform Platform => Platform.SqlServer;
    protected override string MainDb => FixtureSetup.MainDb;
    protected override string MainConnectionString => TestConnection("master");
    protected override string ConfigPrefix => "SqlServer";
    protected override string ProductName => "SchemaBoundChangeProduct";

    private const string TableJson = """
        { "Name": "[sb_orders]", "Schema": "[dbo]",
          "Columns": [ { "Name": "[id]", "DataType": "INT", "Nullable": false },
                       { "Name": "[qty]", "DataType": "INT", "Nullable": false },
                       { "Name": "[total]", "DataType": "INT", "Nullable": true, "ComputedExpression": "dbo.fn_sb_price([qty])", "Persisted": true } ],
          "Indexes": [ { "Name": "[PK_sb_orders]", "PrimaryKey": true, "Unique": true, "Clustered": true, "IndexColumns": "[id]" },
                       { "Name": "[IX_sb_orders_total]", "IndexColumns": "[total]" } ],
          "CheckConstraints": [ { "Name": "[CK_sb_orders_price]", "Expression": "dbo.fn_sb_price([qty]) >= 0" } ] }
        """;

    private static string FunctionScript(int factor) =>
        "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\n" +
        $"CREATE OR ALTER FUNCTION [dbo].[fn_sb_price](@qty INT) RETURNS INT WITH SCHEMABINDING AS BEGIN RETURN @qty * {factor} END\r\nGO\r\n";

    private const string ViewScript =
        "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\n" +
        "CREATE OR ALTER VIEW [dbo].[vw_sb_orders] WITH SCHEMABINDING AS SELECT id, qty, dbo.fn_sb_price(qty) AS price FROM dbo.sb_orders\r\nGO\r\n";

    private const string IndexedViewJson = """
        { "Schema": "[dbo]", "Name": "[iv_sb_orders]", "Definition": "SELECT id, dbo.fn_sb_price(qty) AS price FROM dbo.sb_orders",
          "Indexes": [ { "Name": "[IX_iv_sb_orders]", "IndexColumns": "[id]", "Clustered": true, "Unique": true } ] }
        """;

    private static readonly string[] Drops =
    [
        "IF OBJECT_ID('dbo.sb_filter') IS NOT NULL DROP SECURITY POLICY [dbo].[sb_filter]", "DROP FUNCTION IF EXISTS [dbo].[fn_sb_rep]",
        "DROP VIEW IF EXISTS [dbo].[vw_sb_hand]", "DROP VIEW IF EXISTS [dbo].[vw_sb_orders]", "DROP VIEW IF EXISTS [dbo].[vw_sb_const]", "DROP VIEW IF EXISTS [dbo].[iv_sb_orders]", "DROP TABLE IF EXISTS [dbo].[sb_orders]",
        "DROP FUNCTION IF EXISTS [dbo].[fn_sb_price]", "DELETE FROM SchemaSmith.ProductOwnership WHERE ProductName = 'SchemaBoundChangeProduct'"
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

    private void WritePackage(int factor, bool withView = true, bool withTable = true, bool withIndexedView = false)
    {
        WritePackage("""
            { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" },
            { "FolderPath": "SchemaBound Views", "QuenchSlot": "AfterTablesObjects", "ObjectType": "SchemaBoundViews" }
            """, "Functions", "SchemaBound Views", "Indexed Views");
        WriteFile("Functions", "fn_sb_price.sql", FunctionScript(factor));
        if (withView) WriteFile("SchemaBound Views", "vw_sb_orders.sql", ViewScript);
        if (withTable) WriteFile("Tables", "sb_orders.json", TableJson);
        if (withIndexedView) WriteFile("Indexed Views", "iv_sb_orders.json", IndexedViewJson);
    }

    [Test]
    public void AChangedFunction_UnderADeclaredIndexedView_DropsIt_AndTheViewPassPutsItBackWithItsGrant()
    {
        WritePackage(factor: 2, withView: false, withIndexedView: true);
        var first = Deploy();
        Assert.That(first.Outcomes.Values, Is.All.EqualTo(ScriptOutcome.Applied), first.Describe());
        Exec("IF USER_ID('sb_perm_user') IS NULL CREATE USER sb_perm_user WITHOUT LOGIN; GRANT SELECT ON [dbo].[iv_sb_orders] TO sb_perm_user");
        Assert.That(Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.iv_sb_orders') AND name = 'IX_iv_sb_orders'"), Is.EqualTo(1), "setup: the indexed view is built");

        WritePackage(factor: 3, withView: false, withIndexedView: true);
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcomes["Functions"], Is.EqualTo(ScriptOutcome.Applied), second.Describe());
            Assert.That(FunctionDefinition(), Does.Contain("* 3"), "the changed body must have been applied");
            Assert.That(Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.iv_sb_orders') AND name = 'IX_iv_sb_orders'"), Is.EqualTo(1), "the indexed view and its index are back");
            Assert.That(Count("SELECT COUNT(*) FROM sys.database_permissions WHERE class = 1 AND major_id = OBJECT_ID('dbo.iv_sb_orders') AND grantee_principal_id = USER_ID('sb_perm_user') AND permission_name = 'SELECT'"), Is.EqualTo(1),
                "the grant on the indexed view has to be put back after the view pass re-creates it");
            Assert.That(second.Audit.Any(r => r.ObjectType == "indexedView" && r.Action == "dropped"), Is.True, "the manifest names the dropped indexed view; audit: " + second.DescribeAudit());
        });
    }

    // A schema-bound security policy binds its predicate function. The policy's script is the package's, but the
    // dependent walk knew only modules, so it called the policy undeclared and refused the function change.
    [Test]
    public void AChangedPredicateFunction_UnderADeclaredSchemaBoundSecurityPolicy_DropsIt_AndThePolicyComesBack()
    {
        static string Predicate(string body) =>
            "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\n" +
            $"CREATE OR ALTER FUNCTION [dbo].[fn_sb_rep](@qty INT) RETURNS TABLE WITH SCHEMABINDING AS RETURN SELECT 1 AS ok WHERE {body}\r\nGO\r\n";
        const string policy = "IF OBJECT_ID(N'dbo.sb_filter') IS NULL\r\n" +
                              "  EXEC(N'CREATE SECURITY POLICY [dbo].[sb_filter] ADD FILTER PREDICATE dbo.fn_sb_rep(qty) ON dbo.sb_orders WITH (STATE = ON, SCHEMABINDING = ON)')\r\n";
        void Package(string body)
        {
            WritePackage("""
                { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" },
                { "FolderPath": "Security Policies", "QuenchSlot": "AfterTablesObjects", "ObjectType": "SecurityPolicies" }
                """, "Functions", "Security Policies");
            WriteFile("Functions", "fn_sb_price.sql", FunctionScript(2));
            WriteFile("Functions", "fn_sb_rep.sql", Predicate(body));
            WriteFile("Tables", "sb_orders.json", TableJson);
            WriteFile("Security Policies", "sb_filter.sql", policy);
        }

        Package("@qty >= 0");
        Deploy();
        Assert.That(Count("SELECT COUNT(*) FROM sys.security_policies WHERE name = 'sb_filter'"), Is.EqualTo(1), "setup: the policy is in place");

        Package("@qty >= 1");
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.fn_sb_rep'))")?.ToString(), Does.Contain("@qty >= 1"), "the changed predicate must have been applied: " + second.Describe());
            Assert.That(Count("SELECT COUNT(*) FROM sys.security_policies WHERE name = 'sb_filter' AND is_schema_bound = 1"), Is.EqualTo(1), "the policy is back, still schema-bound");
            Assert.That(second.Audit.Any(r => r.ObjectType == "securityPolicy" && r.Action == "dropped"), Is.True, "the manifest names the dropped policy; audit: " + second.DescribeAudit());
        });
    }

    [Test]
    public void AChangedFunction_UnderADeclaredSchemaBoundView_PutsTheViewBackWithItsGrant()
    {
        WritePackage(factor: 2, withTable: true);
        Deploy();
        Exec("IF USER_ID('sb_perm_user') IS NULL CREATE USER sb_perm_user WITHOUT LOGIN; GRANT SELECT ON [dbo].[vw_sb_orders] TO sb_perm_user");

        WritePackage(factor: 3, withTable: true);
        Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(FunctionDefinition(), Does.Contain("* 3"), "the changed body must have been applied");
            Assert.That(Count("SELECT COUNT(*) FROM sys.database_permissions WHERE class = 1 AND major_id = OBJECT_ID('dbo.vw_sb_orders') AND grantee_principal_id = USER_ID('sb_perm_user') AND permission_name = 'SELECT'"), Is.EqualTo(1),
                "the grant the DROP took off the schema-bound view has to come back with it");
        });
    }

    // A view the run had already skipped as unchanged, then dropped to change the function it is bound to and put back,
    // was still counted as unchanged and never as run.
    [Test]
    public void ADependentSkippedAsUnchanged_ThenDroppedAndPutBack_IsReportedAsRun()
    {
        void Package(int factor)
        {
            WritePackage("""
                { "FolderPath": "Views", "QuenchSlot": "Objects", "ObjectType": "Views" },
                { "FolderPath": "Functions", "QuenchSlot": "Objects", "ObjectType": "Functions" }
                """, "Views", "Functions");
            WriteFile("Functions", "fn_sb_price.sql", FunctionScript(factor));
            WriteFile("Views", "vw_sb_const.sql",
                "SET ANSI_NULLS ON\r\nSET QUOTED_IDENTIFIER ON\r\nGO\r\n\r\nCREATE OR ALTER VIEW [dbo].[vw_sb_const] WITH SCHEMABINDING AS SELECT dbo.fn_sb_price(2) AS price\r\nGO\r\n");
        }
        Package(factor: 2);
        Deploy();

        Package(factor: 3);
        var second = Deploy();

        var view = second.Audit.Where(r => r.ObjectName.Contains("vw_sb_const")).Select(r => r.Action).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(Count("SELECT price FROM dbo.vw_sb_const"), Is.EqualTo(6), "precondition: the view is back, over the new body");
            Assert.That(view, Does.Not.Contain("unchanged"), "the run put the view back, so it was not unchanged; audit: " + second.DescribeAudit());
            Assert.That(view, Does.Contain("ran"), "audit: " + second.DescribeAudit());
        });
    }

    private string FunctionDefinition() => Scalar("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.fn_sb_price'))")?.ToString() ?? "";

    [Test]
    public void AChangedFunction_UnderDeclaredDependents_DropsThemAppliesAndTheRunPutsThemBack()
    {
        WritePackage(factor: 2);
        var first = Deploy();
        Assert.That(first.Outcomes.Values, Is.All.EqualTo(ScriptOutcome.Applied), first.Describe());
        Exec("INSERT INTO dbo.sb_orders (id, qty) VALUES (1, 5)");
        Assert.That(Count("SELECT total FROM dbo.sb_orders WHERE id = 1"), Is.EqualTo(10), "setup: the computed column uses the function");

        WritePackage(factor: 3);
        var second = Deploy();

        Assert.Multiple(() =>
        {
            Assert.That(second.Outcomes["Functions"], Is.EqualTo(ScriptOutcome.Applied), second.Describe());
            Assert.That(FunctionDefinition(), Does.Contain("* 3"), "the changed body must have been applied");
            Assert.That(Count("SELECT total FROM dbo.sb_orders WHERE id = 1"), Is.EqualTo(15), "the computed column is back and uses the new body");
            Assert.That(Count("SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id = OBJECT_ID('dbo.sb_orders') AND name = 'CK_sb_orders_price'"), Is.EqualTo(1), "the CHECK constraint is back");
            Assert.That(Count("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.sb_orders') AND name = 'IX_sb_orders_total'"), Is.EqualTo(1), "the index on the computed column is back");
            Assert.That(Count("SELECT COUNT(*) FROM sys.views WHERE object_id = OBJECT_ID('dbo.vw_sb_orders')"), Is.EqualTo(1), "the schema-bound view is back");
            Assert.That(Count("SELECT price FROM dbo.vw_sb_orders WHERE id = 1"), Is.EqualTo(15), "and it was re-created against the new body");
            Assert.That(second.Audit.Where(r => r.Action == "dropped").Select(r => r.ObjectType), Is.EquivalentTo(new[] { "schemaBoundModule", "constraint", "column" }),
                "the manifest names each dependent that was dropped to let the change through; audit: " + second.DescribeAudit());
        });

        var third = Deploy();
        Assert.Multiple(() =>
        {
            Assert.That(third.Outcomes["Functions"], Is.EqualTo(ScriptOutcome.Unchanged), "an unchanged function is not touched, so nothing is dropped: " + third.Describe());
            Assert.That(third.Audit.Any(r => r.Action == "dropped"), Is.False, "nothing may be dropped when nothing changed; audit: " + third.DescribeAudit());
        });
    }

    [Test]
    public void AChangedFunction_UnderAnUndeclaredDependent_IsRefusedByName_AndNothingIsDropped()
    {
        WritePackage(factor: 2, withView: false, withTable: false);
        Deploy();
        Exec("CREATE VIEW [dbo].[vw_sb_hand] WITH SCHEMABINDING AS SELECT dbo.fn_sb_price(1) AS price");

        WritePackage(factor: 3, withView: false, withTable: false);
        var result = Deploy(expectSuccess: false);

        Assert.Multiple(() =>
        {
            Assert.That(result.Outcomes["Functions"], Is.EqualTo(ScriptOutcome.Failed), result.Describe());
            Assert.That(result.Log.Any(l => l.Contains("dbo.vw_sb_hand") && l.Contains("not declared by the package")), Is.True,
                "the error has to name the dependent and say why it was not dropped; log: " + string.Join("\n", result.Log.Where(l => l.StartsWith("ERROR"))));
            Assert.That(Count("SELECT COUNT(*) FROM sys.views WHERE object_id = OBJECT_ID('dbo.vw_sb_hand')"), Is.EqualTo(1), "a view the package does not declare must survive");
            Assert.That(FunctionDefinition(), Does.Contain("* 2"), "the function is left as it was");
        });
    }

    [Test]
    public void WhatIf_ReportsTheDependentsTheRealRunWouldDrop_AndTouchesNothing()
    {
        WritePackage(factor: 2);
        Deploy();
        WritePackage(factor: 3);

        var whatIf = Deploy(whatIf: true);

        Assert.Multiple(() =>
        {
            Assert.That(whatIf.Audit.Where(r => r.Action == "wouldDrop").Select(r => r.ObjectType), Is.EquivalentTo(new[] { "schemaBoundModule", "constraint", "column" }),
                "the preview lists what the real run would drop; audit: " + whatIf.DescribeAudit());
            Assert.That(FunctionDefinition(), Does.Contain("* 2"), "WhatIf changes nothing");
            Assert.That(Count("SELECT COUNT(*) FROM sys.views WHERE object_id = OBJECT_ID('dbo.vw_sb_orders')"), Is.EqualTo(1), "WhatIf drops nothing");
        });
    }
}
